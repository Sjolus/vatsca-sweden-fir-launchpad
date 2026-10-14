import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateBatch,validateReport,validateEvent } from '../src/schema.mjs';
import { event,report,batch,inventory,CLOCK,SUPPORT } from './intake-helpers.mjs';

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
