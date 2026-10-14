import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { statSync } from 'node:fs';
import { Worker } from 'node:worker_threads';
import { fixture,event,report,batch,CLOCK,SUPPORT } from './intake-helpers.mjs';
import {normalizeLimits} from '../src/config.mjs';

test('durable atomic admission deduplicates retries and rejects conflicting event/report identities',t=>{
  const f=fixture(t),e=event();
  assert.deepEqual(f.store.admitBatch('usage',batch(e,e)),{accepted:1,duplicates:1});
  const receipt=f.store.admitReport(report({report_id:e.event_id,context:[e]}));
  f.store.close();const restarted=f.open();
  assert.deepEqual(restarted.admitBatch('usage',batch(e)),{accepted:0,duplicates:1});
  assert.equal(restarted.admitReport(report({report_id:e.event_id,context:[e]})).reference,receipt.reference);
  assert.throws(()=>restarted.admitBatch('usage',batch({...e,outcome:'failed'})),{status:409});
  assert.throws(()=>restarted.admitReport(report({report_id:e.event_id,category:'other'})),{status:409});
  assert.equal(restarted.overview().daily.automatic.count,1);
  assert.equal(restarted.overview().storage.reports.count,1);
  f.advance(8*86400000);
  assert.equal(restarted.admitReport(report({report_id:e.event_id,context:[e]})).reference,receipt.reference,'manual retries remain idempotent after context ages');
});
test('entire mixed batch rolls back on validation/quota conflict without inserting identity metadata',t=>{
  const {store}=fixture(t,{limits:{maxEvents:5,autoPerDay:5,supportAutoPerDay:4}});
  assert.throws(()=>store.admitBatch('usage',batch(event(),event({email:'synthetic@example.invalid'}))),{status:400});
  assert.equal(store.overview().storage.usage.count,0);
  store.admitBatch('usage',batch(event(),event(),event(),event()));
  const before=store.health();
  assert.throws(()=>store.admitBatch('usage',batch(event({support_id:randomUUID()}))),{status:429});
  assert.equal(store.health().counter_rows,before.counter_rows);
  assert.equal(store.health().burst_rows,before.burst_rows);
  store.admitBatch('errors',batch(event({event_name:'operation_failed',support_id:randomUUID()})));
  assert.throws(()=>store.admitBatch('errors',batch(event({event_name:'operation_failed',support_id:randomUUID()}))),{status:429});
  assert.match(store.admitReport(report()).reference,/^LP-[a-f0-9]{24}$/,'manual reserve survives full automatic allowance');
});
test('daily quotas survive restarts, and retained data caps survive midnight even if expiry is broken',t=>{
  const f=fixture(t,{limits:{maxEvents:3,autoPerDay:3,supportAutoPerDay:2,usageStopPercent:80}});
  f.store.admitBatch('errors',batch(event({event_name:'operation_failed'}),event({event_name:'operation_failed'})));
  f.store.close();let store=f.open();
  assert.throws(()=>store.admitBatch('errors',batch(event({event_name:'operation_failed'}))),{status:429});
  f.advance(86400000);
  store.admitBatch('errors',batch(event({event_name:'operation_failed',occurred_at:new Date(f.now()).toISOString()})));
  assert.throws(()=>store.admitBatch('errors',batch(event({event_name:'operation_failed',occurred_at:new Date(f.now()).toISOString(),support_id:randomUUID()}))),{status:429});
  assert.equal(store.overview().storage.errors.count,3);
});
test('bounded source-identity auxiliary tables reject random UUID floods before creating more rows',t=>{
  const {store}=fixture(t,{limits:{maxCounterRows:3,maxBurstRows:2}});
  store.admitBatch('errors',batch(event({event_name:'operation_failed',support_id:randomUUID()}),event({event_name:'operation_failed',support_id:randomUUID()})));
  for(let i=0;i<10;i++)assert.throws(()=>store.admitBatch('errors',batch(event({event_name:'operation_failed',support_id:randomUUID()}))),{status:429});
  assert.equal(store.health().counter_rows,3);assert.equal(store.health().burst_rows,2);
});
test('payload and manual quotas, minute bursts, physical free-space guard and missing totals fail closed',t=>{
  const f=fixture(t,{limits:{maxEventBytes:800,maxReports:1,supportReportsPerMinute:1}}),e=event();
  f.store.admitBatch('errors',batch({...e,event_name:'operation_failed'}));
  assert.throws(()=>f.store.admitBatch('errors',batch(event({event_name:'operation_failed'}),event({event_name:'operation_failed'}))),{status:429});
  f.store.admitReport(report());assert.throws(()=>f.store.admitReport(report()),{status:429});
  const low=f.open({freeSpace:()=>1});
  assert.throws(()=>low.admitBatch('errors',batch(event({event_name:'operation_failed'}))),{status:503});
  f.store.db.prepare("DELETE FROM storage_totals WHERE kind='usage'").run();
  assert.throws(()=>f.store.admitBatch('errors',batch(event({event_name:'operation_failed'}))),{status:503});
});
test('expiry frees logical budget, preserves open reports, and deletion excludes owner/auth data',t=>{
  const f=fixture(t,{limits:{eventTtlDays:8,closedReportTtlDays:1}});
  f.store.admitBatch('usage',batch(event()));
  const open=f.store.admitReport(report()),closed=f.store.admitReport(report());
  f.store.setReportStatus(closed.reference,'closed');
  f.store.db.exec("CREATE TABLE synthetic_auth_owner(id INTEGER); INSERT INTO synthetic_auth_owner VALUES (1);");
  f.advance(9*86400000);assert.deepEqual(f.store.housekeeping(),{events:1,reports:1});
  assert.equal(f.store.getReport(open.reference).status,'new');assert.equal(f.store.getReport(closed.reference),null);
  f.advance(40*86400000);assert.equal(f.store.listReports().length,1,'old unresolved reports remain in inbox');
  assert.deepEqual(f.store.deleteSupport(SUPPORT),{events:0,reports:1});
  assert.equal(f.store.db.prepare('SELECT COUNT(*) n FROM synthetic_auth_owner').get().n,1);
  assert.equal(f.store.overview().storage.reports.count,0);
});
test('SQLite page ceiling, DELETE journal and full synchronization enforce bounded physical database growth',t=>{
  const {store,path}=fixture(t,{limits:{maxDbBytes:131072}});
  assert.equal(store.db.prepare('PRAGMA journal_mode').get().journal_mode,'delete');
  assert.equal(store.db.prepare('PRAGMA synchronous').get().synchronous,2);
  assert.ok(store.db.prepare('PRAGMA max_page_count').get().max_page_count*4096<=131072);
  let stopped=false,accepted=0;
  for(let i=0;i<200;i++) {try{store.admitReport(report({support_id:randomUUID()}));accepted++;}catch(error){assert.equal(error.status,503);stopped=true;break;}}
  assert.ok(stopped);assert.ok(statSync(path).size<=131072);
  assert.equal(store.overview().daily.reports.count,accepted,'SQLITE_FULL must roll back quota and inserted identity metadata');
  assert.equal(store.overview().storage.reports.count,accepted);
});

