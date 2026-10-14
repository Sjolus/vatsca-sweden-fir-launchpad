import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createStore } from '../src/store.mjs';
import { createAuth, prepareEnrollment, exactOrigin, secret, digest, cookieValue, SESSION_COOKIE } from '../src/auth.mjs';
import { authenticator } from './auth-fixture.mjs';

const origin = 'https://launchpad.example';
function fixture(t) {
  const directory = mkdtempSync(join(tmpdir(), 'launchpad-auth-')), path = join(directory, 'synthetic.sqlite');
  const store = createStore({ path, freeSpace: () => 10 ** 12 }); let clock = Date.now();
  const auth = createAuth({ store, origin, now: () => clock });
  t.after(() => { store.close(); rmSync(directory, { recursive: true, force: true }); });
  const token = secret(); prepareEnrollment(store, { token, now: clock });
  return { store, auth, token, path, advance: milliseconds => { clock += milliseconds; }, now: () => clock };
}
async function enroll(f, device = authenticator()) {
  const flow = await f.auth.begin('register', f.token);
  const session = await f.auth.finish('register', flow.cookie, device.registration(flow.options, origin));
  return { device, session, handle: f.store.db.prepare('SELECT user_handle FROM auth_owner').get().user_handle };
}

test('exact HTTPS origin, host-bound RP and ambiguous cookie rejection', () => {
  assert.deepEqual(exactOrigin('https://launchpad.space.sjolus.org'), { origin: 'https://launchpad.space.sjolus.org', rpID: 'launchpad.space.sjolus.org' });
  for (const value of ['http://launchpad.example', 'https://launchpad.example/', 'https://launchpad.example/path', 'https://owner:password@launchpad.example']) assert.throws(() => exactOrigin(value));
  assert.equal(cookieValue(`${SESSION_COOKIE}=one; ${SESSION_COOKIE}=two`, SESSION_COOKIE), undefined);
});

test('real signed enrollment and login create hashed, expiring and revocable sessions', async t => {
  const f = fixture(t), { device, session, handle } = await enroll(f);
  assert.equal(f.auth.current(session), true); assert.equal(f.auth.recent(session), true);
  assert.equal(f.store.db.prepare('SELECT token_hash FROM auth_sessions').get().token_hash, digest(session));
  assert.equal(f.store.db.prepare('SELECT enrollment_hash FROM auth_owner').get().enrollment_hash, null);
  await assert.rejects(f.auth.begin('register', f.token));
  f.advance(300_001); assert.equal(f.auth.recent(session), false);
  const flow = await f.auth.begin('authenticate');
  const second = await f.auth.finish('authenticate', flow.cookie, device.authentication(flow.options, origin, handle));
  assert.equal(f.auth.current(second), true); f.auth.logout(second); assert.equal(f.auth.current(second), false);
  f.advance(3_600_000); assert.equal(f.auth.current(session), false);
});

test('enrollment requires operator code and invalid ceremonies do not consume it', async t => {
  const f = fixture(t), device = authenticator();
  await assert.rejects(f.auth.begin('register', secret()));
  await assert.rejects(f.auth.begin('register'));
  for (const overrides of [{ client: { origin: 'https://attacker.example' } }, { client: { challenge: 'wrong' } }, { rpID: 'space.sjolus.org' }, { uv: false }, { client: { crossOrigin: true } }, { client: { topOrigin: origin } }]) {
    const flow = await f.auth.begin('register', f.token);
    await assert.rejects(f.auth.finish('register', flow.cookie, device.registration(flow.options, origin, overrides)));
    assert.equal(f.store.db.prepare('SELECT enrollment_hash FROM auth_owner').get().enrollment_hash, digest(f.token));
  }
  assert.equal((await enroll(f, device)).session.length, 43);
});

test('session activity refreshes idle expiry without extending the absolute deadline', async t => {
  const f = fixture(t), { session } = await enroll(f);
  for (let index = 0; index < 5; index++) { f.advance(600_000); assert.equal(f.auth.current(session), true); }
  f.advance(600_000); assert.equal(f.auth.current(session), false);
  const flow = await f.auth.begin('authenticate');
  assert.ok(flow.options.challenge);
  f.auth.prune(); assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM auth_sessions').get().n, 0);
});

