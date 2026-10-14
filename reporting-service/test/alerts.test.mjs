import test from 'node:test';
import assert from 'node:assert/strict';
import { createAlerts } from '../src/alerts.mjs';

test('disabled notifications never contact a service', async () => {
  const alerts = createAlerts({ fetchImpl: () => assert.fail('disabled') });
  alerts.observe({}); await alerts.close();
});
test('notification payloads are fixed, bounded and cooled down even on transport failure', async () => {
  let clock = 0; const calls = [];
  const alerts = createAlerts({ endpoint: 'https://notifications.example.test/private-topic', token: 'synthetic-secret',
    origin: 'https://launchpad.example.test', now: () => clock,
    fetchImpl: async (url, options) => { calls.push(options); throw new Error('synthetic request failure'); } });
  const snapshot = { health: { ok: false, physical_bytes: 1, max_db_bytes: 2 }, accepted: { errors: 0, reports: 0 },
    support_id: 'DO-NOT-SEND', private_text: 'DO-NOT-SEND' };
  alerts.observe(snapshot); await new Promise(resolve => setImmediate(resolve));
  for (let i = 0; i < 100; i++) alerts.observe(snapshot);
  assert.equal(calls.length, 1);
  clock += 30 * 60 * 1000;
  alerts.observe(snapshot); await alerts.close();
  assert.equal(calls.length, 2);
  assert.equal(calls[0].redirect, 'error');
  assert.equal(calls[0].headers.Click, 'https://launchpad.example.test/backoffice/');
  assert.ok(!JSON.stringify(calls).includes('DO-NOT-SEND'));
  assert.ok(!calls[0].body.includes('synthetic-secret'));
});
test('reports and error counts use fixed notifications without claiming a failure rate', async () => {
  const calls = [];
  const alerts = createAlerts({ endpoint: 'https://notifications.example.test/topic', token: 'synthetic-secret',
    origin: 'https://launchpad.example.test', fetchImpl: async (url, options) => { calls.push(options.body); return {}; } });
  const health = { ok: true, physical_bytes: 1, max_db_bytes: 100 };
  alerts.observe({ health, accepted: { errors: 0, reports: 0 } });
  alerts.observe({ health, accepted: { errors: 0, reports: 1 } });
  await new Promise(resolve => setImmediate(resolve));
  alerts.observe({ health, accepted: { errors: 20, reports: 1 } });
  await alerts.close();
  assert.equal(calls.length, 2);
  assert.ok(calls.every(text => !text.includes('%')));
});
test('notifications reject credential-bearing, insecure or tokenless destinations', () => {
  for (const endpoint of ['http://example.test/topic','https://user:pass@example.test/topic','https://example.test/topic?secret=yes'])
    assert.throws(() => createAlerts({ endpoint, token: 'secret', origin: 'https://example.test' }));
  assert.throws(() => createAlerts({ endpoint: 'https://example.test/topic', origin: 'https://example.test' }));
});
