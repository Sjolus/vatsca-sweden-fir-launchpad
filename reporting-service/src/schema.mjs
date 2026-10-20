export class IntakeError extends Error {
  constructor(status, code, retryAfter = undefined) {
    super(code); this.status = status; this.code = code; this.retryAfter = retryAfter;
  }
}
export const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
export const VERSION = /^[0-9]{1,6}(?:\.[0-9]{1,6}){1,3}(?:-(?:alpha|beta|rc|preview|dev)(?:\.[0-9]{1,6}){0,2})?$/;
// Public VATCAN/ALB registration formats, not general free-form build suffixes.
const DLL_VENDOR_VERSION = /^[0-9]{1,6}(?:\.[0-9]{1,6}){1,3}(?:-(?:19|20)[0-9]{2}wb|T)$/;
export const FEATURES = Object.freeze(['check_updates','controller_profile_review','gng_package_review','gng_cleanup_review','gng_restore_review','setup_guide','install_or_update','adopt','font_check']);
export const INVENTORY = Object.freeze({euroscope:'configured_app',gng:'package_metadata',trackaudio:'configured_app',vacs:'configured_app',vatis:'configured_app',vatefs:'registered_msi',topsky:'plugin_file',rdf:'plugin_file',vatefs_plugin:'plugin_file',afv_euroscope_bridge:'plugin_file'});
export const INVENTORY_STATUSES = Object.freeze(['known','unknown','absent','unavailable']);
export const DLL_PRODUCTS = Object.freeze(['unknown','topsky','rdf','vatefs','afv_euroscope_bridge','arrival_load_balancer','cdm','ground_radar','maestro','vatiris','vch','vatcan_bookings','saaman']);
export const ENUMS = Object.freeze({
  application: ['launchpad','euroscope','gng','trackaudio','vacs','vatis','vatefs','vatiris','maintenance','saaman'],
  operation: ['install','update','repair','replace','remove','cleanup','restore','restore_cleanup','prerequisite','setup','adopt','check','save','download','launch'],
  outcome: ['completed','cancelled','blocked','failed','unknown'],
  stage: ['start','check','prepare','download','verify','apply','finish','welcome','applications','gng','profile','fonts','preferences'],
  error_code: ['unknown','download_timeout','network_unavailable','package_invalid','installation_failed','installation_identity_mismatch','settings_save_failed','browser_process_failed','operation_stopped','unexpected_error'],
  duration_bucket: ['under_1s','under_10s','under_1m','under_5m','over_5m'],
  source: ['cache','download','import','discovery','browse','compact','expanded','standard','larger','largest','existing','fresh','preferences'],
});
const USAGE = ['session_started','setup_step','operation_started','operation_finished','gng_package_obtained','preference_changed','application_opened','feature_used','preferences_snapshot'];
const ERRORS = ['operation_failed','unhandled_error'];
const REQUIRED = ['event_id','support_id','session_id','occurred_at','launchpad_version','event_name'];
const OPTIONAL = ['operation_id',...Object.keys(ENUMS),'package_version','previous_version','restart_required','recovery_available'];
const own = (o,k) => Object.hasOwn(o,k);
function valid(condition) { if (!condition) throw new IntakeError(400,'invalid_request'); }
function object(value) { valid(value !== null && typeof value === 'object' && !Array.isArray(value)); }
function keys(value, required, optional = []) {
  object(value); valid(required.every(k => own(value,k)) && Object.keys(value).every(k => required.includes(k) || optional.includes(k)));
}
export function uuid(value) { valid(typeof value === 'string' && UUID.test(value)); return value.toLowerCase(); }
function version(value) { valid(typeof value === 'string' && value.length <= 40 && VERSION.test(value)); return value; }
function dllVersion(value) { valid(typeof value === 'string' && value.length <= 40 && (VERSION.test(value) || DLL_VENDOR_VERSION.test(value))); return value; }
function enumeration(key,value) { valid(typeof value === 'string' && ENUMS[key].includes(value)); return value; }
function timestamp(value,now,allowExpired = false) {
  valid(typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value));
  const millis = Date.parse(value);
  valid(Number.isFinite(millis) && (allowExpired || (millis >= now - 7 * 86400000 && millis <= now + 300000)));
  const normalized = new Date(millis).toISOString();
  // Date.parse normalises invalid dates such as February 30; those are not timestamps.
  valid(normalized.slice(0,19) === value.slice(0,19));
  return normalized;
}
export function validateEvent(value, stream, now = Date.now(),allowExpired = false) {
  if(stream==='inventory')return validateInventory(value,now);
  const extra=value?.event_name==='feature_used'?['feature']:value?.event_name==='preferences_snapshot'?['compact_layout','text_scale','always_on_top','theme']:[];
  keys(value,REQUIRED,[...OPTIONAL,...extra]);
  valid(stream === 'usage' || stream === 'errors' || stream === 'context');
  valid((stream === 'usage' ? USAGE : stream === 'errors' ? ERRORS : [...USAGE,...ERRORS]).includes(value.event_name));
  const result = {
    event_id: uuid(value.event_id), support_id: uuid(value.support_id), session_id: uuid(value.session_id),
    occurred_at: timestamp(value.occurred_at,now,allowExpired), launchpad_version: version(value.launchpad_version), event_name: value.event_name,
  };
  if (own(value,'operation_id')) result.operation_id = uuid(value.operation_id);
  for (const key of Object.keys(ENUMS)) if (own(value,key)) result[key] = enumeration(key,value[key]);
  for (const key of ['package_version','previous_version']) if (own(value,key)) result[key] = version(value[key]);
  for (const key of ['restart_required','recovery_available']) if (own(value,key)) { valid(typeof value[key] === 'boolean'); result[key] = value[key]; }
  if(value.event_name==='application_opened')valid(['euroscope','trackaudio','vacs','vatis','vatefs','vatiris'].includes(value.application));
  if(value.event_name==='feature_used') {valid(FEATURES.includes(value.feature));result.feature=value.feature;}
  if(value.event_name==='preferences_snapshot') {
    valid(typeof value.compact_layout==='boolean' && typeof value.always_on_top==='boolean' && ['standard','larger','largest'].includes(value.text_scale));
    result.compact_layout=value.compact_layout;result.text_scale=value.text_scale;result.always_on_top=value.always_on_top;
    if(own(value,'theme')){valid(['dark','light'].includes(value.theme));result.theme=value.theme;}
  }
  valid(Buffer.byteLength(JSON.stringify(value)) <= (stream === 'usage' ? 2048 : 8192));
  return result;
}
function validateInventory(value,now) {
  keys(value,[...REQUIRED,'items'],['dll_inventory']);
  valid(value.event_name==='inventory_snapshot' && Array.isArray(value.items) && value.items.length===Object.keys(INVENTORY).length);
  const items=new Map();
  for(const item of value.items) {
    keys(item,['component','status','source'],['version']);
    valid(own(INVENTORY,item.component) && !items.has(item.component) && INVENTORY[item.component]===item.source && INVENTORY_STATUSES.includes(item.status));
    valid(item.status==='known'?own(item,'version'):!own(item,'version'));
    items.set(item.component,{component:item.component,status:item.status,source:item.source,...(item.status==='known'?{version:version(item.version)}:{})});
  }
  valid(Buffer.byteLength(JSON.stringify(value))<=8192);
  return {event_id:uuid(value.event_id),support_id:uuid(value.support_id),session_id:uuid(value.session_id),occurred_at:timestamp(value.occurred_at,now),
    launchpad_version:version(value.launchpad_version),event_name:'inventory_snapshot',items:Object.keys(INVENTORY).map(key=>items.get(key)),
    ...(own(value,'dll_inventory')?{dll_inventory:validateDllInventory(value.dll_inventory)}:{})};
}
function validateDllInventory(value) {
  keys(value,['status','entries','unreadable_files']);
  valid(['complete','partial','unavailable'].includes(value.status) && Array.isArray(value.entries) && value.entries.length<=64);
  valid(Number.isInteger(value.unreadable_files) && value.unreadable_files>=0 && value.unreadable_files<=4096);
  valid(value.status!=='complete' || value.unreadable_files===0);
  valid(value.status!=='unavailable' || value.entries.length===0);
  valid(Buffer.byteLength(JSON.stringify(value))<=6144);
  const identities=new Set();
  const entries=value.entries.map(entry=>{
    keys(entry,['sha256','product','role','copies','version_source'],['version','file_version']);
    valid(typeof entry.sha256==='string' && /^[0-9a-f]{64}$/.test(entry.sha256));
    valid(DLL_PRODUCTS.includes(entry.product) && ['current','staged'].includes(entry.role));
    valid(Number.isInteger(entry.copies) && entry.copies>=1 && entry.copies<=4096);
    valid(['unknown','resource','embedded','registration'].includes(entry.version_source));
    valid(entry.version_source==='unknown'?!own(entry,'version'):own(entry,'version'));
    const identity=entry.sha256+entry.role;valid(!identities.has(identity));identities.add(identity);
    return {sha256:entry.sha256,product:entry.product,role:entry.role,copies:entry.copies,
      ...(own(entry,'version')?{version:dllVersion(entry.version)}:{}),...(own(entry,'file_version')?{file_version:version(entry.file_version)}:{}),version_source:entry.version_source};
  });
  return {status:value.status,entries,unreadable_files:value.unreadable_files};
}
export function validateBatch(value,stream,now = Date.now()) {
  keys(value,['schema_version','events']);
  valid(value.schema_version === 1 && Array.isArray(value.events) && value.events.length > 0 && value.events.length <= 25);
  valid(Buffer.byteLength(JSON.stringify(value)) <= 65536);
  return value.events.map(event => validateEvent(event,stream,now));
}
export function validateReport(value,now = Date.now(),allowExpiredContext = false) {
  keys(value,['schema_version','report_id','support_id','launchpad_version','application','category'],['context']);
  valid(value.schema_version === 1 && ['setup','installation','update','gng','launch','appearance','other'].includes(value.category));
  const result = {schema_version:1,report_id:uuid(value.report_id),support_id:uuid(value.support_id),
    launchpad_version:version(value.launchpad_version),application:enumeration('application',value.application),category:value.category};
  if (own(value,'context')) {
    valid(Array.isArray(value.context) && value.context.length <= 20);
    result.context = value.context.map(event => validateEvent(event,'context',now,allowExpiredContext));
    valid(result.context.every(event => event.support_id === result.support_id));
  }
  valid(Buffer.byteLength(JSON.stringify(value)) <= 16384);
  return result;
}
