import { createHmac, randomBytes } from 'node:crypto';
import { BlockList, isIP } from 'node:net';
import { IntakeError } from './schema.mjs';
import { normalizeLimits } from './config.mjs';

export function sendJson(res,status,value,extra = {}) {
  if (res.destroyed || res.writableEnded) return;
  res.writeHead(status,{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store',
    'X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer',...extra});
  res.end(JSON.stringify(value));
}
export async function readJson(req,limit) {
  if (req.headers['content-encoding'] && req.headers['content-encoding'] !== 'identity') throw new IntakeError(415,'unsupported_encoding');
  if (!/^application\/json(?:\s*;\s*charset=utf-8)?$/i.test(req.headers['content-type'] || '')) throw new IntakeError(415,'expected_json');
  const declared = req.headers['content-length'];
  if (declared && (!/^\d+$/.test(declared) || Number(declared)>limit)) throw new IntakeError(413,'request_too_large');
  const chunks=[]; let bytes=0;
  for await (const chunk of req) {
    req.reportingChargeBytes?.(chunk.length);
    bytes+=chunk.length;
    if (bytes>limit) throw new IntakeError(413,'request_too_large');
    chunks.push(chunk);
  }
  try { return JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(Buffer.concat(chunks))); }
  catch { throw new IntakeError(400,'invalid_request'); }
}

export function createRequestGate({limits:requested,now=Date.now,trustedProxyCidrs=[]}={}) {
  const limits=normalizeLimits(requested), sources=new Map(), secret=randomBytes(32), trusted=new BlockList();
  for (const value of trustedProxyCidrs) {
    const [ip,rawBits]=value.split('/'), family=isIP(ip);
    const bits=rawBits===undefined?(family===4?32:128):Number(rawBits);
    if (!family || !Number.isInteger(bits) || bits<0 || bits>(family===4?32:128)) throw new Error('Invalid trusted proxy range');
    trusted.addSubnet(ip,bits,family===4?'ipv4':'ipv6');
  }
  let minute=-1,requests=0,bytes=0,active=0;
  function reset() {
    const current=Math.floor(now()/60000);
    if (current!==minute) {minute=current;requests=0;bytes=0;}
    return current;
  }
  function chargeBytes(count) {reset();bytes+=count;if(bytes>limits.globalBytesPerMinute) throw new IntakeError(429,'rate_limited',60);}
  function address(req) {
    const peer=(req.socket.remoteAddress || '').replace(/^::ffff:/,'');
    const family=isIP(peer);
    if (family && trusted.check(peer,family===4?'ipv4':'ipv6')) {
      // The configured final proxy must append its observed client address.
      // Never trust forwarding headers on a direct connection.
      const last=String(req.headers['x-forwarded-for'] || '').split(',').at(-1).trim();
      if (isIP(last)) return last;
    }
    return peer;
  }
  function enter(req) {
    const current=reset();requests++;
    if (requests>limits.globalRequestsPerMinute || active>=limits.maxConcurrent) throw new IntakeError(429,'rate_limited',60);
    const source=createHmac('sha256',secret).update(address(req)).digest('hex');
    let entry=sources.get(source);
    if (!entry || entry.minute!==current) {
      if (sources.size>=limits.maxSources) for(const [key,value] of sources) if(value.minute!==current) sources.delete(key);
      if (!entry && sources.size>=limits.maxSources) throw new IntakeError(429,'rate_limited',60);
      entry={minute:current,count:0};sources.set(source,entry);
    }
    entry.count++;
    if(entry.count>limits.sourceRequestsPerMinute) throw new IntakeError(429,'rate_limited',60);
    active++;let released=false;
    req.reportingChargeBytes=chargeBytes;
    return ()=>{if(!released){active--;released=true;}};
  }
  return {enter,chargeBytes,stats:()=>({sources:sources.size,active,requests,bytes})};
}

export function createIntakeHandler({store,onAccepted=()=>{}}) {
  return async (req,res,path) => {
    const stream=path==='/api/v1/usage/batches'?'usage':path==='/api/v1/errors/batches'?'errors':path==='/api/v1/inventory/batches'?'inventory':null;
    if (!stream && path!=='/api/v1/reports') return false;
    if(req.method!=='POST') {sendJson(res,405,{error:'method_not_allowed'},{Allow:'POST'});return true;}
    const body=await readJson(req,stream?65536:16384);
    const result=stream?store.admitBatch(stream,body):store.admitReport(body);
    onAccepted(stream||'reports',stream?result.accepted:result.duplicate?0:1);
    sendJson(res,stream?202:201,result);return true;
  };
}
