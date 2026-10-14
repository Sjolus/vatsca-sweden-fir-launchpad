import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { createStore } from '../src/store.mjs';
import { AUTH_PERMISSIONS, createAuth, initializeAuth, prepareEnrollment, secret, digest } from '../src/auth.mjs';
import { authenticator } from './auth-fixture.mjs';

const origin = 'https://launchpad.example';
function fixture(t, legacy = false) {
  const directory = mkdtempSync(join(tmpdir(), 'launchpad-auth-users-'));
  const store = createStore({ path: join(directory, 'synthetic.sqlite'), freeSpace: () => 10 ** 12 });
  let clock = Date.now();
  t.after(() => { store.close(); rmSync(directory, { recursive: true, force: true }); });
  const f = { store, now: () => clock, advance: ms => { clock += ms; } };
  if (!legacy) {
    f.auth = createAuth({ store, origin, now: f.now });
    f.token = secret(); prepareEnrollment(store, { token: f.token, now: clock });
  }
  return f;
}
async function enroll(f, token = f.token, device = authenticator()) {
  const flow = await f.auth.begin('register', token);
  const session = await f.auth.finish('register', flow.cookie, device.registration(flow.options, origin));
  return { device, session, handle: Buffer.from(flow.options.user.id, 'base64url').toString() };
}
async function login(f, enrolled) {
  const flow = await f.auth.begin('authenticate');
  return f.auth.finish('authenticate', flow.cookie, enrolled.device.authentication(flow.options, origin, enrolled.handle));
}
function createUser(f, name = 'reviewer', scopes = ['usage']) { return f.auth.createUser({ username: name, permissions: scopes }); }

test('auth migration preserves legacy owner credentials and sessions and is idempotent', t => {
  const f = fixture(t, true), epoch = randomUUID(), handle = randomUUID(), token = secret();
  f.store.db.exec(`
    CREATE TABLE auth_owner(singleton INTEGER PRIMARY KEY,user_handle TEXT NOT NULL,credential_id TEXT,public_key BLOB,counter INTEGER NOT NULL DEFAULT 0,transports TEXT NOT NULL DEFAULT '[]',epoch TEXT NOT NULL,enrollment_hash TEXT,enrollment_expires INTEGER);
    CREATE TABLE auth_sessions(token_hash TEXT PRIMARY KEY,epoch TEXT NOT NULL,created_at INTEGER NOT NULL,last_seen INTEGER NOT NULL,expires_at INTEGER NOT NULL);
    CREATE TABLE auth_challenges(id TEXT PRIMARY KEY,kind TEXT NOT NULL,challenge TEXT NOT NULL,binder_hash TEXT NOT NULL,token_hash TEXT,created_at INTEGER NOT NULL,expires_at INTEGER NOT NULL,consumed INTEGER NOT NULL DEFAULT 0);
  `);
  f.store.db.prepare('INSERT INTO auth_owner(singleton,user_handle,credential_id,public_key,epoch) VALUES(1,?,?,?,?)').run(handle, 'synthetic-legacy', Buffer.from('synthetic'), epoch);
  f.store.db.prepare('INSERT INTO auth_sessions VALUES(?,?,?,?,?)').run(digest(token), epoch, f.now(), f.now(), f.now() + 3600000);
  f.store.db.prepare('INSERT INTO auth_challenges(id,kind,challenge,binder_hash,created_at,expires_at) VALUES(?,?,?,?,?,?)').run(randomUUID(), 'authenticate', 'legacy', 'binder', f.now(), f.now() + 300000);
  const before = f.store.db.prepare('SELECT * FROM auth_owner').get();
  initializeAuth(f.store); initializeAuth(f.store);
  const auth = createAuth({ store: f.store, origin, now: f.now });
  assert.deepEqual(f.store.db.prepare('SELECT * FROM auth_owner').get(), before);
  assert.deepEqual(auth.identity(token), { username: 'maintainer', owner: true, permissions: AUTH_PERMISSIONS });
  assert.equal(f.store.db.prepare('SELECT count(*) n FROM auth_challenges').get().n, 0);
  assert.equal(f.store.db.prepare('SELECT user_id FROM auth_sessions').get().user_id, null);
  assert.deepEqual(auth.listUsers(), []);
});

