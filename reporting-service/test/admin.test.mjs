import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createStore } from '../src/store.mjs';
import { createServer } from '../src/server.mjs';
import { readConfig } from '../src/config.mjs';
import { prepareEnrollment, secret, SESSION_COOKIE, FLOW_COOKIE } from '../src/auth.mjs';
import { authenticator } from './auth-fixture.mjs';
const origin = 'https://launchpad.example';
async function fixture(t, limits = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'launchpad-admin-'));
  const config = { ...readConfig({ PUBLIC_ORIGIN: origin }), dataPath: join(directory, 'synthetic.sqlite'), privacyOperator: 'Synthetic maintainer', privacyContact: 'Synthetic support contact' };
  config.limits = { ...config.limits, ...limits };
  const store = createStore({ path: config.dataPath, freeSpace: () => 10 ** 12 }); let clock = Date.now();
  const { server, gate } = createServer({ store, config, now: () => clock });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); store.close(); rmSync(directory, { recursive: true, force: true }); });
  const base = `http://127.0.0.1:${server.address().port}`;
  const request = (path, body, cookie, other = {}) => fetch(base + path, { method: body === undefined ? 'GET' : 'POST', headers: { ...(body === undefined ? {} : { 'Content-Type': 'application/json', Origin: origin }), ...(cookie ? { Cookie: cookie } : {}), ...other }, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual' });
  const code = secret(); prepareEnrollment(store, { token: code, now: clock });
  return { store, config, gate, request, code, advance: value => { clock += value; }, now: () => clock };
}
async function enroll(f) {
  const device = authenticator(), optionsResponse = await f.request('/auth/register-options', { enrollment_code: f.code });
  assert.equal(optionsResponse.status, 200); const cookie = optionsResponse.headers.getSetCookie()[0].split(';')[0];
  const options = await optionsResponse.json();
  const response = await f.request('/auth/register-verify', { response: device.registration(options, origin) }, cookie);
  assert.equal(response.status, 200); return response.headers.getSetCookie().find(value => value.startsWith(SESSION_COOKIE + '=')).split(';')[0];
}
function seedReport(f) {
  const support_id = randomUUID();
  const result = f.store.admitReport({ schema_version: 1, report_id: randomUUID(), support_id, launchpad_version: '2.2.0', application: 'gng', category: 'update' });
  return { ...result, support_id };
}

test('public pages disclose no records; backoffice data requires real session cookies', async t => {
  const f = await fixture(t), report = seedReport(f);
  for (const path of ['/api/admin/reports', '/api/admin/usage', '/api/admin/overview', '/api/admin/errors', '/api/admin/health', `/api/admin/support/${report.support_id}`, `/api/admin/reports/${report.reference}`]) {
    const response = await f.request(path, undefined, undefined, { 'x-owner': 'owner' });
    assert.equal(response.status, 401); assert.doesNotMatch(await response.text(), new RegExp(report.support_id));
  }
  const page = await f.request('/backoffice/'); assert.equal(page.status, 200); assert.match(page.headers.get('content-security-policy'), /frame-ancestors 'none'/); assert.doesNotMatch(await page.text(), new RegExp(report.support_id));
  const bundle = await f.request('/backoffice/passkey.js'); assert.equal(bundle.status, 200); assert.match(await bundle.text(), /SimpleWebAuthnBrowser/);
  const usageScript = await f.request('/backoffice/usage.js'); assert.equal(usageScript.status, 200); assert.match(await usageScript.text(), /renderUsageDashboard/);
  const privacy = await f.request('/privacy'); assert.equal(privacy.status, 200); assert.match(await privacy.text(), /pseudonymous/);
  const info = await f.request('/privacy/info'); assert.equal((await info.json()).operator, 'Synthetic maintainer');
  assert.equal((await f.request('/backoffice/../../package.json')).status, 404);
});

test('usage analytics default to a bounded UTC window and reject ambiguous filters', async t => {
  const f = await fixture(t), session = await enroll(f);
  const response = await f.request('/api/admin/usage', undefined, session); assert.equal(response.status, 200);
  const data = await response.json(); assert.equal(data.window.days, 7); assert.equal(data.window.timezone, 'UTC');
  assert.equal(data.cohort.active_installations, 0); assert.equal(data.inventory.snapshot_installations, 0);
  assert.equal(data.window.complete_days, false); assert.equal(data.daily.at(-1).partial, true);
  assert.match(response.headers.get('cache-control'), /no-store/);
  for (const filter of ['days=365', 'days=', 'days=7&days=30', 'application=unrecognized', 'launchpad_version=private-path', 'support_id=anything']) {
    assert.equal((await f.request('/api/admin/usage?' + filter, undefined, session)).status, 400, filter);
  }
  f.store.admitBatch('usage', {schema_version:1,events:[{event_id:randomUUID(),support_id:randomUUID(),session_id:randomUUID(),occurred_at:new Date(f.now()).toISOString(),launchpad_version:'2.2.0',event_name:'application_opened',application:'euroscope'}]});
  const filtered = await f.request('/api/admin/usage?days=30&application=euroscope&launchpad_version=2.2.0', undefined, session);
  assert.equal(filtered.status, 200); const selection = await filtered.json();
  assert.equal(selection.window.days, 30); assert.equal(selection.filters.application, 'euroscope'); assert.equal(selection.filters.launchpad_version, '2.2.0');
  assert.equal(selection.cohort.active_installations, 1); assert.equal(selection.cohort.app_launches, 1);
  assert.equal(selection.cohort.returning_available, false);
});

test('HTTP passkey flow binds cookies, uses private responses and supports logout', async t => {
  const f = await fixture(t), session = await enroll(f);
  assert.match(session, new RegExp('^' + SESSION_COOKIE));
  const state = await f.request('/auth/session', undefined, session); assert.equal((await state.json()).signed_in, true);
  const auth = await f.request('/api/admin/overview', undefined, session); assert.equal(auth.status, 200); assert.match(auth.headers.get('cache-control'), /no-store/);
  const duplicate = await f.request('/api/admin/reports', undefined, `${session}; ${session}`); assert.equal(duplicate.status, 401);
  const foreign = await f.request('/auth/logout', {}, session, { Origin: 'https://attacker.example' }); assert.equal(foreign.status, 403);
  const logout = await f.request('/auth/logout', {}, session); assert.equal(logout.status, 200); assert.equal((await f.request('/api/admin/reports', undefined, session)).status, 401);
});

test('verified dashboard refreshes do not spend public capacity or fail when it is exhausted', async t => {
  for (const limits of [{ sourceRequestsPerMinute: 3 }, { globalRequestsPerMinute: 3 }]) {
    const f = await fixture(t, limits), session = await enroll(f), seeded = seedReport(f);
    const paths = ['/', '/backoffice', '/backoffice/', '/backoffice/app.js', '/backoffice/usage.js',
      '/backoffice/style.css', '/backoffice/logo.png', '/backoffice/passkey.js', '/privacy', '/privacy/', '/privacy/info',
      '/auth/session', '/api/admin/overview', '/api/admin/usage?days=7', '/api/admin/health', '/api/admin/errors',
      '/api/admin/reports', `/api/admin/reports/${seeded.reference}`, `/api/admin/support/${seeded.support_id}`];
    async function refresh() {
      for (const path of paths) {
        const response = await f.request(path, undefined, session);
        assert.equal(response.status, path === '/' ? 302 : 200, path);
        await response.arrayBuffer();
      }
    }
    const initial = f.gate.stats();
    for (let attempt = 0; attempt < 7; attempt++) await refresh();
    assert.deepEqual(f.gate.stats(), initial, 'Authenticated refreshes do not charge public request, source, byte or concurrency budgets');
    assert.equal((await f.request('/backoffice/')).status, 200);
    const limited = await f.request('/backoffice/'); assert.equal(limited.status, 429); assert.equal(limited.headers.get('retry-after'), '60');
    const exhausted = f.gate.stats();
    await refresh();
    assert.deepEqual(f.gate.stats(), exhausted, 'Already-exhausted public limits cannot block the verified dashboard');

    const statusPath = `/api/admin/reports/${seeded.reference}/status`;
    assert.equal((await f.request(statusPath, { status: 'fixed' }, session)).status, 200);
    assert.equal((await f.request(statusPath, { status: 'closed' }, session, { Origin: 'https://attacker.example' })).status, 403);
    assert.equal((await f.request(statusPath, { status: 'x'.repeat(33000) }, session)).status, 413);
    assert.deepEqual(f.gate.stats(), exhausted, 'Authenticated actions retain body/origin checks without consuming public budgets');

    for (const path of ['/api/v1/usage/batches', '/api/v1/inventory/batches', '/api/v1/errors/batches', '/api/v1/reports',
      '/auth/register-options', '/auth/register-verify', '/auth/authenticate-options', '/auth/authenticate-verify']) {
      assert.equal((await f.request(path, {}, session)).status, 429, `${path} remains limited even with a real session`);
    }
    for (const path of ['/api/admin/not-a-route', '/api/admin/support/invalid', '/backoffice/unknown.js']) {
      assert.equal((await f.request(path, undefined, session)).status, 429, 'Only known dashboard paths are exempt');
    }
    for (const cookie of [`${SESSION_COOKIE}=${secret()}`, `${session}; ${session}`]) {
      assert.equal((await f.request('/backoffice/', undefined, cookie)).status, 429, 'Forged or duplicate cookies do not bypass limits');
    }
    assert.equal((await f.request('/auth/logout', {}, session)).status, 200, 'Exhausted public budgets cannot trap a signed-in maintainer');
    assert.equal((await f.request('/api/admin/health', undefined, session)).status, 429, 'A revoked session immediately loses its exemption');
    f.advance(60000);
    assert.equal((await f.request('/api/admin/health', undefined, session)).status, 401, 'Revoked cookies never authorize the private API');
  }
});

test('idle and absolute session expiry remove the dashboard rate-limit exemption', async t => {
  for (const expiry of ['idle', 'absolute']) {
    const f = await fixture(t, { sourceRequestsPerMinute: 3 }), session = await enroll(f);
    if (expiry === 'absolute') {
      for (let visit = 0; visit < 4; visit++) {
        f.advance(14 * 60000);
        assert.equal((await f.request('/auth/session', undefined, session)).status, 200);
      }
      f.advance(4 * 60000 + 1);
    } else f.advance(15 * 60000 + 1);
    for (let attempt = 0; attempt < 3; attempt++) assert.equal((await f.request('/backoffice/')).status, 200);
    for (const path of ['/backoffice/', '/auth/session', '/api/admin/health']) {
      assert.equal((await f.request(path, undefined, session)).status, 429, `${expiry}-expired session cannot bypass exhausted limits`);
    }
    f.advance(60000);
    assert.equal((await f.request('/api/admin/health', undefined, session)).status, 401);
    assert.equal((await (await f.request('/auth/session', undefined, session)).json()).signed_in, false);
  }
});

test('private report access, validated filters and origin-checked status changes', async t => {
  const f = await fixture(t), seeded = seedReport(f), session = await enroll(f);
  const list = await f.request('/api/admin/reports?application=gng&days=7', undefined, session); const records = await list.json(); assert.equal(records[0].reference, seeded.reference);
  const detail = await f.request(`/api/admin/reports/${seeded.reference}`, undefined, session); assert.equal((await detail.json()).payload.support_id, seeded.support_id);
  const denied = await f.request(`/api/admin/reports/${seeded.reference}/status`, { status: 'fixed' }, session, { Origin: 'https://attacker.example' }); assert.equal(denied.status, 403);
  const invalid = await f.request(`/api/admin/reports/${seeded.reference}/status`, { status: 'fixed', note: 'Forbidden free text' }, session); assert.equal(invalid.status, 400);
  const accepted = await f.request(`/api/admin/reports/${seeded.reference}/status`, { status: 'investigating' }, session); assert.equal(accepted.status, 200); assert.equal((await accepted.json()).status, 'investigating');
  assert.equal((await f.request('/api/admin/reports?application=unknown', undefined, session)).status, 400);
  assert.equal((await f.request('/api/admin/reports?offset=100000', undefined, session)).status, 400);
  assert.equal((await f.request('/api/admin/reports?launchpad_version=arbitrary-text', undefined, session)).status, 400);
  assert.equal(f.store.db.prepare("SELECT count(*) AS n FROM admin_audit WHERE action='status_investigating'").get().n, 1);
});

test('support deletion requires fresh verified session and exact confirmation', async t => {
  const f = await fixture(t), seeded = seedReport(f), session = await enroll(f);
  const lookup = await f.request(`/api/admin/support/${seeded.support_id}`, undefined, session); assert.equal((await lookup.json()).reports.length, 1);
  await f.request(`/api/admin/reports/${seeded.reference}`, undefined, session);
  assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM admin_audit WHERE target IN (?,?)').get(seeded.support_id, seeded.reference).n, 2);
  assert.equal((await f.request(`/api/admin/support/${seeded.support_id}/delete`, { confirm: randomUUID() }, session)).status, 400);
  f.advance(300_001);
  assert.equal((await f.request(`/api/admin/support/${seeded.support_id}/delete`, { confirm: seeded.support_id }, session)).status, 403);
  // Re-enrollment is an explicit operator recovery, also obtaining a fresh session.
  const code = secret(); prepareEnrollment(f.store, { token: code, replace: true, now: f.now() }); f.code = code;
  const fresh = await enroll(f);
  assert.equal((await f.request(`/api/admin/support/${seeded.support_id}/delete`, { confirm: seeded.support_id }, fresh)).status, 200);
  assert.equal(f.store.getReport(seeded.reference), null);
  await f.request(`/api/admin/support/${seeded.support_id}`, undefined, fresh);
  assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM admin_audit WHERE target IN (?,?)').get(seeded.support_id, seeded.reference).n, 0);
});

test('malformed authentication never echoes submitted secrets or accepts unsafe origins', async t => {
  const f = await fixture(t);
  const foreign = await f.request('/auth/register-options', { enrollment_code: f.code }, undefined, { Origin: 'https://attacker.example' }); assert.equal(foreign.status, 403);
  const unknown = await f.request('/auth/register-options', { enrollment_code: f.code, other: 'sentinel-private' }); assert.equal(unknown.status, 400); assert.doesNotMatch(await unknown.text(), /sentinel-private/);
  const failed = await f.request('/auth/register-verify', { response: { private: 'sentinel-private' } }, `${FLOW_COOKIE}=invalid`); assert.equal(failed.status, 400); assert.doesNotMatch(await failed.text(), /sentinel-private/);
  assert.equal(f.store.db.prepare('SELECT credential_id FROM auth_owner').get().credential_id, null);
  const large = await f.request('/auth/register-options', { enrollment_code: 'x'.repeat(33000) }); assert.equal(large.status, 413);
  const compressed = await f.request('/auth/register-options', { enrollment_code: f.code }, undefined, { 'Content-Encoding': 'gzip' }); assert.equal(compressed.status, 403);
  const crossSite = await f.request('/auth/register-options', { enrollment_code: f.code }, undefined, { 'Sec-Fetch-Site': 'cross-site' }); assert.equal(crossSite.status, 403);
});
