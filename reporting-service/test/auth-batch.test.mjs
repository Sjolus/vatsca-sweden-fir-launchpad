import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { request as httpRequest } from 'node:http';
import { createStore } from '../src/store.mjs';
import { createServer } from '../src/server.mjs';
import { createAdminHandler } from '../src/admin.mjs';
import { readConfig } from '../src/config.mjs';
import { createAuth, prepareEnrollment, secret, SESSION_COOKIE, ACCESS_CONFLICT_MESSAGE } from '../src/auth.mjs';
import { authenticator } from './auth-fixture.mjs';

const origin = 'https://launchpad.example', path = '/api/admin/users/access';
async function fixture(t) {
  const directory = mkdtempSync(join(tmpdir(), 'launchpad-auth-batch-'));
  const config = { ...readConfig({ PUBLIC_ORIGIN: origin }), dataPath: join(directory, 'synthetic.sqlite') };
  let clock = Date.now(); const now = () => clock;
  const store = createStore({ path: config.dataPath, now, freeSpace: () => 10 ** 12 });
  const auth = createAuth({ store, origin, now }), admin = createAdminHandler({ store, config, now });
  const { server } = createServer({ store, config, now, adminHandler: admin });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); store.close(); rmSync(directory, { recursive: true, force: true }); });
  const token = secret(); prepareEnrollment(store, { token, now: clock });
  const base = `http://127.0.0.1:${server.address().port}`;
  const request = (body, session, headers = {}, target = path) => fetch(base + target, {
    method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json', Origin: origin,
      ...(session ? { Cookie: `${SESSION_COOKIE}=${session}` } : {}), ...headers }, body: body === undefined ? undefined : JSON.stringify(body),
  });
  return { store, auth, admin, server, base, token, request, now, advance: ms => { clock += ms; } };
}
async function enroll(f, token = f.token) {
  const device = authenticator(), flow = await f.auth.begin('register', token);
  return f.auth.finish('register', flow.cookie, device.registration(flow.options, origin));
}
const add = (f, username, permissions = ['usage']) => f.auth.createUser({ username, permissions });
const change = (user, overrides = {}) => ({ id: user.id, permissions: [...user.permissions], enabled: user.enabled,
  expected: { permissions: [...user.permissions], enabled: user.enabled }, ...overrides });
const snapshot = f => ['auth_users', 'auth_sessions', 'auth_challenges', 'auth_owner'].map(table => f.store.db.prepare(`SELECT * FROM ${table} ORDER BY rowid`).all());

test('atomic access batch returns the full safe list and preserves individual access semantics', async t => {
  const f = await fixture(t), ownerSession = await enroll(f);
  const a = add(f, 'alpha'), b = add(f, 'bravo'), c = add(f, 'charlie'), untouched = add(f, 'delta');
  const aSession = await enroll(f, a.invitation), bSession = await enroll(f, b.invitation);
  f.auth.updateUser(c.user.id, { enabled: false }); const invite = f.auth.inviteUser(c.user.id);
  const cBefore = f.auth.listUsers().find(row => row.id === c.user.id);
  const result = f.auth.updateUsers([change(a.user, { enabled: false }), change(b.user, { permissions: ['health', 'inventory'] }), change(cBefore, { enabled: true })]);
  assert.deepEqual(result.map(row => row.username), ['alpha', 'bravo', 'charlie', 'delta']);
  assert.deepEqual(result.find(row => row.id === b.user.id).permissions, ['inventory', 'health']);
  assert.deepEqual(result.find(row => row.id === untouched.user.id), untouched.user);
  assert.equal(f.auth.current(aSession), false); assert.deepEqual(f.auth.identity(bSession).permissions, ['inventory', 'health']);
  assert.equal(f.auth.current(ownerSession), true); assert.equal(f.auth.current(await enroll(f, invite.invitation)), true);
  for (const row of result) assert.deepEqual(Object.keys(row).sort(), ['id', 'username', 'permissions', 'enabled', 'enrolled', 'invitation_expires_at'].sort());
});

