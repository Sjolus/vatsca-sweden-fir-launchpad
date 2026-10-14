import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {createContext,runInContext} from 'node:vm';
import {usageFixture} from './admin-usage-fixture.mjs';

const scopes=['usage','inventory','reports','errors','support','health'];
const owner={authenticated:true,username:'maintainer',owner:true,permissions:scopes};
const member=(permissions=[])=>({authenticated:true,username:'reviewer',owner:false,permissions});
const reply=(value,status=200)=>({ok:status>=200&&status<300,status,json:async()=>value});
const walk=node=>[node,...node.children.flatMap(walk)];
class Node {
  constructor(tag='div',text='') {Object.assign(this,{tag,textContent:text,value:'',hidden:false,children:[],dataset:{},attributes:{},listeners:{},classList:{toggle(){}}});}
  append(...children){this.children.push(...children);}
  replaceChildren(...children){this.children=children;}
  addEventListener(name,fn){this.listeners[name]=fn;}
  setAttribute(name,value){this.attributes[name]=value;}
  removeAttribute(name){delete this.attributes[name];}
  close(){this.open=false;}
  showModal(){this.open=true;}
  focus(){this.focused=true;}
  select(){this.selected=true;}
  get text(){return[this.textContent,...this.children.map(child=>child.text)].join(' ').replace(/\s+/g,' ').trim();}
  async fire(name){return this.listeners[name]?.({preventDefault(){}});}
}
function browser({session=member(),hash='',fetch:handler}={}) {
  const nodes=new Map(),calls=[],order=[],copied=[],windowListeners={};
  const node=id=>{if(!nodes.has(id))nodes.set(id,new Node());return nodes.get(id);};
  const tabs=['overview','inventory','reports','errors','support','health','users'].map(view=>{const n=new Node('button');n.dataset.view=view;return n;});
  node('days-filter').value='7';
  const location={hash,pathname:'/backoffice/',search:''};
  const document={body:{dataset:{}},getElementById:node,querySelectorAll:()=>tabs,createElement:tag=>new Node(tag),createElementNS:(_,tag)=>new Node(tag),createTextNode:text=>new Node('#text',text),createDocumentFragment:()=>new Node('#fragment')};
  const context=createContext({document,Node,URLSearchParams,setTimeout:()=>1,clearTimeout(){},confirm:()=>true,navigator:{clipboard:{writeText:async text=>copied.push(text)}},
    window:{location,addEventListener:(name,handler)=>{windowListeners[name]=handler;},history:{replaceState:(_,__,url)=>{order.push('strip');location.hash='';location.replaced=url;}},SimpleWebAuthnBrowser:{browserSupportsWebAuthn:()=>true,startRegistration:async()=>({id:'synthetic'}),startAuthentication:async()=>({id:'synthetic'})}},
    fetch:async(path,options)=>{
      const call={path,body:options.body?JSON.parse(options.body):undefined};calls.push(call);order.push(path);
      const result=await handler?.(call);if(result)return result;
      if(path==='/auth/session')return reply(session);
      if(path.startsWith('/api/admin/usage?')){const {inventory,...usage}=usageFixture();return reply(usage);}
      if(path.startsWith('/api/admin/inventory?')){const {window,filters,inventory}=usageFixture();return reply({window,filters,inventory});}
      if(path==='/api/admin/health')return reply({ok:true});
      if(path==='/api/admin/users')return reply({users:[]});
      if(path.startsWith('/api/admin/support/'))return reply({reports:[{reference:'LP-synthetic'}],events:[]});
      return reply([]);
    }});
  runInContext(readFileSync(new URL('../public/usage.js',import.meta.url),'utf8'),context);
  runInContext(readFileSync(new URL('../public/app.js',import.meta.url),'utf8').replace(/boot\(\);\s*$/,''),context);
  const beforeUnload=()=>{const event={prevented:false,preventDefault(){this.prevented=true;}};windowListeners.beforeunload?.(event);return event;};
  return{context,node,tabs,calls,order,copied,location,beforeUnload,run:source=>runInContext(source,context),boot:()=>context.boot()};
}
const findButton=(b,text)=>walk(b.node('view')).find(node=>node.tag==='button'&&node.text===text);
const accessEditor=b=>walk(b.node('view')).find(node=>node.tag==='form'&&node.className==='access-editor');
const accessInput=(b,label)=>{const input=walk(b.node('view')).find(node=>node.tag==='input'&&node.attributes['aria-label']===label);assert.ok(input,`Accessible input ${label}`);return input;};
const setAccess=async(b,label,value)=>{const input=accessInput(b,label);input.checked=value;await input.fire('change');};
const userFixture=()=>[{id:'reviewer-id',username:'reviewer',permissions:['reports'],enabled:true,enrolled:true},{id:'observer-id',username:'observer',permissions:['usage'],enabled:false,enrolled:false},{id:'unchanged-id',username:'unchanged',permissions:[],enabled:true,enrolled:true}];
async function openUsers(b){await b.boot();b.run("currentView='users'");await b.context.load();}

