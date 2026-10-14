import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {performance} from 'node:perf_hooks';
import {fixture,event,batch,CLOCK} from './intake-helpers.mjs';
import {INVENTORY} from '../src/schema.mjs';

const DAY=86400000;
const A='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',B='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',C='cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const S1='11111111-1111-4111-8111-111111111111',S2='22222222-2222-4222-8222-222222222222';
function record(f,name,overrides={}) {return event({event_name:name,support_id:A,session_id:S1,occurred_at:new Date(f.now()).toISOString(),launchpad_version:'2.3.0',application:'launchpad',...overrides});}
function inventory(f,id=A,overrides={},version='2.3.0') {
  const {application,operation,outcome,...base}=record(f,'inventory_snapshot',{support_id:id,launchpad_version:version});
  return {...base,items:Object.entries(INVENTORY).map(([component,source])=>({component,source,status:'absent',...overrides[component]}))};
}
function at(f,date){f.advance(Date.parse(date)-f.now());}
const add=(f,...rows)=>f.store.admitBatch('usage',batch(...rows));
const dll=(index=1,overrides={})=>({sha256:index.toString(16).padStart(64,'0'),product:'unknown',role:'current',copies:1,version_source:'unknown',...overrides});
const withDlls=(row,entries=[],overrides={})=>({...row,dll_inventory:{status:'complete',entries,unreadable_files:0,...overrides}});

test('DLL analytics use latest independent snapshots and count installations rather than file copies',t=>{
  const f=fixture(t),D=randomUUID(),E=randomUUID();
  const known=dll(1,{product:'topsky',version:'3.0.0-beta.19',file_version:'0.0.0.0',version_source:'embedded',copies:3});
  const first=withDlls(inventory(f,A),[known,{...known,role:'staged',copies:1},dll(2)]);
  assert.deepEqual(f.store.admitBatch('inventory',batch(first,first)),{accepted:1,duplicates:1});
  f.store.admitBatch('inventory',batch(withDlls(inventory(f,B),[{...known,copies:2},dll(3,{product:'vatefs',file_version:'0.1.2.0'})],{status:'partial',unreadable_files:2}),
    inventory(f,C),withDlls(inventory(f,D),[],{status:'unavailable',unreadable_files:1}),withDlls(inventory(f,E),[])));
  const result=f.store.usage(),details=result.inventory.dll_inventory;
  assert.equal(result.cohort.active_installations,0);assert.equal(result.inventory.snapshot_installations,5);
  assert.deepEqual([details.complete_installations,details.partial_installations,details.unavailable_installations,details.not_shared_installations],[2,1,1,1]);
  assert.equal(details.shared_installations,4);assert.equal(details.unreadable_files,3);
  assert.equal(details.current_installations,2);assert.equal(details.staged_installations,1);
  const row=details.rows.find(row=>row.product==='topsky' && row.role==='current');
  assert.equal(row.installations,2);assert.equal(row.copies,5);assert.equal(row.version,'3.0.0-beta.19');assert.equal(row.file_version,'0.0.0.0');
  assert.equal(details.rows.find(row=>row.role==='staged').installations,1);
  assert.equal(details.total_groups,4);assert.equal(details.omitted_groups,0);
  assert.ok(!JSON.stringify(result).includes(A));assert.ok(!JSON.stringify(result).includes(B));
  const selected=f.store.usage({application:'vatefs'}).inventory;
  assert.equal(selected.snapshot_installations,5);assert.equal(selected.dll_inventory.shared_installations,4);
  assert.equal(selected.dll_inventory.product_filter,'vatefs');assert.equal(selected.dll_inventory.rows.length,1);
  assert.equal(Object.hasOwn(f.store.usage({application:'trackaudio'}).inventory,'dll_inventory'),false);
  f.advance(1);f.store.admitBatch('inventory',batch(inventory(f,A)));
  const newer=f.store.usage().inventory.dll_inventory;
  assert.equal(newer.not_shared_installations,2);assert.equal(newer.staged_installations,0,'newer legacy snapshot does not carry forward older DLLs');
  assert.equal(newer.rows.find(row=>row.product==='topsky').installations,1);
  f.store.deleteSupport(B);assert.equal(f.store.usage().inventory.dll_inventory.rows.length,0);
});

