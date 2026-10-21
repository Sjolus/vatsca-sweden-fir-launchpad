import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {fixture,event,batch,inventory,CLOCK,SUPPORT} from './intake-helpers.mjs';
import {validateEvent,VIEWS,ENUMS} from '../src/schema.mjs';

const DAY=86400000;
function observation(f,name,extra={}) {
  const {outcome,...base}=event();
  return {...base,event_name:name,occurred_at:new Date(f.now()).toISOString(),...extra};
}
const add=(f,...rows)=>f.store.admitBatch('usage',batch(...rows));
function task(f,outcome,extra={}) {
  const operation_id=randomUUID();
  const {reason,...startDetails}=extra;
  const start=observation(f,'operation_started',{operation_id,...startDetails});
  const finish=observation(f,'operation_finished',{operation_id,outcome,...extra});
  add(f,start,finish);return {start,finish};
}

test('navigation and task diagnostics are typed, bounded and event-specific',()=>{
  for(const view of VIEWS)assert.equal(validateEvent(event({event_name:'view_opened',view}),'usage',CLOCK).view,view);
  for(const operation of ['configure_sign_in','configure_sweden_profile','configure_plugin_profile'])
    assert.equal(validateEvent(event({operation,outcome:'partial',reason:'conflict'}),'usage',CLOCK).operation,operation);
  for(const reason of ENUMS.reason)assert.equal(validateEvent(event({reason}),'usage',CLOCK).reason,reason);
  const error=event({event_name:'operation_failed',service:'github',reason:'rate_limited',http_status:403,error_code:'rate_limited'});
  assert.equal(validateEvent(error,'errors',CLOCK).http_status,403);
  assert.equal(validateEvent(error,'context',CLOCK).service,'github');
  for(const status of [100,200,403,429,599])assert.equal(validateEvent({...error,http_status:status},'errors',CLOCK).http_status,status);
  for(const status of [99,600,403.5,'403',null,{},true])assert.throws(()=>validateEvent({...error,http_status:status},'errors',CLOCK));
  for(const extra of [{view:'private-profile'}, {view:'apps',path:'C:\\private'}, {view:null}])
    assert.throws(()=>validateEvent(event({event_name:'view_opened',...extra}),'usage',CLOCK));
  assert.throws(()=>validateEvent(event({event_name:'view_opened'}),'usage',CLOCK));
  for(const extra of [{view:'apps'},{http_status:403},{service:'github'},{reason:'message with secret'}])
    assert.throws(()=>validateEvent(event(extra),'usage',CLOCK));
  assert.throws(()=>validateEvent(event({event_name:'operation_started',reason:'busy'}),'usage',CLOCK));
  assert.throws(()=>validateEvent({...error,service:'https://github.com/private'},'errors',CLOCK));
  assert.throws(()=>validateEvent({...error,message:'private'},'errors',CLOCK));
});

test('task outcomes correlate usage attempts without treating unmatched or error-only records as failures',t=>{
  const f=fixture(t),attempt=randomUUID(),other=randomUUID();
  const start=observation(f,'operation_started',{operation_id:attempt,application:'vatis',operation:'configure_sign_in'});
  add(f,start,start,{...start,event_id:randomUUID()});
  f.advance(1);
  add(f,observation(f,'operation_finished',{operation_id:attempt,application:'vatis',operation:'configure_sign_in',outcome:'partial',reason:'missing_configuration',duration_bucket:'under_1s'}));
  f.advance(1);
  add(f,observation(f,'operation_finished',{operation_id:attempt,application:'vatis',operation:'configure_sign_in',outcome:'completed',reason:'already_configured',duration_bucket:'under_10s'}));
  task(f,'blocked',{application:'trackaudio',operation:'configure_sign_in',reason:'client_running'});
  task(f,'partial',{operation:'configure_sweden_profile',reason:'conflict'});
  task(f,'failed');task(f,'cancelled');task(f,'unknown');
  add(f,observation(f,'operation_started',{operation_id:other}),observation(f,'operation_finished',{operation_id:randomUUID(),outcome:'failed'}),
    observation(f,'operation_finished',{outcome:'completed'}));
  f.store.admitBatch('errors',batch(observation(f,'operation_failed',{operation_id:other,outcome:'failed',support_id:randomUUID()})));
  const result=f.store.usage().tasks;
  assert.deepEqual(result.summary,{started:7,installations:1,matched:6,incomplete:1,unmatched_finishes:1,unlinked_records:1});
  assert.equal(result.outcomes.reduce((sum,row)=>sum+row.attempts,0),7);
  assert.equal(result.outcomes.find(row=>row.outcome==='failed').attempts,1);
  assert.equal(result.by_task.find(row=>row.application==='vatis').completed,1,'latest terminal observation wins once per attempt');
  assert.deepEqual(result.durations,[{duration_bucket:'under_10s',attempts:1}]);
  assert.ok(result.reasons.some(row=>row.reason==='already_configured' && row.outcome==='completed'));
  assert.ok(!JSON.stringify(result).includes(SUPPORT));assert.ok(!JSON.stringify(result).includes(attempt));
});