test('account input is bounded, canonical and exposes no invitation hashes or passkey material', t => {
  const f = fixture(t), created = createUser(f, 'ReViewer', ['health', 'usage']);
  assert.equal(created.user.username, 'reviewer'); assert.deepEqual(created.user.permissions, ['usage', 'health']);
  assert.equal(created.user.enrolled, false); assert.equal(created.user.enabled, true);
  assert.equal(created.expires_at, new Date(f.now() + 600000).toISOString());
  assert.deepEqual(Object.keys(created.user).sort(), ['id', 'username', 'permissions', 'enabled', 'enrolled', 'invitation_expires_at'].sort());
  const row = f.store.db.prepare('SELECT * FROM auth_users').get();
  assert.equal(row.invitation_hash, digest(created.invitation));
  assert.ok(!JSON.stringify(row).includes(created.invitation)); assert.ok(!JSON.stringify(f.auth.listUsers()).includes(row.invitation_hash));
  for (const name of ['REVIEWER', 'maintainer', 'Maintainer', 'a', 'a'.repeat(33), 'space name', 'éclair', '<script>', '1abc', 'safe\n']) assert.throws(() => createUser(f, name));
  for (const scopes of [undefined, 'usage', ['unknown'], ['usage', 'usage'], [null]]) assert.throws(() => f.auth.createUser({ username: 'invalid', permissions: scopes }));
  assert.throws(() => f.auth.updateUser(created.user.id, {}));
  assert.throws(() => f.auth.updateUser(created.user.id, { enabled: 1 }));
  assert.throws(() => f.auth.updateUser(created.user.id, { owner: true }));
  assert.throws(() => f.auth.inviteUser(created.user.id, { reset: 'true' }));
  assert.throws(() => f.auth.updateUser(randomUUID(), { enabled: true }));
  assert.equal(f.auth.listUsers().length, 1);
  f.auth.updateUser(created.user.id, { permissions: [] }); assert.deepEqual(f.auth.listUsers()[0].permissions, []);
  for (let index = 1; index < 25; index++) createUser(f, `user_${index}`);
  assert.throws(() => createUser(f, 'overflow'), /limit/);
  f.auth.updateUser(created.user.id, { enabled: false });
  assert.throws(() => createUser(f, 'overflow'), /limit/); assert.equal(f.auth.listUsers().length, 25);
});

test('user invitation enrolls a verified passkey and discoverable login returns only current access', async t => {
  const f = fixture(t), owner = await enroll(f), invitation = createUser(f, 'helper', ['reports']);
  const account = await enroll(f, invitation.invitation);
  const identity = { username: 'helper', owner: false, permissions: ['reports'] };
  assert.deepEqual(f.auth.identity(account.session), identity); assert.equal(f.auth.recent(account.session), true);
  assert.equal(f.store.db.prepare('SELECT user_id FROM auth_sessions WHERE token_hash=?').get(digest(account.session)).user_id, invitation.user.id);
  assert.equal(f.store.db.prepare('SELECT invitation_hash FROM auth_users').get().invitation_hash, null);
  assert.deepEqual(f.auth.identity(await login(f, account)), identity);
  f.auth.updateUser(invitation.user.id, { permissions: ['inventory', 'usage'] });
  assert.deepEqual(f.auth.identity(account.session).permissions, ['usage', 'inventory']);
  const copy = f.auth.identity(account.session); copy.permissions.push('health');
  assert.deepEqual(f.auth.identity(account.session).permissions, ['usage', 'inventory']);
  assert.deepEqual(f.auth.identity(owner.session).permissions, AUTH_PERMISSIONS);
  f.auth.logout(account.session); assert.equal(f.auth.identity(account.session), false);
});

