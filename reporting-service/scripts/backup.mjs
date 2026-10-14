#!/usr/bin/env node
import {DatabaseSync,backup as sqliteBackup} from 'node:sqlite';
import {mkdirSync,lstatSync,readdirSync,statfsSync,openSync,closeSync,fsyncSync,unlinkSync,renameSync,chmodSync,realpathSync} from 'node:fs';
import {resolve,join,isAbsolute,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {DEFAULT_LIMITS} from '../src/config.mjs';

const AGE=2*86400000;
const regular=path=>{const stat=lstatSync(path);if(!stat.isFile() || stat.isSymbolicLink())throw new Error('Expected a regular backup file');return stat;};
function sync(path,mode='r+') {const fd=openSync(path,mode);try{fsyncSync(fd);}finally{closeSync(fd);}}
function syncDirectory(path) {if(process.platform!=='win32')sync(path,'r');}

// The source stays in a read transaction while SQLite copies it. This fixes the
// snapshot size and avoids a continuously growing source restarting the backup.
export async function createBackup({source,directory,now=Date.now,maxBytes=DEFAULT_LIMITS.maxDbBytes,
  reserveBytes=DEFAULT_LIMITS.freeSpaceReserveBytes,freeSpace,copy=sqliteBackup}={}) {
  if(!isAbsolute(source) || !isAbsolute(directory) || !Number.isSafeInteger(maxBytes) || maxBytes<131072 || maxBytes>DEFAULT_LIMITS.maxDbBytes)
    throw new Error('Absolute paths and a bounded database size are required');
  regular(source);
  mkdirSync(directory,{recursive:true,mode:0o700});
  if(lstatSync(directory).isSymbolicLink() || !lstatSync(directory).isDirectory())throw new Error('Choose a dedicated backup directory');
  const actualDirectory=realpathSync(directory),actualSource=realpathSync(source);
  if(dirname(actualSource)===actualDirectory)throw new Error('Backups require a separate directory and enforced storage quota');
  const lock=join(actualDirectory,'backup.lock'),pending=join(actualDirectory,'backup.pending.sqlite');
  const lockFd=openSync(lock,'wx',0o600);
  let db,ownsPending=false;
  try {
    const stamp=now(),name=`backup-${stamp}.sqlite`,destination=join(actualDirectory,name);
    let retained=[];
    for(const entry of readdirSync(actualDirectory)) {
      if(entry==='backup.lock')continue;
      if(!/^backup-\d{13}\.sqlite$/.test(entry))throw new Error('Unexpected backup entry; inspect it before retrying');
      const path=join(actualDirectory,entry),stat=regular(path),created=Number(entry.slice(7,20));
      if(created>stamp || stat.size>maxBytes)throw new Error('Backup inventory exceeds its limits');
      if(created<stamp-AGE)unlinkSync(path);else retained.push({path,created,size:stat.size});
    }
    retained.sort((a,b)=>b.created-a.created);
    // At most three completed files can remain if a prior process stopped
    // between promotion and rotation. Never accumulate another temporary copy.
    if(retained.length>3)throw new Error('Backup inventory exceeds its limits');
    for(const old of retained.splice(2))unlinkSync(old.path);
    if(retained.some(file=>file.path===destination))throw new Error('A snapshot already exists for this timestamp');
    db=new DatabaseSync(actualSource,{readOnly:true,timeout:5000});
    db.exec('PRAGMA busy_timeout=5000; BEGIN;');
    const pageSize=db.prepare('PRAGMA page_size').get().page_size;
    const pages=db.prepare('PRAGMA page_count').get().page_count;
    if(pages*pageSize>maxBytes)throw new Error('Source exceeds the backup size ceiling');
    if(!db.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name='reports'").get())throw new Error('Source is not a reporting database');
    const available=freeSpace || (()=>{const fs=statfsSync(actualDirectory);return fs.bavail*fs.bsize;});
    if(available()<maxBytes+reserveBytes)throw new Error('Backup reserve is unavailable');
    const pendingFd=openSync(pending,'wx',0o600);closeSync(pendingFd);ownsPending=true;
    const deadline=Date.now()+60000;
    await copy(db,pending,{rate:64,progress:({totalPages})=>{
      if(totalPages*pageSize>maxBytes || Date.now()>deadline || available()<reserveBytes)throw new Error('Backup reached its resource limit');
    }});
    db.exec('ROLLBACK');db.close();db=null;
    if(regular(pending).size>maxBytes)throw new Error('Backup exceeds the size ceiling');
    const check=new DatabaseSync(pending,{readOnly:true});
    try {if(check.prepare('PRAGMA quick_check').get().quick_check!=='ok')throw new Error('Backup integrity verification failed');}
    finally {check.close();}
    chmodSync(pending,0o600);sync(pending);renameSync(pending,destination);ownsPending=false;syncDirectory(actualDirectory);
    for(const old of retained.slice(1))unlinkSync(old.path);
    syncDirectory(actualDirectory);
    return {snapshots:Math.min(2,retained.length+1),bytes:regular(destination).size,created_at:stamp};
  } finally {
    if(db){try{db.exec('ROLLBACK');}catch{}db.close();}
    if(ownsPending){try{unlinkSync(pending);}catch{}}
    closeSync(lockFd);unlinkSync(lock);
  }
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
    const args=process.argv.slice(2);
    if(args.length!==4 || args[0]!=='--database' || args[2]!=='--directory')throw new Error('Invalid arguments');
    const result=await createBackup({source:args[1],directory:args[3]});
    console.log(JSON.stringify({backup:'complete',...result}));
  } catch {console.error('Backup failed. Existing verified snapshots were retained where within their age limit. Inspect storage and backup inventory.');process.exitCode=1;}
}
