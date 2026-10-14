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
  const text = render.renderInventory(data).text;
  assert.match(text, /Inventory installations 30/); assert.match(text, /inventory-only installations/);
  assert.match(text, /TopSky/); assert.match(text, /2\.3\.4 14 \/ 30/);
  assert.match(text, /Present · version unknown 14 Absent 15 Unavailable 1/);
  assert.doesNotMatch(text, /14 \/ 0/); assert.doesNotMatch(text, /latest release|current release/);
});
test('filters state cohort scope without treating source absence as a missing installation', () => {
  const render = renderer(), data = usageFixture(); data.filters = { application: 'trackaudio', launchpad_version: '2.2.0' };
  const text = render.renderUsageDashboard(data).text;
  assert.match(text, /Usage cohort: installations with TrackAudio activity/);
  assert.match(text, /Only observations from Launchpad 2\.2\.0/);
  assert.match(text, /No preference observation: 2/);
  const inventory = render.renderInventory(data).text;
  assert.doesNotMatch(inventory, /Usage cohort:/);
  assert.match(inventory, /inventory denominator still includes every matching snapshot/);
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
  const walk=node=>[node,...node.children.filter(child=>child instanceof TestNode).flatMap(walk)];
  const chart=walk(dashboard).find(node=>node.attributes.role==='img');
  assert.match(chart.attributes['aria-label'],/2026-10-14 \(today, partial\): 14 active installations/);
  data.window.coverage_complete=false;
  assert.match(render.renderUsageDashboard(data).text,/Today is partial.*Partial coverage/);
  data.window.days=30;data.cohort.returning_available=false;data.cohort.returning_reason='retention';
  const month=render.renderUsageDashboard(data).text;
  assert.match(month,/30 UTC days, including today/);assert.match(month,/Returning installations Unavailable/);
});