test('read scopes show only assigned pages and never prefetch hidden pages',async()=>{
  for(const scope of scopes){
    const b=browser({session:member([scope])});await b.boot();
    assert.deepEqual(b.tabs.filter(tab=>!tab.hidden).map(tab=>tab.dataset.view),[scope==='usage'?'overview':scope]);
    assert.equal(b.node('signed-in-user').textContent,'reviewer');
    const reads=b.calls.filter(call=>call.path.startsWith('/api/admin/'));
    assert.equal(reads.length,scope==='support'?0:1);
    if(reads.length)assert.ok(reads[0].path.startsWith(`/api/admin/${scope}`));
    const before=b.calls.length;await b.tabs.at(-1).fire('click');assert.equal(b.calls.length,before);
    if(scope!=='reports'){await b.context.openReport({reference:'LP-hidden'});assert.equal(b.calls.length,before);}
    if(scope==='usage')assert.doesNotMatch(b.node('view').text,/Software & plugin observations|View software & plugin versions/);
  }
});
test('no assigned pages gives a signed-in empty state without any admin fetch',async()=>{
  const b=browser();await b.boot();assert.equal(b.calls.length,1);assert.equal(b.node('workspace').hidden,false);
  assert.match(b.node('view').text,/no reporting pages are assigned/);assert.equal(b.node('refresh').disabled,true);assert.ok(b.tabs.every(tab=>tab.hidden));
});
test('guest report and support views never expose owner mutations or unauthorized detail links',async()=>{
  const b=browser({session:member(['reports','support']),fetch:({path})=>path==='/api/admin/reports/LP-synthetic'?reply({reference:'LP-synthetic',status:'new',payload:{category:'setup'}}):undefined});
  await b.boot();await b.context.openReport({reference:'LP-synthetic'});assert.equal(b.node('report-dialog').open,true);assert.equal(b.node('report-status-form').hidden,true);
  const before=b.calls.length;await b.node('report-status-form').fire('submit');assert.equal(b.calls.length,before);
  b.run("currentView='support';lookup='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");await b.context.load();assert.doesNotMatch(b.node('view').text,/Delete current records|Delete records for/);
  const restricted=browser({session:member(['support'])});await restricted.boot();restricted.run("lookup='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'");await restricted.context.load();
  assert.doesNotMatch(restricted.node('view').text,/Open report|Delete current records/);
});
test('403 clears visible private data and refreshes permissions without an automatic retry loop',async()=>{
  let sessions=0,deny=false;
  const b=browser({fetch:({path})=>path==='/auth/session'?reply(++sessions===1?owner:member(['inventory'])):path.startsWith('/api/admin/usage?')&&deny?reply({error:'Access changed.'},403):undefined});
  await b.boot();b.node('report-details').append(new Node('p','synthetic-private'));b.node('report-dialog').showModal();deny=true;await b.context.load();
  assert.equal(sessions,2);assert.equal(b.node('report-dialog').open,false);assert.equal(b.node('report-details').children.length,0);assert.match(b.node('view').text,/Your access has been refreshed/);
  assert.deepEqual(b.tabs.filter(tab=>!tab.hidden).map(tab=>tab.dataset.view),['inventory']);assert.equal(b.calls.filter(call=>call.path.startsWith('/api/admin/inventory')).length,0);
  await b.context.load();assert.equal(b.calls.at(-1).path,'/api/admin/inventory?days=7');
});
test('invitation fragment disappears before requests and only enters the registration POST body',async()=>{
  const token='a'.repeat(43),b=browser({hash:`#invite=${token}`,session:member(['errors'])});await b.boot();
  assert.equal(b.location.hash,'');assert.equal(b.location.replaced,'/backoffice/');assert.deepEqual(b.order,['strip']);assert.equal(b.node('enroll-field').hidden,true);assert.equal(b.node('enrollment').value,'');
  await b.node('login-form').fire('submit');assert.deepEqual(b.calls.find(call=>call.path==='/auth/register-options').body,{enrollment_code:token});
  assert.ok(b.calls.every(call=>!call.path.includes(token)));assert.equal(b.run('invitationToken'),'');assert.equal(b.node('signed-in-user').textContent,'reviewer');assert.ok(b.calls.at(-1).path.startsWith('/api/admin/errors'));
});
test('leaving invitation mode cancels a pending ceremony before verification',async()=>{
  let finish;const prompt=new Promise(resolve=>{finish=resolve;});const b=browser({hash:'#invite='+'b'.repeat(43)});await b.boot();b.context.window.SimpleWebAuthnBrowser.startRegistration=()=>prompt;
  const pending=b.node('login-form').fire('submit');await new Promise(resolve=>setImmediate(resolve));await b.node('enroll-toggle').fire('click');finish({id:'synthetic'});await pending;
  assert.equal(b.run('invitationToken'),'');assert.ok(!b.calls.some(call=>call.path.endsWith('-verify')));assert.equal(b.node('login-submit').textContent,'Sign in with a passkey');
});
test('owner creates a least-privilege invitation, explicitly copies it, and refresh removes the displayed secret',async()=>{
  const url='https://example.invalid/backoffice/#invite='+'c'.repeat(43),users=[];
  const b=browser({session:owner,fetch:({path,body})=>{
    if(path==='/api/admin/users'&&body){users.push({id:'user-id',username:body.username,permissions:body.permissions,enabled:true,enrolled:false});return reply({user:users[0],invitation_url:url,expires_at:'2026-10-14T12:10:00Z'});}
    if(path==='/api/admin/users')return reply({users});
  }});await b.boot();b.run("currentView='users'");await b.context.load();
  const form=walk(b.node('view')).find(node=>node.tag==='form'),inputs=walk(form).filter(node=>node.tag==='input');assert.equal(inputs.filter(input=>input.checked).length,0);
  inputs[0].value='reviewer';inputs.find(input=>input.value==='inventory').checked=true;await form.fire('submit');
  assert.deepEqual(b.calls.find(call=>call.path==='/api/admin/users'&&call.body).body,{username:'reviewer',permissions:['inventory']});assert.equal(b.copied.length,0);
  assert.match(b.node('view').text,/Invitation for reviewer/);
  assert.equal(walk(b.node('view')).find(node=>node.readOnly).value,url);await walk(b.node('view')).find(node=>node.text==='Copy invitation link').fire('click');assert.deepEqual(b.copied,[url]);
  await b.context.load();assert.ok(!walk(b.node('view')).some(node=>node.value===url));
});
test('an accepted invitation remains copyable when the following user-list refresh fails',async()=>{
  let created=false;const url='https://example.invalid/#invite='+'d'.repeat(43);
  const b=browser({session:owner,fetch:({path,body})=>{
    if(path==='/api/admin/users'&&body){created=true;return reply({invitation_url:url,expires_at:'2026-10-14T12:10:00Z'});}
    if(path==='/api/admin/users'&&created)return reply({error:'Temporarily unavailable.'},503);
  }});await b.boot();b.run("currentView='users'");await b.context.load();const form=walk(b.node('view')).find(node=>node.tag==='form');walk(form).find(node=>node.tag==='input').value='reviewer';await form.fire('submit');
  assert.equal(walk(b.node('view')).find(node=>node.readOnly).value,url);assert.match(b.node('view').text,/invitation was created, but the user list could not refresh/);
});
test('passkey reset requires confirmation; disabled access and page permissions are independent',async()=>{
  const user={id:'user-id',username:'reviewer',permissions:['reports'],enabled:true,enrolled:true};
  const b=browser({session:owner,fetch:({path,body})=>{
    if(path==='/api/admin/users/access'){Object.assign(user,body.users[0]);return reply({users:[user]});}
    return path==='/api/admin/users'?reply({users:[user]}):body?reply({user}):undefined;
  }});await openUsers(b);
  const reset=findButton(b,'Reset passkey…');b.context.confirm=()=>false;await reset.fire('click');assert.ok(!b.calls.some(call=>call.path.endsWith('/invite')));
  b.context.confirm=()=>true;await reset.fire('click');assert.deepEqual(b.calls.find(call=>call.path.endsWith('/invite')).body,{reset:true});
  await setAccess(b,'reviewer: Allow sign-in',false);await accessEditor(b).fire('submit');
  assert.deepEqual(b.calls.find(call=>call.path==='/api/admin/users/access').body,{users:[{id:'user-id',permissions:['reports'],enabled:false,expected:{permissions:['reports'],enabled:true}}]});
  assert.notEqual(findButton(b,'Reset passkey…').disabled,true);assert.match(findButton(b,'Reset passkey…').title,/keeps access disabled/);
  const card=b.context.invitationCard({user,invitation_url:'https://example.invalid/#invite='+'f'.repeat(43),expires_at:'2026-10-14T12:10:00Z'});
  assert.match(card.text,/Enable sign-in and save access before this invitation can be accepted/);
});

