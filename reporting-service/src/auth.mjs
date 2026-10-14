import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { generateRegistrationOptions, generateAuthenticationOptions, verifyRegistrationResponse, verifyAuthenticationResponse } from '@simplewebauthn/server';

export const SESSION_COOKIE = '__Host-launchpad-session';
export const FLOW_COOKIE = '__Host-launchpad-flow';
export const secret = () => randomBytes(32).toString('base64url');
export const digest = value => createHash('sha256').update(value).digest('hex');
const validSecret = value => typeof value === 'string' && /^[A-Za-z0-9_-]{43}$/.test(value) && Buffer.from(value, 'base64url').toString('base64url') === value;
const uuid = value => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);

export function exactOrigin(value) {
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.origin !== value || url.username || url.password) throw new Error('An exact HTTPS origin is required.');
  return { origin: value, rpID: url.hostname };
}

export function initializeAuth(store) {
  store.db.exec(`
    CREATE TABLE IF NOT EXISTS auth_owner (
      singleton INTEGER PRIMARY KEY CHECK(singleton=1), user_handle TEXT NOT NULL,
      credential_id TEXT, public_key BLOB, counter INTEGER NOT NULL DEFAULT 0,
      transports TEXT NOT NULL DEFAULT '[]', epoch TEXT NOT NULL,
      enrollment_hash TEXT, enrollment_expires INTEGER,
      CHECK((credential_id IS NULL)=(public_key IS NULL)));
    CREATE TABLE IF NOT EXISTS auth_challenges (
      id TEXT PRIMARY KEY, kind TEXT NOT NULL, challenge TEXT NOT NULL, binder_hash TEXT NOT NULL,
      token_hash TEXT, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, consumed INTEGER NOT NULL DEFAULT 0);
    CREATE INDEX IF NOT EXISTS auth_challenges_expiry ON auth_challenges(expires_at);
    CREATE TABLE IF NOT EXISTS auth_sessions (
      token_hash TEXT PRIMARY KEY, epoch TEXT NOT NULL, created_at INTEGER NOT NULL, last_seen INTEGER NOT NULL, expires_at INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS auth_sessions_expiry ON auth_sessions(expires_at);
  `);
  store.db.prepare('INSERT OR IGNORE INTO auth_owner(singleton,user_handle,epoch) VALUES(1,?,?)').run(randomUUID(), randomUUID());
}

// Called by the separate operator CLI, never an HTTP endpoint.
export function prepareEnrollment(store, { token, replace = false, now = Date.now() } = {}) {
  if (!validSecret(token)) throw new Error('The enrollment code must contain 32 random bytes in base64url form.');
  initializeAuth(store);
  store.assertWritable?.();
  store.transaction(() => {
    const owner = store.db.prepare('SELECT * FROM auth_owner WHERE singleton=1').get();
    if (owner.credential_id && !replace) throw new Error('An owner is already enrolled. Explicit credential replacement is required.');
    store.db.prepare(`UPDATE auth_owner SET credential_id=NULL,public_key=NULL,counter=0,transports='[]',epoch=?,enrollment_hash=?,enrollment_expires=? WHERE singleton=1`)
      .run(randomUUID(), digest(token), now + 600_000);
    store.db.exec('DELETE FROM auth_sessions; DELETE FROM auth_challenges;');
  });
}

