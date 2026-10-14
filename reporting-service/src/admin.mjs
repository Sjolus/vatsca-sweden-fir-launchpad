import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
import { createRequire } from 'node:module';
import { createAuth, cookieValue, sessionCookie, SESSION_COOKIE, FLOW_COOKIE } from './auth.mjs';
import { VERSION } from './schema.mjs';
import { readJson } from './intake.mjs';

const publicDirectory = fileURLToPath(new URL('../public/', import.meta.url));
const require = createRequire(import.meta.url);
const uuid = value => /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
const statuses = ['new', 'investigating', 'fixed', 'closed'];
const headers = {
  'Cache-Control': 'private, no-store', 'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer', 'X-Frame-Options': 'DENY', 'X-Robots-Tag': 'noindex, nofollow',
  'Content-Security-Policy': "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), publickey-credentials-get=(self), publickey-credentials-create=(self)',
};
function reply(res, status, value, extra = {}) {
  if (res.writableEnded || res.destroyed) return;
  res.writeHead(status, { ...headers, 'Content-Type': 'application/json; charset=utf-8', ...extra });
  res.end(JSON.stringify(value));
}
function sameOrigin(req, origin) {
  return req.headers.origin === origin && req.headers['sec-fetch-site'] !== 'cross-site' && req.headers['content-type']?.split(';')[0].trim() === 'application/json' && !req.headers['content-encoding'];
}
async function json(req) {
  const value = await readJson(req, 32768);
  if (!value || Array.isArray(value) || typeof value !== 'object') throw new Error('Invalid object');
  return value;
}
function onlyKeys(value, allowed) { if (Object.keys(value).some(key => !allowed.includes(key))) throw new Error('Unexpected field'); }
function browserBundle() {
  const entry = require.resolve('@simplewebauthn/browser');
  return resolve(dirname(entry), '../dist/bundle/index.umd.min.js');
}