test('access matrix has one save action, a read-only owner row and an atomic payload containing only changed users',async()=>{
  const users=userFixture();let batch;
  const b=browser({session:owner,fetch:({path,body})=>{
    if(path==='/api/admin/users/access'){batch=body;for(const change of body.users)Object.assign(users.find(user=>user.id===change.id),{permissions:change.permissions,enabled:change.enabled});return reply({users});}
    if(path==='/api/admin/users')return reply({users});
  }});await openUsers(b);
  assert.equal(walk(b.node('view')).filter(node=>node.tag==='button'&&node.text==='Save access').length,1);
  const table=walk(b.node('view')).find(node=>node.tag==='table'&&node.className==='access-table');assert.ok(table);
  assert.equal(walk(table).filter(node=>node.tag==='th'&&node.scope==='col').length,9);
  const ownerInputs=walk(table).filter(node=>node.tag==='input'&&node.attributes['aria-label'].startsWith('maintainer:'));
  assert.equal(ownerInputs.length,7);assert.ok(ownerInputs.every(input=>input.disabled&&input.checked));assert.equal(findButton(b,'Save access').disabled,true);
  await setAccess(b,'reviewer: Software & plugins',true);await setAccess(b,'observer: Allow sign-in',true);
  assert.match(b.node('view').text,/2 users have unsaved changes/);assert.equal(findButton(b,'Save access').disabled,false);
  assert.equal(findButton(b,'Create invitation').disabled,true);assert.ok(walk(table).filter(node=>node.tag==='button').every(button=>button.disabled));
  const readsBefore=b.calls.length;await walk(table).find(node=>node.tag==='button').fire('click');assert.equal(b.calls.length,readsBefore);
  await accessEditor(b).fire('submit');
  assert.deepEqual(batch,{users:[{id:'reviewer-id',enabled:true,permissions:['inventory','reports'],expected:{enabled:true,permissions:['reports']}},{id:'observer-id',enabled:true,permissions:['usage'],expected:{enabled:false,permissions:['usage']}}]});
  assert.equal(batch.users.some(user=>user.id==='unchanged-id'),false);assert.equal(batch.users.some(user=>user.id==='maintainer'),false);
  assert.match(b.node('view').text,/No unsaved changes/);assert.equal(findButton(b,'Save access').disabled,true);assert.equal(findButton(b,'Create invitation').disabled,false);
});

