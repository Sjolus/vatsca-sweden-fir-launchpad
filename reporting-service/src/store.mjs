import { DatabaseSync } from 'node:sqlite';
import { mkdirSync, lstatSync, statSync, statfsSync, chmodSync } from 'node:fs';
import { dirname } from 'node:path';
import { createHash, randomBytes } from 'node:crypto';
import { normalizeLimits } from './config.mjs';
import { IntakeError, validateBatch, validateReport, uuid, ENUMS, VERSION } from './schema.mjs';
import {createUsageAnalytics} from './usage-analytics.mjs';

const DAY = 86400000;
const digest = value => createHash('sha256').update(value).digest('hex');
const dayOf = millis => new Date(millis).toISOString().slice(0,10);
const quota = () => { throw new IntakeError(429,'capacity_limited',3600); };
const unavailable = () => new IntakeError(503,'storage_unavailable',60);
const safeLimit = (n,max = 100) => Math.min(max,Math.max(1,Number.isSafeInteger(n) ? n : 50));

export function createStore({path,limits:requestedLimits,now = Date.now,freeSpace} = {}) {
  if (typeof path !== 'string' || !path) throw new Error('A database path is required');
  const limits = normalizeLimits(requestedLimits);
  const directory = dirname(path);
  mkdirSync(directory,{recursive:true,mode:0o700});
  try { if (!lstatSync(path).isFile() || lstatSync(path).isSymbolicLink()) throw new Error('Database must be a regular file'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const availableBytes = freeSpace || (() => { const fs = statfsSync(directory); return fs.bavail * fs.bsize; });
  const db = new DatabaseSync(path,{timeout:5000});
  try {
    chmodSync(path,0o600);
    db.exec('PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;');
    const pageSize = Number(db.prepare('PRAGMA page_size').get().page_size);
    const maxPages=Math.floor(limits.maxDbBytes/pageSize);
    const actual=db.prepare(`PRAGMA max_page_count=${maxPages}`).get().max_page_count;
    if(actual>maxPages) throw new Error('Existing database exceeds the configured ceiling');
    db.exec('PRAGMA journal_size_limit=0;');
  } catch(error) {db.close();throw error;}
  let closed = false;
  let lastHousekeepingSuccess=null, lastHousekeepingFailure=null;
  const transaction = action => {
    if (closed) throw unavailable();
    let begun = false;
    try {
      db.exec('BEGIN IMMEDIATE'); begun = true;
      const result = action();
      if (result && typeof result.then === 'function') throw new Error('Transactions must be synchronous');
      db.exec('COMMIT'); return result;
    } catch (error) {
      if (begun) { try { db.exec('ROLLBACK'); } catch {} }
      if (error instanceof IntakeError) throw error;
      if (error.code?.startsWith('ERR_SQLITE') || error.code?.startsWith('SQLITE')) throw unavailable();
      throw error;
    }
  };
  try { db.exec(`
    CREATE TABLE IF NOT EXISTS storage_totals (kind TEXT PRIMARY KEY CHECK(kind IN ('usage','errors','reports')), count INTEGER NOT NULL CHECK(count>=0), bytes INTEGER NOT NULL CHECK(bytes>=0));
    CREATE TABLE IF NOT EXISTS events (
      event_id TEXT PRIMARY KEY, stream TEXT NOT NULL CHECK(stream IN ('usage','errors')),
      support_id TEXT NOT NULL, session_id TEXT NOT NULL, event_name TEXT NOT NULL,
      application TEXT, operation TEXT, outcome TEXT, stage TEXT, error_code TEXT,
      launchpad_version TEXT NOT NULL, occurred_at INTEGER NOT NULL, received_at INTEGER NOT NULL,
      expires_at INTEGER NOT NULL, payload TEXT NOT NULL, bytes INTEGER NOT NULL, payload_hash TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS event_expiry ON events(expires_at);
    CREATE INDEX IF NOT EXISTS event_support ON events(support_id,received_at);
    CREATE INDEX IF NOT EXISTS event_stream_time ON events(stream,received_at);
    CREATE INDEX IF NOT EXISTS event_usage_window ON events(stream,occurred_at,event_name,support_id,session_id,expires_at,application,launchpad_version,received_at,event_id);
    CREATE TABLE IF NOT EXISTS analytics_state(singleton INTEGER PRIMARY KEY CHECK(singleton=1),coverage_start INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS reports (
      report_id TEXT PRIMARY KEY, reference TEXT NOT NULL UNIQUE, support_id TEXT NOT NULL,
      application TEXT NOT NULL, category TEXT NOT NULL, launchpad_version TEXT NOT NULL,
      received_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, closed_at INTEGER,
      status TEXT NOT NULL CHECK(status IN ('new','investigating','fixed','closed')),
      payload TEXT NOT NULL, bytes INTEGER NOT NULL, payload_hash TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS report_support ON reports(support_id,received_at);
    CREATE INDEX IF NOT EXISTS report_closed ON reports(closed_at);
    CREATE TABLE IF NOT EXISTS daily_counters (
      day TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('automatic','reports')), support_id TEXT NOT NULL,
      count INTEGER NOT NULL, bytes INTEGER NOT NULL, PRIMARY KEY(day,kind,support_id));
    CREATE TABLE IF NOT EXISTS support_bursts (
      minute INTEGER NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('automatic','reports')), support_id TEXT NOT NULL,
      count INTEGER NOT NULL, PRIMARY KEY(minute,kind,support_id));
    CREATE TABLE IF NOT EXISTS aggregate_state (
      id INTEGER PRIMARY KEY CHECK(id=1), rows INTEGER NOT NULL CHECK(rows>=0),
      bytes INTEGER NOT NULL CHECK(bytes>=0), limited_count INTEGER NOT NULL CHECK(limited_count>=0));
    CREATE TABLE IF NOT EXISTS daily_aggregates (
      day TEXT NOT NULL, stream TEXT NOT NULL, event_name TEXT NOT NULL, application TEXT NOT NULL,
      operation TEXT NOT NULL, outcome TEXT NOT NULL, stage TEXT NOT NULL, error_code TEXT NOT NULL,
      launchpad_version TEXT NOT NULL, count INTEGER NOT NULL, bytes INTEGER NOT NULL,
      PRIMARY KEY(day,stream,event_name,application,operation,outcome,stage,error_code,launchpad_version));
    CREATE INDEX IF NOT EXISTS aggregate_versions ON daily_aggregates(application,launchpad_version);
  `);
  // Reconcile derived totals on startup under the same writer lock. Public callers
  // cannot reset admission state; quotas stay in SQLite across process restarts.
  transaction(() => {
    db.prepare('INSERT OR IGNORE INTO analytics_state VALUES(1,?)').run(now());
    for (const kind of ['usage','errors','reports']) {
      const totals = kind === 'reports' ? db.prepare('SELECT COUNT(*) count, COALESCE(SUM(bytes),0) bytes FROM reports').get()
        : db.prepare('SELECT COUNT(*) count, COALESCE(SUM(bytes),0) bytes FROM events WHERE stream=?').get(kind);
      db.prepare('INSERT INTO storage_totals VALUES (?,?,?) ON CONFLICT(kind) DO UPDATE SET count=excluded.count,bytes=excluded.bytes').run(kind,totals.count,totals.bytes);
    }
    const totals=db.prepare('SELECT COUNT(*) rows,COALESCE(SUM(bytes),0) bytes FROM daily_aggregates').get();
    db.prepare('INSERT INTO aggregate_state VALUES(1,?,?,0) ON CONFLICT(id) DO UPDATE SET rows=excluded.rows,bytes=excluded.bytes').run(totals.rows,totals.bytes);
  }); } catch (error) {db.close();throw error;}
  function storage() {
    const rows = db.prepare('SELECT * FROM storage_totals').all();
    if (rows.length !== 3) throw unavailable();
    return Object.fromEntries(rows.map(({kind,count,bytes})=>[kind,{count,bytes}]));
  }
  function physicalBytes() {
    let result = 0;
    for (const suffix of ['', '-journal','-wal','-shm']) {
      try { result += statSync(path+suffix).size; } catch (error) { if (error.code !== 'ENOENT') throw error; }
    }
    return result;
  }
  function assertWritable() {
    try {
      if (closed || availableBytes() < limits.freeSpaceReserveBytes || statSync(path).size > limits.maxDbBytes) throw unavailable();
    } catch { throw unavailable(); }
  }
  function updateTotal(kind,count,bytes) {
    if (db.prepare('UPDATE storage_totals SET count=count+?,bytes=bytes+? WHERE kind=?').run(count,bytes,kind).changes !== 1) throw unavailable();
  }
  function pruneCounters(clock) {
    const cutoff = dayOf(clock-DAY);
    db.prepare('DELETE FROM daily_counters WHERE rowid IN (SELECT rowid FROM daily_counters WHERE day<? LIMIT 500)').run(cutoff);
    db.prepare('DELETE FROM support_bursts WHERE rowid IN (SELECT rowid FROM support_bursts WHERE minute<? LIMIT 500)').run(Math.floor(clock/60000)-1);
  }
  function checkDaily(kind,bySupport,clock,usage = false) {
    const day = dayOf(clock), minute = Math.floor(clock/60000);
    const count = [...bySupport.values()].reduce((n,v)=>n+v.count,0), bytes = [...bySupport.values()].reduce((n,v)=>n+v.bytes,0);
    const existing = db.prepare('SELECT count,bytes FROM daily_counters WHERE day=? AND kind=? AND support_id=?');
    const global = existing.get(day,kind,'*') || {count:0,bytes:0};
    const ceiling = usage ? limits.usageStopPercent/100 : 1;
    if (global.count+count > (kind === 'automatic' ? limits.autoPerDay*ceiling : limits.reportsPerDay) ||
        (kind === 'automatic' && global.bytes+bytes > limits.autoBytesPerDay*ceiling)) quota();
    let newCounters = existing.get(day,kind,'*') ? 0 : 1, newBursts = 0;
    for (const [id,added] of bySupport) {
      const current = existing.get(day,kind,id);
      if ((current?.count || 0)+added.count > (kind === 'automatic' ? limits.supportAutoPerDay : limits.supportReportsPerDay)) quota();
      if (!current) newCounters++;
      const burst = db.prepare('SELECT count FROM support_bursts WHERE minute=? AND kind=? AND support_id=?').get(minute,kind,id);
      if ((burst?.count || 0)+added.count > (kind === 'automatic' ? limits.supportAutoPerMinute : limits.supportReportsPerMinute)) quota();
      if (!burst) newBursts++;
    }
    if (db.prepare('SELECT COUNT(*) n FROM daily_counters').get().n+newCounters > limits.maxCounterRows ||
        db.prepare('SELECT COUNT(*) n FROM support_bursts').get().n+newBursts > limits.maxBurstRows) quota();
    // All admission checks precede creation of attacker-controlled identity rows.
    const increment = db.prepare('INSERT INTO daily_counters VALUES (?,?,?,?,?) ON CONFLICT(day,kind,support_id) DO UPDATE SET count=count+excluded.count,bytes=bytes+excluded.bytes');
    increment.run(day,kind,'*',count,bytes);
    for (const [id,added] of bySupport) {
      increment.run(day,kind,id,added.count,added.bytes);
      db.prepare('INSERT INTO support_bursts VALUES (?,?,?,?) ON CONFLICT(minute,kind,support_id) DO UPDATE SET count=count+excluded.count').run(minute,kind,id,added.count);
    }
  }
  function aggregate(event,stream,clock) {
    const state=db.prepare('SELECT * FROM aggregate_state WHERE id=1').get();
    if(!state) throw unavailable();
    const app=event.application || '';
    let version=event.launchpad_version;
    if(!db.prepare('SELECT 1 FROM daily_aggregates WHERE application=? AND launchpad_version=? LIMIT 1').get(app,version) &&
       db.prepare("SELECT COUNT(DISTINCT launchpad_version) n FROM daily_aggregates WHERE application=? AND launchpad_version<>'other'").get(app).n>=limits.maxAggregateVersions) version='other';
    // Only schema-enumerated dimensions and a bounded version cohort enter history.
    // Installation/session/operation IDs and exact event times never enter this table.
    const values=[dayOf(clock),stream,event.event_name,app,event.operation || '',event.outcome || 'unknown',event.stage || '',event.error_code || '',version];
    const existing=db.prepare('SELECT count FROM daily_aggregates WHERE day=? AND stream=? AND event_name=? AND application=? AND operation=? AND outcome=? AND stage=? AND error_code=? AND launchpad_version=?').get(...values);
    const bytes=Buffer.byteLength(JSON.stringify(values))+16;
    if((!existing && (state.rows+1>limits.maxAggregateRows || state.bytes+bytes>limits.maxAggregateBytes)) || existing?.count>=Number.MAX_SAFE_INTEGER) {
      db.prepare('UPDATE aggregate_state SET limited_count=MIN(limited_count+1,9007199254740991) WHERE id=1').run();return;
    }
    db.prepare('INSERT INTO daily_aggregates VALUES(?,?,?,?,?,?,?,?,?,1,?) ON CONFLICT DO UPDATE SET count=count+1').run(...values,bytes);
    if(!existing) db.prepare('UPDATE aggregate_state SET rows=rows+1,bytes=bytes+? WHERE id=1').run(bytes);
  }
  function admitBatch(stream,body) {
    const clock = now(), events = validateBatch(body,stream,clock);
    const storedStream=stream==='inventory'?'usage':stream;
    return transaction(() => {
      const fresh = new Map(); let duplicates = 0;
      const find = db.prepare('SELECT stream,payload_hash FROM events WHERE event_id=?');
      for (const event of events) {
        const payload = JSON.stringify(event), hash = digest(payload);
        const previous = fresh.get(event.event_id) || find.get(event.event_id);
        if (previous) {
          if (previous.stream !== storedStream || previous.payload_hash !== hash) throw new IntakeError(409,'identifier_conflict');
          duplicates++; continue;
        }
        fresh.set(event.event_id,{event,payload,payload_hash:hash,stream:storedStream,bytes:Buffer.byteLength(payload)});
      }
      if (fresh.size === 0) return {accepted:0,duplicates};
      assertWritable(); pruneCounters(clock);
      const totals = storage(), count = fresh.size, bytes = [...fresh.values()].reduce((n,r)=>n+r.bytes,0);
      const ceiling = storedStream === 'usage' ? limits.usageStopPercent/100 : 1;
      if (totals.usage.count+totals.errors.count+count > limits.maxEvents*ceiling ||
          totals.usage.bytes+totals.errors.bytes+bytes > limits.maxEventBytes*ceiling) quota();
      const bySupport = new Map();
      for (const row of fresh.values()) { const t = bySupport.get(row.event.support_id) || {count:0,bytes:0}; t.count++;t.bytes+=row.bytes;bySupport.set(row.event.support_id,t); }
      checkDaily('automatic',bySupport,clock,storedStream === 'usage');
      const insert = db.prepare('INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)');
      for (const row of fresh.values()) {
        const e = row.event;
        insert.run(e.event_id,storedStream,e.support_id,e.session_id,e.event_name,e.application ?? null,e.operation ?? null,e.outcome ?? null,
          e.stage ?? null,e.error_code ?? null,e.launchpad_version,Date.parse(e.occurred_at),clock,clock+limits.eventTtlDays*DAY,row.payload,row.bytes,row.payload_hash);
        if(stream!=='inventory')aggregate(e,stream,clock);
      }
      updateTotal(storedStream,count,bytes);
      return {accepted:count,duplicates};
    });
  }
  function admitReport(body) {
    const clock = now();
    // Existing manual submissions remain idempotent for the report's lifetime,
    // even when their original seven-day context window has since elapsed.
    const known = body && typeof body.report_id==='string' && db.prepare('SELECT 1 FROM reports WHERE report_id=?').get(body.report_id.toLowerCase());
    const report = validateReport(body,clock,!!known), payload = JSON.stringify(report), hash = digest(payload), bytes = Buffer.byteLength(payload);
    return transaction(() => {
      const existing = db.prepare('SELECT reference,payload_hash FROM reports WHERE report_id=?').get(report.report_id);
      if (existing) { if (existing.payload_hash !== hash) throw new IntakeError(409,'identifier_conflict'); return Object.defineProperty({reference:existing.reference},'duplicate',{value:true}); }
      assertWritable(); pruneCounters(clock);
      const totals = storage().reports;
      if (totals.count+1 > limits.maxReports || totals.bytes+bytes > limits.maxReportBytes) quota();
      checkDaily('reports',new Map([[report.support_id,{count:1,bytes}]]),clock);
      const reference = 'LP-'+randomBytes(12).toString('hex');
      db.prepare('INSERT INTO reports VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)').run(report.report_id,reference,report.support_id,
        report.application,report.category,report.launchpad_version,clock,clock,null,'new',payload,bytes,hash);
      updateTotal('reports',1,bytes);
      return {reference};
    });
  }
  function removeEvents(rows) {
    const erase = db.prepare('DELETE FROM events WHERE event_id=?');
    for (const row of rows) { erase.run(row.event_id); updateTotal(row.stream,-1,-row.bytes); }
  }
  function removeReports(rows) {
    const erase = db.prepare('DELETE FROM reports WHERE report_id=?');
    for (const row of rows) { erase.run(row.report_id); updateTotal('reports',-1,-row.bytes); }
  }
  function housekeeping() {
    try { const result=transaction(() => {
      const clock = now(); pruneCounters(clock);
      const events = db.prepare('SELECT event_id,stream,bytes FROM events WHERE expires_at<=? LIMIT 500').all(clock);
      const reports = db.prepare("SELECT report_id,bytes FROM reports WHERE status='closed' AND closed_at<=? LIMIT 100").all(clock-limits.closedReportTtlDays*DAY);
      removeEvents(events); removeReports(reports);
      const aggregates=db.prepare('SELECT rowid,bytes FROM daily_aggregates WHERE day<? LIMIT 500').all(dayOf(clock-limits.aggregateTtlDays*DAY));
      for(const row of aggregates) db.prepare('DELETE FROM daily_aggregates WHERE rowid=?').run(row.rowid);
      db.prepare('UPDATE aggregate_state SET rows=rows-?,bytes=bytes-? WHERE id=1').run(aggregates.length,aggregates.reduce((n,row)=>n+row.bytes,0));
      return {events:events.length,reports:reports.length};
    });lastHousekeepingSuccess=now();lastHousekeepingFailure=null;return result;
    } catch(error) {lastHousekeepingFailure=now();throw error;}
  }
  function addDimensions(f,input) {
    if (ENUMS.application.includes(input.application)) {f.sql+=' AND application=?';f.params.push(input.application);}
    if (typeof input.launchpad_version==='string' && input.launchpad_version.length<=40 && VERSION.test(input.launchpad_version)) {f.sql+=' AND launchpad_version=?';f.params.push(input.launchpad_version);}
    return f;
  }
  function filters(input = {}) {
    const days = input.days === 7 ? 7 : 30, params = [now()-days*DAY];
    return addDimensions({sql:'received_at>=?',params},input);
  }
  const reportColumns = 'reference,support_id,application,category,launchpad_version,status,received_at,updated_at,closed_at';
  function listReports(input = {}) {
    const f = input.days===7 || input.days===30 ? filters(input) : {sql:'1=1',params:[]};
    if (!(input.days===7 || input.days===30)) addDimensions(f,input);
    if (['new','investigating','fixed','closed'].includes(input.status)) {f.sql+=' AND status=?';f.params.push(input.status);}
    return db.prepare(`SELECT ${reportColumns} FROM reports WHERE ${f.sql} ORDER BY received_at DESC LIMIT ? OFFSET ?`).all(...f.params,safeLimit(input.limit),Math.max(0,Math.min(10000,Number.isSafeInteger(input.offset)?input.offset:0)));
  }
  function getReport(reference) {
    const row = db.prepare(`SELECT ${reportColumns},payload FROM reports WHERE reference=?`).get(reference);
    return row ? {...row,payload:JSON.parse(row.payload)} : null;
  }
  function setReportStatus(reference,status) {
    if (!['new','investigating','fixed','closed'].includes(status)) throw new IntakeError(400,'invalid_request');
    return transaction(() => {
      assertWritable();
      db.prepare('UPDATE reports SET status=?,updated_at=?,closed_at=? WHERE reference=? AND status<>?').run(status,now(),status==='closed'?now():null,reference,status);
      return getReport(reference);
    });
  }
  function groupedErrors(input = {}) {
    const f = filters(input);
    // Groups are computed from capped raw rows: no unbounded fingerprint or label table.
    return db.prepare(`SELECT application,error_code,operation,stage,launchpad_version,COUNT(*) count,MAX(received_at) last_seen FROM events WHERE stream='errors' AND ${f.sql} GROUP BY application,error_code,operation,stage,launchpad_version ORDER BY count DESC LIMIT ?`).all(...f.params,safeLimit(input.limit));
  }
  function lookupSupport(id) {
    id = uuid(id);
    return {reports:db.prepare(`SELECT ${reportColumns} FROM reports WHERE support_id=? ORDER BY received_at DESC LIMIT 100`).all(id),
      events:db.prepare('SELECT stream,payload,received_at FROM events WHERE support_id=? ORDER BY received_at DESC LIMIT 100').all(id).map(r=>({...JSON.parse(r.payload),stream:r.stream,received_at:r.received_at}))};
  }
  function deleteSupport(id) {
    id = uuid(id); const removed={events:0,reports:0};
    // Bound each rollback journal transaction; a deletion need not copy the entire database.
    for (;;) {
      const part = transaction(() => {
        const events = db.prepare('SELECT event_id,stream,bytes FROM events WHERE support_id=? LIMIT 500').all(id);
        const reports = db.prepare('SELECT report_id,bytes FROM reports WHERE support_id=? LIMIT 100').all(id);
        removeEvents(events);removeReports(reports);return {events:events.length,reports:reports.length};
      });
      removed.events+=part.events;removed.reports+=part.reports;
      if (!part.events && !part.reports) break;
    }
    transaction(() => {db.prepare('DELETE FROM daily_counters WHERE support_id=?').run(id);db.prepare('DELETE FROM support_bursts WHERE support_id=?').run(id);});
    return removed;
  }
  function overview(input = {}) {
    const f = filters(input);
    return {storage:storage(),daily:dailyCounters(),aggregate_summary:aggregateSummary(input),
      events:db.prepare(`SELECT event_name,outcome,COUNT(*) count FROM events WHERE stream='usage' AND event_name<>'inventory_snapshot' AND ${f.sql} GROUP BY event_name,outcome ORDER BY count DESC LIMIT 50`).all(...f.params),
      versions:db.prepare(`SELECT launchpad_version,COUNT(*) count FROM events WHERE stream='usage' AND event_name<>'inventory_snapshot' AND ${f.sql} GROUP BY launchpad_version ORDER BY count DESC LIMIT 50`).all(...f.params),
      report_status:db.prepare(`SELECT status,COUNT(*) count FROM reports WHERE ${f.sql} GROUP BY status`).all(...f.params)};
  }
  function dailyCounters() {
    const day=dayOf(now()),daily={};
    for(const kind of ['automatic','reports']) daily[kind]=db.prepare('SELECT count,bytes FROM daily_counters WHERE day=? AND kind=? AND support_id=?').get(day,kind,'*') || {count:0,bytes:0};
    return daily;
  }
  function aggregateSummary(input={}) {
    const days=input.days===7 || input.days===30 ? input.days : limits.aggregateTtlDays;
    const f=addDimensions({sql:'day>=?',params:[dayOf(now()-(days-1)*DAY)]},input);
    return {retention_days:limits.aggregateTtlDays,limited_count:db.prepare('SELECT limited_count FROM aggregate_state WHERE id=1').get().limited_count,
      totals:Object.fromEntries(db.prepare(`SELECT stream,SUM(count) count FROM daily_aggregates WHERE ${f.sql} GROUP BY stream`).all(...f.params).map(row=>[row.stream,row.count])),
      events:db.prepare(`SELECT event_name,outcome,SUM(count) count FROM daily_aggregates WHERE ${f.sql} GROUP BY event_name,outcome ORDER BY count DESC LIMIT 50`).all(...f.params),
      versions:db.prepare(`SELECT launchpad_version,SUM(count) count FROM daily_aggregates WHERE stream='usage' AND ${f.sql} GROUP BY launchpad_version ORDER BY count DESC LIMIT 50`).all(...f.params),
      daily:db.prepare(`SELECT day,SUM(CASE WHEN stream='usage' THEN count ELSE 0 END) usage,SUM(CASE WHEN stream='errors' THEN count ELSE 0 END) errors FROM daily_aggregates WHERE ${f.sql} GROUP BY day ORDER BY day LIMIT 365`).all(...f.params)};
  }
  function health() {
    const free = availableBytes(),physical = physicalBytes(),totals=storage(),aggregate=db.prepare('SELECT * FROM aggregate_state WHERE id=1').get();
    const daily=dailyCounters();
    return {ok:!closed && free>=limits.freeSpaceReserveBytes && physical<=limits.maxDbBytes,
      physical_bytes:physical,max_db_bytes:limits.maxDbBytes,free_bytes:free,reserved_free_bytes:limits.freeSpaceReserveBytes,
      usage_paused:totals.usage.count+totals.errors.count>=limits.maxEvents*limits.usageStopPercent/100 || totals.usage.bytes+totals.errors.bytes>=limits.maxEventBytes*limits.usageStopPercent/100 || daily.automatic.count>=limits.autoPerDay*limits.usageStopPercent/100 || daily.automatic.bytes>=limits.autoBytesPerDay*limits.usageStopPercent/100,
      storage:totals,usage_storage_includes_inventory:true,inventory_records:db.prepare("SELECT COUNT(*) n FROM events WHERE event_name='inventory_snapshot'").get().n,
      counter_rows:db.prepare('SELECT COUNT(*) n FROM daily_counters').get().n,burst_rows:db.prepare('SELECT COUNT(*) n FROM support_bursts').get().n,
      aggregate:{rows:aggregate.rows,bytes:aggregate.bytes,limited_count:aggregate.limited_count,max_rows:limits.maxAggregateRows,max_bytes:limits.maxAggregateBytes,retention_days:limits.aggregateTtlDays},
      retention:{last_success:lastHousekeepingSuccess,last_failure:lastHousekeepingFailure,
        expired_events:db.prepare('SELECT COUNT(*) n FROM events WHERE expires_at<=?').get(now()).n},backup:{status:'operator_verification_required'}};
  }
  const usage=createUsageAnalytics({db,now,limits});
  return {db,limits,transaction,assertWritable,admitBatch,admitReport,housekeeping,overview,usage,listReports,getReport,setReportStatus,groupedErrors,lookupSupport,deleteSupport,health,
    close(){if (!closed) {db.close();closed=true;}}};
}
