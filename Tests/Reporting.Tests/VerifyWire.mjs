// Pipe Reporting.Tests --emit-wire into this checker. No HTTP or persistent store.
import assert from 'node:assert/strict';
import { validateBatch, validateReport, ENUMS, DLL_PRODUCTS, VIEWS } from '../../reporting-service/src/schema.mjs';

let input = '';
for await (const chunk of process.stdin) input += chunk.toString('utf8');
const fixture = JSON.parse(input), now = Date.now();
let events = 0;
for (const [key, stream] of [['usage_batches', 'usage'], ['error_batches', 'errors'], ['inventory_batches', 'inventory']]) {
  assert.ok(fixture[key].length > 0);
  for (const batch of fixture[key]) events += validateBatch(batch, stream, now).length;
}
assert.equal(fixture.reports.length, 7);
for (const report of fixture.reports) validateReport(report, now);
const applications = fixture.usage_batches.flatMap(batch => batch.events).map(event => event.application).filter(Boolean);
const products = fixture.inventory_batches.flatMap(batch => batch.events).flatMap(event => event.dll_inventory?.entries ?? []).map(entry => entry.product);
assert.deepEqual([...new Set(applications)].sort(), [...ENUMS.application].sort());
assert.deepEqual([...new Set(products)].sort(), [...DLL_PRODUCTS].sort());
const transmitted = [...fixture.usage_batches, ...fixture.error_batches].flatMap(batch => batch.events);
for (const [key, values] of Object.entries(ENUMS)) {
  const observed = transmitted.map(event => event[key]).filter(value => value !== undefined);
  assert.deepEqual([...new Set(observed)].sort(), [...values].sort(), `All ${key} values cross the client/server boundary`);
}
assert.deepEqual([...new Set(transmitted.filter(event => event.event_name === 'view_opened').map(event => event.view))].sort(), [...VIEWS].sort());
assert.ok(fixture.reports.some(report => report.application === 'saaman'));
console.log(`PASS desktop/server wire compatibility: ${events} events covering every enum and ${fixture.reports.length} report categories.`);