test('reverting edits clears dirty state and discard restores original values without fetching or saving',async()=>{
  const b=browser({session:owner,fetch:({path})=>path==='/api/admin/users'?reply({users:userFixture()}):undefined});await openUsers(b);
  await setAccess(b,'reviewer: Errors',true);assert.equal(b.beforeUnload().prevented,true);
  await setAccess(b,'reviewer: Errors',false);assert.equal(findButton(b,'Save access').disabled,true);assert.equal(b.beforeUnload().prevented,false);
  await setAccess(b,'reviewer: Allow sign-in',false);await setAccess(b,'observer: Reports',true);
  const calls=b.calls.length;await findButton(b,'Discard changes').fire('click');assert.equal(b.calls.length,calls);
  assert.equal(accessInput(b,'reviewer: Allow sign-in').checked,true);assert.equal(accessInput(b,'observer: Reports').checked,false);
  assert.equal(b.beforeUnload().prevented,false);assert.match(b.node('view').text,/No unsaved changes/);
});

test('conflict and unavailable responses retain drafts and expected originals until explicit discard',async()=>{
  for(const status of [409,503]){
    const b=browser({session:owner,fetch:({path})=>path==='/api/admin/users'?reply({users:userFixture()}):path==='/api/admin/users/access'?reply({error:status===409?'Access changed elsewhere. Refresh before saving.':'Unavailable.'},status):undefined});await openUsers(b);
    await setAccess(b,'reviewer: Errors',true);const editor=accessEditor(b);await editor.fire('submit');
    assert.equal(accessEditor(b),editor);assert.equal(accessInput(b,'reviewer: Errors').checked,true);assert.equal(accessInput(b,'reviewer: Errors').disabled,false);
    assert.equal(findButton(b,'Save access').disabled,false);assert.match(b.node('message').textContent,/Save not confirmed; your edits are still shown/);
    const before=b.calls.filter(call=>call.path==='/api/admin/users').length;await editor.fire('submit');
    assert.equal(b.calls.filter(call=>call.path==='/api/admin/users').length,before,'failed saves never silently refresh away drafts');
    const saves=b.calls.filter(call=>call.path==='/api/admin/users/access');assert.deepEqual(saves[0].body,saves[1].body);
    assert.deepEqual(saves[0].body.users[0].expected,{enabled:true,permissions:['reports']});
    await findButton(b,'Discard changes').fire('click');assert.equal(accessInput(b,'reviewer: Errors').checked,false);
  }
});