test('batch shape validation rejects missing, extra, duplicate and malformed fields without writes', async t => {
  const f = await fixture(t), a = add(f, 'alpha'), valid = change(a.user), before = snapshot(f);
  const invalid = [undefined, null, {}, [], Array(26).fill(valid), [valid, valid], [valid, { ...valid, id: valid.id.toUpperCase() }],
    [{ ...valid, id: 'maintainer' }], [{ ...valid, id: valid.id + '\n' }], [{ ...valid, expected: undefined }],
    [{ ...valid, enabled: 1 }], [{ ...valid, permissions: undefined }], [{ ...valid, permissions: ['usage', 'usage'] }],
    [{ ...valid, permissions: ['owner'] }], [{ ...valid, credential_id: 'forbidden' }],
    [{ ...valid, expected: { permissions: ['usage'], enabled: 'true' } }],
    [{ ...valid, expected: { permissions: ['usage'], enabled: true, epoch: 'forbidden' } }],
    [{ ...valid, expected: { enabled: true } }], [{ ...valid, expected: { permissions: ['usage', 'usage'], enabled: true } }],
  ];
  for (const batch of invalid) { assert.throws(() => f.auth.updateUsers(batch)); assert.deepEqual(snapshot(f), before); }
  const ownerHandle = f.store.db.prepare('SELECT user_handle FROM auth_owner').get().user_handle;
  assert.throws(() => f.auth.updateUsers([{ ...valid, id: ownerHandle }]), { status: 409, message: ACCESS_CONFLICT_MESSAGE });
  assert.deepEqual(snapshot(f), before);
});

test('a stale or missing last row leaves every earlier row, session and invitation unchanged', async t => {
  const f = await fixture(t), a = add(f, 'alpha'), b = add(f, 'bravo');
  await enroll(f, a.invitation); const before = snapshot(f);
  for (const last of [change(b.user, { expected: { permissions: ['errors'], enabled: true } }),
    change(b.user, { expected: { permissions: ['usage'], enabled: false } }), change(b.user, { id: randomUUID() })]) {
    assert.throws(() => f.auth.updateUsers([change(a.user, { enabled: false }), last]), { status: 409, message: ACCESS_CONFLICT_MESSAGE });
    assert.deepEqual(snapshot(f), before);
  }
  f.auth.updateUsers([change(a.user, { permissions: ['inventory'] })]);
  const afterFirst = snapshot(f);
  assert.throws(() => f.auth.updateUsers([change(a.user, { enabled: false }), change(b.user, { permissions: [] })]), { status: 409 });
  assert.deepEqual(snapshot(f), afterFirst);
});

test('write failure rolls back earlier access changes including session and challenge revocation', async t => {
  const f = await fixture(t), a = add(f, 'alpha'), b = add(f, 'bravo');
  const session = await enroll(f, a.invitation); await f.auth.begin('authenticate');
  const before = snapshot(f);
  f.store.db.exec("CREATE TRIGGER fail_second_access BEFORE UPDATE OF permissions ON auth_users WHEN OLD.username='bravo' BEGIN SELECT RAISE(ABORT,'synthetic write failure'); END;");
  assert.throws(() => f.auth.updateUsers([change(a.user, { enabled: false }), change(b.user, { permissions: ['reports'] })]));
  assert.deepEqual(snapshot(f), before); assert.equal(f.auth.current(session), true);
});

test('one batch accepts the full 25-account bound and compares permission sets canonically', async t => {
  const f = await fixture(t);
  const users = Array.from({ length: 25 }, (_, index) => add(f, `reader_${index}`, ['health', 'usage']).user);
  const updates = users.map(user => change(user, { permissions: ['inventory'], expected: { permissions: ['health', 'usage'], enabled: true } }));
  assert.equal(f.auth.updateUsers(updates).length, 25);
  assert.ok(f.auth.listUsers().every(user => user.permissions.join() === 'inventory'));
});

