import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';
import { usageFixture, emptyUsageFixture } from './admin-usage-fixture.mjs';

class TestNode {
  constructor(tag, text = '') { this.tag = tag; this.textContent = text; this.children = []; this.dataset = {}; this.attributes = {}; this.listeners = new Map(); this.value = ''; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = []; this.textContent = ''; this.append(...children); }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, listener) { const list = this.listeners.get(name) ?? []; list.push(listener); this.listeners.set(name, list); }
  fire(name) { for (const listener of this.listeners.get(name) ?? []) listener({ target: this, preventDefault() {} }); }
  focus() { this.focused = true; }
  get text() { return [this.textContent, ...this.children.map(child => child.text ?? String(child))].join(' ').replace(/\s+/g, ' ').trim(); }
}
function renderer() {
  const document = { createElement: tag => new TestNode(tag), createElementNS: (_, tag) => new TestNode(tag), createTextNode: text => new TestNode('#text', text), createDocumentFragment: () => new TestNode('#fragment') };
  const context = createContext({ document, Node: TestNode, URLSearchParams });
  runInContext(readFileSync(new URL('../public/usage.js', import.meta.url), 'utf8'), context);
  runInContext(readFileSync(new URL('../public/app.js', import.meta.url), 'utf8').replace(/boot\(\);\s*$/, ''), context);
  return context;
}
const walk = node => [node, ...node.children.filter(child => child instanceof TestNode).flatMap(walk)];
function inventoryCard(node, heading) {
  const card = walk(node).filter(item => ['article', 'section'].includes(item.tag) &&
    walk(item).some(child => /^h[2-4]$/.test(child.tag) && child.text === heading)).at(-1);
  assert.ok(card, `The inventory has a card for ${heading}`);
  return card;
}
function assertFiniteChart(node) {
  for (const shape of walk(node).filter(item => item.tag === 'rect')) {
    for (const key of ['x', 'y', 'width', 'height']) {
      if (shape.attributes[key] !== undefined) assert.ok(Number.isFinite(Number(shape.attributes[key])), `Finite ${key} in chart`);
    }
  }
  assert.doesNotMatch(node.text, /NaN|Infinity|undefined/);
}
function assertCoverage(card, total, counts) {
  const chart = walk(card).find(node => node.tag === 'svg' && node.attributes.role === 'img' &&
    node.attributes['aria-label']?.includes('inventory installations:'));
  assert.ok(chart, 'Component coverage is available as a labelled chart');
  const width = Number(chart.attributes.viewBox.split(' ')[2]), rectangles = chart.children.filter(node => node.tag === 'rect');
  assert.equal(rectangles.length, 4, 'Coverage distinguishes all four observation states');
  let previous = 0;
  for (const [index, [label, count]] of counts.entries()) {
    assert.ok(chart.attributes['aria-label'].includes(`${label} ${count}`), `${label} has its observed count in the accessible chart label`);
    const fraction = Number(rectangles[index].attributes.width) / width;
    assert.ok(Math.abs(fraction - count / total) < 1e-9, `${label} occupies its share of the full inventory sample`);
    assert.ok(Math.abs(Number(rectangles[index].attributes.x) / width - previous) < 1e-9, 'Coverage segments are contiguous');
    previous += fraction;
  }
  assert.ok(Math.abs(previous - 1) < 1e-9, 'A complete inventory snapshot accounts for the whole sample');
  assertFiniteChart(card);
}
function assertVersionBar(card, version, count, total) {
  const chart = walk(card).find(node => node.tag === 'svg' && node.attributes.role === 'img' &&
    node.attributes['aria-label']?.includes(`, ${version}:`));
  assert.ok(chart, `Observed version ${version} has an accessible bar`);
  assert.ok(chart.attributes['aria-label'].includes(`${count} / ${total}`), 'Version labels retain the full inventory denominator');
  const width = Number(chart.attributes.viewBox.split(' ')[2]);
  const fractions = chart.children.filter(node => node.tag === 'rect').map(node => Number(node.attributes.width) / width);
  assert.ok(fractions.some(value => Math.abs(value - count / total) < 1e-9), 'Version fill represents its share of the whole sample');
  assert.ok(fractions.includes(1), 'The version track represents the whole sample');
}