test('dirty navigation and refresh require confirmation; busy saves block repeat requests and navigation',async()=>{
  let finish;const pending=new Promise(resolve=>{finish=resolve;});const users=userFixture();
  const b=browser({session:owner,fetch:({path})=>path==='/api/admin/users'?reply({users}):path==='/api/admin/users/access'?pending:undefined});await openUsers(b);
  await setAccess(b,'reviewer: Errors',true);b.context.confirm=()=>false;let calls=b.calls.length;
  await b.node('refresh').fire('click');await b.tabs[0].fire('click');await b.node('sign-out').fire('click');
  assert.equal(b.calls.length,calls);assert.equal(b.run('currentView'),'users');assert.equal(accessInput(b,'reviewer: Errors').checked,true);
  const editor=accessEditor(b),saving=editor.fire('submit');assert.equal(findButton(b,'Save access').disabled,true);assert.equal(b.node('refresh').disabled,true);
  assert.ok(walk(editor).filter(node=>node.tag==='input').every(input=>input.disabled));assert.equal(b.beforeUnload().prevented,true);
  calls=b.calls.length;await editor.fire('submit');await b.tabs[0].fire('click');await b.node('refresh').fire('click');assert.equal(b.calls.length,calls);
  users[0].permissions.push('errors');finish(reply({users}));await saving;
  assert.equal(findButton(b,'Save access').disabled,true);assert.equal(b.beforeUnload().prevented,false);
  await setAccess(b,'reviewer: Errors',false);b.context.confirm=()=>true;await b.node('refresh').fire('click');
  assert.equal(accessInput(b,'reviewer: Errors').checked,true,'confirmed refresh restores server state');assert.equal(b.beforeUnload().prevented,false);
});