test('reissuing invitations invalidates old codes and flows; expiry cannot be revived', async t => {
  const f = fixture(t), first = createUser(f), device = authenticator();
  const flow = await f.auth.begin('register', first.invitation);
  const next = f.auth.inviteUser(first.user.id);
  assert.notEqual(next.invitation, first.invitation);
  await assert.rejects(f.auth.begin('register', first.invitation));
  await assert.rejects(f.auth.finish('register', flow.cookie, device.registration(flow.options, origin)));
  f.advance(540000);
  const expiring = await f.auth.begin('register', next.invitation); f.advance(60000);
  await assert.rejects(f.auth.finish('register', expiring.cookie, device.registration(expiring.options, origin)));
  await assert.rejects(f.auth.begin('register', next.invitation));
  f.auth.prune(); assert.equal(f.store.db.prepare('SELECT invitation_hash FROM auth_users').get().invitation_hash, null);
  assert.equal(f.auth.listUsers()[0].invitation_expires_at, null);
});

test('concurrent user enrollment has one winner and a credential cannot belong to two accounts', async t => {
  const f = fixture(t), invited = createUser(f), device = authenticator(), secondDevice = authenticator();
  const [a, b] = await Promise.all([f.auth.begin('register', invited.invitation), f.auth.begin('register', invited.invitation)]);
  const results = await Promise.allSettled([f.auth.finish('register', a.cookie, device.registration(a.options, origin)), f.auth.finish('register', b.cookie, secondDevice.registration(b.options, origin))]);
  assert.equal(results.filter(item => item.status === 'fulfilled').length, 1);
  await assert.rejects(f.auth.begin('register', invited.invitation));
  const enrolledId = f.store.db.prepare('SELECT credential_id FROM auth_users').get().credential_id;
  const other = createUser(f, 'another');
  const duplicate = await f.auth.begin('register', other.invitation);
  const winner = enrolledId === device.id ? device : secondDevice;
  await assert.rejects(f.auth.finish('register', duplicate.cookie, winner.registration(duplicate.options, origin)), /already enrolled/);
  assert.equal(f.store.db.prepare('SELECT count(*) n FROM auth_sessions').get().n, 1);
});

test('account disable and explicit passkey reset revoke sessions and in-flight ceremonies', async t => {
  const f = fixture(t), owner = await enroll(f), invited = createUser(f), account = await enroll(f, invited.invitation);
  assert.throws(() => f.auth.inviteUser(invited.user.id), /reset/);
  const pending = await f.auth.begin('authenticate');
  const finishing = f.auth.finish('authenticate', pending.cookie, account.device.authentication(pending.options, origin, account.handle));
  f.auth.updateUser(invited.user.id, { enabled: false });
  await assert.rejects(finishing); assert.equal(f.auth.current(account.session), false); assert.equal(f.auth.recent(account.session), false);
  assert.equal(f.auth.current(owner.session), true);
  await assert.rejects(login(f, account));
  f.auth.updateUser(invited.user.id, { enabled: true }); assert.equal(f.auth.current(account.session), false);
  const fresh = await login(f, account); assert.equal(f.auth.current(fresh), true);
  const reset = f.auth.inviteUser(invited.user.id, { reset: true }); assert.equal(f.auth.current(fresh), false);
  await assert.rejects(login(f, account));
  const replacement = await enroll(f, reset.invitation); assert.equal(f.auth.current(replacement.session), true);
  await assert.rejects(login(f, account)); assert.equal(f.auth.current(owner.session), true);
});

test('disabled accounts can safely reset a lost passkey before access is enabled', async t => {
  const f = fixture(t), invited = createUser(f), account = await enroll(f, invited.invitation);
  f.auth.updateUser(invited.user.id, { enabled: false });
  assert.throws(() => f.auth.inviteUser(invited.user.id), /reset/);
  const replacement = f.auth.inviteUser(invited.user.id, { reset: true });
  assert.equal(replacement.user.enabled, false); assert.equal(replacement.user.enrolled, false);
  assert.equal(f.auth.current(account.session), false);
  await assert.rejects(f.auth.begin('register', replacement.invitation));
  await assert.rejects(login(f, account));
  f.auth.updateUser(invited.user.id, { enabled: true });
  await assert.rejects(login(f, account));
  assert.equal(f.auth.current(account.session), false);
  const fresh = await enroll(f, replacement.invitation); assert.equal(f.auth.current(fresh.session), true);
  await assert.rejects(login(f, account));
});