test('DLL registration versions remain distinct from Windows file metadata in stored analytics',t=>{
  const f=fixture(t),entry=dll(1,{product:'vatcan_bookings',version:'1.1.6-2022wb',file_version:'1.0.0.1',version_source:'registration'});
  const snapshot=withDlls(inventory(f,A),[entry]);
  assert.deepEqual(f.store.admitBatch('inventory',batch(snapshot)),{accepted:1,duplicates:0});
  f.store.close();const reopened=f.open();
  const row=reopened.usage().inventory.dll_inventory.rows[0];
  assert.equal(row.version,entry.version);assert.equal(row.file_version,entry.file_version);assert.equal(row.version_source,'registration');
  assert.deepEqual(reopened.admitBatch('inventory',batch(snapshot)),{accepted:0,duplicates:1});
});

test('DLL reporting retains immutable legacy retries and bounds grouped output without inventing absence',t=>{
  const f=fixture(t),legacy=inventory(f,A);
  assert.deepEqual(f.store.admitBatch('inventory',batch(legacy)),{accepted:1,duplicates:0});
  assert.deepEqual(f.store.admitBatch('inventory',batch({...legacy,items:[...legacy.items].reverse()})),{accepted:0,duplicates:1});
  assert.throws(()=>f.store.admitBatch('inventory',batch(withDlls(legacy))),{code:'identifier_conflict'});
  for(let installation=0;installation<5;installation++) {
    const entries=Array.from({length:24},(_,i)=>dll(installation*24+i+1));
    f.store.admitBatch('inventory',batch(withDlls(inventory(f,randomUUID()),entries,{status:'partial'})));
  }
  const details=f.store.usage().inventory.dll_inventory;
  assert.equal(details.partial_installations,5);assert.equal(details.not_shared_installations,1);assert.equal(details.complete_installations,0);
  assert.equal(details.total_groups,120);assert.equal(details.rows.length,100);assert.equal(details.omitted_groups,20);
  assert.equal(details.current_installations,5);assert.equal(details.unreadable_files,0);
  assert.equal(details.rows.every(row=>row.version===null && row.file_version===null),true,'unknown versions remain unknown');
  assert.ok(Buffer.byteLength(JSON.stringify(details))<40000,'bounded rows prevent an unbounded dashboard response');
  f.store.close();const reopened=f.open();
  assert.deepEqual(reopened.admitBatch('inventory',batch(legacy)),{accepted:0,duplicates:1});
  assert.equal(reopened.usage().inventory.dll_inventory.not_shared_installations,1);
});

test('DLL scans obey release, future and retention boundaries even without housekeeping',t=>{
  const f=fixture(t,{initialClock:CLOCK-10*DAY,limits:{eventTtlDays:8}});
  f.store.admitBatch('inventory',batch(withDlls(inventory(f,A),[dll()])));
  f.advance(10*DAY);
  f.store.admitBatch('inventory',batch(withDlls(inventory(f,B,{},'2.2.0'),[dll(2)]),
    withDlls({...inventory(f,C),occurred_at:new Date(f.now()+1).toISOString()},[dll(3)])));
  assert.equal(f.store.usage({days:30}).inventory.dll_inventory.rows.length,1);
  assert.equal(f.store.usage({days:30,launchpad_version:'2.3.0'}).inventory.dll_inventory.shared_installations,0);
  f.advance(1);assert.equal(f.store.usage({days:30}).inventory.dll_inventory.rows.length,2);
});