test('late Users reads and access-save replies cannot recreate editor state after navigation or logout',async()=>{
  let read,save;const delayedRead=new Promise(resolve=>{read=resolve;}),delayedSave=new Promise(resolve=>{save=resolve;});let defer=true;
  const b=browser({session:owner,fetch:({path})=>path==='/api/admin/users'?(defer?delayedRead:reply({users:userFixture()})):path==='/api/admin/users/access'?delayedSave:undefined});
  await b.boot();b.run("currentView='users'");const loading=b.context.load();await b.tabs[0].fire('click');read(reply({users:userFixture()}));await loading;await new Promise(resolve=>setImmediate(resolve));
  assert.equal(b.run('userAccessState'),null);assert.doesNotMatch(b.node('view').text,/User access\. Checked permissions/);
  defer=false;b.run("currentView='users'");await b.context.load();await setAccess(b,'reviewer: Errors',true);const saving=accessEditor(b).fire('submit');b.context.showLogin();save(reply({users:userFixture()}));await saving;
  assert.equal(b.run('userAccessState'),null);assert.equal(b.node('view').children.length,0);assert.equal(b.node('workspace').hidden,true);
});

test('failed server logout still clears local private drafts and describes the remaining session honestly',async()=>{
  const b=browser({session:owner,fetch:({path})=>path==='/api/admin/users'?reply({users:userFixture()}):path==='/auth/logout'?reply({error:'Unavailable.'},503):undefined});await openUsers(b);
  await setAccess(b,'reviewer: Errors',true);await b.node('sign-out').fire('click');
  assert.equal(b.node('workspace').hidden,true);assert.equal(b.node('view').children.length,0);assert.equal(b.run('userAccessState'),null);
  assert.match(b.node('message').textContent,/Private pages are hidden, but the server could not confirm sign-out/);
});

test('expired owner verification preserves drafts and explicit passkey verification never saves automatically',async()=>{
  const users=userFixture();let saves=0;
  const b=browser({session:owner,fetch:({path,body})=>{
    if(path==='/api/admin/users')return reply({users});
    if(path==='/api/admin/users/access'){
      if(++saves===1)return reply({error:'Verify your passkey again.',code:'reauthentication_required'},403);
      for(const change of body.users)Object.assign(users.find(user=>user.id===change.id),{enabled:change.enabled,permissions:change.permissions});return reply({users});
    }
  }});await openUsers(b);await setAccess(b,'reviewer: Errors',true);const editor=accessEditor(b);await editor.fire('submit');
  assert.equal(accessEditor(b),editor);assert.equal(accessInput(b,'reviewer: Errors').checked,true);assert.equal(findButton(b,'Save access').disabled,true);
  assert.equal(findButton(b,'Verify passkey').hidden,false);assert.equal(b.calls.filter(call=>call.path==='/auth/session').length,1,'verification expiry does not discard drafts by refreshing permissions');
  await editor.fire('submit');assert.equal(saves,1,'both the button and submit handler block until verification');
  await findButton(b,'Verify passkey').fire('click');assert.equal(saves,1,'passkey verification does not retry the mutation automatically');
  assert.equal(accessEditor(b),editor);assert.equal(accessInput(b,'reviewer: Errors').checked,true);assert.equal(findButton(b,'Save access').disabled,false);
  assert.equal(findButton(b,'Verify passkey').hidden,true);assert.match(b.node('message').textContent,/press Save access when ready/);
  assert.deepEqual(b.calls.filter(call=>call.path.startsWith('/auth/')).map(call=>call.path),['/auth/session','/auth/authenticate-options','/auth/authenticate-verify','/auth/session']);
  await editor.fire('submit');assert.equal(saves,2);
  const requests=b.calls.filter(call=>call.path==='/api/admin/users/access');assert.deepEqual(requests[0].body,requests[1].body,'same drafts and expected originals survive verification');
  assert.match(b.node('view').text,/No unsaved changes/);
});