test('task and navigation rendering separates requests, outcomes and missing results', () => {
  const render=renderer(),data=usageFixture(),screen=render.renderUsageDashboard(data);
  assert.match(screen.text,/Application start requests 148/);
  assert.match(screen.text,/What gets done/);assert.match(screen.text,/16 Completed 2 Partly completed/);
  assert.match(screen.text,/28 task starts from 19 installations · 25 have a matched result/);
  assert.match(screen.text,/3 starts have no matching result\. 2 results have no matching start\. 4 records lack correlation fields/);
  assert.match(screen.text,/It is not counted as a failure/);
  assert.match(screen.text,/vATIS · Configure Swedish profile/);assert.match(screen.text,/SAAMAN · Configure plugin profile/);
  assert.match(screen.text,/Where people go/);assert.match(screen.text,/Setup · Applications 24 19/);
  assert.match(screen.text,/App settings 8 7/);assert.match(screen.text,/Earlier setup-guide choices/);
  assertFiniteChart(screen);
  const empty=render.renderUsageDashboard(emptyUsageFixture());
  assert.match(empty.text,/No correlated task starts/);assert.match(empty.text,/No navigation observations yet/);
  assert.doesNotMatch(empty.text,/success rate|0%|NaN|Infinity/);
});

test('inventory freshness describes the independent snapshot sample without implying a session', () => {
  const render=renderer(),data=usageFixture(),screen=render.renderInventory(data);
  assert.match(screen.text,/21 Last 24 hours 9 1–7 days old 0 Over 7 days old/);
  assert.match(screen.text,/snapshots do not report whether EuroScope is running or connected/);
  delete data.inventory.freshness;
  assert.doesNotMatch(render.renderInventory(data).text,/Last 24 hours/,'older API responses do not invent age counts');
});

test('error rows use compact context and do not label every download as AeroNav', () => {
  const render=renderer(),screen=render.renderErrorGroups([
    {application:'saaman',operation:'download',stage:'download',error_code:'rate_limited',reason:'rate_limited',service:'github',http_status:403,launchpad_version:'2.6.0',affected_installations:2,count:9,first_seen:1,last_seen:2},
    {application:'gng',operation:'download',stage:'check',error_code:'access_denied',reason:'conflict',service:'aero_nav',http_status:403,launchpad_version:'2.6.0',affected_installations:1,count:1,first_seen:2,last_seen:2},
  ]);
  assert.equal(walk(screen).filter(node=>node.tag==='th').length,8);
  assert.equal((screen.text.match(/Rate limited/g)??[]).length,1,'identical problem and reason are shown once');
  assert.match(screen.text,/download GitHub HTTP 403/);assert.match(screen.text,/AeroNav HTTP 403/);
  assert.doesNotMatch(screen.text,/AeroNav download/);
  assert.match(screen.text,/Access denied Conflicting configuration/);
  assert.ok(walk(screen).some(node=>node.className==='error-context error-service'));
});
test('usage rendering distinguishes unavailable comparison and missing observations from zero', () => {
  const render = renderer(), data = emptyUsageFixture();
  data.cohort.returning_reason = 'retention';
  const text = render.renderUsageDashboard(data).text;
  assert.match(text, /Returning installations Unavailable/);
  assert.match(text, /preceding period falls outside raw-data retention/);
  assert.match(text, /Partial coverage/); assert.match(text, /No usage observations/);
  assert.doesNotMatch(text, /0%/);
});
test('inventory-only installations and actual plugin versions use the inventory denominator', () => {
  const render = renderer(), data = usageFixture(); data.cohort.active_installations = 0;
  const inventory = render.renderInventory(data), text = inventory.text;
  assert.match(text, /Inventory installations 30/); assert.match(text, /inventory-only installations/);
  assert.match(text, /TopSky/); assert.match(text, /2\.3\.4 14 \/ 30/);
  assert.match(text, /Version unknown 14 Not detected 15 Unavailable 1/);
  assert.doesNotMatch(text, /14 \/ 0/); assert.doesNotMatch(text, /latest release|current release/);
  const hero = walk(inventory).find(node => node.tag === 'section' && node.text.includes('Inventory installations'));
  assert.match(hero.text, /Latest snapshot · 2026-10-13 20:00 UTC/);
  assert.doesNotMatch(hero.text, /\d+%/, 'The sample headline does not aggregate coverage percentages across components');
  const groups = walk(inventory).filter(node => node.tag === 'section' &&
    walk(node).some(heading => heading.tag === 'h2' && /^(Applications & GNG|EuroScope plugins)$/.test(heading.text)));
  assert.equal(groups.length, 2);
  assert.deepEqual(walk(groups[0]).filter(node => node.tag === 'h3').map(node => node.text), ['EuroScope', 'Swedish GNG', 'TrackAudio', 'VACS', 'vATIS', 'VatEFS']);
  assert.deepEqual(walk(groups[1]).filter(node => node.tag === 'h3').map(node => node.text), ['TopSky', 'RDF', 'VatEFS plugin', 'AFV EuroScope Bridge']);
  const card = inventoryCard(inventory, 'TopSky');
  const detected = walk(card).find(node => node.attributes['aria-label'] === 'TopSky: detected in 26 of 30 inventory installations.');
  assert.ok(detected, 'Detected includes known and unknown versions');
  assert.equal(detected.attributes.role, 'group', 'The named detection count has an accessible grouping role');
  assertCoverage(card, 30, [['Version known', 18], ['Version unknown', 8], ['Not detected', 3], ['Unavailable', 1]]);
  assertVersionBar(card, '2.3.4', 14, 30); assertVersionBar(card, '2.3.3', 4, 30);
});
test('filters state cohort scope without treating source absence as a missing installation', () => {
  const render = renderer(), data = usageFixture(); data.filters = { application: 'trackaudio', launchpad_version: '2.2.0' };
  const text = render.renderUsageDashboard(data).text;
  assert.match(text, /Usage cohort: installations with TrackAudio activity/);
  assert.match(text, /Only observations from Launchpad 2\.2\.0/);
  assert.match(text, /No preference observation: 2/);
  data.inventory.components = data.inventory.components.filter(component => component.component === 'trackaudio');
  const inventory = render.renderInventory(data);
  assert.doesNotMatch(inventory.text, /Usage cohort:|TopSky|EuroScope plugins/);
  assert.match(inventory.text, /inventory denominator still includes every matching snapshot/);
  assert.match(inventory.text, /Only observations from Launchpad 2\.2\.0/);
  assert.ok(walk(inventory).some(node => node.text === '1 component'), 'A single filtered component uses the singular count');
  assert.doesNotMatch(inventory.text, /1 components/);
  const card = inventoryCard(inventory, 'TrackAudio');
  assert.match(card.text, /27 \/ 30 detected/);
  assertCoverage(card, 30, [['Version known', 27], ['Version unknown', 0], ['Not detected', 2], ['Unavailable', 1]]);
  assertVersionBar(card, '1.4.0', 23, 30);
});