test('batch HTTP returns full state and fixed conflict while keeping the individual endpoint', async t => {
  const f = await fixture(t), session = await enroll(f), a = add(f, 'alpha'), b = add(f, 'bravo');
  const body = { users: [change(a.user, { permissions: ['reports'] })] };
  const success = await f.request(body, session); assert.equal(success.status, 200);
  assert.deepEqual(await success.json(), { users: f.auth.listUsers() });
  const before = snapshot(f), conflict = await f.request(body, session);
  assert.equal(conflict.status, 409); assert.deepEqual(await conflict.json(), { error: ACCESS_CONFLICT_MESSAGE });
  assert.deepEqual(snapshot(f), before);
  assert.equal((await f.request({ users: [change(b.user)], extra: true }, session)).status, 400);
  assert.equal((await f.request({}, session)).status, 400);
  assert.equal((await f.request({ users: [change(b.user)] }, session, {}, path + '?ignored=true')).status, 400);
  assert.equal((await f.request({ permissions: ['errors'] }, session, {}, `/api/admin/users/${b.user.id}`)).status, 200);
  assert.deepEqual(f.auth.listUsers().find(user => user.id === b.user.id).permissions, ['errors']);
  assert.equal(f.admin.isAuthenticatedDashboardRequest({ method: 'POST', headers: { cookie: `${SESSION_COOKIE}=${session}` } }, path), true);
  assert.equal(f.admin.isAuthenticatedDashboardRequest({ method: 'POST', headers: {} }, path), false);
});

test('batch endpoint requires exact origin and a recent owner session', async t => {
  const f = await fixture(t), owner = await enroll(f), account = add(f, 'reader', ['usage', 'inventory', 'reports', 'errors', 'support', 'health']);
  const guest = await enroll(f, account.invitation), before = snapshot(f), body = { users: [change(account.user, { enabled: false })] };
  assert.equal((await f.request(body)).status, 401);
  assert.equal((await f.request(body, guest)).status, 403);
  assert.equal((await f.request(body, owner, { Origin: 'https://other.example' })).status, 403);
  assert.equal((await f.request(body, owner, { 'Sec-Fetch-Site': 'cross-site' })).status, 403);
  assert.equal((await f.request(undefined, owner)).status, 404);
  assert.deepEqual(snapshot(f), before);
  f.advance(300001); const expiredVerification = await f.request(body, owner);
  assert.equal(expiredVerification.status, 403); assert.equal((await expiredVerification.json()).code, 'reauthentication_required');
  assert.equal(f.auth.listUsers()[0].enabled, true);
});

test('streamed batch rechecks revoked owner authority before applying any access', async t => {
  const f = await fixture(t), owner = await enroll(f), account = add(f, 'reader');
  let bodyRead; const started = new Promise(resolve => { bodyRead = resolve; });
  f.server.once('request', incoming => {
    const original = incoming[Symbol.asyncIterator].bind(incoming);
    incoming[Symbol.asyncIterator] = function () { const iterator = original(), next = iterator.next.bind(iterator);
      iterator.next = async (...args) => { const value = await next(...args); if (!value.done) bodyRead(); return value; }; return iterator; };
  });
  let request;
  const result = new Promise((resolve, reject) => {
    request = httpRequest(f.base + path, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json', Cookie: `${SESSION_COOKIE}=${owner}` } }, response => {
      response.resume(); response.on('end', () => resolve(response.statusCode)); response.on('error', reject);
    });
    request.on('error', reject); request.setTimeout(5000, () => request.destroy(new Error('Synthetic request timed out'))); request.write('{"users":');
  });
  await started; prepareEnrollment(f.store, { token: secret(), replace: true, now: f.now() });
  request.end(JSON.stringify([change(account.user, { enabled: false })]) + '}');
  assert.equal(await result, 401); assert.equal(f.auth.listUsers()[0].enabled, true);
});
