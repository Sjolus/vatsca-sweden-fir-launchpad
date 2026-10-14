import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { generateRegistrationOptions, generateAuthenticationOptions, verifyRegistrationResponse, verifyAuthenticationResponse } from '@simplewebauthn/server';

export const SESSION_COOKIE = '__Host-launchpad-session';
export const FLOW_COOKIE = '__Host-launchpad-flow';
export const AUTH_PERMISSIONS = Object.freeze(['usage', 'inventory', 'reports', 'errors', 'support', 'health']);
export const ACCESS_CONFLICT_MESSAGE = 'Access changed elsewhere. Refresh the user list before saving.';
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
  store.transaction(() => {
    store.db.exec(`
      CREATE TABLE IF NOT EXISTS auth_owner (
        singleton INTEGER PRIMARY KEY CHECK(singleton=1), user_handle TEXT NOT NULL,
        credential_id TEXT, public_key BLOB, counter INTEGER NOT NULL DEFAULT 0,
        transports TEXT NOT NULL DEFAULT '[]', epoch TEXT NOT NULL,
        enrollment_hash TEXT, enrollment_expires INTEGER,
        CHECK((credential_id IS NULL)=(public_key IS NULL)));
      CREATE TABLE IF NOT EXISTS auth_users (
        id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE COLLATE NOCASE, user_handle TEXT NOT NULL UNIQUE,
        credential_id TEXT UNIQUE, public_key BLOB, counter INTEGER NOT NULL DEFAULT 0,
        transports TEXT NOT NULL DEFAULT '[]', epoch TEXT NOT NULL, enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
        permissions TEXT NOT NULL, invitation_hash TEXT UNIQUE, invitation_expires INTEGER,
        CHECK((credential_id IS NULL)=(public_key IS NULL)));
      CREATE TABLE IF NOT EXISTS auth_challenges (
        id TEXT PRIMARY KEY, kind TEXT NOT NULL, challenge TEXT NOT NULL, binder_hash TEXT NOT NULL,
        token_hash TEXT, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL, consumed INTEGER NOT NULL DEFAULT 0,
        user_id TEXT REFERENCES auth_users(id), epoch TEXT);
      CREATE INDEX IF NOT EXISTS auth_challenges_expiry ON auth_challenges(expires_at);
      CREATE TABLE IF NOT EXISTS auth_sessions (
        token_hash TEXT PRIMARY KEY, epoch TEXT NOT NULL, created_at INTEGER NOT NULL, last_seen INTEGER NOT NULL,
        expires_at INTEGER NOT NULL, user_id TEXT REFERENCES auth_users(id));
      CREATE INDEX IF NOT EXISTS auth_sessions_expiry ON auth_sessions(expires_at);
    `);
    // Existing owner credentials and sessions survive the additive migration.
    // An unfinished ceremony may not cross the old/new challenge format.
    const columns = table => store.db.prepare(`PRAGMA table_info(${table})`).all().map(row => row.name);
    if (!columns('auth_sessions').includes('user_id')) store.db.exec('ALTER TABLE auth_sessions ADD COLUMN user_id TEXT REFERENCES auth_users(id)');
    if (!columns('auth_challenges').includes('user_id')) store.db.exec('ALTER TABLE auth_challenges ADD COLUMN user_id TEXT REFERENCES auth_users(id)');
    if (!columns('auth_challenges').includes('epoch')) store.db.exec('DELETE FROM auth_challenges; ALTER TABLE auth_challenges ADD COLUMN epoch TEXT');
    store.db.prepare('INSERT OR IGNORE INTO auth_owner(singleton,user_handle,epoch) VALUES(1,?,?)').run(randomUUID(), randomUUID());
  });
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