test('empty inventory and a filter with no catalogue component never fabricate coverage', () => {
  const render = renderer(), data = emptyUsageFixture();
  data.inventory.components = usageFixture().inventory.components.map(component => ({ ...component, known: 0, unknown: 0, absent: 0, unavailable: 0, versions: [] }));
  const empty = render.renderInventory(data);
  assert.match(empty.text, /Inventory installations 0/); assert.match(empty.text, /No snapshot in this selection/);
  assert.match(empty.text, /No software or plugin observations for this selection/);
  assert.doesNotMatch(empty.text, /0%|NaN|Infinity/);
  assert.equal(walk(empty).filter(node => node.tag === 'svg').length, 0);
  data.inventory.snapshot_installations = 30; data.inventory.components = []; data.filters.application = 'vatiris';
  const filtered = render.renderInventory(data);
  assert.match(filtered.text, /Inventory installations 30/);
  assert.match(filtered.text, /selected application has no component in the fixed software inventory/);
  assert.equal(walk(filtered).filter(node => node.tag === 'svg').length, 0);
});

test('unknown versions, explicit absence and unavailable observations remain distinct', () => {
  const render = renderer(), data = usageFixture();
  for (const [state, description] of [['unknown', /DLL detected, but no readable version was reported\. Its age cannot be determined/], ['absent', /Not found in the checked locations/], ['unavailable', /Could not reliably inspect the configured locations\. Plugin presence is unknown/]]) {
    data.inventory.components = [{ component: 'vatefs_plugin', source: 'plugin_file', known: 0, unknown: 0, absent: 0, unavailable: 0, versions: [], [state]: 30 }];
    const card = inventoryCard(render.renderInventory(data), 'VatEFS plugin');
    assert.match(card.text, description);
    assert.match(card.text, state === 'unknown' ? /30 \/ 30 detected/ : /0 \/ 30 detected/);
    assertCoverage(card, 30, [['Version known', 0], ['Version unknown', state === 'unknown' ? 30 : 0], ['Not detected', state === 'absent' ? 30 : 0], ['Unavailable', state === 'unavailable' ? 30 : 0]]);
    assert.equal(walk(card).filter(node => node.tag === 'svg').length, 1, 'Missing version metadata does not create an invented version bar');
    assert.equal(walk(card).filter(node => node.className === 'inventory-version').length, 0, 'Unknown, absent and unavailable states never manufacture a DLL version');
    assert.doesNotMatch(card.text, /latest release|current release|up to date|outdated/);
  }
});

