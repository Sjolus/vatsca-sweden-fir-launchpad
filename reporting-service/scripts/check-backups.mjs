#!/usr/bin/env node
import {lstatSync,realpathSync,opendirSync,openSync,closeSync,unlinkSync,fsyncSync} from 'node:fs';
import {isAbsolute,resolve,join,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {DEFAULT_LIMITS} from '../src/config.mjs';

const HOUR=3600000,EXPIRY=48*HOUR,FRESHNESS=30*HOUR,MAX_SNAPSHOTS=3;
const managed=/^backup-([0-9]{13})\.sqlite$/;
function regular(path) {
  const stat=lstatSync(path);
  if(!stat.isFile() || stat.isSymbolicLink() || stat.nlink!==1)throw new Error('Invalid backup inventory');
  return stat;
}
function safeDirectory(path) {
  if(typeof path!=='string' || !isAbsolute(path))throw new Error('A dedicated absolute backup directory is required');
  const absolute=resolve(path);
  // Refuse linked ancestors as well as a linked final directory. The maintenance
  // command must never follow a redirected operator-supplied cleanup path.
  for(let current=absolute;;current=dirname(current)) {
    const stat=lstatSync(current);
    if(!stat.isDirectory() || stat.isSymbolicLink())throw new Error('Invalid backup directory');
    if(dirname(current)===current)break;
  }
  return realpathSync(absolute);
}
function syncDirectory(path) {
  if(process.platform==='win32')return;
  const fd=openSync(path,'r');try{fsyncSync(fd);}finally{closeSync(fd);}
}

// Independent of snapshot creation: run hourly so a failed daily backup cannot
// silently retain old snapshots. Sharing the writer lock prevents racing a copy
// or promotion. An existing lock/pending file is preserved for operator review.
export function checkBackups({directory,now=Date.now,maxBytes=DEFAULT_LIMITS.maxDbBytes}={}) {
  if(!Number.isSafeInteger(maxBytes) || maxBytes<131072 || maxBytes>DEFAULT_LIMITS.maxDbBytes)throw new Error('Invalid backup size limit');
  const stamp=now();if(!Number.isSafeInteger(stamp) || stamp<EXPIRY)throw new Error('Invalid backup check time');
  const actual=safeDirectory(directory),lock=join(actual,'backup.lock');
  const lockFd=openSync(lock,'wx',0o600);
  try {
    const files=[],entries=opendirSync(actual);let bytes=0;
    try {
      for(let entry;(entry=entries.readSync())!==null;) {
        if(entry.name==='backup.lock')continue;
        const match=managed.exec(entry.name);
        if(!match || files.length>=MAX_SNAPSHOTS)throw new Error('Invalid backup inventory');
        const path=join(actual,entry.name),stat=regular(path),created=Number(match[1]);
        if(created>stamp || stat.size===0 || stat.size>maxBytes)throw new Error('Invalid backup inventory');
        bytes+=stat.size;if(bytes>MAX_SNAPSHOTS*maxBytes)throw new Error('Invalid backup inventory');
        files.push({path,created,size:stat.size,device:stat.dev,inode:stat.ino});
      }
    } finally {entries.closeSync();}
    // Validate the complete bounded inventory before removing anything. Never
    // delete a pending copy, lock belonging to another process, or unknown file.
    let expired=0,expiredBytes=0;
    const retained=[];
    for(const file of files) {
      if(file.created>=stamp-EXPIRY){retained.push(file);continue;}
      const current=regular(file.path);
      if(current.dev!==file.device || current.ino!==file.inode || current.size!==file.size)throw new Error('Backup inventory changed');
      unlinkSync(file.path);expired++;expiredBytes+=file.size;
    }
    if(expired)syncDirectory(actual);
    const newest=retained.length?Math.max(...retained.map(file=>file.created)):null;
    const status=newest===null?'missing':stamp-newest>FRESHNESS?'stale':'fresh';
    return {ok:status==='fresh',status,snapshots:retained.length,bytes:bytes-expiredBytes,
      expired_snapshots:expired,expired_bytes:expiredBytes,newest_created_at:newest,newest_age_ms:newest===null?null:stamp-newest};
  } finally {closeSync(lockFd);unlinkSync(lock);}
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
    const args=process.argv.slice(2);
    if(args.length!==2 || args[0]!=='--directory')throw new Error('Invalid arguments');
    const result=checkBackups({directory:args[1]});
    console.log(JSON.stringify({backup_check:'complete',...result}));
    if(!result.ok)process.exitCode=1;
  } catch {
    console.error('Backup check failed. Inspect the dedicated backup volume, active backup jobs and managed inventory.');
    process.exitCode=1;
  }
}
