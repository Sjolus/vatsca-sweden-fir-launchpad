#!/usr/bin/env node
import {readFileSync,lstatSync} from 'node:fs';
import {isAbsolute,basename,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {randomUUID} from 'node:crypto';
import {createStore} from '../src/store.mjs';
import {uuid} from '../src/schema.mjs';

// Run on an offline copy, after verifying the deletion list against the current
// operator records. The service never restores or exposes a backup by itself.
export function prepareRestore({path,deletions,now=Date.now,limits,freeSpace}={}) {
  if(!isAbsolute(path) || basename(path)!=='restore-staging.sqlite')throw new Error('Use an offline restore-staging.sqlite copy');
  const stat=lstatSync(path);
  if(!stat.isFile() || stat.isSymbolicLink() || stat.nlink!==1)throw new Error('Use a separate regular staging copy');
  if(!Array.isArray(deletions) || deletions.length>10000)throw new Error('A verified bounded deletion list is required');
  const ids=[...new Set(deletions.map(uuid))];
  const store=createStore({path,now,limits,freeSpace});
  try {
    store.assertWritable();
    if(store.db.prepare('PRAGMA quick_check').get().quick_check!=='ok')throw new Error('Restore integrity verification failed');
    const has=name=>!!store.db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?").get(name);
    store.transaction(()=>{
      // Restored credentials/counters may predate a later recovery. Require
      // explicit owner enrollment again instead of reviving the old credential.
      for(const table of ['auth_sessions','auth_challenges','admin_audit'])if(has(table))store.db.exec(`DELETE FROM ${table}`);
      if(has('auth_owner'))store.db.prepare("UPDATE auth_owner SET user_handle=?,credential_id=NULL,public_key=NULL,counter=0,transports='[]',epoch=?,enrollment_hash=NULL,enrollment_expires=NULL").run(randomUUID(),randomUUID());
      // The restored snapshot cannot establish continuous observation through
      // the lost interval, even if older session rows are still available.
      store.db.prepare('UPDATE analytics_state SET coverage_start=? WHERE singleton=1').run(now());
    });
    for(const id of ids)store.deleteSupport(id);
    for(let pass=0;pass<300;pass++) {
      const result=store.housekeeping();
      const oldAggregate=store.db.prepare('SELECT 1 FROM daily_aggregates WHERE day<? LIMIT 1').get(new Date(now()-store.limits.aggregateTtlDays*86400000).toISOString().slice(0,10));
      if(!result.events && !result.reports && !oldAggregate)break;
      if(pass===299)throw new Error('Restore cleanup exceeded its bounded work limit');
    }
    store.transaction(()=>{
      // A snapshot cannot know how much intake happened after it was taken.
      // Fail closed for the restore day; normal daily quotas resume next UTC day.
      const day=new Date(now()).toISOString().slice(0,10);
      const write=store.db.prepare("INSERT INTO daily_counters VALUES(?,?, '*',?,?) ON CONFLICT(day,kind,support_id) DO UPDATE SET count=excluded.count,bytes=excluded.bytes");
      write.run(day,'automatic',store.limits.autoPerDay,store.limits.autoBytesPerDay);
      write.run(day,'reports',store.limits.reportsPerDay,store.limits.maxReportBytes);
    });
    return {prepared:true,reenrollment_required:true,intake_resumes:'next_utc_day'};
  } finally {store.close();}
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
    const args=process.argv.slice(2);
    if(args.length!==5 || args[0]!=='--offline-staging-copy' || args[1]!=='--database' || args[3]!=='--verified-deletions')throw new Error('Invalid arguments');
    const stat=lstatSync(args[4]);if(!stat.isFile() || stat.isSymbolicLink() || stat.size>524288)throw new Error('Invalid deletion manifest');
    const deletions=JSON.parse(readFileSync(args[4],'utf8'));
    console.log(JSON.stringify(prepareRestore({path:args[2],deletions})));
  } catch {console.error('Restore preparation failed. Keep the staging database offline; verify the backup, deletion records and storage.');process.exitCode=1;}
}
