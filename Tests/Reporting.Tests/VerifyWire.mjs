// Pipe Reporting.Tests --emit-wire into this checker. No HTTP or persistent store.
import assert from 'node:assert/strict';
import { validateBatch, validateReport } from '../../reporting-service/src/schema.mjs';

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
console.log(`PASS desktop/server wire compatibility: ${events} events covering every enum and ${fixture.reports.length} report categories.`);
