'use strict';
const $ = id => document.getElementById(id);
const title = value => String(value ?? 'Not reported').replaceAll('_', ' ');
const element = (tag, text, className) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (className) node.className = className; return node; };
const time = value => { if (!value) return '—'; const date = new Date(typeof value === 'number' ? value : /^\d+$/.test(value) ? Number(value) : value); return Number.isNaN(date.valueOf()) ? '—' : date.toISOString().replace('T', ' ').slice(0, 16); };
const bytes = value => Number.isFinite(Number(value)) ? `${(Number(value) / 1024 / 1024).toFixed(1)} MiB` : 'Unavailable';
let signedIn = false, enrollment = false, currentView = 'overview', offset = 0, selectedReport, lookup = '', generation = 0, authGeneration = 0, reportGeneration = 0;
let idleDisplayTimer;
function expirePrivateDisplay() { clearTimeout(idleDisplayTimer); idleDisplayTimer = setTimeout(() => { showLogin(); message('Your session has been idle. Sign in again to continue.'); }, 900_000); }

async function request(path, body) {
  const identity = authGeneration;
  const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, credentials: 'same-origin', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (path.startsWith('/api/admin/') && (identity !== authGeneration || !signedIn)) throw new Error('Sign in to view these records.');
  if (!response.ok) {
    if (response.status === 401) showLogin();
    throw new Error(data.error || 'The request could not be completed.');
  }
  if (path.startsWith('/api/admin/')) expirePrivateDisplay();
  return data;
}
function message(text, error = false) { $('message').textContent = text; $('message').classList.toggle('error', error); }
function showLogin() {
  signedIn = false; generation++; authGeneration++; reportGeneration++;
  clearTimeout(idleDisplayTimer);
  $('workspace').hidden = true; $('sign-out').hidden = true; $('login').hidden = false;
  $('view').replaceChildren(); $('report-details').replaceChildren(); $('report-dialog').close();
  selectedReport = null; lookup = ''; $('support-id').value = '';
}
function showWorkspace() { signedIn = true; $('login').hidden = true; $('workspace').hidden = false; $('sign-out').hidden = false; expirePrivateDisplay(); message(''); }
function table(rows, columns, { empty = 'No matching records.', open } = {}) {
  if (!rows?.length) return element('div', empty, 'empty');
  const wrapper = element('div', undefined, 'table-wrap'), table = element('table'), head = element('thead'), heading = element('tr'), body = element('tbody');
  if (columns.length + (open ? 1 : 0) > 4) table.className = 'rich-table';
  for (const column of columns) heading.append(element('th', column.label));
  if (open) heading.append(element('th', 'Details'));
  head.append(heading); table.append(head, body);
  for (const row of rows) {
    const tr = element('tr');
    for (const column of columns) { const value = column.render ? column.render(row[column.key], row) : title(row[column.key]); const cell = element('td'); cell.dataset.label = column.label; cell.append(value instanceof Node ? value : document.createTextNode(String(value))); tr.append(cell); }
    if (open) { const cell = element('td'), button = element('button', 'Open report'); cell.dataset.label = 'Details'; button.addEventListener('click', () => open(row)); button.setAttribute('aria-label', `Open report ${row.reference}`); cell.append(button); tr.append(cell); }
    body.append(tr);
  }
  wrapper.append(table); return wrapper;
}
const reportColumns = [{ key: 'received_at', label: 'Received · UTC', render: time }, { key: 'application', label: 'Application' }, { key: 'category', label: 'Category' }, { key: 'launchpad_version', label: 'Launchpad' }, { key: 'status', label: 'Status', render: (value, row) => { const badge = element('span', title(value), `badge ${value}`); if (['new', 'investigating'].includes(value) && Number(row.updated_at) < Date.now() - 365 * 86400_000) { const wrapper = element('div'); wrapper.append(badge, element('small', 'Review needed · inactive over 12 months', 'review-needed')); return wrapper; } return badge; } }];
const eventColumns = [{ key: 'occurred_at', label: 'When · UTC', render: time }, { key: 'application', label: 'Application' }, { key: 'event_name', label: 'Event' }, { key: 'outcome', label: 'Outcome' }, { key: 'error_code', label: 'Error code' }];
function cards(items) { const row = element('div', undefined, 'cards'); for (const [label, value] of items) { const card = element('div', undefined, 'card'); card.append(element('span', label), element('strong', String(value ?? '—'))); row.append(card); } return row; }
function section(parent, heading, node) { const group = element('section', undefined, 'section'); group.append(element('h2', heading), node); parent.append(group); }
function details(data) { const list = element('dl', undefined, 'detail-list'); for (const [key, value] of Object.entries(data)) { const date = /_at$/.test(key); list.append(element('dt', title(key) + (date ? ' · UTC' : '')), element('dd', value === null ? '—' : date ? time(value) : typeof value === 'object' ? JSON.stringify(value) : String(value ?? '—'))); } return list; }
function filters() {
  const query = new URLSearchParams();
  if ($('days-filter').value) query.set('days', $('days-filter').value);
  if ($('application-filter').value) query.set('application', $('application-filter').value);
  if ($('version-filter').value.trim()) query.set('launchpad_version', $('version-filter').value.trim());
  return query;
}
async function load() {
  if (!signedIn) return;
  $('all-days-option').disabled = ['overview', 'inventory'].includes(currentView);
  if ($('all-days-option').disabled && !$('days-filter').value) $('days-filter').value = '7';
  const ticket = ++generation, view = $('view'), query = filters();
  view.replaceChildren(element('div', 'Loading observations…', 'empty'));
  $('refresh').disabled = true; message('');
  const names = { overview: ['How Launchpad is used', 'Adoption, everyday use and the tools controllers choose. Usage reporting is optional.'], inventory: ['Software & plugin versions', 'Known applications and EuroScope plugins from a separate optional inventory. Observed versions are not a live compatibility check.'], reports: ['Reports', 'Structured reports submitted by users. No free-text descriptions or attachments are collected.'], errors: ['Grouped errors', 'Compare stable error codes across applications and releases. Public submissions are unverified and may be sampled or throttled.'], support: ['Support lookup', 'Paste the support ID voluntarily shared by the user. This private lookup is limited to the most recent 100 reports and 100 events.'], health: ['Service health', 'Logical admission limits and physical storage headroom. Backup and infrastructure readiness require operator verification.'] };
  $('view-title').textContent = names[currentView][0]; $('view-description').textContent = names[currentView][1];
  $('filters').hidden = !['overview', 'inventory', 'reports', 'errors'].includes(currentView); $('status-filter-label').hidden = currentView !== 'reports';
  $('support-form').hidden = currentView !== 'support'; $('pagination').hidden = true;
  try {
    const fragment = document.createDocumentFragment();
    if (currentView === 'overview' || currentView === 'inventory') {
      const data = await request(`/api/admin/usage?${query}`);
      fragment.append(currentView === 'overview' ? renderUsageDashboard(data) : renderInventory(data));
    } else if (currentView === 'reports') {
      if ($('status-filter').value) query.set('status', $('status-filter').value); query.set('offset', String(offset));
      const data = await request(`/api/admin/reports?${query}`);
      if (ticket !== generation) return;
      fragment.append(table(data, reportColumns, { open: openReport }));
      $('pagination').hidden = false; $('previous').disabled = offset === 0; $('next').disabled = data.length < 50; $('page-label').textContent = `Page ${offset / 50 + 1}`;
    } else if (currentView === 'errors') {
      const data = await request(`/api/admin/errors?${query}`);
      fragment.append(table(data, [{ key: 'application', label: 'Application' }, { key: 'error_code', label: 'Error code' }, { key: 'operation', label: 'Operation' }, { key: 'stage', label: 'Stage' }, { key: 'launchpad_version', label: 'Launchpad' }, { key: 'count', label: 'Records' }, { key: 'last_seen', label: 'Last seen · UTC', render: time }]));
    } else if (currentView === 'health') {
      const data = await request('/api/admin/health');
      fragment.append(cards([['Storage admission', data.ok ? 'Ready' : 'Needs attention'], ['Database size', bytes(data.physical_bytes)], ['Database ceiling', bytes(data.max_db_bytes)], ['Free disk space', bytes(data.free_bytes)]]));
      section(fragment, 'Storage safeguards', details({ reserved_free_space: bytes(data.reserved_free_bytes), counter_rows: data.counter_rows, burst_rows: data.burst_rows }));
      if (data.storage) section(fragment, 'Current logical use', table(Object.entries(data.storage).map(([stream, counts]) => ({ stream, ...counts })), [{ key: 'stream', label: 'Stream' }, { key: 'count', label: 'Records' }, { key: 'bytes', label: 'Payload size', render: bytes }]));
      if (data.aggregate) section(fragment, 'Historical count storage', details({ groups: data.aggregate.rows, group_ceiling: data.aggregate.max_rows, size: bytes(data.aggregate.bytes), size_ceiling: bytes(data.aggregate.max_bytes), retention_days: data.aggregate.retention_days, omitted_at_capacity: data.aggregate.limited_count }));
      if (data.retention) section(fragment, 'Retention maintenance', details({ last_success_UTC: time(data.retention.last_success), last_failure_UTC: time(data.retention.last_failure), expired_events: data.retention.expired_events, usage_admission_paused: data.usage_paused ? 'Yes' : 'No' }));
      fragment.append(element('p', 'A healthy API is not proof of a recent restorable backup. Check the deployment’s backup and expiry monitoring separately.', 'muted'));
    } else if (lookup) {
      const supportId = lookup;
      const data = await request(`/api/admin/support/${encodeURIComponent(supportId)}`);
      fragment.append(element('p', supportId)); section(fragment, 'Reports', table(data.reports, reportColumns, { open: openReport })); section(fragment, 'Recent events', table(data.events, eventColumns));
      const removal = element('details'), summary = element('summary', 'Delete records for this support ID'), explanation = element('p', 'This permanently deletes current service records for this ID. It does not recall existing backups. Verify the request first; a sign-in within the last five minutes is required.');
      const button = element('button', 'Delete current records', 'danger');
      button.addEventListener('click', async () => {
        if (!confirm(`Delete current service records for ${supportId}? This cannot be undone here.`)) return;
        button.disabled = true;
        try { await request(`/api/admin/support/${encodeURIComponent(supportId)}/delete`, { confirm: supportId }); lookup = ''; $('support-id').value = ''; await load(); message('Current service records deleted. Existing backup copies follow their separate expiry policy.'); } catch (error) { message(error.message, true); } finally { button.disabled = false; }
      });
      removal.append(summary, explanation, button); fragment.append(removal);
    } else fragment.append(element('div', 'Enter a support ID above to find related records.', 'empty'));
    if (ticket === generation) view.replaceChildren(fragment);
  } catch (error) { if (ticket === generation) { view.replaceChildren(element('div', 'Observations could not be loaded. Refresh to try again.', 'empty')); message(error.message, true); } }
  finally { if (ticket === generation) $('refresh').disabled = false; }
}
async function openReport(row) {
  const ticket = ++reportGeneration, identity = authGeneration;
  try {
    const report = await request(`/api/admin/reports/${encodeURIComponent(row.reference)}`);
    if (ticket !== reportGeneration || identity !== authGeneration || !signedIn) return;
    selectedReport = report;
    $('report-title').textContent = 'Report details'; $('report-message').textContent = '';
    const { payload, ...metadata } = selectedReport; $('report-details').replaceChildren(details(metadata));
    if (payload) { const disclosure = element('details'), summary = element('summary', 'Exact submitted technical fields'); disclosure.append(summary, element('pre', JSON.stringify(payload, null, 2))); $('report-details').append(disclosure); }
    $('report-status').value = selectedReport.status; $('report-dialog').showModal();
  } catch (error) { message(error.message, true); }
}