test('plugin cards distinguish observed DLL metadata from package release versions', () => {
  const render = renderer(), data = usageFixture();
  data.inventory.components = [
    { component: 'vatefs', source: 'registered_msi', known: 30, unknown: 0, absent: 0, unavailable: 0, versions: [{ version: '0.1.0', installations: 30 }] },
    { component: 'vatefs_plugin', source: 'plugin_file', known: 20, unknown: 10, absent: 0, unavailable: 0, versions: [{ version: '0.0.1.2', installations: 20 }] }
  ];
  const inventory = render.renderInventory(data), application = inventoryCard(inventory, 'VatEFS'), plugin = inventoryCard(inventory, 'VatEFS plugin');
  assert.match(inventory.text, /DLL version labels can differ from package releases/);
  assert.match(inventory.text, /Detection does not prove a plugin is enabled or loaded/);
  assert.match(inventory.text, /Version unknown means detected without a readable version; it does not establish age/);
  assert.match(plugin.text, /Plugin file/); assert.match(plugin.text, /Versions read from DLL/);
  assert.match(application.text, /Registered MSI/); assert.doesNotMatch(application.text, /Plugin file|Versions read from DLL/);
  assert.deepEqual(walk(plugin).filter(node => node.className === 'inventory-version').map(node => node.text), ['0.0.1.2']);
  assert.deepEqual(walk(application).filter(node => node.className === 'inventory-version').map(node => node.text), ['0.1.0']);
  assertVersionBar(plugin, '0.0.1.2', 20, 30);
  assert.doesNotMatch(plugin.text, /0\.1\.0|up to date|outdated|latest release|current release/);
});

const dllRow = (index, values = {}) => ({sha256:index.toString(16).padStart(64,'0'),product:'topsky',role:'current',version:'2.6-beta.4',file_version:'0.0.0.0',version_source:'registration',installations:2,copies:3,...values});
const dllSample = rows => ({complete_installations:10,partial_installations:3,unavailable_installations:2,not_shared_installations:15,
  shared_installations:15,unreadable_files:4,current_installations:12,staged_installations:2,total_groups:rows.length,omitted_groups:0,rows});
const plain = value => JSON.parse(JSON.stringify(value));
const hasClass = (node, name) => (node.className ?? '').split(/\s+/).includes(name);

test('DLL grouping preserves individual observations and orders products and locations predictably',()=>{
  const render=renderer(),rows=[
    dllRow(4,{product:'unknown',version:null,version_source:'unknown'}),
    dllRow(3,{role:'staged',installations:20,copies:27}),
    dllRow(2,{installations:4,copies:8}),
    dllRow(1,{product:'cdm',version:'2.27',installations:1}),
    dllRow(2,{version_source:'resource',installations:3,copies:5}),
    dllRow(5,{version:'2.6-beta.3',installations:4,copies:9}),
  ];
  const before=JSON.stringify(rows),groups=render.dllInventoryGroups(rows);
  assert.deepEqual(plain(groups.map(group=>group.product)),['cdm','topsky','unknown']);
  const topsky=groups.find(group=>group.product==='topsky');
  assert.deepEqual(plain(topsky.rows.map(row=>row.role)),['current','current','current','staged']);
  assert.deepEqual(plain(topsky.rows.map(row=>row.installations)),[4,4,3,20]);
  assert.equal(topsky.rows.length,4,'Identical fingerprints with distinct metadata remain separate observations');
  for(const row of groups.flatMap(group=>group.rows))assert.ok(rows.includes(row),'Grouping retains original observation objects without fabricating counts');
  assert.equal(JSON.stringify(rows),before,'Grouping does not mutate the source response');
  assert.deepEqual(plain(render.dllInventoryGroups([...rows].reverse())),plain(groups),'Input order cannot change grouping or tie ordering');
});