test('usage includes today in distinct active cohorts and latest independent inventory snapshots',t=>{
  const f=fixture(t,{initialClock:Date.parse('2026-10-01T00:00:00Z')}),attempt=randomUUID();
  at(f,'2026-10-10T12:00:00Z');
  add(f,record(f,'session_started',{launchpad_version:'2.2.0',session_id:randomUUID()}),record(f,'application_opened',{application:'trackaudio'}));
  at(f,'2026-10-15T12:00:00Z');
  const first=record(f,'session_started');add(f,first,first,
    record(f,'application_opened',{application:'trackaudio'}),record(f,'application_opened',{application:'trackaudio'}),
    record(f,'feature_used',{feature:'check_updates'}),record(f,'feature_used',{feature:'check_updates'}),
    record(f,'gng_package_obtained',{application:'gng',source:'cache'}),
    record(f,'setup_step',{operation_id:attempt,source:'existing',stage:'welcome'}),
    record(f,'preferences_snapshot',{compact_layout:true,text_scale:'standard',always_on_top:false,theme:'dark'}));
  f.store.admitBatch('inventory',batch(inventory(f,A,{trackaudio:{status:'known',version:'1.3.3'},vatefs:{status:'unknown'}})));
  at(f,'2026-10-17T12:00:00Z');
  add(f,record(f,'session_started',{support_id:B,launchpad_version:'2.2.0'}),record(f,'application_opened',{support_id:B,launchpad_version:'2.2.0',application:'vacs'}),
    record(f,'feature_used',{support_id:B,launchpad_version:'2.2.0',feature:'check_updates'}),record(f,'gng_package_obtained',{support_id:B,launchpad_version:'2.2.0',application:'gng',source:'download'}));
  f.store.admitBatch('errors',batch(record(f,'operation_failed',{support_id:randomUUID()})));
  at(f,'2026-10-18T12:00:00Z');
  add(f,record(f,'session_started'),record(f,'session_started',{session_id:S2}),
    record(f,'setup_step',{operation_id:attempt,source:'fresh',stage:'finish'}),
    record(f,'setup_step',{support_id:B,launchpad_version:'2.2.0',source:'preferences'}),
    record(f,'preferences_snapshot',{compact_layout:false,text_scale:'largest',always_on_top:true,theme:'light'}));
  f.store.admitBatch('inventory',batch(inventory(f,A,{trackaudio:{status:'known',version:'1.4.0'}}),inventory(f,C,{trackaudio:{status:'unknown'},vatefs_plugin:{status:'known',version:'1.0.0'}},'2.2.0')));
  at(f,'2026-10-20T12:00:00Z');
  add(f,record(f,'session_started',{support_id:C}),record(f,'application_opened',{support_id:C,application:'trackaudio'}),record(f,'session_started',{support_id:randomUUID(),occurred_at:new Date(f.now()+120000).toISOString()}));
  f.store.admitBatch('inventory',batch(inventory(f,C,{trackaudio:{status:'known',version:'1.5.0'}},'2.2.0')));
  const result=f.store.usage({days:7});
  assert.equal(result.window.start,'2026-10-14T00:00:00.000Z');assert.equal(result.window.as_of,'2026-10-20T12:00:00.000Z');assert.equal(result.window.end_exclusive,'2026-10-20T12:00:00.001Z');assert.equal(result.window.coverage_complete,true);
  assert.equal(result.window.complete_days,false);assert.equal(result.window.partial_day,'2026-10-20');
  assert.deepEqual(result.cohort,{active_installations:3,sessions:4,app_launches:4,app_launch_installations:3,returning_installations:1,returning_available:true,returning_reason:'available'});
  assert.equal(result.daily.reduce((n,d)=>n+d.active_installations,0),5,'daily IDs must not be summed for the window headline');
  assert.deepEqual(result.releases,[{version:'2.3.0',installations:2},{version:'2.2.0',installations:1}]);
  assert.deepEqual(result.features,[{feature:'check_updates',uses:3,installations:2}]);
  assert.deepEqual(result.setup_routes,[{route:'fresh',attempts:1,installations:1}],'route changes replace earlier route; missing attempt IDs do not invent attempts');
  assert.deepEqual(result.display_preferences.compact_layout,[{value:false,installations:1}]);assert.equal(result.display_preferences.missing_installations,2);
  assert.equal(result.inventory.snapshot_installations,2,'inventory uses its independent snapshot cohort');
  const track=result.inventory.components.find(row=>row.component==='trackaudio');
  assert.equal(track.known,2);assert.equal(track.unknown,0);assert.deepEqual(track.versions,[{version:'1.4.0',installations:1},{version:'1.5.0',installations:1}]);
  assert.equal(result.inventory.components.find(row=>row.component==='vatefs').absent,2,'latest full snapshot replaces the previous unknown state');
  assert.ok(!JSON.stringify(result).includes(A));assert.ok(!JSON.stringify(result).includes(C),'analytics never return support IDs');
  const filtered=f.store.usage({days:7,application:'trackaudio'});
  assert.equal(filtered.cohort.active_installations,2);assert.equal(filtered.cohort.sessions,3);assert.equal(filtered.cohort.app_launches,3);assert.equal(filtered.cohort.returning_installations,1);
  assert.equal(filtered.inventory.snapshot_installations,2);assert.equal(filtered.inventory.components.length,1);
  const oldRelease=f.store.usage({days:7,launchpad_version:'2.2.0'});
  assert.equal(oldRelease.cohort.active_installations,1);assert.equal(oldRelease.cohort.sessions,1);assert.equal(oldRelease.inventory.snapshot_installations,1);
  const month=f.store.usage({days:30});assert.equal(month.cohort.returning_installations,null);assert.equal(month.cohort.returning_reason,'retention');assert.equal(month.window.coverage_complete,false);
});

