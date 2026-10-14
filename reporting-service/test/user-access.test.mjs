import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createStore } from '../src/store.mjs';
import { createServer } from '../src/server.mjs';
import { readConfig } from '../src/config.mjs';
import { prepareEnrollment, secret, digest, SESSION_COOKIE, FLOW_COOKIE } from '../src/auth.mjs';
import { INVENTORY } from '../src/schema.mjs';
import { authenticator } from './auth-fixture.mjs';

const origin = 'https://launchpad.example';
const scopes = ['usage', 'inventory', 'reports', 'errors', 'support', 'health'];

async function fixture(t) {
  const directory = mkdtempSync(join(tmpdir(), 'launchpad-user-access-'));
  const config = { ...readConfig({ PUBLIC_ORIGIN: origin }), dataPath: join(directory, 'synthetic.sqlite'),
    privacyOperator: 'Synthetic maintainer', privacyContact: 'Synthetic support contact' };
  let clock = Date.UTC(2026, 9, 14, 12);
  const store = createStore({ path: config.dataPath, now: () => clock, freeSpace: () => 10 ** 12 });
  const { server } = createServer({ store, config, now: () => clock });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    store.close(); rmSync(directory, { recursive: true, force: true });
  });
  const base = `http://127.0.0.1:${server.address().port}`;
  const request = (path, body, cookie, headers = {}) => fetch(base + path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { ...(body === undefined ? {} : { 'Content-Type': 'application/json', Origin: origin }),
      ...(cookie ? { Cookie: cookie } : {}), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual',
  });
  const code = secret(); prepareEnrollment(store, { token: code, now: clock });
  return { store, server, base, request, code, now: () => clock, advance: value => { clock += value; } };
}

function cookie(response, name) {
  const found = response.headers.getSetCookie().find(value => value.startsWith(name + '='));
  assert.ok(found, `${name} cookie was returned`);
  return found.split(';')[0];
}

async function enroll(f, code = f.code) {
  const device = authenticator();
  const begin = await f.request('/auth/register-options', { enrollment_code: code });
  assert.equal(begin.status, 200); const options = await begin.json();
  const result = await f.request('/auth/register-verify', { response: device.registration(options, origin) }, cookie(begin, FLOW_COOKIE));
  assert.equal(result.status, 200);
  return { device, session: cookie(result, SESSION_COOKIE), handle: Buffer.from(options.user.id, 'base64url').toString('utf8') };
}

async function signIn(f, account) {
  const begin = await f.request('/auth/authenticate-options', {});
  assert.equal(begin.status, 200); const options = await begin.json();
  const result = await f.request('/auth/authenticate-verify',
    { response: account.device.authentication(options, origin, account.handle) }, cookie(begin, FLOW_COOKIE));
  assert.equal(result.status, 200); return cookie(result, SESSION_COOKIE);
}

