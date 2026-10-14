import { createServer as httpServer } from 'node:http';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { createStore } from './store.mjs';
import { readConfig,normalizeLimits } from './config.mjs';
import { IntakeError } from './schema.mjs';
import { createRequestGate,createIntakeHandler,sendJson,readJson } from './intake.mjs';
import { createAdminHandler } from './admin.mjs';
import { createAlerts } from './alerts.mjs';

export function createServer({store,config,adminHandler,now=Date.now}={}) {
  const limits=normalizeLimits(config.limits),gate=createRequestGate({limits,now,trustedProxyCidrs:config.trustedProxyCidrs});
  const accepted={usage:0,errors:0,inventory:0,reports:0},rejected={invalid:0,rate:0,storage:0,other:0};
  const intake=createIntakeHandler({store,onAccepted:(kind,count)=>{accepted[kind]+=count;}});
  const admin=adminHandler===false?null:adminHandler || createAdminHandler({store,config,now});
  const server=httpServer({maxHeaderSize:8192},async(req,res)=>{
    res.setHeader('X-Content-Type-Options','nosniff');res.setHeader('Referrer-Policy','no-referrer');res.setHeader('Cache-Control','no-store');
    let release;
    const deadline=setTimeout(()=>{sendJson(res,408,{error:'request_timeout'},{Connection:'close'});res.once('finish',()=>req.destroy());req.socket.setTimeout(1000,()=>req.destroy());},limits.requestTimeoutMs);
    deadline.unref();
    try {
      const path=new URL(req.url,config.origin).pathname;
      // These paths are for the cluster network, excluded from the public ingress.
      if(req.method==='GET' && path==='/healthz') {sendJson(res,200,{ok:true});return;}
      if(req.method==='GET' && path==='/readyz') {const health=store.health();sendJson(res,health.ok?200:503,{ok:health.ok});return;}
      if(req.method==='GET' && path==='/metrics') {
        const metrics=[];
        for(const [stream,count] of Object.entries(accepted)) metrics.push(`launchpad_intake_accepted_total{stream="${stream}"} ${count}`);
        for(const [kind,count] of Object.entries(rejected)) metrics.push(`launchpad_intake_rejected_total{kind="${kind}"} ${count}`);
        const health=store.health();metrics.push(`launchpad_store_physical_bytes ${health.physical_bytes}`,`launchpad_store_ready ${health.ok?1:0}`);
        res.writeHead(200,{'Content-Type':'text/plain; version=0.0.4'});res.end(metrics.join('\n')+'\n');return;
      }
      release=gate.enter(req);
      if(await intake(req,res,path)) return;
      if(admin && await admin(req,res,{store,readJson,sendJson})) return;
      sendJson(res,404,{error:'not_found'});
    } catch(error) {
      const status=error instanceof IntakeError?error.status:503;
      rejected[status===429?'rate':status===503?'storage':status===400||status===409||status===413||status===415?'invalid':'other']++;
      sendJson(res,status,{error:error instanceof IntakeError?error.code:'service_unavailable'},
        {...(error.retryAfter?{'Retry-After':String(error.retryAfter)}:{}),Connection:'close'});
      // No request body, query string, exception message or source address is logged.
    } finally {clearTimeout(deadline);release?.();}
  });
  server.requestTimeout=limits.requestTimeoutMs;server.headersTimeout=Math.min(5000,limits.requestTimeoutMs);
  server.keepAliveTimeout=2000;server.maxConnections=limits.maxConcurrent*4;
  return {server,gate,metrics:{accepted,rejected},pruneAdmin:()=>admin?.prune?.()};
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
  const config=readConfig(),store=createStore({path:config.dataPath,limits:config.limits});
  if(!config.privacyOperator.trim() || !config.privacyContact.trim()) {store.close();throw new Error('Operator details are required');}
  const {server,metrics,pruneAdmin}=createServer({store,config});
  const alerts=createAlerts({endpoint:config.ntfyEndpoint,token:config.ntfyToken,origin:config.origin});
  function maintain() {
    try {store.housekeeping();} catch {/* Quotas still prevent growth; health records the retention failure. */}
    try {pruneAdmin();} catch {/* Auth admission remains bounded independently. */}
    let health;try {health=store.health();} catch {health={ok:false};}
    alerts.observe({health,accepted:metrics.accepted});
  }
  maintain();
  const cleanup=setInterval(maintain,60000);
  cleanup.unref();
  server.listen(config.port,config.host);
  for(const signal of ['SIGTERM','SIGINT']) process.once(signal,()=>{
    clearInterval(cleanup);server.close(async()=>{await alerts.close();store.close();process.exit(0);});
    setTimeout(()=>process.exit(1),10000).unref();
  });
  server.on('error',()=>{clearInterval(cleanup);store.close();void alerts.close();console.error('Reporting listener could not start. Check the configured port and service permissions.');process.exitCode=1;});
  } catch {console.error('Reporting service could not start. Check operator details, configuration and storage access.');process.exitCode=1;}
}