test('long-lived sessions count each day with activity and use the latest usage release without requiring a new session start',t=>{
  const f=fixture(t,{initialClock:Date.parse('2026-10-01T00:00:00Z')});
  at(f,'2026-10-10T12:00:00Z');
  add(f,record(f,'session_started',{launchpad_version:'2.2.0'}));
  at(f,'2026-10-15T12:00:00Z');
  add(f,record(f,'feature_used',{feature:'check_updates',launchpad_version:'2.2.0'}));
  at(f,'2026-10-18T12:00:00Z');
  add(f,record(f,'application_opened',{application:'trackaudio',launchpad_version:'2.3.0'}));
  // A queued session-start can be absent after consent changes or queue limits.
  add(f,record(f,'feature_used',{support_id:B,feature:'font_check'}));
  at(f,'2026-10-20T12:00:00Z');
  const result=f.store.usage();
  assert.equal(result.cohort.active_installations,2);assert.equal(result.cohort.sessions,2);
  assert.equal(result.cohort.returning_installations,1);
  assert.deepEqual(result.releases,[{version:'2.3.0',installations:2}]);
  assert.equal(result.daily.find(row=>row.date==='2026-10-15').active_installations,1);
  assert.equal(result.daily.find(row=>row.date==='2026-10-18').active_installations,2);
  assert.equal(result.daily.reduce((sum,row)=>sum+row.sessions,0),3);
  const app=f.store.usage({application:'trackaudio'});
  assert.equal(app.cohort.active_installations,1);assert.equal(app.cohort.sessions,1);
  assert.deepEqual(app.features,[{feature:'check_updates',uses:1,installations:1}]);
  assert.equal(app.cohort.returning_installations,0,'previous window must match the same application scope');
});

test('today-only activity appears immediately while inventory, errors and future observations keep separate denominators',t=>{
  const f=fixture(t),futureId=randomUUID(),session=record(f,'session_started');
  add(f,session,session,record(f,'feature_used',{feature:'check_updates'}),record(f,'application_opened',{application:'trackaudio'}),
    record(f,'session_started',{support_id:futureId,occurred_at:new Date(f.now()+1).toISOString()}));
  f.store.admitBatch('errors',batch(record(f,'operation_failed',{support_id:B})));
  f.store.admitBatch('inventory',batch(inventory(f,C,{trackaudio:{status:'known',version:'1.4.0'}},'2.2.0'),
    {...inventory(f,futureId,{trackaudio:{status:'known',version:'1.5.0'}}),occurred_at:new Date(f.now()+1).toISOString()}));
  const result=f.store.usage();
  assert.equal(result.cohort.active_installations,1);assert.equal(result.cohort.sessions,1);assert.equal(result.cohort.app_launches,1);
  assert.equal(result.inventory.snapshot_installations,1);assert.equal(result.inventory.latest_snapshot_at,new Date(f.now()).toISOString());
  assert.equal(result.daily.length,7);assert.equal(result.daily.at(-1).date,new Date(f.now()).toISOString().slice(0,10));
  assert.equal(result.daily.at(-1).partial,true);assert.equal(result.daily.at(-1).active_installations,1);
  assert.equal(result.daily.filter(row=>row.partial).length,1);assert.equal(result.window.coverage_complete,false);
  const app=f.store.usage({application:'trackaudio'});assert.equal(app.cohort.active_installations,1);assert.equal(app.inventory.snapshot_installations,1);
  const release=f.store.usage({launchpad_version:'2.2.0'});assert.equal(release.cohort.active_installations,0);assert.equal(release.inventory.snapshot_installations,1);
  f.advance(1);assert.equal(f.store.usage().cohort.active_installations,2);assert.equal(f.store.usage().inventory.snapshot_installations,2);
});

