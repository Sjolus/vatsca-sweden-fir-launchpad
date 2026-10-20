import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateBatch,validateReport,validateEvent,DLL_PRODUCTS } from '../src/schema.mjs';
import { event,report,batch,inventory,CLOCK,SUPPORT } from './intake-helpers.mjs';

const dll=(overrides={})=>({sha256:'a'.repeat(64),product:'unknown',role:'current',copies:1,version_source:'unknown',...overrides});
const detailed=(entries=[dll()],overrides={})=>inventory({dll_inventory:{status:'complete',entries,unreadable_files:0,...overrides}});

test('detailed DLL inventory is optional, bounded and canonical without changing legacy payloads',()=>{
  const old=inventory(),legacy=validateEvent(old,'inventory',CLOCK);
  assert.equal(Object.hasOwn(legacy,'dll_inventory'),false);
  assert.deepEqual(Object.keys(legacy),['event_id','support_id','session_id','occurred_at','launchpad_version','event_name','items']);
  const entries=DLL_PRODUCTS.map((product,index)=>dll({sha256:index.toString(16).padStart(64,'0'),product,role:index%2?'staged':'current',copies:index+1,
    ...(index%3===0?{}:index%3===1?{version:'3.0.0-beta.19',version_source:'embedded',file_version:'0.0.0.0'}:{version:'1.2.3.4',version_source:'resource'})}));
  const result=validateEvent(detailed(entries),'inventory',CLOCK).dll_inventory;
  assert.deepEqual(result,{status:'complete',entries,unreadable_files:0});
  assert.deepEqual(Object.keys(result),['status','entries','unreadable_files']);
  assert.deepEqual(Object.keys(result.entries[1]),['sha256','product','role','copies','version','file_version','version_source']);
  assert.equal(validateEvent(detailed([dll(),dll({role:'staged'})]),'inventory',CLOCK).dll_inventory.entries.length,2,'same fingerprint may occur in both locations');
  assert.equal(validateEvent(detailed([],{status:'partial'}),'inventory',CLOCK).dll_inventory.status,'partial','bounds can truncate without unreadable files');
  assert.equal(validateEvent(detailed([],{status:'unavailable',unreadable_files:1}),'inventory',CLOCK).dll_inventory.status,'unavailable');
  assert.equal(validateEvent(detailed([dll({file_version:'0.0.0.0'})]),'inventory',CLOCK).dll_inventory.entries[0].file_version,'0.0.0.0');
});

test('DLL registration labels preserve public vendor suffixes without widening application or file versions',()=>{
  const entry=dll({product:'vatcan_bookings',version:'1.1.6-2022wb',file_version:'1.0.0.1',version_source:'registration'});
  assert.deepEqual(validateEvent(detailed([entry]),'inventory',CLOCK).dll_inventory.entries[0],entry);
  assert.equal(validateEvent(detailed([{...entry,product:'arrival_load_balancer',version:'0.2.11T'}]),'inventory',CLOCK).dll_inventory.entries[0].version,'0.2.11T');
  assert.equal(validateEvent(detailed([dll({version:'2.6-beta.4',version_source:'registration'})]),'inventory',CLOCK).dll_inventory.entries[0].version_source,'registration');
  for(const version of ['1.1.6-person','1.1.6-2022private','1.1.6-2022wb+private','1.1.6-2022wb\n','1.1.6-2022wb ','1.1.6-2022WB','0.2.11T-private','0.2.11t','0.2.11T\n'])
    assert.throws(()=>validateEvent(detailed([{...entry,version}]),'inventory',CLOCK),{code:'invalid_request'});
  assert.throws(()=>validateEvent(detailed([{...entry,file_version:entry.version}]),'inventory',CLOCK),{code:'invalid_request'});
  assert.throws(()=>validateEvent(detailed([dll({version_source:'registration'})]),'inventory',CLOCK),{code:'invalid_request'});
  assert.throws(()=>validateEvent(event({package_version:entry.version}),'usage',CLOCK),{code:'invalid_request'});
  assert.throws(()=>validateEvent(event({package_version:'0.2.11T'}),'usage',CLOCK),{code:'invalid_request'});
  assert.throws(()=>validateEvent(detailed([{...entry,file_version:'0.2.11T'}]),'inventory',CLOCK),{code:'invalid_request'});
  const legacy=inventory();legacy.items[0].version=entry.version;legacy.items[0].status='known';
  assert.throws(()=>validateEvent(legacy,'inventory',CLOCK),{code:'invalid_request'});
});