test('user authentication rejects valid signatures from unregistered or differently bound credentials', async t => {
  const f = fixture(t), owner = await enroll(f), invited = createUser(f), account = await enroll(f, invited.invitation);
  for (const [device, handle, overrides] of [
    [authenticator(), account.handle, {}],
    [account.device, owner.handle, {}],
    [account.device, account.handle, { uv: false }],
    [account.device, account.handle, { client: { origin: 'https://attacker.example' } }],
  ]) {
    const flow = await f.auth.begin('authenticate');
    await assert.rejects(f.auth.finish('authenticate', flow.cookie, device.authentication(flow.options, origin, handle, overrides)));
  }
  assert.equal(f.store.db.prepare('SELECT count(*) n FROM auth_sessions').get().n, 2);
  const other = createUser(f, 'other');
  const sameCredential = await f.auth.begin('register', other.invitation);
  await assert.rejects(f.auth.finish('register', sameCredential.cookie, owner.device.registration(sameCredential.options, origin)), /already enrolled/);
  assert.equal(f.auth.listUsers().find(row => row.id === other.user.id).enrolled, false);
});

test('owner recovery retains accounts while revoking every existing session and flow', async t => {
  const f = fixture(t), owner = await enroll(f), invited = createUser(f), account = await enroll(f, invited.invitation);
  const pending = await f.auth.begin('authenticate');
  prepareEnrollment(f.store, { token: secret(), replace: true, now: f.now() });
  assert.equal(f.auth.current(owner.session), false); assert.equal(f.auth.current(account.session), false);
  assert.equal(f.auth.listUsers()[0].enrolled, true);
  await assert.rejects(f.auth.finish('authenticate', pending.cookie, account.device.authentication(pending.options, origin, account.handle)));
  assert.deepEqual(f.auth.identity(await login(f, account)), { username: 'reviewer', owner: false, permissions: ['usage'] });
});

test('guest sessions rotate only that account and cannot consume reserved owner capacity', async t => {
  const f = fixture(t), owner = await enroll(f), invited = createUser(f), account = await enroll(f, invited.invitation);
  const other = createUser(f, 'other'), another = await enroll(f, other.invitation);
  let latest;
  for (let index = 0; index < 5; index++) { f.advance(1000); latest = await login(f, account); }
  assert.equal(f.auth.current(account.session), false); assert.equal(f.auth.current(latest), true);
  assert.equal(f.auth.current(another.session), true); assert.equal(f.auth.current(owner.session), true);
  assert.equal(f.store.db.prepare('SELECT count(*) n FROM auth_sessions WHERE user_id=?').get(invited.user.id).n, 5);
  // Fill the shared allowance with synthetic sessions owned by another account.
  const row = f.store.db.prepare('SELECT epoch FROM auth_users WHERE id=?').get(other.user.id);
  const insert = f.store.db.prepare('INSERT INTO auth_sessions(token_hash,epoch,created_at,last_seen,expires_at,user_id) VALUES(?,?,?,?,?,?)');
  while (f.store.db.prepare('SELECT count(*) n FROM auth_sessions').get().n < 80) insert.run(digest(secret()), row.epoch, f.now(), f.now(), f.now() + 3600000, other.user.id);
  const pending = createUser(f, 'newcomer');
  await assert.rejects(enroll(f, pending.invitation), /active sessions/);
  const ownerLogin = await login(f, owner); assert.equal(f.auth.current(ownerLogin), true);
  assert.equal(f.auth.listUsers().find(user => user.id === pending.user.id).enrolled, false, 'capacity failure rolls back enrollment');
  assert.equal(f.auth.current(latest), true); assert.equal(f.auth.current(another.session), true);
  const ownerEpoch = f.store.db.prepare('SELECT epoch FROM auth_owner').get().epoch;
  while (f.store.db.prepare('SELECT count(*) n FROM auth_sessions').get().n < 100) insert.run(digest(secret()), ownerEpoch, f.now(), f.now(), f.now() + 3600000, null);
  assert.equal(f.auth.current(await login(f, owner)), true);
  assert.equal(f.auth.current(owner.session), false, 'at the global ceiling only the oldest owner session is replaced');
  assert.equal(f.auth.current(latest), true); assert.equal(f.auth.current(another.session), true);
  assert.equal(f.store.db.prepare('SELECT count(*) n FROM auth_sessions').get().n, 100);
});