test('7- and 30-date windows roll at UTC midnight, include exact boundaries and deduplicate installations across days',t=>{
  for(const days of [7,30]) {
    const clock=Date.parse('2026-10-20T12:00:00Z'),today=Date.parse('2026-10-20T00:00:00Z'),start=today-(days-1)*DAY;
    const f=fixture(t,{initialClock:start-35*DAY}),outside=randomUUID();
    at(f,new Date(start-1).toISOString());add(f,record(f,'session_started',{support_id:outside}));
    at(f,new Date(start).toISOString());add(f,record(f,'session_started'));
    at(f,new Date(start+DAY).toISOString());add(f,record(f,'feature_used',{support_id:B,feature:'font_check'}));
    at(f,new Date(clock).toISOString());add(f,record(f,'feature_used',{support_id:B,feature:'check_updates'}));
    const current=f.store.usage({days});
    assert.equal(current.window.start,new Date(start).toISOString());assert.equal(current.daily.length,days);
    assert.equal(current.cohort.active_installations,2);assert.equal(current.cohort.sessions,2);
    assert.equal(current.daily.reduce((sum,row)=>sum+row.active_installations,0),3);
    assert.equal(current.window.coverage_complete,true);assert.equal(current.cohort.returning_available,days===7);
    at(f,new Date(today+DAY).toISOString());add(f,record(f,'session_started',{support_id:C}));
    const midnight=f.store.usage({days});
    assert.equal(midnight.window.start,new Date(start+DAY).toISOString());
    assert.equal(midnight.cohort.active_installations,2,'oldest date rolls out; exact midnight activity is counted');
    assert.equal(midnight.cohort.sessions,2);assert.equal(midnight.daily.at(-1).active_installations,1);
    assert.equal(midnight.daily.at(-1).partial,true);assert.equal(midnight.daily[0].active_installations,1);
  }
});

test('returning overlap includes the entire contiguous preceding UTC period without a partial-day gap',t=>{
  const f=fixture(t,{initialClock:Date.parse('2026-09-01T00:00:00Z')});
  at(f,'2026-10-06T23:59:59.999Z');add(f,record(f,'session_started',{support_id:C}));
  at(f,'2026-10-07T00:00:00.000Z');add(f,record(f,'session_started',{support_id:B}));
  at(f,'2026-10-13T23:59:59.999Z');add(f,record(f,'session_started'));
  at(f,'2026-10-14T00:00:00.000Z');add(f,record(f,'feature_used',{feature:'font_check'}));
  at(f,'2026-10-20T12:00:00.000Z');add(f,record(f,'feature_used',{support_id:B,feature:'check_updates'}),record(f,'feature_used',{support_id:C,feature:'check_updates'}));
  const result=f.store.usage();
  assert.equal(result.window.previous_start,'2026-10-07T00:00:00.000Z');assert.equal(result.window.previous_end_exclusive,result.window.start);
  assert.equal(result.cohort.active_installations,3);assert.equal(result.cohort.returning_installations,2);
});

test('inventory shares durable capped storage without entering usage, session or historical aggregate metrics',t=>{
  const f=fixture(t,{initialClock:CLOCK-2*DAY});
  const inv=inventory(f,C,{topsky:{status:'known',version:'3.0.0'}});
  assert.deepEqual(f.store.admitBatch('inventory',batch(inv,inv)),{accepted:1,duplicates:1});
  const reverse={...inv,items:[...inv.items].reverse()};assert.deepEqual(f.store.admitBatch('inventory',batch(reverse)),{accepted:0,duplicates:1});
  f.advance(2*DAY);
  const usage=f.store.usage();assert.equal(usage.cohort.active_installations,0);assert.equal(usage.cohort.sessions,0);assert.equal(usage.inventory.snapshot_installations,1);
  assert.deepEqual(f.store.overview().events,[]);assert.deepEqual(f.store.overview().versions,[]);assert.deepEqual(f.store.overview().aggregate_summary.events,[]);
  assert.equal(f.store.health().usage_storage_includes_inventory,true);assert.equal(f.store.health().inventory_records,1);assert.equal(f.store.health().storage.usage.count,1);
  assert.equal(usage.cohort.returning_reason,'coverage');
  f.store.close();const reopened=f.open();assert.deepEqual(reopened.admitBatch('inventory',batch(inv)),{accepted:0,duplicates:1});
  assert.equal(reopened.usage().inventory.snapshot_installations,1);
  reopened.deleteSupport(C);assert.equal(reopened.usage().inventory.snapshot_installations,0);
});

