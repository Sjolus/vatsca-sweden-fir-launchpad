import {test} from 'node:test';
import assert from 'node:assert/strict';
import {join} from 'node:path';
import {mkdirSync,readdirSync,readFileSync,writeFileSync,statSync,copyFileSync} from 'node:fs';
import {DatabaseSync} from 'node:sqlite';
import {createBackup} from '../scripts/backup.mjs';
import {prepareRestore} from '../scripts/prepare-restore.mjs';
import {initializeAuth,prepareEnrollment,secret,createAuth} from '../src/auth.mjs';
import {createStore} from '../src/store.mjs';
import {fixture,event,batch,report,SUPPORT} from './intake-helpers.mjs';

test('SQLite backups are consistent, bounded to two snapshots plus staging, and preserve known-good copies on failure',async t=>{
  const f=fixture(t),directory=join(f.directory,'backups');
  const options={source:f.path,directory,now:f.now,maxBytes:1048576,reserveBytes:131072,freeSpace:()=>10*1048576};
  f.store.admitBatch('usage',batch(event()));f.store.admitReport(report());
  await createBackup(options);f.advance(1000);await createBackup(options);
  const before=new Map(readdirSync(directory).map(name=>[name,readFileSync(join(directory,name))]));
  f.advance(1000);
  await assert.rejects(createBackup({...options,copy:async(_db,path)=>{writeFileSync(path,'interrupted copy');throw new Error('synthetic');}}));
  assert.equal(readdirSync(directory).length,2);
  for(const [name,bytes] of before)assert.deepEqual(readFileSync(join(directory,name)),bytes);
  const result=await createBackup(options);assert.equal(result.snapshots,2);
  for(const file of readdirSync(directory)) {
    assert.ok(statSync(join(directory,file)).size<=1048576);
    const db=new DatabaseSync(join(directory,file),{readOnly:true});
    try{assert.equal(db.prepare('PRAGMA quick_check').get().quick_check,'ok');assert.equal(db.prepare('SELECT COUNT(*) n FROM reports').get().n,1);assert.equal(db.prepare('SELECT COUNT(*) n FROM events').get().n,1);}finally{db.close();}
  }
});

test('backup refuses low space, unexpected inventory, concurrent lock and oversize source before copying',async t=>{
  const f=fixture(t),directory=join(f.directory,'backups');
  const options={source:f.path,directory,now:f.now,maxBytes:1048576,reserveBytes:131072,freeSpace:()=>1};
  await assert.rejects(createBackup(options));assert.deepEqual(readdirSync(directory),[]);
  writeFileSync(join(directory,'backup.lock'),'synthetic active operation');
  await assert.rejects(createBackup(options));assert.equal(readFileSync(join(directory,'backup.lock'),'utf8'),'synthetic active operation');
  const other=join(f.directory,'unknown-backups');mkdirSync(other);writeFileSync(join(other,'unrelated.txt'),'preserve');
  await assert.rejects(createBackup({...options,directory:other,freeSpace:()=>1e9}));assert.equal(readFileSync(join(other,'unrelated.txt'),'utf8'),'preserve');
  f.store.db.exec('CREATE TABLE synthetic_large_record(value BLOB); INSERT INTO synthetic_large_record VALUES(zeroblob(2097152));');
  const oversized=join(f.directory,'oversized-backups');let copied=false;
  await assert.rejects(createBackup({...options,directory:oversized,freeSpace:()=>1e9,copy:async()=>{copied=true;}}));
  assert.equal(copied,false);assert.deepEqual(readdirSync(oversized),[]);
});

test('backup age retention removes only expired managed snapshots and rejects corrupted replacements',async t=>{
  const f=fixture(t),directory=join(f.directory,'backups');
  const options={source:f.path,directory,now:f.now,maxBytes:1048576,reserveBytes:131072,freeSpace:()=>1e9};
  await createBackup(options);f.advance(3*86400000);await createBackup(options);
  assert.equal(readdirSync(directory).length,1);
  f.advance(1000);
  await assert.rejects(createBackup({...options,copy:async(_db,path)=>writeFileSync(path,'not SQLite')}));
  assert.equal(readdirSync(directory).length,1);
});

test('offline restore reapplies expiry/deletions, revokes all authentication and blocks a fresh quota allowance that day',async t=>{
  const f=fixture(t),directory=join(f.directory,'backups'),staging=join(f.directory,'restore-staging.sqlite');
  initializeAuth(f.store);prepareEnrollment(f.store,{token:secret(),now:f.now()});
  f.store.admitReport(report());f.store.admitBatch('usage',batch(event()));
  f.store.db.prepare('INSERT INTO auth_sessions(token_hash,epoch,created_at,last_seen,expires_at) VALUES(?,?,?,?,?)').run('synthetic',f.store.db.prepare('SELECT epoch FROM auth_owner').get().epoch,f.now(),f.now(),f.now()+3600000);
  const auth=createAuth({store:f.store,origin:'https://launchpad.example',now:f.now});
  const invited=auth.createUser({username:'helper',permissions:['reports']});
  const enrolled=auth.createUser({username:'reviewer',permissions:['usage']});
  f.store.db.prepare("UPDATE auth_users SET credential_id='synthetic-credential',public_key=?,invitation_hash=NULL,invitation_expires=NULL WHERE id=?").run(Buffer.from('synthetic'),enrolled.user.id);
  await createBackup({source:f.path,directory,now:f.now,maxBytes:1048576,reserveBytes:131072,freeSpace:()=>1e9});
  copyFileSync(join(directory,readdirSync(directory)[0]),staging);f.advance(31*86400000);
  assert.throws(()=>prepareRestore({path:staging}),'omitting the operator deletion record must stop restore');
  const prepared=prepareRestore({path:staging,deletions:[SUPPORT],now:f.now,freeSpace:()=>1e9});
  assert.equal(prepared.reenrollment_required,true);
  const store=createStore({path:staging,now:f.now,freeSpace:()=>1e9});
  try {
    assert.equal(store.overview().storage.reports.count,0);assert.equal(store.overview().storage.usage.count,0);
    assert.equal(store.db.prepare('SELECT COUNT(*) n FROM auth_sessions').get().n,0);
    assert.equal(store.db.prepare('SELECT coverage_start FROM analytics_state WHERE singleton=1').get().coverage_start,f.now());
    assert.equal(store.usage().window.coverage_complete,false);
    const owner=store.db.prepare('SELECT credential_id,enrollment_hash FROM auth_owner').get();assert.equal(owner.credential_id,null);assert.equal(owner.enrollment_hash,null);
    for(const id of [invited.user.id,enrolled.user.id]) {
      const user=store.db.prepare('SELECT * FROM auth_users WHERE id=?').get(id);
      assert.equal(user.enabled,0);assert.equal(user.credential_id,null);assert.equal(user.public_key,null);
      assert.equal(user.invitation_hash,null);assert.equal(user.invitation_expires,null);assert.equal(user.counter,0);
    }
    assert.throws(()=>store.admitReport(report()),{status:429});
    f.advance(86400000);assert.ok(store.admitReport(report()).reference);
  } finally {store.close();}
});