test('DLL search matches names, versions and exact fingerprints independently of location',()=>{
  const render=renderer(),rows=[dllRow(11),dllRow(12,{role:'staged',version:'2.6-beta.3'}),
    dllRow(13,{product:'arrival_load_balancer',version:'0.2.11T',file_version:'1.2.3.4'}),dllRow(14,{product:'unknown',version:null,file_version:null,version_source:'unknown'})];
  const matches=options=>render.dllInventoryGroups(rows,options).flatMap(group=>group.rows);
  for(const query of [' TOPSKY ','2.6 beta 4','2.6-beta.4','ARRIVAL LOAD BALANCER','arrival_load_balancer','1.2.3.4',rows[3].sha256.toUpperCase()]) {
    assert.ok(matches({query}).length>0,`Search finds ${query}`);
  }
  assert.equal(matches({query:'topsky'}).length,2);
  assert.equal(matches({query:'topsky',role:'current'}).length,1);
  assert.equal(matches({query:'topsky',role:'staged'})[0],rows[1]);
  assert.equal(matches({query:'2.6 beta 4',role:'staged'}).length,0,'Search and location both restrict results');
  assert.equal(matches({query:'not-a-reported-plugin'}).length,0);
  assert.equal(matches({role:'all'}).length,rows.length);
});

test('all-DLL view shows fingerprints, both version sources and locations without treating copies as installations',()=>{
  const render=renderer(),data=usageFixture();
  data.inventory.dll_inventory={complete_installations:10,partial_installations:3,unavailable_installations:2,not_shared_installations:15,
    shared_installations:15,unreadable_files:4,current_installations:12,staged_installations:2,total_groups:102,omitted_groups:100,rows:[
      {sha256:'a'.repeat(64),product:'topsky',role:'current',version:'3.0.0-beta.19',file_version:'0.0.0.0',version_source:'embedded',installations:10,copies:24},
      {sha256:'b'.repeat(64),product:'unknown',role:'staged',version:null,file_version:null,version_source:'unknown',installations:2,copies:5}
    ]};
  const inventory=render.renderInventory(data),panel=inventoryCard(inventory,'All DLLs found');
  assert.match(panel.text,/10 Complete scans 3 Partial scans 2 Unavailable 15 Not shared/);
  assert.match(panel.text,/15 \/ 30 shared detailed DLL observations/);
  assert.match(panel.text,/Not shared includes older clients and installations without this optional consent/);
  assert.match(panel.text,/Partial or unavailable scans cannot establish that an unlisted DLL is absent/);
  assert.match(panel.text,/Current and staged describe file locations, not whether a plugin is loaded/);
  const coverage=walk(panel).find(node=>hasClass(node,'dll-coverage'));
  assert.ok(coverage?.tag==='details' && !coverage.open,'Lengthy scan coverage starts collapsed');
  assert.match(coverage.children[0].text,/15 of 30 installations shared DLL details/);
  assert.ok(panel.children.some(node=>node.tag==='p' && /Partial or unavailable scans/.test(node.text)),'Incomplete coverage warning stays visible outside the disclosure');
  const builds=walk(panel).filter(node=>hasClass(node,'dll-build'));
  assert.equal(builds.length,2);assert.ok(builds.every(node=>node.tag==='details' && !node.open));
  const [known,unknown]=builds;
  assert.match(known.children[0].text,/3\.0\.0 beta 19 Current 10 \/ 30 · 33%/);
  assert.doesNotMatch(known.children[0].text,/Embedded version|File version|file copies|a{64}/,'Technical details stay out of the collapsed row');
  assert.match(known.text,/Embedded version label File version: 0\.0\.0\.0 24 file copies across 10 installations/);
  assert.match(unknown.children[0].text,/Version unknown b{12} Staged 2 \/ 30/);
  assert.match(unknown.text,/No readable version; age is unknown 5 file copies across 2 installations/);
  assert.deepEqual(builds.map(build=>walk(build).find(node=>node.tag==='code').text),['a'.repeat(64),'b'.repeat(64)],'Full fingerprints remain available in each native disclosure');
  assert.match(known.children[0].attributes['aria-label'],/TopSky, 3\.0\.0 beta 19, current, 10 of 30 installations/);
  assert.match(unknown.children[0].attributes['aria-label'],/Unidentified DLL/);
  assert.match(panel.text,/Showing 2 of 102 binary groups; 100 additional groups are not displayed/);
  assert.match(panel.text,/Search and location filters cover the displayed groups only/);
  assert.doesNotMatch(panel.text,/24 \/ 30|latest release|up to date|outdated|\.dll|C:\\/);
  const legacy=walk(inventory).find(node=>node.tag==='details' && node.children[0]?.text==='Recognized component summaries · includes older clients');
  assert.ok(legacy && !legacy.open);assert.match(legacy.text,/four component checks predate detailed DLL reporting/);
  assert.ok(inventory.children.indexOf(panel)<inventory.children.indexOf(legacy),'all DLLs precede the legacy component summary');
  data.inventory.dll_inventory.rows[0].version_source='resource';
  assert.match(render.renderInventory(data).text,/Windows version resource/);
  Object.assign(data.inventory.dll_inventory.rows[0],{product:'vatcan_bookings',version:'1.1.6-2022wb',file_version:'1.0.0.1',version_source:'registration'});
  assert.match(render.renderInventory(data).text,/VATCAN Bookings.*1\.1\.6-2022wb.*EuroScope plugin registration File version: 1\.0\.0\.1/);
  Object.assign(data.inventory.dll_inventory.rows[0],{product:'saaman',version:'1.3.37',file_version:null,version_source:'registration'});
  assert.match(render.renderInventory(data).text,/SAAMAN.*1\.3\.37.*EuroScope plugin registration/);
  assertFiniteChart(inventory);
});