test('SAAMAN uses existing typed application and detailed DLL fields while legacy inventory stays unchanged',()=>{
  const entry=dll({product:'saaman',version:'1.3.37',version_source:'registration'});
  const snapshot=validateEvent(detailed([entry]),'inventory',CLOCK);
  assert.deepEqual(snapshot.dll_inventory.entries,[entry]);
  assert.equal(snapshot.items.length,10);
  assert.equal(validateEvent(event({application:'saaman',event_name:'operation_finished',operation:'install'}),'usage',CLOCK).application,'saaman');
  assert.equal(validateReport(report({application:'saaman'}),CLOCK).application,'saaman');
  assert.throws(()=>validateEvent(event({application:'saaman',event_name:'application_opened'}),'usage',CLOCK),{code:'invalid_request'});
  assert.throws(()=>validateEvent(detailed([{...entry,profile_path:'C:\\Synthetic\\SAAMAN.json'}]),'inventory',CLOCK),{code:'invalid_request'});
});

test('DLL inventory rejects private fields, ambiguous sources, invalid hashes and inflated bounds',()=>{
  for(const change of [
    {filename:'private.dll'},{path:'C:\\Synthetic\\private.dll'},{product_name:'private product'},{product:'custom'},
    {sha256:'A'.repeat(64)},{sha256:'a'.repeat(63)},{sha256:'g'.repeat(64)},{role:'loaded'},
    {copies:0},{copies:4097},{copies:1.5},{copies:'1'},
    {version:'1.2.3'},{file_version:'1.2.3+private'},{version_source:'native'},
    {version_source:'resource'},{version_source:'embedded'},{version_source:'embedded',version:'My private build'},
  ])assert.throws(()=>validateEvent(detailed([dll(change)]),'inventory',CLOCK),{code:'invalid_request'});
  for(const change of [{status:'not_shared'},{unreadable_files:-1},{unreadable_files:4097},{unreadable_files:1.5},{unreadable_files:1},
    {status:'unavailable'},{entries:[dll(),dll()]},{filename:'private.dll'},{entries:null}])
    assert.throws(()=>validateEvent(detailed(undefined,change),'inventory',CLOCK),{code:'invalid_request'});
  const entries=Array.from({length:64},(_,index)=>dll({sha256:index.toString(16).padStart(64,'0')}));
  assert.ok(Buffer.byteLength(JSON.stringify({status:'complete',entries,unreadable_files:0}))>6144);
  assert.throws(()=>validateEvent(detailed(entries),'inventory',CLOCK),{code:'invalid_request'},'byte limit applies before count limit');
  assert.throws(()=>validateEvent(detailed([...entries,dll()]),'inventory',CLOCK),{code:'invalid_request'});
  const extra=detailed();extra.dll_inventory.entries[0].secret='synthetic';
  assert.throws(()=>validateEvent(extra,'inventory',CLOCK));
  for(const stream of ['usage','errors','context'])assert.throws(()=>validateEvent(detailed(),stream,CLOCK));
  assert.throws(()=>validateReport(report({context:[detailed()]}),CLOCK));
});

test('strict schema rejects identifying fields, arbitrary diagnostics and wrong stream before storage',()=>{
  for(const key of ['name','email','cid','password','hoppie','path','stack','message','url','exception','headers']) {
    assert.throws(()=>validateBatch(batch(event({[key]:'synthetic-private-value'})),'usage',CLOCK),{code:'invalid_request'});
    assert.throws(()=>validateReport(report({[key]:'synthetic-private-value'}),CLOCK),{code:'invalid_request'});
  }
  for(const [key,value] of [['application','C:\\Synthetic\\EuroScope.exe'],['launchpad_version','2.2.0+person'],['error_code','failure: secret'],['source','https://example.invalid/?token=synthetic']])
    assert.throws(()=>validateEvent(event({[key]:value}),'usage',CLOCK));
  assert.throws(()=>validateEvent(event({event_name:'unhandled_error'}),'usage',CLOCK));
  assert.throws(()=>validateEvent(event(),'errors',CLOCK));
  assert.throws(()=>validateEvent(event({support_id:'aaaaaaaa-aaaa-1aaa-8aaa-aaaaaaaaaaaa'}),'usage',CLOCK));
});
test('version and timestamp canonicalization permits known vendor formats without arbitrary labels',()=>{
  for(const version of ['3.2.3.2','2610.1.3','4.1.0-beta.19','2.2.1-rc.1'])
    assert.equal(validateEvent(event({package_version:version,occurred_at:'2026-10-12T12:00:00.0000000Z'}),'usage',CLOCK).package_version,version);
  for(const version of ['3.2.3 user','2610/01 rev.3','2.2.0-person','1.2.3+build','1'.repeat(41)])
    assert.throws(()=>validateEvent(event({package_version:version}),'usage',CLOCK));
  for(const occurred_at of ['2026-02-30T12:00:00Z','2026-10-01T00:00:00Z','2026-10-12T12:06:00Z','2026-10-12T14:00:00+02:00'])
    assert.throws(()=>validateEvent(event({occurred_at}),'usage',CLOCK));
  assert.equal(validateEvent(event({support_id:SUPPORT.toUpperCase()}),'usage',CLOCK).support_id,SUPPORT);
});
test('batch/report envelopes and same-identity context are bounded and explicit',()=>{
  assert.throws(()=>validateBatch(batch(),'usage',CLOCK));
  assert.throws(()=>validateBatch(batch(...Array.from({length:26},()=>event())),'usage',CLOCK));
  assert.throws(()=>validateBatch({...batch(event()),schema_version:2},'usage',CLOCK));
  assert.throws(()=>validateReport(report({context:Array.from({length:21},()=>event())}),CLOCK));
  assert.throws(()=>validateReport(report({context:[event({support_id:'cccccccc-cccc-4ccc-8ccc-cccccccccccc'})]}),CLOCK));
  assert.equal(validateReport(report({context:[event(),event({event_name:'operation_failed',error_code:'package_invalid'})]}),CLOCK).context.length,2);
  assert.throws(()=>validateEvent(event({restart_required:'yes'}),'usage',CLOCK));
});