async function boot() {
  if (document.body.dataset.page === 'privacy') {
    try { const data = await request('/privacy/info'); $('operator').textContent = `Operated by ${data.operator}.`; $('contact').textContent = data.contact; }
    catch { $('operator').textContent = 'Contact the Launchpad project maintainer for operator information and deletion requests.'; }
    return;
  }
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault(); const button = $('login-submit'); button.disabled = true; message('Follow the passkey prompt on your device.');
    try {
      const library = window.SimpleWebAuthnBrowser;
      if (!library?.browserSupportsWebAuthn()) throw new Error('This browser does not support passkeys. Use a current browser on the HTTPS Launchpad site.');
      const kind = enrollment ? 'register' : 'authenticate';
      const options = await request(`/auth/${kind}-options`, enrollment ? { enrollment_code: $('enrollment').value.trim() } : {});
      const response = enrollment ? await library.startRegistration({ optionsJSON: options }) : await library.startAuthentication({ optionsJSON: options });
      await request(`/auth/${kind}-verify`, { response }); $('enrollment').value = ''; showWorkspace(); await load();
    } catch (error) { message(error.name === 'NotAllowedError' ? 'The passkey prompt was cancelled or timed out. Try again when ready.' : error.message, true); }
    finally { button.disabled = false; }
  });
  $('enroll-toggle').addEventListener('click', () => { enrollment = !enrollment; $('enroll-field').hidden = !enrollment; $('enrollment').required = enrollment; $('enrollment').value = ''; $('login-title').textContent = enrollment ? 'Set up your passkey' : 'Welcome back'; $('login-submit').textContent = enrollment ? 'Create maintainer passkey' : 'Sign in with a passkey'; $('enroll-toggle').textContent = enrollment ? 'Back to sign-in' : 'First visit? Set up your passkey'; message(''); if (enrollment) $('enrollment').focus(); });
  $('sign-out').addEventListener('click', async () => { try { await request('/auth/logout', {}); showLogin(); message('Signed out.'); } catch (error) { message(error.message, true); } });
  document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {
    const previous = currentView; currentView = button.dataset.view;
    if (!['overview', 'inventory'].includes(previous) || !['overview', 'inventory'].includes(currentView)) $('days-filter').value = currentView === 'reports' ? '' : ['overview', 'inventory'].includes(currentView) ? '7' : '30';
    offset = 0; document.querySelectorAll('[data-view]').forEach(item => item.removeAttribute('aria-current')); button.setAttribute('aria-current', 'page'); load();
  }));
  $('refresh').addEventListener('click', load);
  $('filters').addEventListener('submit', event => { event.preventDefault(); offset = 0; load(); });
  $('reset-filters').addEventListener('click', () => { $('status-filter').value = ''; $('application-filter').value = ''; $('version-filter').value = ''; $('days-filter').value = currentView === 'reports' ? '' : ['overview', 'inventory'].includes(currentView) ? '7' : '30'; offset = 0; load(); });
  $('support-form').addEventListener('submit', event => { event.preventDefault(); const value = $('support-id').value.trim().toLowerCase(); if (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value)) { message('Enter a complete support UUID from Launchpad.', true); return; } lookup = value; load(); });
  $('previous').addEventListener('click', () => { offset = Math.max(0, offset - 50); load(); }); $('next').addEventListener('click', () => { offset += 50; load(); });
  $('close-report').addEventListener('click', () => $('report-dialog').close());
  $('report-status-form').addEventListener('submit', async event => { event.preventDefault(); if (!selectedReport) return; try { selectedReport = await request(`/api/admin/reports/${encodeURIComponent(selectedReport.reference)}/status`, { status: $('report-status').value }); $('report-message').textContent = 'Status saved.'; await load(); } catch (error) { $('report-message').textContent = error.message; } });
  try { if ((await request('/auth/session')).signed_in) { showWorkspace(); await load(); } else showLogin(); } catch (error) { showLogin(); message('The service could not be reached. Reload to try again.', true); }
}
boot();