export function createAuth({ store, origin, now = () => Date.now() }) {
  const site = exactOrigin(origin);
  initializeAuth(store);
  const owner = () => store.db.prepare('SELECT * FROM auth_owner WHERE singleton=1').get();
  function prune() {
    store.db.prepare('DELETE FROM auth_challenges WHERE expires_at<=?').run(now());
    store.db.prepare('DELETE FROM auth_sessions WHERE expires_at<=? OR last_seen<=?').run(now(), now() - 900_000);
    store.db.prepare('UPDATE auth_owner SET enrollment_hash=NULL,enrollment_expires=NULL WHERE enrollment_expires<=?').run(now());
  }
  function authorizedEnrollment(row, hash) {
    return !row.credential_id && row.enrollment_hash === hash && row.enrollment_expires > now();
  }
  return {
    site,
    async begin(kind, token) {
      if (!['register', 'authenticate'].includes(kind)) throw new Error('Invalid ceremony.');
      const row = owner();
      const tokenHash = kind === 'register' && validSecret(token) ? digest(token) : null;
      if (kind === 'register' && (!tokenHash || !authorizedEnrollment(row, tokenHash))) throw new Error('Enrollment unavailable.');
      if (kind === 'authenticate' && !row.credential_id) throw new Error('Sign-in unavailable.');
      const options = kind === 'register'
        ? await generateRegistrationOptions({ rpName: 'Sweden FIR Launchpad', rpID: site.rpID, userName: 'maintainer', userDisplayName: 'Launchpad maintainer', userID: Buffer.from(row.user_handle), attestationType: 'none', authenticatorSelection: { residentKey: 'required', userVerification: 'required' }, timeout: 60_000 })
        : await generateAuthenticationOptions({ rpID: site.rpID, userVerification: 'required', timeout: 60_000 });
      const id = randomUUID(), binder = secret();
      store.assertWritable?.();
      store.transaction(() => {
        prune();
        const counts = store.db.prepare('SELECT count(*) AS total,sum(created_at>?) AS recent FROM auth_challenges').get(now() - 60_000);
        if (counts.total >= 100 || counts.recent >= 20) throw new Error('Sign-in is busy.');
        store.db.prepare('INSERT INTO auth_challenges(id,kind,challenge,binder_hash,token_hash,created_at,expires_at) VALUES(?,?,?,?,?,?,?)')
          .run(id, kind, options.challenge, digest(binder), tokenHash, now(), now() + 300_000);
      });
      return { options, cookie: `${id}.${binder}` };
    },
    async finish(kind, cookie, response) {
      if (typeof cookie !== 'string' || cookie.length !== 80) throw new Error('Invalid flow.');
      const [id, binder] = cookie.split('.');
      if (!uuid(id) || !validSecret(binder)) throw new Error('Invalid flow.');
      const pending = store.transaction(() => {
        const row = store.db.prepare('SELECT * FROM auth_challenges WHERE id=? AND binder_hash=? AND kind=? AND consumed=0 AND expires_at>?').get(id, digest(binder), kind, now());
        if (row) store.db.prepare('UPDATE auth_challenges SET consumed=1 WHERE id=?').run(id);
        return row;
      });
      if (!pending) throw new Error('Sign-in expired or already used.');
      const client = JSON.parse(Buffer.from(response.response.clientDataJSON, 'base64url').toString('utf8'));
      if (client.crossOrigin === true || client.topOrigin !== undefined) throw new Error('Embedded sign-in is not supported.');
      const expected = { expectedChallenge: pending.challenge, expectedOrigin: site.origin, expectedRPID: site.rpID, requireUserVerification: true };
      let credential;
      const previous = owner();
      if (kind === 'register') {
        const verified = await verifyRegistrationResponse({ ...expected, response });
        if (!verified.verified || !verified.registrationInfo || !pending.token_hash) throw new Error('Passkey verification failed.');
        credential = verified.registrationInfo.credential;
      } else if (kind === 'authenticate') {
        if (!previous.credential_id || response.id !== previous.credential_id || response.response.userHandle !== Buffer.from(previous.user_handle).toString('base64url')) throw new Error('Unknown owner passkey.');
        const verified = await verifyAuthenticationResponse({ ...expected, response, credential: { id: previous.credential_id, publicKey: new Uint8Array(previous.public_key), counter: previous.counter, transports: JSON.parse(previous.transports) } });
        if (!verified.verified) throw new Error('Passkey verification failed.');
        credential = { id: previous.credential_id, publicKey: previous.public_key, counter: verified.authenticationInfo.newCounter, transports: JSON.parse(previous.transports) };
      } else throw new Error('Invalid ceremony.');
      const session = secret();
      store.assertWritable?.();
      store.transaction(() => {
        prune();
        const current = owner();
        if (kind === 'register') {
          if (!authorizedEnrollment(current, pending.token_hash)) throw new Error('Enrollment unavailable.');
          store.db.prepare('UPDATE auth_owner SET credential_id=?,public_key=?,counter=?,transports=?,enrollment_hash=NULL,enrollment_expires=NULL WHERE singleton=1')
            .run(credential.id, Buffer.from(credential.publicKey), credential.counter, JSON.stringify(credential.transports ?? []));
        } else {
          if (current.credential_id !== previous.credential_id || current.epoch !== previous.epoch || current.counter !== previous.counter) throw new Error('Credential changed during sign-in.');
          store.db.prepare('UPDATE auth_owner SET counter=? WHERE singleton=1').run(credential.counter);
        }
        if (store.db.prepare('SELECT count(*) AS count FROM auth_sessions').get().count >= 100) throw new Error('Too many active sessions.');
        store.db.prepare('INSERT INTO auth_sessions(token_hash,epoch,created_at,last_seen,expires_at) VALUES(?,?,?,?,?)').run(digest(session), current.epoch, now(), now(), now() + 3_600_000);
      });
      return session;
    },
    current(token) {
      if (!validSecret(token)) return false;
      const hash = digest(token), time = now();
      const session = store.db.prepare('SELECT s.last_seen FROM auth_sessions s JOIN auth_owner o ON s.epoch=o.epoch WHERE s.token_hash=? AND s.expires_at>? AND s.last_seen>? AND o.credential_id IS NOT NULL').get(hash, time, time - 900_000);
      if (!session) return false;
      // Limit refresh writes while retaining an independent absolute deadline.
      if (session.last_seen <= time - 30_000) store.db.prepare('UPDATE auth_sessions SET last_seen=? WHERE token_hash=?').run(time, hash);
      return true;
    },
    recent(token) {
      return validSecret(token) && !!store.db.prepare('SELECT 1 FROM auth_sessions s JOIN auth_owner o ON s.epoch=o.epoch WHERE s.token_hash=? AND s.expires_at>? AND s.last_seen>? AND s.created_at>? AND o.credential_id IS NOT NULL').get(digest(token), now(), now() - 900_000, now() - 300_000);
    },
    logout(token) { if (validSecret(token)) store.db.prepare('DELETE FROM auth_sessions WHERE token_hash=?').run(digest(token)); },
    prune() { store.transaction(prune); },
  };
}

export function cookieValue(header, name) {
  const values = String(header ?? '').split(';').map(item => item.trim()).filter(item => item.startsWith(name + '=')).map(item => item.slice(name.length + 1));
  return values.length === 1 ? values[0] : undefined;
}
export function sessionCookie(name, value, maxAge) {
  return `${name}=${value}; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=${maxAge}`;
}