test('per-UUID minute/day limits and global automatic byte/manual daily budgets are durable',t=>{
  const f=fixture(t,{limits:{supportAutoPerMinute:1,supportReportsPerMinute:1,supportReportsPerDay:2,reportsPerDay:3,autoBytesPerDay:700}});
  f.store.admitBatch('errors',batch(event({event_name:'operation_failed'})));
  assert.throws(()=>f.store.admitBatch('errors',batch(event({event_name:'operation_failed'}))),{status:429});
  f.advance(60000);
  assert.throws(()=>f.store.admitBatch('errors',batch(event({event_name:'operation_failed',support_id:randomUUID()}),event({event_name:'operation_failed',support_id:randomUUID()}))),{status:429});
  f.store.admitReport(report());assert.throws(()=>f.store.admitReport(report()),{status:429});
  f.advance(60000);f.store.admitReport(report());
  f.advance(60000);assert.throws(()=>f.store.admitReport(report()),{status:429});
  f.store.admitReport(report({support_id:randomUUID()}));
  f.store.close();const restarted=f.open();
  assert.throws(()=>restarted.admitReport(report({support_id:randomUUID()})),{status:429});
  assert.equal(restarted.overview().daily.reports.count,3);
});

test('identifier-free aggregate history deduplicates, bounds release cohorts, survives raw expiry and obeys retention',t=>{
  const f=fixture(t,{limits:{maxAggregateVersions:1,eventTtlDays:8}}),first=event();
  f.store.admitBatch('usage',batch(first));f.store.admitBatch('usage',batch(first));
  f.store.admitBatch('usage',batch(event({launchpad_version:'3.0.0'}),event({launchpad_version:'4.0.0',application:'trackaudio'})));
  let overview=f.store.overview();
  assert.equal(overview.aggregate_summary.totals.usage,3);
  assert.equal(overview.aggregate_summary.versions.find(row=>row.launchpad_version==='other').count,1);
  assert.equal(f.store.overview({application:'trackaudio',launchpad_version:'4.0.0'}).aggregate_summary.totals.usage,1);
  const columns=f.store.db.prepare('PRAGMA table_info(daily_aggregates)').all().map(row=>row.name);
  assert.ok(!columns.some(name=>/id|payload|time/.test(name)),'aggregate schema cannot retain correlation identifiers');
  f.advance(9*86400000);f.store.housekeeping();overview=f.store.overview();
  assert.equal(overview.storage.usage.count,0);assert.equal(overview.aggregate_summary.totals.usage,3);
  f.advance(357*86400000);f.store.housekeeping();
  assert.equal(f.store.health().aggregate.rows,0);assert.equal(f.store.health().aggregate.bytes,0);
  assert.equal(f.store.health().retention.last_success,f.now());
});