test('attempts never join across installation, application, operation, time order or release filters',t=>{
  const f=fixture(t),operation_id=randomUUID(),second=randomUUID();
  add(f,observation(f,'operation_finished',{operation_id,outcome:'completed'}));
  f.advance(1);
  add(f,observation(f,'operation_started',{operation_id}),
    observation(f,'operation_finished',{operation_id,application:'trackaudio',outcome:'completed'}),
    observation(f,'operation_finished',{operation_id,support_id:second,outcome:'completed'}),
    observation(f,'operation_finished',{operation_id,operation:'install',outcome:'completed'}));
  const result=f.store.usage().tasks;
  assert.deepEqual(result.summary,{started:1,installations:1,matched:0,incomplete:1,unmatched_finishes:4,unlinked_records:0});
  assert.deepEqual(f.store.usage({application:'trackaudio'}).tasks.summary,{started:0,installations:0,matched:0,incomplete:0,unmatched_finishes:1,unlinked_records:0});
  const old=observation(f,'operation_started',{operation_id:randomUUID(),launchpad_version:'2.5.2'});
  add(f,old,observation(f,'operation_finished',{operation_id:old.operation_id,launchpad_version:'2.6.0',outcome:'completed'}));
  assert.equal(f.store.usage({launchpad_version:'2.6.0'}).tasks.summary.unmatched_finishes,1);
  assert.equal(f.store.usage({launchpad_version:'2.5.2'}).tasks.summary.incomplete,1);
});

test('window boundaries, future results and expired starts remain visible as incomplete coverage',t=>{
  const f=fixture(t,{initialClock:CLOCK-8*DAY,limits:{eventTtlDays:8}}),operation_id=randomUUID();
  add(f,observation(f,'operation_started',{operation_id}));f.advance(8*DAY);
  add(f,observation(f,'operation_finished',{operation_id,outcome:'completed'}));
  assert.equal(f.store.usage({days:30}).tasks.summary.started,0,'expired start is not resurrected before housekeeping');
  assert.equal(f.store.usage({days:30}).tasks.summary.unmatched_finishes,1);
  const current=observation(f,'operation_started',{operation_id:randomUUID()});
  add(f,current,observation(f,'operation_finished',{operation_id:current.operation_id,outcome:'completed',occurred_at:new Date(f.now()+1).toISOString()}));
  assert.equal(f.store.usage().tasks.summary.incomplete,1);
  f.advance(1);assert.equal(f.store.usage().tasks.summary.matched,1);
});