test('cancelled verification keeps drafts but another account or revoked session clears them',async()=>{
  for(const nextSession of [owner,member(['inventory']),{authenticated:false}]){
    let verified=false;
    const b=browser({session:owner,fetch:({path})=>{
      if(path==='/api/admin/users')return reply({users:userFixture()});
      if(path==='/api/admin/users/access')return reply({error:'Verify again.',code:'reauthentication_required'},403);
      if(path==='/auth/authenticate-verify'){verified=true;return reply({ok:true});}
      if(path==='/auth/session'&&verified)return reply(nextSession);
    }});await openUsers(b);await setAccess(b,'reviewer: Errors',true);await accessEditor(b).fire('submit');
    if(nextSession===owner){
      b.context.window.SimpleWebAuthnBrowser.startAuthentication=async()=>{const error=new Error('Cancelled');error.name='NotAllowedError';throw error;};
      await findButton(b,'Verify passkey').fire('click');assert.equal(accessInput(b,'reviewer: Errors').checked,true);assert.equal(findButton(b,'Save access').disabled,true);
      assert.match(b.node('message').textContent,/cancelled.*edits are still here/);assert.equal(b.calls.some(call=>call.path==='/auth/authenticate-verify'),false);
    }else{
      await findButton(b,'Verify passkey').fire('click');assert.equal(b.run('userAccessState'),null);assert.equal(accessEditor(b),undefined);
      assert.equal(b.run('owner'),false);assert.ok(b.tabs.find(tab=>tab.dataset.view==='users').hidden||b.node('workspace').hidden);
      assert.equal(b.calls.filter(call=>call.path==='/api/admin/users/access').length,1);
    }
  }
});

test('network and JSON failures after verification dispatch clear uncertain owner identity and private drafts',async()=>{
  for(const phase of ['verify','session'])for(const failure of ['network','json']){
    let verificationSent=false;
    const b=browser({session:owner,fetch:({path})=>{
      if(path==='/api/admin/users')return reply({users:userFixture()});
      if(path==='/api/admin/users/access')return reply({error:'Verify again.',code:'reauthentication_required'},403);
      if(path==='/auth/authenticate-verify')verificationSent=true;
      if((phase==='verify'&&path==='/auth/authenticate-verify')||(phase==='session'&&path==='/auth/session'&&verificationSent)){
        if(failure==='network')throw new TypeError('Synthetic transport failure');
        return {ok:true,status:200,json:async()=>{throw new SyntaxError('Synthetic invalid JSON');}};
      }
    }});await openUsers(b);await setAccess(b,'reviewer: Errors',true);await accessEditor(b).fire('submit');
    b.node('report-details').append(new Node('p','synthetic-private'));b.node('report-dialog').showModal();
    await findButton(b,'Verify passkey').fire('click');
    assert.equal(verificationSent,true,`${phase}/${failure}: verification request was dispatched`);
    assert.equal(b.run('owner'),false);assert.equal(b.run('signedIn'),false);assert.equal(b.run('userAccessState'),null);
    assert.equal(b.node('workspace').hidden,true);assert.equal(b.node('view').children.length,0);
    assert.equal(b.node('report-details').children.length,0);assert.equal(b.node('report-dialog').open,false);
    assert.match(b.node('message').textContent,/Private pages have been cleared; sign in again/);
    assert.equal(b.calls.filter(call=>call.path==='/api/admin/users/access').length,1,'uncertain verification never retries the save');
    assert.equal(b.calls.filter(call=>call.path==='/auth/session').length,phase==='session'?2:1,'failure occurs at the intended boundary');
  }
});

