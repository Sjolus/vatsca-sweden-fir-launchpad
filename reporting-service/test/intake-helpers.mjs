import { mkdtempSync,rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { createStore } from '../src/store.mjs';
import {INVENTORY} from '../src/schema.mjs';
export const CLOCK = Date.parse('2026-10-12T12:00:00Z');
export const SUPPORT = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export function event(overrides={}) {return {event_id:randomUUID(),support_id:SUPPORT,session_id:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',occurred_at:new Date(CLOCK).toISOString(),launchpad_version:'2.2.0',event_name:'operation_finished',application:'gng',operation:'update',outcome:'completed',...overrides};}
export function report(overrides={}) {return {schema_version:1,report_id:randomUUID(),support_id:SUPPORT,launchpad_version:'2.2.0',application:'gng',category:'update',...overrides};}
export const batch=(...events)=>({schema_version:1,events});
export function inventory(overrides={}) {
  const e=event();
  return {event_id:e.event_id,support_id:e.support_id,session_id:e.session_id,occurred_at:e.occurred_at,launchpad_version:e.launchpad_version,event_name:'inventory_snapshot',
    items:Object.entries(INVENTORY).map(([component,source])=>({component,source,status:'absent'})),...overrides};
}
export function fixture(t,options={}) {
  const directory=mkdtempSync(join(tmpdir(),'launchpad-reporting-synthetic-'));
  let clock=options.initialClock ?? CLOCK;
  const path=join(directory,'reports.sqlite');
  const stores=[];
  const open=(extra={})=>{const store=createStore({path,now:()=>clock,freeSpace:()=>1024*1024*1024,...options,...extra});stores.push(store);return store;};
  t.after(()=>{for(const s of stores)s.close();rmSync(directory,{recursive:true,force:true});});
  const store=open();
  return {directory,path,store,open,now:()=>clock,advance:millis=>clock+=millis};
}