async function createUser(f, owner, username, permissions) {
  const response = await f.request('/api/admin/users', { username, permissions }, owner.session);
  assert.equal(response.status, 201); const result = await response.json();
  assert.deepEqual(Object.keys(result).sort(), ['expires_at', 'invitation_url', 'user']);
  const invitation = new URL(result.invitation_url);
  assert.equal(invitation.origin, origin); assert.equal(invitation.pathname, '/backoffice/');
  assert.equal(invitation.search, '', 'Enrollment secrets stay out of request URLs');
  assert.match(invitation.hash, /^#invite=[A-Za-z0-9_-]{43}$/);
  assert.equal(Date.parse(result.expires_at), f.now() + 600_000);
  return { ...result, code: invitation.hash.slice('#invite='.length) };
}

function seed(f) {
  const support_id = randomUUID();
  const report = f.store.admitReport({ schema_version: 1, report_id: randomUUID(), support_id,
    launchpad_version: '2.3.1', application: 'gng', category: 'update' });
  const event = (event_name, overrides = {}) => ({ event_id: randomUUID(), support_id: randomUUID(), session_id: randomUUID(),
    occurred_at: new Date(f.now()).toISOString(), launchpad_version: '2.3.1', event_name, ...overrides });
  f.store.admitBatch('usage', { schema_version: 1, events: [
    event('application_opened', { application: 'euroscope' }),
    event('application_opened', { application: 'trackaudio', launchpad_version: '2.3.0' }),
  ] });
  f.store.admitBatch('inventory', { schema_version: 1, events: Array.from({ length: 3 }, () => event('inventory_snapshot', {
    items: Object.entries(INVENTORY).map(([component, source]) => ({ component, source, status: 'absent' })),
  })) });
  return { ...report, support_id };
}

test('owner creates and lists safe accounts; invited users enroll using the normal passkey flow', async t => {
  const f = await fixture(t), owner = await enroll(f);
  const created = await createUser(f, owner, 'reviewer_one', ['health', 'usage']);
  assert.equal(created.user.username, 'reviewer_one'); assert.equal(created.user.enabled, true);
  assert.equal(created.user.enrolled, false); assert.deepEqual(created.user.permissions, ['usage', 'health']);
  const list = await f.request('/api/admin/users', undefined, owner.session);
  assert.equal(list.status, 200); assert.match(list.headers.get('cache-control'), /no-store/);
  const before = await list.json(); assert.deepEqual(before.users, [created.user]);
  const viewer = await enroll(f, created.code);
  const state = await (await f.request('/auth/session', undefined, viewer.session)).json();
  assert.deepEqual(state, { signed_in: true, authenticated: true, username: 'reviewer_one', owner: false, permissions: ['usage', 'health'] });
  const after = await (await f.request('/api/admin/users', undefined, owner.session)).json();
  assert.equal(after.users[0].enrolled, true); assert.equal(after.users[0].invitation_expires_at, null);
  assert.deepEqual(Object.keys(after.users[0]).sort(), ['enabled', 'enrolled', 'id', 'invitation_expires_at', 'permissions', 'username']);
  for (const response of [before, after, state]) {
    const text = JSON.stringify(response);
    for (const secretValue of [created.code, digest(created.code), owner.device.id, viewer.device.id, viewer.handle]) {
      assert.equal(text.includes(secretValue), false, 'Account/session views contain no invitation, credential or WebAuthn handle');
    }
    assert.doesNotMatch(text, /"(?:public_key|credential_id|token_hash|epoch|enrollment_hash)"/);
  }
  assert.equal((await f.request('/api/admin/users?include=credentials', undefined, owner.session)).status, 400);
});

test('each single read permission allows only its explicit routes and never grants mutations', async t => {
  const f = await fixture(t), owner = await enroll(f), report = seed(f);
  const reads = [
    ['usage', '/api/admin/usage'], ['inventory', '/api/admin/inventory'], ['reports', '/api/admin/reports'],
    ['reports', `/api/admin/reports/${report.reference}`], ['errors', '/api/admin/errors'],
    ['support', `/api/admin/support/${report.support_id}`], ['health', '/api/admin/health'],
    ['owner', '/api/admin/overview'], ['owner', '/api/admin/users'],
  ];
  for (const scope of scopes) {
    const created = await createUser(f, owner, `${scope}_reader`, [scope]);
    const viewer = await enroll(f, created.code);
    for (const [permission, path] of reads) {
      const response = await f.request(path, undefined, viewer.session);
      assert.equal(response.status, permission === scope ? 200 : 403, `${scope}: ${path}`);
      if (response.status === 403) assert.equal((await response.text()).includes(report.support_id), false);
      else await response.arrayBuffer();
    }
    const mutations = [
      ['/api/admin/users', { username: 'unauthorized', permissions: ['usage'] }],
      [`/api/admin/users/${created.user.id}`, { permissions: scopes }],
      [`/api/admin/users/${created.user.id}/invite`, { reset: true }],
      [`/api/admin/reports/${report.reference}/status`, { status: 'fixed' }],
      [`/api/admin/support/${report.support_id}/delete`, { confirm: report.support_id }],
    ];
    for (const [path, body] of mutations) assert.equal((await f.request(path, body, viewer.session)).status, 403, `${scope}: POST ${path}`);
    assert.equal((await f.request('/api/admin/not-a-route', undefined, viewer.session)).status, 404);
  }
  assert.equal(f.store.getReport(report.reference).status, 'new');
  assert.equal((await (await f.request('/api/admin/users', undefined, owner.session)).json()).users.length, scopes.length);
});

test('usage and inventory permissions return separate projections with the selected independent cohorts', async t => {
  const f = await fixture(t), owner = await enroll(f); seed(f);
  const usage = await enroll(f, (await createUser(f, owner, 'usage_reader', ['usage'])).code);
  const inventory = await enroll(f, (await createUser(f, owner, 'inventory_reader', ['inventory'])).code);
  const query = '?application=euroscope&launchpad_version=2.3.1&days=30';
  const usageResponse = await f.request('/api/admin/usage' + query, undefined, usage.session);
  assert.equal(usageResponse.status, 200); const activity = await usageResponse.json();
  assert.equal(Object.hasOwn(activity, 'inventory'), false); assert.equal(activity.cohort.active_installations, 1);
  const inventoryResponse = await f.request('/api/admin/inventory' + query, undefined, inventory.session);
  assert.equal(inventoryResponse.status, 200); const software = await inventoryResponse.json();
  assert.deepEqual(Object.keys(software).sort(), ['filters', 'inventory', 'window']);
  assert.deepEqual(software.window, activity.window); assert.deepEqual(software.filters, activity.filters);
  assert.equal(software.window.days, 30); assert.equal(software.filters.application, 'euroscope');
  assert.equal(software.filters.launchpad_version, '2.3.1'); assert.equal(software.inventory.snapshot_installations, 3);
  for (const [path, account] of [['usage', usage], ['inventory', inventory]]) {
    for (const filter of ['days=7&days=30', 'days=365', 'application=private', 'support_id=anything']) {
      assert.equal((await f.request(`/api/admin/${path}?${filter}`, undefined, account.session)).status, 400);
    }
  }
});

test('permission changes affect every existing session immediately and disabled sessions never revive', async t => {
  const f = await fixture(t), owner = await enroll(f);
  const created = await createUser(f, owner, 'changing_reader', ['usage']);
  const viewer = await enroll(f, created.code), secondSession = await signIn(f, viewer);
  assert.equal((await f.request(`/api/admin/users/${created.user.id}`, { permissions: ['errors'] }, owner.session)).status, 200);
  for (const session of [viewer.session, secondSession]) {
    assert.equal((await f.request('/api/admin/usage', undefined, session)).status, 403);
    assert.equal((await f.request('/api/admin/errors', undefined, session)).status, 200);
    assert.deepEqual((await (await f.request('/auth/session', undefined, session)).json()).permissions, ['errors']);
  }
  assert.equal((await f.request(`/api/admin/users/${created.user.id}`, { permissions: [] }, owner.session)).status, 200);
  for (const path of ['/api/admin/errors', '/api/admin/users', '/api/admin/overview']) {
    assert.equal((await f.request(path, undefined, viewer.session)).status, 403, 'An empty selection grants no private views');
  }
  assert.equal((await f.request(`/api/admin/users/${created.user.id}`, { enabled: false }, owner.session)).status, 200);
  for (const session of [viewer.session, secondSession]) {
    assert.equal((await f.request('/api/admin/errors', undefined, session)).status, 401);
    assert.equal((await (await f.request('/auth/session', undefined, session)).json()).signed_in, false);
  }
  assert.equal((await f.request(`/api/admin/users/${created.user.id}`, { enabled: true, permissions: ['health'] }, owner.session)).status, 200);
  for (const session of [viewer.session, secondSession]) assert.equal((await f.request('/api/admin/health', undefined, session)).status, 401);
  const fresh = await signIn(f, viewer);
  assert.equal((await f.request('/api/admin/health', undefined, fresh)).status, 200);
  assert.equal((await f.request('/api/admin/users', undefined, fresh)).status, 403);
});

test('user management requires a recent owner session, the exact origin and strict safe fields', async t => {
  const f = await fixture(t), owner = await enroll(f);
  const valid = { username: 'safe_reader', permissions: ['usage'] };
  for (const headers of [{ Origin: 'https://other.example' }, { Origin: origin + '/' }, { Origin: '' },
    { Origin: 'http://launchpad.example' }, { 'Sec-Fetch-Site': 'cross-site' }, { 'Content-Type': 'text/plain' }, { 'Content-Encoding': 'identity' }]) {
    assert.equal((await f.request('/api/admin/users', valid, owner.session, headers)).status, 403);
  }
  const invalid = [null, [], {}, { username: 'safe_reader' }, { permissions: ['usage'] },
    { ...valid, owner: true }, { ...valid, permissions: ['owner'] }, { ...valid, permissions: ['usage', 'usage'] },
    { ...valid, permissions: 'usage' }, ...['<script>', '../../owner', 'has space', 'safe\n', 'x', 'a'.repeat(33), 'maintainer', 'Maintainer']
      .map(username => ({ ...valid, username }))];
  for (const body of invalid) {
    const response = await f.request('/api/admin/users', body, owner.session);
    assert.equal(response.status, 400, JSON.stringify(body));
    assert.doesNotMatch(await response.text(), /<script>|\.\.\/|"owner"|"public_key"/);
  }
  assert.equal((await (await f.request('/api/admin/users', undefined, owner.session)).json()).users.length, 0);
  const created = await createUser(f, owner, valid.username, valid.permissions);
  const path = `/api/admin/users/${created.user.id}`;
  for (const body of [{}, { enabled: 'false' }, { enabled: true, owner: true }, { permissions: ['unknown'] }, { username: 'renamed' }]) {
    assert.equal((await f.request(path, body, owner.session)).status, 400);
  }
  for (const body of [{}, { reset: 'true' }, { reset: false, owner: true }]) {
    assert.equal((await f.request(path + '/invite', body, owner.session)).status, 400);
  }
  assert.equal((await f.request(path + '?extra=yes', { enabled: false }, owner.session)).status, 400);
  assert.equal((await f.request('/api/admin/users', valid, `${owner.session}; ${owner.session}`)).status, 401);
  assert.equal((await f.request('/api/admin/users', valid, `${SESSION_COOKIE}=${secret()}`)).status, 401);
  f.advance(300_001);
  for (const [url, body] of [['/api/admin/users', { username: 'late_reader', permissions: ['usage'] }],
    [path, { enabled: false }], [path + '/invite', { reset: false }]]) {
    assert.equal((await f.request(url, body, owner.session)).status, 403, 'An active but older session cannot manage access');
  }
  assert.equal((await f.request('/api/admin/users', undefined, owner.session)).status, 200, 'Older owner sessions can still read');
  owner.session = await signIn(f, owner);
  assert.equal((await f.request(path, { enabled: false }, owner.session)).status, 200);
});

test('owner passkey reset invitations revoke user sessions and permit a replacement passkey', async t => {
  const f = await fixture(t), owner = await enroll(f);
  const created = await createUser(f, owner, 'reset_reader', ['health']);
  const viewer = await enroll(f, created.code);
  const path = `/api/admin/users/${created.user.id}/invite`;
  assert.equal((await f.request(path, { reset: false }, owner.session)).status, 400, 'Enrolled credentials require explicit replacement');
  const result = await f.request(path, { reset: true }, owner.session);
  assert.equal(result.status, 200); const reset = await result.json();
  assert.deepEqual(Object.keys(reset).sort(), ['expires_at', 'invitation_url', 'user']);
  assert.equal(reset.user.id, created.user.id); assert.equal(reset.user.enrolled, false);
  assert.equal((await f.request('/api/admin/health', undefined, viewer.session)).status, 401);
  const replacement = await enroll(f, new URL(reset.invitation_url).hash.slice('#invite='.length));
  assert.equal((await f.request('/api/admin/health', undefined, replacement.session)).status, 200);
});

test('a streamed management request rechecks owner authority after the body finishes', async t => {
  const f = await fixture(t), owner = await enroll(f);
  const path = '/api/admin/users', replacementCode = secret();
  let markBodyStarted;
  const bodyStarted = new Promise(resolve => { markBodyStarted = resolve; });
  f.server.once('request', incoming => {
    assert.equal(incoming.url, path);
    // Observe the parser's first read without switching the incoming stream to
    // flowing mode before the asynchronous route handler has attached to it.
    const original = incoming[Symbol.asyncIterator].bind(incoming);
    incoming[Symbol.asyncIterator] = function () {
      const iterator = original();
      const next = iterator.next.bind(iterator);
      iterator.next = async (...args) => {
        const value = await next(...args);
        if (!value.done) markBodyStarted();
        return value;
      };
      return iterator;
    };
  });
  let request;
  const result = new Promise((resolve, reject) => {
    request = httpRequest(f.base + path, { method: 'POST', headers: {
      Origin: origin, 'Content-Type': 'application/json', Cookie: owner.session,
    } }, response => {
      const chunks = []; response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => resolve({ status: response.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
      response.on('error', reject);
    });
    request.on('error', reject);
    request.setTimeout(5000, () => request.destroy(new Error('Synthetic streamed request timed out')));
    request.write('{"username":');
  });
  await bodyStarted;
  prepareEnrollment(f.store, { token: replacementCode, replace: true, now: f.now() });
  request.end('"late_reader","permissions":["usage"]}');
  const response = await result;
  assert.equal(response.status, 401); assert.doesNotMatch(response.body, /invitation_url/);
  const replacement = await enroll(f, replacementCode);
  const users = await (await f.request('/api/admin/users', undefined, replacement.session)).json();
  assert.deepEqual(users.users, [], 'A revoked owner cannot create access by finishing an earlier request');
});