function permissions(value) {
  if (!Array.isArray(value) || value.length > AUTH_PERMISSIONS.length || new Set(value).size !== value.length || value.some(item => !AUTH_PERMISSIONS.includes(item))) throw new Error('Invalid permissions.');
  return AUTH_PERMISSIONS.filter(item => value.includes(item));
}
function username(value) {
  if (typeof value !== 'string' || value.trim() !== value || !/^[a-z][a-z0-9_-]{2,31}$/i.test(value) || value.toLowerCase() === 'maintainer') throw new Error('Invalid username.');
  return value.toLowerCase();
}
function fields(value, allowed) {
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(key => !allowed.includes(key))) throw new Error('Invalid account fields.');
}

export function createAuth({ store, origin, now = () => Date.now() }) {
  const site = exactOrigin(origin);
  initializeAuth(store);
  const owner = () => store.db.prepare('SELECT * FROM auth_owner WHERE singleton=1').get();
  const user = id => uuid(id) ? store.db.prepare('SELECT * FROM auth_users WHERE id=?').get(id) : undefined;
  const principal = id => id === null ? owner() : user(id);
  const enabled = row => !!row && (row.id === undefined || row.enabled === 1);
  const publicUser = row => ({ id: row.id, username: row.username, permissions: permissions(JSON.parse(row.permissions)), enabled: row.enabled === 1, enrolled: !!row.credential_id, invitation_expires_at: row.invitation_expires > now() ? new Date(row.invitation_expires).toISOString() : null });
  function prune() {
    store.db.prepare('DELETE FROM auth_challenges WHERE expires_at<=?').run(now());
    store.db.prepare('DELETE FROM auth_sessions WHERE expires_at<=? OR last_seen<=?').run(now(), now() - 900_000);
    store.db.prepare('UPDATE auth_owner SET enrollment_hash=NULL,enrollment_expires=NULL WHERE enrollment_expires<=?').run(now());
    store.db.prepare('UPDATE auth_users SET invitation_hash=NULL,invitation_expires=NULL WHERE invitation_expires<=?').run(now());
  }
  function authorizedEnrollment(row, hash) {
    if (!enabled(row) || row.credential_id || !hash) return false;
    return row.id === undefined ? row.enrollment_hash === hash && row.enrollment_expires > now() : row.invitation_hash === hash && row.invitation_expires > now();
  }
  function enrollment(hash) {
    if (!hash) return undefined;
    const row = owner();
    if (authorizedEnrollment(row, hash)) return row;
    const invited = store.db.prepare('SELECT * FROM auth_users WHERE invitation_hash=?').get(hash);
    return authorizedEnrollment(invited, hash) ? invited : undefined;
  }
  function invalidateUser(id) {
    store.db.prepare('DELETE FROM auth_sessions WHERE user_id=?').run(id);
    // Discoverable login has no account identity until its signed response arrives.
    store.db.prepare("DELETE FROM auth_challenges WHERE user_id=? OR kind='authenticate'").run(id);
  }
  function invitation(row) {
    const token = secret(), expiry = now() + 600_000;
    invalidateUser(row.id);
    store.db.prepare("UPDATE auth_users SET credential_id=NULL,public_key=NULL,counter=0,transports='[]',epoch=?,invitation_hash=?,invitation_expires=? WHERE id=?")
      .run(randomUUID(), digest(token), expiry, row.id);
    return { user: publicUser(user(row.id)), invitation: token, expires_at: new Date(expiry).toISOString() };
  }
  function applyAccess(row, scopes, nextEnabled) {
    if (scopes !== undefined) store.db.prepare('UPDATE auth_users SET permissions=? WHERE id=?').run(JSON.stringify(scopes), row.id);
    if (nextEnabled !== undefined && nextEnabled !== (row.enabled === 1)) {
      invalidateUser(row.id);
      if (nextEnabled) store.db.prepare('UPDATE auth_users SET enabled=1,epoch=? WHERE id=?').run(randomUUID(), row.id);
      else store.db.prepare('UPDATE auth_users SET enabled=0,epoch=?,invitation_hash=NULL,invitation_expires=NULL WHERE id=?').run(randomUUID(), row.id);
    }
  }
  function sessionIdentity(token, recent = false, refresh = false) {
    if (!validSecret(token)) return false;
    const hash = digest(token), time = now();
    const session = store.db.prepare('SELECT * FROM auth_sessions WHERE token_hash=? AND expires_at>? AND last_seen>?').get(hash, time, time - 900_000);
    if (!session || (recent && session.created_at <= time - 300_000)) return false;
    const row = principal(session.user_id);
    if (!enabled(row) || !row.credential_id || row.epoch !== session.epoch) return false;
    if (refresh && session.last_seen <= time - 30_000) store.db.prepare('UPDATE auth_sessions SET last_seen=? WHERE token_hash=?').run(time, hash);
    return { username: row.id === undefined ? 'maintainer' : row.username, owner: row.id === undefined, permissions: row.id === undefined ? [...AUTH_PERMISSIONS] : permissions(JSON.parse(row.permissions)) };
  }
  return {
    site,
    listUsers() { return store.db.prepare('SELECT * FROM auth_users ORDER BY username').all().map(publicUser); },
    createUser(input) {
      fields(input, ['username', 'permissions']);
      const name = username(input.username), scopes = permissions(input.permissions);
      store.assertWritable?.();
      return store.transaction(() => {
        if (store.db.prepare('SELECT count(*) AS count FROM auth_users').get().count >= 25) throw new Error('The account limit has been reached.');
        if (store.db.prepare('SELECT 1 FROM auth_users WHERE username=? COLLATE NOCASE').get(name)) throw new Error('Username already exists.');
        const id = randomUUID();
        store.db.prepare('INSERT INTO auth_users(id,username,user_handle,epoch,enabled,permissions) VALUES(?,?,?,?,1,?)').run(id, name, randomUUID(), randomUUID(), JSON.stringify(scopes));
        return invitation(user(id));
      });
    },
    updateUser(id, input) {
      fields(input, ['permissions', 'enabled']);
      if (!Object.keys(input).length || ('enabled' in input && typeof input.enabled !== 'boolean')) throw new Error('Invalid account update.');
      const scopes = 'permissions' in input ? permissions(input.permissions) : undefined;
      store.assertWritable?.();
      return store.transaction(() => {
        const row = user(id); if (!row) throw new Error('Unknown account.');
        applyAccess(row, scopes, input.enabled);
        return publicUser(user(id));
      });
    },
    updateUsers(changes) {
      if (!Array.isArray(changes) || changes.length < 1 || changes.length > 25) throw new Error('Invalid account updates.');
      const updates = changes.map(change => {
        fields(change, ['id', 'permissions', 'enabled', 'expected']);
        fields(change.expected, ['permissions', 'enabled']);
        if (!uuid(change.id) || change.id.length !== 36 || typeof change.enabled !== 'boolean' || typeof change.expected.enabled !== 'boolean') throw new Error('Invalid account update.');
        return { id: change.id.toLowerCase(), permissions: permissions(change.permissions), enabled: change.enabled,
          expected: { permissions: permissions(change.expected.permissions), enabled: change.expected.enabled } };
      });
      if (new Set(updates.map(change => change.id)).size !== updates.length) throw new Error('Duplicate account update.');
      store.assertWritable?.();
      return store.transaction(() => {
        // Resolve and compare the entire selection before touching access,
        // sessions or invitations. SQLite also rolls back every effect if a
        // later write fails.
        const rows = updates.map(change => {
          const row = user(change.id);
          if (!row || (row.enabled === 1) !== change.expected.enabled ||
              JSON.stringify(permissions(JSON.parse(row.permissions))) !== JSON.stringify(change.expected.permissions)) {
            const error = new Error(ACCESS_CONFLICT_MESSAGE); error.code = 'access_conflict'; error.status = 409; throw error;
          }
          return row;
        });
        for (let index = 0; index < updates.length; index++) applyAccess(rows[index], updates[index].permissions, updates[index].enabled);
        return store.db.prepare('SELECT * FROM auth_users ORDER BY username').all().map(publicUser);
      });
    },
    inviteUser(id, options = {}) {
      fields(options, ['reset']);
      if ('reset' in options && typeof options.reset !== 'boolean') throw new Error('Invalid reset choice.');
      store.assertWritable?.();
      return store.transaction(() => {
        const row = user(id); if (!row) throw new Error('Account unavailable.');
        if (row.credential_id && options.reset !== true) throw new Error('Explicit passkey reset is required.');
        return invitation(row);
      });
    },
    async begin(kind, token) {
      if (!['register', 'authenticate'].includes(kind)) throw new Error('Invalid ceremony.');
      const tokenHash = kind === 'register' && validSecret(token) ? digest(token) : null;
      const row = kind === 'register' ? enrollment(tokenHash) : undefined;
      if (kind === 'register' && !row) throw new Error('Enrollment unavailable.');
      if (kind === 'authenticate' && !owner().credential_id && !store.db.prepare('SELECT 1 FROM auth_users WHERE enabled=1 AND credential_id IS NOT NULL LIMIT 1').get()) throw new Error('Sign-in unavailable.');
      const options = kind === 'register'
        ? await generateRegistrationOptions({ rpName: 'Sweden FIR Launchpad', rpID: site.rpID, userName: row.username ?? 'maintainer', userDisplayName: row.username ?? 'Launchpad maintainer', userID: Buffer.from(row.user_handle), attestationType: 'none', authenticatorSelection: { residentKey: 'required', userVerification: 'required' }, timeout: 60_000 })
        : await generateAuthenticationOptions({ rpID: site.rpID, userVerification: 'required', timeout: 60_000 });
      const id = randomUUID(), binder = secret();
      store.assertWritable?.();
      store.transaction(() => {
        prune();
        if (kind === 'register') {
          const current = principal(row.id ?? null);
          if (!authorizedEnrollment(current, tokenHash) || current.epoch !== row.epoch) throw new Error('Enrollment unavailable.');
        }
        const counts = store.db.prepare('SELECT count(*) AS total,sum(created_at>?) AS recent FROM auth_challenges').get(now() - 60_000);
        if (counts.total >= 100 || counts.recent >= 20) throw new Error('Sign-in is busy.');
        store.db.prepare('INSERT INTO auth_challenges(id,kind,challenge,binder_hash,token_hash,created_at,expires_at,user_id,epoch) VALUES(?,?,?,?,?,?,?,?,?)')
          .run(id, kind, options.challenge, digest(binder), tokenHash, now(), now() + 300_000, row?.id ?? null, row?.epoch ?? null);
      });
      return { options, cookie: `${id}.${binder}` };
    },
    async finish(kind, cookie, response) {
      if (!['register', 'authenticate'].includes(kind) || typeof cookie !== 'string' || cookie.length !== 80) throw new Error('Invalid flow.');
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
      let credential, previous;
      if (kind === 'register') {
        previous = principal(pending.user_id);
        if (!authorizedEnrollment(previous, pending.token_hash) || previous.epoch !== pending.epoch) throw new Error('Enrollment unavailable.');
        const verified = await verifyRegistrationResponse({ ...expected, response });
        if (!verified.verified || !verified.registrationInfo) throw new Error('Passkey verification failed.');
        credential = verified.registrationInfo.credential;
      } else {
        const ownerRow = owner();
        previous = ownerRow.credential_id === response.id ? ownerRow : store.db.prepare('SELECT * FROM auth_users WHERE credential_id=?').get(response.id);
        if (!enabled(previous) || !previous.credential_id || response.response.userHandle !== Buffer.from(previous.user_handle).toString('base64url')) throw new Error('Unknown passkey.');
        const verified = await verifyAuthenticationResponse({ ...expected, response, credential: { id: previous.credential_id, publicKey: new Uint8Array(previous.public_key), counter: previous.counter, transports: JSON.parse(previous.transports) } });
        if (!verified.verified) throw new Error('Passkey verification failed.');
        credential = { id: previous.credential_id, publicKey: previous.public_key, counter: verified.authenticationInfo.newCounter, transports: JSON.parse(previous.transports) };
      }
      const session = secret();
      store.assertWritable?.();
      store.transaction(() => {
        prune();
        if (!store.db.prepare('SELECT 1 FROM auth_challenges WHERE id=? AND consumed=1 AND expires_at>?').get(id, now())) throw new Error('Sign-in expired or changed.');
        const current = principal(previous.id ?? null);
        if (!enabled(current) || current.epoch !== previous.epoch) throw new Error('Credential changed during sign-in.');
        if (kind === 'register') {
          if (!authorizedEnrollment(current, pending.token_hash)) throw new Error('Enrollment unavailable.');
          if (owner().credential_id === credential.id || store.db.prepare('SELECT 1 FROM auth_users WHERE credential_id=?').get(credential.id)) throw new Error('Passkey already enrolled.');
          const values = [credential.id, Buffer.from(credential.publicKey), credential.counter, JSON.stringify(credential.transports ?? [])];
          if (current.id === undefined) store.db.prepare('UPDATE auth_owner SET credential_id=?,public_key=?,counter=?,transports=?,enrollment_hash=NULL,enrollment_expires=NULL WHERE singleton=1').run(...values);
          else store.db.prepare('UPDATE auth_users SET credential_id=?,public_key=?,counter=?,transports=?,invitation_hash=NULL,invitation_expires=NULL WHERE id=?').run(...values, current.id);
        } else {
          if (current.credential_id !== previous.credential_id || current.counter !== previous.counter) throw new Error('Credential changed during sign-in.');
          if (current.id === undefined) store.db.prepare('UPDATE auth_owner SET counter=? WHERE singleton=1').run(credential.counter);
          else store.db.prepare('UPDATE auth_users SET counter=? WHERE id=?').run(credential.counter, current.id);
        }
        if (current.id !== undefined) {
          const ownSessions = store.db.prepare('SELECT token_hash FROM auth_sessions WHERE user_id=? ORDER BY created_at,token_hash').all(current.id);
          for (const old of ownSessions.slice(0, Math.max(0, ownSessions.length - 4))) store.db.prepare('DELETE FROM auth_sessions WHERE token_hash=?').run(old.token_hash);
          // Leave space for owner access even if guests deliberately sign in
          // repeatedly. Only this user's oldest sessions can be replaced.
          if (store.db.prepare('SELECT count(*) AS count FROM auth_sessions').get().count >= 80) throw new Error('Too many active sessions.');
        } else if (store.db.prepare('SELECT count(*) AS count FROM auth_sessions').get().count >= 100) {
          store.db.prepare('DELETE FROM auth_sessions WHERE token_hash=(SELECT token_hash FROM auth_sessions WHERE user_id IS NULL ORDER BY created_at,token_hash LIMIT 1)').run();
        }
        if (store.db.prepare('SELECT count(*) AS count FROM auth_sessions').get().count >= 100) throw new Error('Too many active sessions.');
        store.db.prepare('INSERT INTO auth_sessions(token_hash,epoch,created_at,last_seen,expires_at,user_id) VALUES(?,?,?,?,?,?)').run(digest(session), current.epoch, now(), now(), now() + 3_600_000, current.id ?? null);
      });
      return session;
    },
    identity(token) { return sessionIdentity(token, false, true); },
    current(token) { return !!sessionIdentity(token, false, true); },
    recent(token) { return !!sessionIdentity(token, true); },
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