test('configured retention always preserves dedupe through the accepted retry horizon',t=>{
  assert.throws(()=>normalizeLimits({eventTtlDays:7}));
  const f=fixture(t,{limits:{eventTtlDays:8}}),e=event({occurred_at:new Date(CLOCK+300000).toISOString()});
  f.store.admitBatch('usage',batch(e));f.advance(7*86400000+300000);f.store.housekeeping();
  assert.deepEqual(f.store.admitBatch('usage',batch(e)),{accepted:0,duplicates:1});
});

test('aggregate capacity drops only optional historical counts without changing durable raw acknowledgement',t=>{
  const f=fixture(t,{limits:{maxAggregateRows:1}});
  assert.equal(f.store.admitBatch('errors',batch(event({event_name:'operation_failed'}),event({event_name:'operation_failed',stage:'apply'}))).accepted,2);
  assert.equal(f.store.health().aggregate.rows,1);assert.equal(f.store.health().aggregate.limited_count,1);
  f.store.close();const restarted=f.open();
  assert.equal(restarted.health().aggregate.limited_count,1);
  assert.equal(restarted.overview().storage.errors.count,2);
});
test('two concurrent SQLite writers cannot exceed the same global admission ceiling',async t=>{
  const f=fixture(t,{limits:{maxEvents:10,autoPerDay:10}});f.store.close();
  const moduleUrl=new URL('../src/store.mjs',import.meta.url).href;
  const tasks=Array.from({length:2},()=>new Promise((resolve,reject)=>{
    const worker=new Worker(`const {parentPort,workerData}=require('node:worker_threads'); import(workerData.moduleUrl).then(({createStore})=>{const store=createStore({path:workerData.path,now:()=>workerData.clock,freeSpace:()=>1e9,limits:{maxEvents:10,autoPerDay:10}});let accepted=0;for(const event of workerData.events){try{accepted+=store.admitBatch('errors',{schema_version:1,events:[event]}).accepted;}catch(e){if(e.status!==429)throw e;}}store.close();parentPort.postMessage(accepted);}).catch(e=>{throw e;});`,{eval:true,workerData:{moduleUrl,path:f.path,clock:CLOCK,events:Array.from({length:10},()=>event({event_name:'operation_failed',support_id:randomUUID()}))}});
    worker.once('message',resolve);worker.once('error',reject);
  }));
  assert.equal((await Promise.all(tasks)).reduce((a,b)=>a+b,0),10);
  assert.equal(f.open().overview().storage.errors.count,10);
});