test('navigation counts page opens and inventory freshness uses each independent latest snapshot',t=>{
  const f=fixture(t,{initialClock:CLOCK-10*DAY}),older=randomUUID(),recent=randomUUID();
  f.store.admitBatch('inventory',batch(inventory({support_id:older,occurred_at:new Date(f.now()).toISOString()})));
  f.advance(8*DAY);
  f.store.admitBatch('inventory',batch(inventory({support_id:recent,occurred_at:new Date(f.now()).toISOString()}),inventory({occurred_at:new Date(f.now()).toISOString()})));
  f.advance(2*DAY);
  f.store.admitBatch('inventory',batch(inventory({occurred_at:new Date(f.now()).toISOString()})));
  add(f,observation(f,'view_opened',{view:'setup_applications'}),observation(f,'view_opened',{view:'setup_applications'}),observation(f,'view_opened',{view:'apps'}));
  const result=f.store.usage({days:30});
  assert.deepEqual(result.views,[{view:'setup_applications',opens:2,installations:1},{view:'apps',opens:1,installations:1}]);
  assert.equal(result.cohort.active_installations,1);assert.equal(result.inventory.snapshot_installations,3);
  assert.deepEqual(result.inventory.freshness,[{age:'last_24h',installations:1},{age:'older_than_seven_days',installations:1},{age:'one_to_seven_days',installations:1}]);
  assert.equal(Object.hasOwn(result.inventory,'connected_installations'),false);
  assert.equal(f.store.usage().inventory.snapshot_installations,2);
});

test('error groups expose affected installations and safe remote context without contaminating usage',t=>{
  const f=fixture(t),other=randomUUID();
  const error=observation(f,'operation_failed',{service:'github',http_status:403,error_code:'rate_limited',reason:'rate_limited'});
  f.store.admitBatch('errors',batch(error,{...error,event_id:randomUUID()},{...error,event_id:randomUUID(),support_id:other},
    {...error,event_id:randomUUID(),service:'aero_nav',http_status:429}));
  const groups=f.store.groupedErrors();
  assert.equal(groups.length,2);assert.equal(groups[0].count,3);assert.equal(groups[0].affected_installations,2);
  assert.equal(groups[0].service,'github');assert.equal(groups[0].http_status,403);assert.equal(groups[0].first_seen,CLOCK);
  assert.equal(groups[1].count,1);assert.equal(groups[1].affected_installations,1);
  assert.equal(f.store.usage().cohort.active_installations,0);assert.equal(f.store.usage().tasks.summary.started,0);
  assert.ok(!JSON.stringify(groups).includes(SUPPORT));assert.ok(!JSON.stringify(groups).includes(other));
});

test('task aggregation stays grouped at the automatic storage ceiling',t=>{
  const f=fixture(t,{initialClock:CLOCK-30*DAY});f.advance(30*DAY);
  const count=f.store.limits.maxEvents*f.store.limits.usageStopPercent/100;
  const identities=Array.from({length:1000},()=>randomUUID());
  f.store.transaction(()=>{
    const insert=f.store.db.prepare('INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)');
    for(let i=0;i<count/2;i++) {
      const operation_id=randomUUID(),support_id=identities[i%identities.length];
      for(const event_name of ['operation_started','operation_finished']) {
        const e=observation(f,event_name,{operation_id,support_id,application:i%2?'vatis':'trackaudio',operation:'configure_sign_in',...(event_name==='operation_finished'?{outcome:i%3?'completed':'blocked',reason:i%3?'already_configured':'client_running',duration_bucket:'under_1s'}:{})});
        const payload=JSON.stringify(e);
        insert.run(e.event_id,'usage',support_id,e.session_id,event_name,e.application,e.operation,e.outcome??null,null,null,e.launchpad_version,CLOCK,CLOCK,CLOCK+DAY,payload,Buffer.byteLength(payload),'synthetic');
      }
    }
  });
  const started=performance.now(),result=f.store.usage({days:30});
  t.diagnostic(`${count} synthetic operation records: ${Math.round(performance.now()-started)} ms`);
  assert.equal(result.tasks.summary.started,count/2);assert.equal(result.tasks.summary.matched,count/2);
  assert.equal(result.tasks.summary.installations,1000);assert.equal(result.tasks.by_task.length,2);
  assert.equal(result.tasks.outcomes.reduce((sum,row)=>sum+row.attempts,0),count/2);
  assert.ok(Buffer.byteLength(JSON.stringify(result.tasks))<4096,'query returns grouped counts, not individual tasks');
});
