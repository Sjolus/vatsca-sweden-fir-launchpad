import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';
import { usageFixture, emptyUsageFixture } from './admin-usage-fixture.mjs';

class TestNode {
  constructor(tag, text = '') { this.tag = tag; this.textContent = text; this.children = []; this.dataset = {}; this.attributes = {}; }
  append(...children) { this.children.push(...children); }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener() {}
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
  assert.match(panel.text,/TopSky.*3\.0\.0 beta 19 Embedded version label File version: 0\.0\.0\.0 Current 10 \/ 30 · 33% 24 file copies/);
  assert.match(panel.text,/Unidentified DLL · b{12} Full fingerprint b{64} Version unknown No readable version; age is unknown Staged 2 \/ 30/);
  assert.match(panel.text,/Showing 2 of 102 binary groups; 100 additional groups are not displayed/);
  assert.doesNotMatch(panel.text,/24 \/ 30|latest release|up to date|outdated|\.dll|C:\\/);
  const legacy=walk(inventory).find(node=>node.tag==='details' && node.children[0]?.text==='Recognized component summaries · includes older clients');
  assert.ok(legacy && !legacy.open);assert.match(legacy.text,/four component checks predate detailed DLL reporting/);
  assert.ok(inventory.children.indexOf(panel)<inventory.children.indexOf(legacy),'all DLLs precede the legacy component summary');
  data.inventory.dll_inventory.rows[0].version_source='resource';
  assert.match(render.renderInventory(data).text,/Windows version resource/);
  assertFiniteChart(inventory);
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
