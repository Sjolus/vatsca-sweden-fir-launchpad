import {test} from 'node:test';
import assert from 'node:assert/strict';
import {once} from 'node:events';
import {request} from 'node:http';
import {readFileSync} from 'node:fs';
import {createServer} from '../src/server.mjs';
import {createRequestGate} from '../src/intake.mjs';
import {fixture,event,report,inventory,batch,CLOCK} from './intake-helpers.mjs';

async function service(t,limits={}) {
  const f=fixture(t,{limits});
  const {server,gate,metrics}=createServer({store:f.store,adminHandler:false,now:f.now,config:{origin:'https://launchpad.example.invalid',limits}});
  server.listen(0,'127.0.0.1');await once(server,'listening');
  t.after(async()=>{server.closeAllConnections();server.close();await once(server,'close');});
  const base=`http://127.0.0.1:${server.address().port}`;
  const post=(path,body,headers={})=>fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:typeof body==='string'?body:JSON.stringify(body)});
  return {...f,base,post,server,gate,metrics};
}
test('HTTP intake validates whole request, acknowledges durable batches and idempotent reports, never exposes public reads',async t=>{
  const f=await service(t),e=event();
  let response=await f.post('/api/v1/usage/batches',batch(e));assert.equal(response.status,202);assert.deepEqual(await response.json(),{accepted:1,duplicates:0});
  response=await f.post('/api/v1/usage/batches',batch(e));assert.deepEqual(await response.json(),{accepted:0,duplicates:1});
  const r=report({context:[e]});response=await f.post('/api/v1/reports',r);assert.equal(response.status,201);const first=await response.json();
  response=await f.post('/api/v1/reports',r);assert.deepEqual(await response.json(),first);
  assert.equal(f.metrics.accepted.reports,1,'retries must not generate new-report notifications');
  response=await fetch(f.base+'/api/v1/reports/'+first.reference);assert.equal(response.status,404);
  response=await fetch(f.base+'/api/v1/usage/batches');assert.equal(response.status,405);
  response=await f.post('/api/v1/reports',{...r,category:'other'});assert.equal(response.status,409);
  assert.equal(f.store.getReport(first.reference).payload.report_id,r.report_id);
});
test('compressed, over-limit, invalid UTF-8 and identifying payloads are rejected without persistence or body logging',async t=>{
  const f=await service(t),marker='SYNTHETIC_PRIVATE_INFORMATION_DO_NOT_STORE';
  const logs=[],oldError=console.error,oldLog=console.log;
  console.error=(...x)=>logs.push(x);console.log=(...x)=>logs.push(x);
  try {
    assert.equal((await f.post('/api/v1/usage/batches',batch(event()),{'Content-Encoding':'gzip'})).status,415);
    assert.equal((await f.post('/api/v1/usage/batches',' '.repeat(65537))).status,413);
    assert.equal((await f.post('/api/v1/reports',' '.repeat(16385))).status,413);
    assert.equal((await f.post('/api/v1/errors/batches',batch(event({event_name:'unhandled_error',message:marker})))).status,400);
    assert.equal((await f.post('/api/v1/usage/batches',batch(event(),event({email:marker})))).status,400);
    const malformed=await fetch(f.base+'/api/v1/reports',{method:'POST',headers:{'Content-Type':'application/json'},body:Buffer.from([0xff,0xfe])});assert.equal(malformed.status,400);
    assert.equal(f.store.overview().storage.usage.count,0);assert.equal(f.store.overview().storage.errors.count,0);
    assert.equal(readFileSync(f.path).includes(Buffer.from(marker)),false);assert.equal(JSON.stringify(logs).includes(marker),false);
  } finally {console.error=oldError;console.log=oldLog;}
});
test('actual streamed bytes are bounded without Content-Length',async t=>{
  const f=await service(t);
  const status=await new Promise((resolve,reject)=>{
    const req=request(f.base+'/api/v1/usage/batches',{method:'POST',headers:{'Content-Type':'application/json','Transfer-Encoding':'chunked'}},res=>{res.resume();resolve(res.statusCode);});
    req.on('error',reject);for(let i=0;i<70;i++)req.write(' '.repeat(1024));req.end();
  });
  assert.equal(status,413);assert.equal(f.store.overview().storage.usage.count,0);
});
test('HTTP limits return bounded Retry-After and no acknowledgement when storage is unavailable',async t=>{
  const f=await service(t,{sourceRequestsPerMinute:2});
  assert.equal((await f.post('/api/v1/usage/batches',batch(event()))).status,202);
  assert.equal((await f.post('/api/v1/usage/batches',batch(event()))).status,202);
  const limited=await f.post('/api/v1/reports',report());assert.equal(limited.status,429);assert.equal(limited.headers.get('retry-after'),'60');
  f.advance(60000);f.store.db.exec("DELETE FROM storage_totals WHERE kind='errors'");
  const unavailable=await f.post('/api/v1/usage/batches',batch(event()));assert.equal(unavailable.status,503);assert.equal((await unavailable.json()).error,'storage_unavailable');
});
test('source cache/concurrency/byte budgets are bounded and spoofed proxy headers do not create identities',()=>{
  let clock=CLOCK;
  const gate=createRequestGate({now:()=>clock,limits:{maxSources:2,maxConcurrent:1,sourceRequestsPerMinute:2,globalBytesPerMinute:100}});
  const req=(address,forwarded)=>({socket:{remoteAddress:address},headers:{'x-forwarded-for':forwarded}});
  let leave=gate.enter(req('192.0.2.1','198.51.100.1'));
  assert.throws(()=>gate.enter(req('192.0.2.2')),{status:429});leave();leave();
  gate.enter(req('192.0.2.1','198.51.100.2'))();assert.equal(gate.stats().sources,1);
  assert.throws(()=>gate.enter(req('192.0.2.1','198.51.100.3')),{status:429});
  gate.enter(req('192.0.2.2'))();assert.throws(()=>gate.enter(req('192.0.2.3')),{status:429});assert.equal(gate.stats().sources,2);
  gate.chargeBytes(100);assert.throws(()=>gate.chargeBytes(1),{status:429});
  clock+=60000;gate.enter(req('192.0.2.3'))();assert.ok(gate.stats().sources<=2);assert.equal(gate.stats().active,0);
  const trusted=createRequestGate({trustedProxyCidrs:['192.0.2.0/24']});
  trusted.enter(req('192.0.2.1','spoofed, 198.51.100.1'))();trusted.enter(req('192.0.2.1','spoofed, 198.51.100.2'))();assert.equal(trusted.stats().sources,2);
});
test('slow incomplete bodies time out without writes or holding concurrency indefinitely',async t=>{
  const f=await service(t,{requestTimeoutMs:80});
  const status=await new Promise((resolve,reject)=>{
    const req=request(f.base+'/api/v1/reports',{method:'POST',headers:{'Content-Type':'application/json','Content-Length':'100'}},res=>{res.resume();resolve(res.statusCode);});
    req.on('error',error=>{if(error.code!=='ECONNRESET')reject(error);});req.write('{');
  });
  assert.equal(status,408);await new Promise(resolve=>setTimeout(resolve,30));
  assert.equal(f.store.overview().storage.reports.count,0);assert.equal(f.gate.stats().active,0);
});

test('inventory endpoint validates, acknowledges and deduplicates independently of usage metrics',async t=>{
  const f=await service(t),snapshot=inventory();
  let response=await f.post('/api/v1/inventory/batches',batch(snapshot));
  assert.equal(response.status,202);assert.deepEqual(await response.json(),{accepted:1,duplicates:0});
  response=await f.post('/api/v1/inventory/batches',batch(snapshot));assert.deepEqual(await response.json(),{accepted:0,duplicates:1});
  assert.equal(f.metrics.accepted.inventory,1);assert.equal(f.metrics.accepted.usage,0);
  assert.equal((await f.post('/api/v1/usage/batches',batch(snapshot))).status,400);
  assert.equal((await f.post('/api/v1/inventory/batches',batch(event()))).status,400);
  const changed=structuredClone(snapshot);changed.items[0].status='unknown';
  assert.equal((await f.post('/api/v1/inventory/batches',batch(changed))).status,409);
  assert.equal((await fetch(f.base+'/api/v1/inventory/batches')).status,405);
  assert.deepEqual(f.store.overview().events,[]);
});
