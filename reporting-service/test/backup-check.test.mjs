import {test} from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,mkdirSync,writeFileSync,readFileSync,readdirSync,rmSync,symlinkSync,linkSync,truncateSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {spawnSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {checkBackups} from '../scripts/check-backups.mjs';

const HOUR=3600000,CLOCK=Date.parse('2026-10-14T12:00:00Z'),MAX=131072;
function fixture(t) {
  const root=mkdtempSync(join(tmpdir(),'launchpad-backup-check-synthetic-')),directory=join(root,'backups');
  mkdirSync(directory);t.after(()=>rmSync(root,{recursive:true,force:true}));
  const snapshot=(ageHours,text='synthetic snapshot')=>{const path=join(directory,`backup-${CLOCK-ageHours*HOUR}.sqlite`);writeFileSync(path,text);return path;};
  return {root,directory,snapshot,check:()=>checkBackups({directory,now:()=>CLOCK,maxBytes:MAX})};
}

test('independent backup check expires only old managed snapshots and reports bounded freshness metadata',t=>{
  const f=fixture(t),old=f.snapshot(49),boundary=f.snapshot(48),fresh=f.snapshot(24);
  const result=f.check();
  assert.deepEqual(result,{ok:true,status:'fresh',snapshots:2,bytes:36,expired_snapshots:1,expired_bytes:18,newest_created_at:CLOCK-24*HOUR,newest_age_ms:24*HOUR});
  assert.deepEqual(readdirSync(f.directory).sort(),[boundary,fresh].map(path=>path.split(/[\\/]/).at(-1)).sort());
  assert.throws(()=>readFileSync(old));assert.equal(readFileSync(boundary,'utf8'),'synthetic snapshot');
  assert.ok(!JSON.stringify(result).includes(f.root));
});

test('missing and stale backups fail freshness without extending two-day retention',t=>{
  const f=fixture(t);assert.equal(f.check().status,'missing');
  const stale=f.snapshot(31);assert.equal(f.check().status,'stale');assert.equal(f.check().ok,false);
  f.snapshot(49);const result=f.check();assert.equal(result.expired_snapshots,1);assert.equal(result.snapshots,1);assert.equal(result.status,'stale');
  assert.equal(readFileSync(stale,'utf8'),'synthetic snapshot');
  const exact=fixture(t);exact.snapshot(30);assert.equal(exact.check().ok,true);
  const onlyExpired=fixture(t);onlyExpired.snapshot(49);assert.equal(onlyExpired.check().status,'missing');assert.deepEqual(readdirSync(onlyExpired.directory),[]);
});

test('live lock and orphan pending work are preserved and never trigger expiry',t=>{
  const f=fixture(t),old=f.snapshot(49),lock=join(f.directory,'backup.lock'),pending=join(f.directory,'backup.pending.sqlite');
  writeFileSync(lock,'synthetic active lock');writeFileSync(pending,'synthetic copy');
  assert.throws(f.check);assert.equal(readFileSync(lock,'utf8'),'synthetic active lock');assert.equal(readFileSync(old,'utf8'),'synthetic snapshot');
  rmSync(lock);assert.throws(f.check);assert.equal(readFileSync(pending,'utf8'),'synthetic copy');assert.equal(readFileSync(old,'utf8'),'synthetic snapshot');
  assert.ok(!readdirSync(f.directory).includes('backup.lock'),'only the checker-owned lock is released');
});

test('unexpected entries, count/byte overflow, future timestamps and nonfiles stop before deleting any snapshot',t=>{
  for(const kind of ['unexpected','count','oversize','future','directory','empty']) {
    const f=fixture(t),old=f.snapshot(49);
    if(kind==='unexpected')writeFileSync(join(f.directory,'unrelated.txt'),'preserve');
    if(kind==='count')for(const age of [1,2,3])f.snapshot(age);
    if(kind==='oversize'){const file=f.snapshot(1);truncateSync(file,MAX+1);}
    if(kind==='future')f.snapshot(-1);
    if(kind==='directory')mkdirSync(join(f.directory,`backup-${CLOCK-HOUR}.sqlite`));
    if(kind==='empty')f.snapshot(1,'');
    assert.throws(f.check,undefined,kind);assert.equal(readFileSync(old,'utf8'),'synthetic snapshot',kind);
    assert.ok(!readdirSync(f.directory).includes('backup.lock'),kind);
  }
});

test('linked backup paths and hard-linked snapshots are rejected without modifying their targets',t=>{
  const f=fixture(t),target=join(f.root,'outside');mkdirSync(target);writeFileSync(join(target,'keep.txt'),'preserve');
  const link=join(f.root,'linked-backups');symlinkSync(target,link,process.platform==='win32'?'junction':'dir');
  assert.throws(()=>checkBackups({directory:link,now:()=>CLOCK,maxBytes:MAX}));
  const ancestor=join(link,'nested');mkdirSync(ancestor);
  assert.throws(()=>checkBackups({directory:ancestor,now:()=>CLOCK,maxBytes:MAX}));
  symlinkSync(target,join(f.directory,`backup-${CLOCK-49*HOUR}.sqlite`),process.platform==='win32'?'junction':'dir');
  assert.throws(f.check);assert.equal(readFileSync(join(target,'keep.txt'),'utf8'),'preserve');
  const hard=fixture(t),original=join(hard.root,'original.sqlite');writeFileSync(original,'preserve');
  linkSync(original,join(hard.directory,`backup-${CLOCK-49*HOUR}.sqlite`));assert.throws(hard.check);assert.equal(readFileSync(original,'utf8'),'preserve');
});

test('CLI returns nonzero for stale/missing backups and logs no directory names or file contents',t=>{
  const f=fixture(t),script=fileURLToPath(new URL('../scripts/check-backups.mjs',import.meta.url));
  function run(){return spawnSync(process.execPath,[script,'--directory',f.directory],{encoding:'utf8'});}
  let result=run();assert.equal(result.status,1);assert.equal(JSON.parse(result.stdout).status,'missing');
  writeFileSync(join(f.directory,`backup-${Date.now()-31*HOUR}.sqlite`),'PRIVATE_SYNTHETIC_CONTENT');
  result=run();assert.equal(result.status,1);assert.equal(JSON.parse(result.stdout).status,'stale');
  writeFileSync(join(f.directory,`backup-${Date.now()-HOUR}.sqlite`),'PRIVATE_SYNTHETIC_CONTENT');
  result=run();assert.equal(result.status,0);assert.equal(JSON.parse(result.stdout).status,'fresh');
  writeFileSync(join(f.directory,'private-name.txt'),'PRIVATE_SYNTHETIC_CONTENT');
  result=run();assert.equal(result.status,1);assert.equal(result.stdout,'');
  assert.ok(!result.stderr.includes(f.root));assert.ok(!result.stderr.includes('private-name'));assert.ok(!result.stderr.includes('PRIVATE_SYNTHETIC_CONTENT'));
});