test('options transport and JSON failures before verification dispatch preserve unsaved drafts',async()=>{
  for(const failure of ['network','json']){
    const b=browser({session:owner,fetch:({path})=>{
      if(path==='/api/admin/users')return reply({users:userFixture()});
      if(path==='/api/admin/users/access')return reply({error:'Verify again.',code:'reauthentication_required'},403);
      if(path==='/auth/authenticate-options'){
        if(failure==='network')throw new TypeError('Synthetic transport failure');
        return {ok:true,status:200,json:async()=>{throw new SyntaxError('Synthetic invalid JSON');}};
      }
    }});await openUsers(b);await setAccess(b,'reviewer: Errors',true);const editor=accessEditor(b);await editor.fire('submit');
    await findButton(b,'Verify passkey').fire('click');
    assert.equal(accessEditor(b),editor);assert.equal(accessInput(b,'reviewer: Errors').checked,true);
    assert.equal(b.run('owner'),true);assert.equal(b.node('workspace').hidden,false);
    assert.equal(findButton(b,'Save access').disabled,true);assert.equal(findButton(b,'Verify passkey').hidden,false);
    assert.equal(b.calls.some(call=>call.path==='/auth/authenticate-verify'),false);
    assert.equal(b.calls.filter(call=>call.path==='/api/admin/users/access').length,1);
  }
});

test('saved access keeps only the same unexpired pending invitation and drops invalidated links',async()=>{
  const future=new Date(Date.now()+600000).toISOString(),later=new Date(Date.now()+900000).toISOString(),past=new Date(Date.now()-60000).toISOString();
  for(const [name,originalExpiry,changes,retained] of [
    ['active',future,{},true],['disabled pending',future,{enabled:false},true],
    ['disabled invalidated',future,{enabled:false,invitation_expires_at:null},false],
    ['consumed',future,{enrolled:true,invitation_expires_at:null},false],
    ['replaced',future,{invitation_expires_at:later},false],['expired',past,{},false],['removed',future,null,false],
  ]){
    const user={...userFixture()[0],enrolled:false,invitation_expires_at:originalExpiry};
    const url='https://example.invalid/#invite='+'h'.repeat(43),invitation={user:{...user},invitation_url:url,expires_at:originalExpiry};
    const b=browser({session:owner,fetch:({path,body})=>{
      if(path==='/api/admin/users')return reply({users:[user]});
      if(path==='/api/admin/users/access')return reply({users:changes===null?[]:[{...user,permissions:body.users[0].permissions,...changes}]});
    }});await openUsers(b);b.node('view').replaceChildren(b.context.renderUsers([user],invitation));
    await setAccess(b,'reviewer: Errors',true);await accessEditor(b).fire('submit');
    assert.equal(walk(b.node('view')).some(node=>node.readOnly&&node.value===url),retained,name);
    if(retained&&changes.enabled===false)assert.match(b.node('view').text,/Enable sign-in and save access before this invitation can be accepted/);
    if(!retained)assert.match(b.node('message').textContent,/displayed invitation is no longer active/);
  }
});
test('late user-creation responses cannot display invitation secrets after logout',async()=>{
  let finish;const pending=new Promise(resolve=>{finish=resolve;});const b=browser({session:owner,fetch:({path,body})=>path==='/api/admin/users'&&body?pending:undefined});
  await b.boot();b.run("currentView='users'");await b.context.load();const form=walk(b.node('view')).find(node=>node.tag==='form');walk(form).find(node=>node.tag==='input').value='reviewer';
  const saving=form.fire('submit');b.context.showLogin();finish(reply({invitation_url:'https://example.invalid/#invite='+'e'.repeat(43)}));await saving;
  assert.equal(b.node('view').children.length,0);assert.equal(b.node('workspace').hidden,true);assert.equal(b.copied.length,0);
});