test('release distributions are bounded and preserve long-tail counts; absent history is never called returning',t=>{
  const f=fixture(t,{initialClock:CLOCK-2*DAY});
  for(let i=0;i<53;i++) {
    const id=randomUUID(),version=`2.3.${i}`;
    add(f,record(f,'session_started',{support_id:id,launchpad_version:version}));
    f.store.admitBatch('inventory',batch(inventory(f,id,{euroscope:{status:'known',version}},version)));
  }
  f.advance(2*DAY);
  const result=f.store.usage();assert.equal(result.releases.length,50);assert.equal(result.releases.find(row=>row.version==='other').installations,4);
  const versions=result.inventory.components.find(row=>row.component==='euroscope').versions;assert.equal(versions.length,50);assert.equal(versions.reduce((n,row)=>n+row.installations,0),53);
  assert.equal(result.cohort.returning_installations,null);assert.equal(result.cohort.returning_available,false);
  assert.throws(()=>f.store.usage({days:365}),{status:400});assert.throws(()=>f.store.usage({application:'private/path'}),{status:400});
});

test('expiry remains effective in analytics even if housekeeping has not removed expired rows',t=>{
  const f=fixture(t,{initialClock:CLOCK-10*DAY,limits:{eventTtlDays:8}});
  add(f,record(f,'session_started'));f.store.admitBatch('inventory',batch(inventory(f)));
  f.advance(10*DAY);assert.equal(f.store.health().storage.usage.count,2);
  const result=f.store.usage({days:30});assert.equal(result.cohort.active_installations,0);assert.equal(result.inventory.snapshot_installations,0);
  assert.equal(result.window.coverage_complete,false);assert.equal(result.cohort.returning_reason,'retention');
});

test('analytics remain bounded at the optional storage ceiling with many repeated installation observations',t=>{
  const f=fixture(t,{initialClock:CLOCK-30*DAY});
  at(f,new Date(CLOCK).toISOString());
  const ids=Array.from({length:1000},()=>randomUUID()),sessions=ids.map(()=>randomUUID());
  const count=f.store.limits.maxEvents*f.store.limits.usageStopPercent/100;
  // Insert synthetic history directly: this tests the read workload at its hard
  // storage ceiling, without bypassing daily admission limits in production.
  f.store.transaction(()=>{
    const insert=f.store.db.prepare('INSERT INTO events VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)');
    for(let i=0;i<count;i++) {
      const at=CLOCK-DAY*(1+i%28),feature=i%2===0;
      const e=record(f,feature?'feature_used':'application_opened',{
        support_id:ids[i%ids.length],session_id:sessions[i%ids.length],occurred_at:new Date(at).toISOString(),
        application:feature?'launchpad':'trackaudio',...(feature?{feature:'check_updates'}:{})});
      const payload=JSON.stringify(e);
      insert.run(e.event_id,'usage',e.support_id,e.session_id,e.event_name,e.application,null,null,null,null,e.launchpad_version,at,at,CLOCK+DAY,payload,Buffer.byteLength(payload),'synthetic');
    }
  });
  for(const filter of [{days:7},{days:30},{days:30,application:'trackaudio'}]) {
    const started=performance.now(),result=f.store.usage(filter);
    t.diagnostic(`${count} synthetic records, ${filter.days} days${filter.application?' with app filter':''}: ${Math.round(performance.now()-started)} ms`);
    assert.equal(result.cohort.active_installations,filter.application?500:1000);
    assert.equal(result.cohort.sessions,result.cohort.active_installations);
    assert.equal(result.releases.length,1);
    assert.equal(result.inventory.snapshot_installations,0);
    assert.ok(Buffer.byteLength(JSON.stringify(result))<16384,'response must contain bounded groups, never raw observations');
  }
});