export function createAdminHandler({ store, config, now = () => Date.now() }) {
  const auth = createAuth({ store, origin: config.origin, now });
  store.db.exec('CREATE TABLE IF NOT EXISTS admin_audit(id INTEGER PRIMARY KEY,occurred_at INTEGER NOT NULL,action TEXT NOT NULL,target TEXT);');
  function audit(action, target = null) {
    store.transaction(() => {
      store.db.prepare('DELETE FROM admin_audit WHERE occurred_at<?').run(now() - 30 * 86400_000);
      store.db.exec('DELETE FROM admin_audit WHERE id NOT IN (SELECT id FROM admin_audit ORDER BY id DESC LIMIT 999);');
      store.db.prepare('INSERT INTO admin_audit(occurred_at,action,target) VALUES(?,?,?)').run(now(), action, target);
    });
  }
  const staticFiles = new Map([
    ['/backoffice/', ['index.html', 'text/html; charset=utf-8']],
    ['/backoffice', ['index.html', 'text/html; charset=utf-8']],
    ['/backoffice/app.js', ['app.js', 'text/javascript; charset=utf-8']],
    ['/backoffice/usage.js', ['usage.js', 'text/javascript; charset=utf-8']],
    ['/backoffice/style.css', ['style.css', 'text/css; charset=utf-8']],
    ['/backoffice/logo.png', ['logo.png', 'image/png']],
    ['/privacy', ['privacy.html', 'text/html; charset=utf-8']],
    ['/privacy/', ['privacy.html', 'text/html; charset=utf-8']],
  ]);
  async function adminHandler(req, res) {
    const url = new URL(req.url, config.origin), path = url.pathname;
    if (path === '/' && req.method === 'GET') { res.writeHead(302, { ...headers, Location: '/backoffice/' }); res.end(); return true; }
    if (req.method === 'GET' && path === '/privacy/info') {
      reply(res, 200, { operator: config.privacyOperator || 'Launchpad maintainer', contact: config.privacyContact || 'Contact the project maintainer through the Launchpad repository.' }); return true;
    }
    if (req.method === 'GET' && (staticFiles.has(path) || path === '/backoffice/passkey.js')) {
      try {
        const [name, type] = staticFiles.get(path) ?? [null, 'text/javascript; charset=utf-8'];
        const file = name ? resolve(publicDirectory, name) : browserBundle();
        const data = readFileSync(file);
        res.writeHead(200, { ...headers, 'Content-Type': type }); res.end(data);
      } catch { reply(res, 503, { error: 'The page is temporarily unavailable.' }); }
      return true;
    }
    if (!path.startsWith('/auth/') && !path.startsWith('/api/admin/')) return false;
    const token = cookieValue(req.headers.cookie, SESSION_COOKIE);
    try {
      if (path === '/auth/session' && req.method === 'GET') {
        reply(res, 200, { signed_in: !!auth.current(token) }); return true;
      }
      if (path.startsWith('/auth/')) {
        if (req.method !== 'POST') { reply(res, 405, { error: 'Use POST.' }, { Allow: 'POST' }); return true; }
        if (!sameOrigin(req, config.origin)) { reply(res, 403, { error: 'Open this page on the configured Launchpad site.' }); return true; }
        const body = await json(req), action = path.slice('/auth/'.length);
        if (action === 'logout') {
          onlyKeys(body, []); auth.logout(token);
          reply(res, 200, { ok: true }, { 'Set-Cookie': [sessionCookie(SESSION_COOKIE, '', 0), sessionCookie(FLOW_COOKIE, '', 0)] }); return true;
        }
        if (!['register-options', 'register-verify', 'authenticate-options', 'authenticate-verify'].includes(action)) { reply(res, 404, { error: 'Not found.' }); return true; }
        const kind = action.startsWith('register-') ? 'register' : 'authenticate';
        if (action.endsWith('-options')) {
          onlyKeys(body, kind === 'register' ? ['enrollment_code'] : []);
          const flow = await auth.begin(kind, body.enrollment_code);
          reply(res, 200, flow.options, { 'Set-Cookie': sessionCookie(FLOW_COOKIE, flow.cookie, 300) });
        } else {
          onlyKeys(body, ['response']);
          const session = await auth.finish(kind, cookieValue(req.headers.cookie, FLOW_COOKIE), body.response);
          audit(kind === 'register' ? 'owner_enrolled' : 'signed_in');
          reply(res, 200, { ok: true }, { 'Set-Cookie': [sessionCookie(SESSION_COOKIE, session, 3600), sessionCookie(FLOW_COOKIE, '', 0)] });
        }
        return true;
      }
      if (!auth.current(token)) { reply(res, 401, { error: 'Sign in with your maintainer passkey.' }); return true; }
      if (req.method === 'GET') {
        const application = url.searchParams.get('application') || undefined;
        const launchpad_version = url.searchParams.get('launchpad_version') || undefined;
        const days = url.searchParams.has('days') ? Number(url.searchParams.get('days')) : undefined;
        if ((application && !['launchpad','euroscope','gng','trackaudio','vacs','vatis','vatefs','vatiris','maintenance'].includes(application)) || (launchpad_version && (launchpad_version.length > 40 || !VERSION.test(launchpad_version))) || (days !== undefined && ![7,30].includes(days))) throw new Error('Invalid filters');
        if (path === '/api/admin/overview') { reply(res, 200, store.overview({ application, launchpad_version, days })); return true; }
        if (path === '/api/admin/usage') {
          if ([...url.searchParams.keys()].some(key => !['application', 'launchpad_version', 'days'].includes(key)) || [...url.searchParams.keys()].some(key => url.searchParams.getAll(key).length !== 1)) throw new Error('Invalid filters');
          reply(res, 200, store.usage({ application, launchpad_version, days: days ?? 7 })); return true;
        }
        if (path === '/api/admin/health') { reply(res, 200, store.health()); return true; }
        if (path === '/api/admin/errors') { audit('errors_viewed'); reply(res, 200, store.groupedErrors({ limit: 50, application, launchpad_version, days })); return true; }
        if (path === '/api/admin/reports') {
          const status = url.searchParams.get('status') || undefined;
          const offset = Number(url.searchParams.get('offset') || '0');
          if ((status && !statuses.includes(status)) || !Number.isInteger(offset) || offset < 0 || offset > 10_000) throw new Error('Invalid filters');
          audit('reports_viewed'); reply(res, 200, store.listReports({ status, limit: 50, offset, application, launchpad_version, days })); return true;
        }
        const support = /^\/api\/admin\/support\/([^/]+)$/.exec(path);
        if (support && uuid(support[1])) {
          const records = store.lookupSupport(support[1]);
          if (records.reports.length || records.events.length) audit('support_viewed', support[1]);
          else audit('support_lookup_empty');
          reply(res, 200, records); return true;
        }
        const report = /^\/api\/admin\/reports\/([A-Za-z0-9-]{1,80})$/.exec(path);
        if (report) { const found = store.getReport(report[1]); if (found) audit('report_viewed', report[1]); reply(res, found ? 200 : 404, found ?? { error: 'Report not found.' }); return true; }
      } else if (req.method === 'POST') {
        if (!sameOrigin(req, config.origin)) { reply(res, 403, { error: 'Use the Launchpad backoffice for changes.' }); return true; }
        const body = await json(req);
        const report = /^\/api\/admin\/reports\/([A-Za-z0-9-]{1,80})\/status$/.exec(path);
        if (report) {
          onlyKeys(body, ['status']); if (!statuses.includes(body.status)) throw new Error('Invalid status');
          const found = store.setReportStatus(report[1], body.status);
          if (found) audit('status_' + body.status, report[1]);
          reply(res, found ? 200 : 404, found ?? { error: 'Report not found.' }); return true;
        }
        const support = /^\/api\/admin\/support\/([^/]+)\/delete$/.exec(path);
        if (support && uuid(support[1])) {
          onlyKeys(body, ['confirm']);
          if (!auth.recent(token)) { reply(res, 403, { error: 'Sign out and sign in again before deleting records.' }); return true; }
          if (body.confirm !== support[1]) throw new Error('Confirm the support ID');
          store.transaction(() => {
            store.db.prepare('DELETE FROM admin_audit WHERE lower(target)=? OR target IN (SELECT reference FROM reports WHERE support_id=?)').run(support[1].toLowerCase(), support[1].toLowerCase());
          });
          const result = store.deleteSupport(support[1]); audit('support_deleted'); reply(res, 200, result); return true;
        }
      }
      reply(res, 404, { error: 'Not found.' });
    } catch (error) {
      const status = [413, 429, 503].includes(error.status) ? error.status : String(error.code ?? '').startsWith('ERR_SQLITE') ? 503 : 400;
      const retry = status === 429 && Number.isFinite(error.retryAfter) ? { 'Retry-After': String(Math.min(3600, Math.max(1, Math.ceil(error.retryAfter)))) } : {};
      const message = status === 429 ? 'Too many requests. Wait before trying again.' : status === 503 ? 'The service is temporarily unavailable.' : path.startsWith('/auth/') ? 'Sign-in could not be completed. Try again; enrollment codes expire after ten minutes.' : 'This request could not be completed. Check the selection and try again.';
      reply(res, status, { error: message }, { ...retry, ...([413, 429].includes(status) ? { Connection: 'close' } : {}), ...(path.startsWith('/auth/') ? { 'Set-Cookie': sessionCookie(FLOW_COOKIE, '', 0) } : {}) });
    }
    return true;
  }
  adminHandler.prune = () => {
    auth.prune();
    store.db.prepare('DELETE FROM admin_audit WHERE occurred_at<?').run(now() - 30 * 86400_000);
  };
  return adminHandler;
}