test('identifiers, versions and timestamps reject trailing line terminators and whitespace',()=>{
  for(const suffix of ['\n','\r','\r\n','\u2028','\u2029',' ','\t']) {
    for(const key of ['event_id','support_id','session_id','launchpad_version','occurred_at']) {
      const value=event();value[key]+=suffix;
      assert.throws(()=>validateEvent(value,'usage',CLOCK),{code:'invalid_request'},`${key} with trailing terminator`);
    }
    for(const key of ['report_id','support_id','launchpad_version']) {
      const value=report();value[key]+=suffix;
      assert.throws(()=>validateReport(value,CLOCK),{code:'invalid_request'},`${key} with trailing terminator`);
    }
  }
});

test('usage features and display snapshots accept only typed event-specific fields',()=>{
  assert.equal(validateEvent(event({event_name:'feature_used',feature:'font_check'}),'usage',CLOCK).feature,'font_check');
  assert.throws(()=>validateEvent(event({event_name:'feature_used',feature:'custom/path'}),'usage',CLOCK));
  assert.throws(()=>validateEvent(event({feature:'font_check'}),'usage',CLOCK));
  assert.throws(()=>validateEvent(event({event_name:'feature_used'}),'usage',CLOCK));
  const prefs=event({event_name:'preferences_snapshot',compact_layout:false,text_scale:'larger',always_on_top:true,theme:'light'});
  assert.equal(validateEvent(prefs,'usage',CLOCK).compact_layout,false);
  for(const change of [{text_scale:'150.222%'},{compact_layout:0},{always_on_top:null},{theme:'custom-theme'},{density:'compact'}])assert.throws(()=>validateEvent({...prefs,...change},'usage',CLOCK));
  assert.throws(()=>validateEvent(event({event_name:'application_opened',application:'gng'}),'usage',CLOCK),'folder opening is not an application launch');
  assert.equal(validateEvent(event({event_name:'application_opened',application:'vatefs'}),'usage',CLOCK).application,'vatefs');
});

test('inventory requires a full fixed catalogue, validated sources/status/versions and a separate endpoint',()=>{
  const inv=inventory();inv.items[0]={...inv.items[0],status:'known',version:'3.2.3.2'};
  assert.equal(validateBatch(batch(inv),'inventory',CLOCK)[0].items.length,10);
  for(const stream of ['usage','errors','context'])assert.throws(()=>validateEvent(inv,stream,CLOCK));
  assert.throws(()=>validateReport(report({context:[inv]}),CLOCK));
  assert.throws(()=>validateBatch(batch(event()),'inventory',CLOCK));
  for(const changes of [item=>({...item,component:'custom_dll'}),item=>({...item,path:'C:\\Synthetic\\private.dll'}),item=>({...item,source:'download'}),item=>({...item,status:'unknown'}),item=>({...item,version:'3.2.3+username'}),item=>({...item,status:'maybe'})]) {
    const invalid=structuredClone(inv);invalid.items[0]=changes(invalid.items[0]);assert.throws(()=>validateBatch(batch(invalid),'inventory',CLOCK));
  }
  assert.throws(()=>validateBatch(batch({...inv,items:inv.items.slice(1)}),'inventory',CLOCK));
  assert.throws(()=>validateBatch(batch({...inv,items:[...inv.items.slice(0,9),inv.items[0]]}),'inventory',CLOCK));
});