test('DLL search and location controls refresh results, explain empty matches and reset without losing observations',()=>{
  const render=renderer(),rows=[dllRow(11),dllRow(12,{role:'staged',version:'2.6-beta.3'}),
    dllRow(13,{product:'cdm',version:'2.27'}),dllRow(14,{product:'unknown',version:null,version_source:'unknown'})];
  const panel=render.dllInventoryPanel(dllSample(rows),30),find=id=>walk(panel).find(node=>node.id===id);
  const search=find('dll-search'),location=find('dll-location'),result=find('dll-results'),reset=walk(panel).find(node=>node.tag==='button' && node.text==='Reset filters');
  const builds=()=>walk(panel).filter(node=>hasClass(node,'dll-build'));
  assert.ok(search && location && result && reset);
  assert.equal(result.attributes['aria-live'],'polite');assert.equal(result.attributes.role,'status');
  assert.ok(reset.disabled);assert.equal(builds().length,4);
  search.value='TOPSKY';search.fire('input');
  assert.equal(builds().length,2);assert.match(result.text,/2 of 4 binary entries · 1 group/);assert.equal(reset.disabled,false);
  location.value='staged';location.fire('change');
  assert.equal(builds().length,1);assert.match(builds()[0].children[0].text,/2\.6 beta 3.*Staged/);
  search.value='not-a-reported-plugin';search.fire('input');
  assert.equal(builds().length,0);assert.match(result.text,/0 of 4 binary entries/);
  assert.match(panel.text,/No DLLs match these filters/);assert.doesNotMatch(panel.text,/No DLLs installed|No plugins installed|uninstalled/);
  assert.equal(search.value,'not-a-reported-plugin','Filtering retains the input instead of replacing the toolbar');
  reset.fire('click');
  assert.equal(search.value,'');assert.equal(location.value,'all');assert.equal(builds().length,4);assert.ok(reset.disabled);assert.ok(search.focused);
  assert.doesNotMatch(panel.text,/No DLLs match these filters/);assertFiniteChart(panel);
});