test('idle expiry and enrollment invitation expiry cannot be revived', async t => {
  const f = fixture(t), { session } = await enroll(f);
  f.advance(900_000); assert.equal(f.auth.current(session), false); assert.equal(f.auth.recent(session), false);
  const code = secret(); prepareEnrollment(f.store, { token: code, replace: true, now: f.now() });
  f.advance(600_000); await assert.rejects(f.auth.begin('register', code));
});

test('concurrent enrollment has one winner and replays cannot replace owner', async t => {
  const f = fixture(t), a = authenticator(), b = authenticator();
  const [first, second] = await Promise.all([f.auth.begin('register', f.token), f.auth.begin('register', f.token)]);
  const outcomes = await Promise.allSettled([f.auth.finish('register', first.cookie, a.registration(first.options, origin)), f.auth.finish('register', second.cookie, b.registration(second.options, origin))]);
  assert.equal(outcomes.filter(item => item.status === 'fulfilled').length, 1);
  await assert.rejects(f.auth.finish('register', first.cookie, a.registration(first.options, origin)));
  assert.throws(() => prepareEnrollment(f.store, { token: secret(), now: f.now() }), /already enrolled/);
});

test('login rejects wrong signatures, missing verification, RP and owner identity', async t => {
  const f = fixture(t), { device, handle } = await enroll(f);
  for (const overrides of [{ badSignature: true }, { uv: false }, { rpID: 'attacker.example' }, { client: { origin: 'https://attacker.example' } }, { client: { crossOrigin: true } }]) {
    const flow = await f.auth.begin('authenticate');
    await assert.rejects(f.auth.finish('authenticate', flow.cookie, device.authentication(flow.options, origin, handle, overrides)));
  }
  const wrongHandle = await f.auth.begin('authenticate');
  await assert.rejects(f.auth.finish('authenticate', wrongHandle.cookie, device.authentication(wrongHandle.options, origin, 'someone-else')));
  const other = await f.auth.begin('authenticate');
  await assert.rejects(f.auth.finish('authenticate', other.cookie, authenticator().authentication(other.options, origin, handle)));
});

test('browser binding, kind, expiry and rate ceilings hold in SQLite', async t => {
  const f = fixture(t), device = authenticator();
  const flow = await f.auth.begin('register', f.token), response = device.registration(flow.options, origin);
  await assert.rejects(f.auth.finish('register', flow.cookie.slice(0, 37) + secret(), response));
  await assert.rejects(f.auth.finish('authenticate', flow.cookie, response));
  f.advance(300_001); await assert.rejects(f.auth.finish('register', flow.cookie, response));
  await enroll(f, device);
  for (let i = 0; i < 19; i++) await f.auth.begin('authenticate');
  await assert.rejects(f.auth.begin('authenticate'), /busy/);
  assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM auth_challenges').get().n, 20);
  f.advance(600_001); f.auth.prune(); assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM auth_challenges').get().n, 0);
});

test('explicit recovery invalidates previous owner and sessions', async t => {
  const f = fixture(t), { session } = await enroll(f);
  const replacement = secret(); prepareEnrollment(f.store, { token: replacement, replace: true, now: f.now() });
  assert.equal(f.auth.current(session), false);
  await assert.rejects(f.auth.begin('register', f.token));
  f.token = replacement; await enroll(f); assert.equal(f.store.db.prepare('SELECT count(*) AS n FROM auth_sessions').get().n, 1);
});

test('enrollment CLI refuses log output and stdin mode never prints its code', t => {
  const f = fixture(t), cli = fileURLToPath(new URL('../scripts/enroll-owner.mjs', import.meta.url));
  const refused = spawnSync(process.execPath, [cli, '--database', f.path], { encoding: 'utf8' });
  assert.notEqual(refused.status, 0); assert.match(refused.stderr, /trusted interactive terminal/);
  const synthetic = secret();
  const accepted = spawnSync(process.execPath, [cli, '--database', f.path, '--code-from-stdin'], { input: synthetic, encoding: 'utf8' });
  assert.equal(accepted.status, 0, accepted.stderr); assert.doesNotMatch(accepted.stdout + accepted.stderr, new RegExp(synthetic)); assert.match(accepted.stdout, /expires in ten minutes/);
});