test('missing or unavailable detailed DLL data stays distinct from absence and can render without fixed components',()=>{
  const render=renderer(),data=usageFixture();
  data.inventory.dll_inventory={complete_installations:0,partial_installations:0,unavailable_installations:0,not_shared_installations:30,
    shared_installations:0,unreadable_files:0,current_installations:0,staged_installations:0,total_groups:0,omitted_groups:0,rows:[]};
  let panel=inventoryCard(render.renderInventory(data),'All DLLs found');
  assert.match(panel.text,/No detailed DLL observations were shared/);
  assert.doesNotMatch(panel.text,/No plugins installed|No DLLs installed|uninstalled|Version unknown/);
  data.inventory.components=[];data.filters.application='vatiris';
  Object.assign(data.inventory.dll_inventory,{not_shared_installations:0,unavailable_installations:30,shared_installations:30,product_filter:'vatiris'});
  panel=inventoryCard(render.renderInventory(data),'All DLLs found');
  assert.match(panel.text,/Showing DLLs recognized as VATIRIS; unidentified DLLs remain visible under All applications/);
  assert.match(panel.text,/No matching DLL was reported.*partial or unavailable scans do not prove absence/);
  assert.doesNotMatch(panel.text,/selected application has no component/);
  data.filters.application='saaman'; data.inventory.dll_inventory.product_filter='saaman';
  assert.match(render.renderInventory(data).text,/Showing DLLs recognized as SAAMAN; unidentified DLLs remain visible under All applications/);
  assertFiniteChart(panel);
});

test('long version distributions keep three visible groups and disclose every remaining bounded group', () => {
  const render = renderer(), data = usageFixture();
  data.inventory.snapshot_installations = 120;
  const versions = [{ version: '9.0.0', installations: 20 }, { version: '8.0.0', installations: 15 }, { version: '7.0.0', installations: 10 }, { version: 'other', installations: 9 },
    ...Array.from({ length: 46 }, (_, index) => ({ version: `1.0.${index}`, installations: 1 }))];
  data.inventory.components = [{ component: 'topsky', source: 'plugin_file', known: 100, unknown: 10, absent: 5, unavailable: 5, versions }];
  const card = inventoryCard(render.renderInventory(data), 'TopSky');
  const details = walk(card).filter(node => node.tag === 'details'); assert.equal(details.length, 1);
  assert.equal(details[0].children[0].tag, 'summary'); assert.match(details[0].children[0].text, /Show 47 more version groups/);
  assert.ok(!details[0].open && !Object.hasOwn(details[0].attributes, 'open'), 'Additional versions use a closed native disclosure');
  const collapsed = new Set(walk(details[0]));
  const visible = walk(card).filter(node => node.tag === 'li' && !collapsed.has(node));
  assert.equal(visible.length, 3); assert.match(visible[0].text, /^9\.0\.0 20 \/ 120/); assert.match(visible[2].text, /^7\.0\.0 10 \/ 120/);
  assert.equal(walk(details[0]).filter(node => node.tag === 'li').length, 47);
  assert.equal(walk(card).filter(node => node.tag === 'li').length, 50);
  assert.match(details[0].text, /Other observed versions 9 \/ 120/); assert.match(details[0].text, /1\.0\.45 1 \/ 120/);
  for (const row of versions) assertVersionBar(card, row.version === 'other' ? 'Other observed versions' : row.version, row.installations, 120);
  assertCoverage(card, 120, [['Version known', 100], ['Version unknown', 10], ['Not detected', 5], ['Unavailable', 5]]);
  assert.doesNotMatch(card.text, /latest release|current release|up to date|outdated/);
});

test('usage and inventory identify today as partial using the server observation time', () => {
  const render=renderer(),data=usageFixture();
  const dashboard=render.renderUsageDashboard(data),inventory=render.renderInventory(data);
  for(const node of [dashboard,inventory]) {
    assert.match(node.text,/2026-10-08 – 2026-10-14 · 7 UTC days, including today/);
    assert.match(node.text,/Today is partial; updated 14:30 UTC/);
    assert.doesNotMatch(node.text,/Today is excluded|full UTC days|Partial coverage/);
  }
  assert.match(dashboard.text,/Today\*/);assert.match(dashboard.text,/2026-10-14 · Today, partial/);
  assert.match(dashboard.text,/Also seen in the preceding 7 complete UTC days/);
  const chart=walk(dashboard).find(node=>node.attributes.role==='img');
  assert.match(chart.attributes['aria-label'],/2026-10-14 \(today, partial\): 14 active installations/);
  data.window.coverage_complete=false;
  assert.match(render.renderUsageDashboard(data).text,/Today is partial.*Partial coverage/);
  data.window.days=30;data.cohort.returning_available=false;data.cohort.returning_reason='retention';
  const month=render.renderUsageDashboard(data).text;
  assert.match(month,/30 UTC days, including today/);assert.match(month,/Returning installations Unavailable/);
});
