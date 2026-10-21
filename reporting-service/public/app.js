'use strict';
const $ = id => document.getElementById(id);
const title = value => String(value ?? 'Not reported').replaceAll('_', ' ');
const element = (tag, text, className) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (className) node.className = className; return node; };
const time = value => { if (!value) return '—'; const date = new Date(typeof value === 'number' ? value : /^\d+$/.test(value) ? Number(value) : value); return Number.isNaN(date.valueOf()) ? '—' : date.toISOString().replace('T', ' ').slice(0, 16); };
const bytes = value => Number.isFinite(Number(value)) ? `${(Number(value) / 1024 / 1024).toFixed(1)} MiB` : 'Unavailable';
const permissionNames = { usage: 'Usage overview', inventory: 'Software & plugins', reports: 'Reports', errors: 'Errors', support: 'Support lookup', health: 'Service health' };
const viewPermission = { overview: 'usage', inventory: 'inventory', reports: 'reports', errors: 'errors', support: 'support', health: 'health' };
let signedIn = false, owner = false, permissions = new Set(), enrollment = false, currentView = 'overview', offset = 0, selectedReport, lookup = '', generation = 0, authGeneration = 0, reportGeneration = 0;
let idleDisplayTimer, permissionRefresh, invitationToken = '', signInAttempt = 0;
let userAccessState = null;
function leaveUserAccess() {
  if (!userAccessState) return true;
  if (userAccessState.busy) { message('Wait for the current access change to finish.'); return false; }
  if (userAccessState.dirtyCount() && !confirm('Discard your unsaved access changes?')) return false;
  userAccessState = null; return true;
}
function canView(view) { return signedIn && (view === 'users' ? owner : owner || permissions.has(viewPermission[view])); }
function captureInvitation() {
  const location = window.location, fragment = new URLSearchParams(location.hash.slice(1));
  if (!fragment.has('invite')) return;
  const token = fragment.get('invite');
  // Remove the fragment before any request or ceremony. Keep the one-use secret
  // in memory only; never copy it to a query, storage, analytics or page text.
  window.history.replaceState(null, '', location.pathname + location.search);
  if (/^[A-Za-z0-9_-]{43}$/.test(token ?? '')) invitationToken = token;
}
function expirePrivateDisplay() { clearTimeout(idleDisplayTimer); idleDisplayTimer = setTimeout(() => { showLogin(); message('Your session has been idle. Sign in again to continue.'); }, 900_000); }

async function request(path, body) {
  const identity = authGeneration;
  const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, credentials: 'same-origin', cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json();
  if (path.startsWith('/api/admin/') && (identity !== authGeneration || !signedIn)) throw new Error('Sign in to view these records.');
  if (!response.ok) {
    if (response.status === 401) showLogin();
    if (response.status === 403 && path.startsWith('/api/admin/') && data.code !== 'reauthentication_required') await refreshPermissions();
    const error = new Error(data.error || 'The request could not be completed.'); error.code = data.code; throw error;
  }
  if (path.startsWith('/api/admin/')) expirePrivateDisplay();
  return data;
}
function message(text, error = false) { $('message').textContent = text; $('message').classList.toggle('error', error); }
function clearPrivateDisplay() {
  generation++; authGeneration++; reportGeneration++;
  userAccessState = null;
  $('view').replaceChildren(); $('report-details').replaceChildren(); $('report-dialog').close();
  selectedReport = null; lookup = ''; $('support-id').value = '';
}
function showLogin() {
  signInAttempt++; signedIn = false; owner = false; permissions = new Set(); clearPrivateDisplay();
  clearTimeout(idleDisplayTimer);
  $('workspace').hidden = true; $('sign-out').hidden = true; $('login').hidden = false; $('signed-in-user').textContent = '';
}
function showWorkspace(session) {
  if (!session?.authenticated) { showLogin(); return false; }
  clearPrivateDisplay(); signedIn = true; owner = session.owner === true;
  permissions = new Set((session.permissions ?? []).filter(value => Object.hasOwn(permissionNames, value)));
  $('signed-in-user').textContent = `${session.username}${owner ? ' · Owner' : ''}`;
  $('login').hidden = true; $('workspace').hidden = false; $('sign-out').hidden = false;
  const available = [...Object.keys(viewPermission), 'users'].filter(canView);
  if (!canView(currentView)) currentView = available[0] ?? null;
  document.querySelectorAll('[data-view]').forEach(button => {
    button.hidden = !canView(button.dataset.view); button.removeAttribute('aria-current');
    if (button.dataset.view === currentView) button.setAttribute('aria-current', 'page');
  });
  $('report-status-form').hidden = !owner;
  $('refresh').disabled = !currentView;
  if (!currentView) showNoPages();
  expirePrivateDisplay(); message(''); return true;
}
function showNoPages() {
  $('view-title').textContent = 'No pages assigned'; $('view-description').textContent = 'Ask the owner to grant access to the pages you need.';
  $('view').replaceChildren(element('div', 'You are signed in, but no reporting pages are assigned to your account.', 'empty'));
  for (const id of ['filters', 'support-form', 'pagination']) $(id).hidden = true;
  $('refresh').disabled = true;
}
async function refreshPermissions() {
  if (permissionRefresh) return permissionRefresh;
  clearPrivateDisplay(); const identity = authGeneration;
  permissionRefresh = (async () => {
    try {
      const session = await request('/auth/session');
      if (identity !== authGeneration || !signedIn) return;
      if (showWorkspace(session) && currentView) $('view').replaceChildren(element('div', 'Your access has been refreshed. Select a page or press Refresh to continue.', 'empty'));
    } catch { if (identity === authGeneration) showLogin(); }
    finally { permissionRefresh = null; }
  })();
  return permissionRefresh;
}
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
  if (!leaveUserAccess()) return;
  if (!currentView || !canView(currentView)) { showNoPages(); return; }
  $('all-days-option').disabled = ['overview', 'inventory'].includes(currentView);
  if ($('all-days-option').disabled && !$('days-filter').value) $('days-filter').value = '7';
  const ticket = ++generation, requestedView = currentView, view = $('view'), query = filters();
  view.replaceChildren(element('div', 'Loading observations…', 'empty'));
  $('refresh').disabled = true; message('');
  const names = { overview: ['How Launchpad is used', 'Adoption, everyday use and the tools controllers choose. Usage reporting is optional.'], inventory: ['Software & plugin versions', 'Applications and EuroScope DLLs from a separate optional inventory. Observed versions are not a live compatibility check.'], reports: ['Reports', 'Structured reports submitted by users. No free-text descriptions or attachments are collected.'], errors: ['Grouped errors', 'Compare stable error codes across applications and releases. Public submissions are unverified and may be sampled or throttled.'], support: ['Support lookup', 'Paste the support ID voluntarily shared by the user. This private lookup is limited to the most recent 100 reports and 100 events.'], health: ['Service health', 'Logical admission limits and physical storage headroom. Backup and infrastructure readiness require operator verification.'], users: ['Users & access', 'Invite named users with a passkey and choose the pages each can read. Only the owner can change reports, delete support records or manage access.'] };
  $('view-title').textContent = names[currentView][0]; $('view-description').textContent = names[currentView][1];
  $('filters').hidden = !['overview', 'inventory', 'reports', 'errors'].includes(currentView); $('status-filter-label').hidden = currentView !== 'reports';
  $('support-form').hidden = currentView !== 'support'; $('pagination').hidden = true;
  try {
    const fragment = document.createDocumentFragment();
    if (currentView === 'overview' || currentView === 'inventory') {
      const data = await request(`/api/admin/${requestedView === 'overview' ? 'usage' : 'inventory'}?${query}`);
      fragment.append(requestedView === 'overview' ? renderUsageDashboard(data) : renderInventory(data));
    } else if (currentView === 'reports') {
      if ($('status-filter').value) query.set('status', $('status-filter').value); query.set('offset', String(offset));
      const data = await request(`/api/admin/reports?${query}`);
      if (ticket !== generation) return;
      fragment.append(table(data, reportColumns, { open: openReport }));
      $('pagination').hidden = false; $('previous').disabled = offset === 0; $('next').disabled = data.length < 50; $('page-label').textContent = `Page ${offset / 50 + 1}`;
    } else if (currentView === 'errors') {
      const data = await request(`/api/admin/errors?${query}`);
      fragment.append(element('p', 'Counts describe error-reporting installations. They are separate from usage and are not failure rates.', 'scope-note'));
      fragment.append(renderErrorGroups(data));
    } else if (currentView === 'health') {
      const data = await request('/api/admin/health');
      fragment.append(cards([['Storage admission', data.ok ? 'Ready' : 'Needs attention'], ['Database size', bytes(data.physical_bytes)], ['Database ceiling', bytes(data.max_db_bytes)], ['Free disk space', bytes(data.free_bytes)]]));
      section(fragment, 'Storage safeguards', details({ reserved_free_space: bytes(data.reserved_free_bytes), counter_rows: data.counter_rows, burst_rows: data.burst_rows }));
      if (data.storage) section(fragment, 'Current logical use', table(Object.entries(data.storage).map(([stream, counts]) => ({ stream, ...counts })), [{ key: 'stream', label: 'Stream' }, { key: 'count', label: 'Records' }, { key: 'bytes', label: 'Payload size', render: bytes }]));
      if (data.aggregate) section(fragment, 'Historical count storage', details({ groups: data.aggregate.rows, group_ceiling: data.aggregate.max_rows, size: bytes(data.aggregate.bytes), size_ceiling: bytes(data.aggregate.max_bytes), retention_days: data.aggregate.retention_days, omitted_at_capacity: data.aggregate.limited_count }));
      if (data.retention) section(fragment, 'Retention maintenance', details({ last_success_UTC: time(data.retention.last_success), last_failure_UTC: time(data.retention.last_failure), expired_events: data.retention.expired_events, usage_admission_paused: data.usage_paused ? 'Yes' : 'No' }));
      fragment.append(element('p', 'A healthy API is not proof of a recent restorable backup. Check the deployment’s backup and expiry monitoring separately.', 'muted'));
    } else if (currentView === 'users') {
      const data = await request('/api/admin/users');
      if (ticket !== generation) return;
      fragment.append(renderUsers(data.users));
    } else if (currentView === 'support' && lookup) {
      const supportId = lookup;
      const data = await request(`/api/admin/support/${encodeURIComponent(supportId)}`);
      fragment.append(element('p', supportId)); section(fragment, 'Reports', table(data.reports, reportColumns, { open: canView('reports') ? openReport : undefined })); section(fragment, 'Recent events', table(data.events, eventColumns));
      if (owner) {
      const removal = element('details'), summary = element('summary', 'Delete records for this support ID'), explanation = element('p', 'This permanently deletes current service records for this ID. It does not recall existing backups. Verify the request first; a sign-in within the last five minutes is required.');
      const button = element('button', 'Delete current records', 'danger');
      button.addEventListener('click', async () => {
        if (!owner) return;
        if (!confirm(`Delete current service records for ${supportId}? This cannot be undone here.`)) return;
        button.disabled = true;
        try { await request(`/api/admin/support/${encodeURIComponent(supportId)}/delete`, { confirm: supportId }); lookup = ''; $('support-id').value = ''; await load(); message('Current service records deleted. Existing backup copies follow their separate expiry policy.'); } catch (error) { message(error.message, true); } finally { button.disabled = false; }
      });
      removal.append(summary, explanation, button); fragment.append(removal);
      }
    } else fragment.append(element('div', 'Enter a support ID above to find related records.', 'empty'));
    if (ticket === generation) view.replaceChildren(fragment);
  } catch (error) { if (ticket === generation) { view.replaceChildren(element('div', 'Observations could not be loaded. Refresh to try again.', 'empty')); message(error.message, true); } }
  finally { if (ticket === generation) $('refresh').disabled = false; }
}
async function openReport(row) {
  if (!canView('reports')) return;
  const ticket = ++reportGeneration, identity = authGeneration;
  try {
    const report = await request(`/api/admin/reports/${encodeURIComponent(row.reference)}`);
    if (ticket !== reportGeneration || identity !== authGeneration || !signedIn) return;
    selectedReport = report;
    $('report-title').textContent = 'Report details'; $('report-message').textContent = '';
    const { payload, ...metadata } = selectedReport; $('report-details').replaceChildren(details(metadata));
    if (payload) { const disclosure = element('details'), summary = element('summary', 'Exact submitted technical fields'); disclosure.append(summary, element('pre', JSON.stringify(payload, null, 2))); $('report-details').append(disclosure); }
    $('report-status').value = selectedReport.status; $('report-status-form').hidden = !owner; $('report-dialog').showModal();
  } catch (error) { message(error.message, true); }
}

function permissionChoices(selected = []) {
  const fieldset = element('fieldset', undefined, 'user-permissions'); fieldset.append(element('legend', 'Pages this user can read'));
  const controls = [];
  for (const [permission, name] of Object.entries(permissionNames)) {
    const label = element('label'), checkbox = element('input'); checkbox.type = 'checkbox'; checkbox.value = permission; checkbox.checked = selected.includes(permission);
    label.append(checkbox, document.createTextNode(name)); fieldset.append(label); controls.push(checkbox);
  }
  return { fieldset, read: () => controls.filter(input => input.checked).map(input => input.value) };
}
function invitationCard(data) {
  const card = element('section', undefined, 'invitation-card'); card.append(element('h2', data.user?.username ? `Invitation for ${data.user.username}` : 'Invitation ready'));
  card.append(element('p', 'Send this link privately to the intended user. It can create one passkey, and is shown only now.'), element('p', `Expires ${time(data.expires_at)} UTC. Leaving or refreshing this page clears the displayed link.`, 'muted'));
  if (data.user?.enabled === false) card.append(element('p', 'Enable sign-in and save access before this invitation can be accepted.', 'notice'));
  const label = element('label', 'One-use invitation link'), link = element('input'); link.type = 'text'; link.readOnly = true; link.value = data.invitation_url; link.autocomplete = 'off'; link.spellcheck = false;
  label.append(link); card.append(label);
  const copy = element('button', 'Copy invitation link'); copy.type = 'button';
  copy.addEventListener('click', async () => {
    if (!owner || !canView('users')) return;
    try { await navigator.clipboard.writeText(link.value); message('Invitation link copied. Share it privately.'); }
    catch { link.focus(); link.select(); message('Copy is unavailable. The invitation link is selected for copying.'); }
  });
  card.append(copy); return card;
}
async function changeUser(path, body, button) {
  if (!owner || currentView !== 'users') return;
  if (userAccessState?.busy || userAccessState?.dirtyCount()) { message('Save or discard your access changes first.'); return; }
  const matrix = userAccessState;
  if (matrix) { matrix.busy = true; matrix.update(); }
  const ticket = generation, identity = authGeneration; button.disabled = true; message('');
  try {
    const result = await request(path, body);
    if (ticket !== generation || identity !== authGeneration || !owner) return;
    let users;
    try { users = await request('/api/admin/users'); }
    catch (error) {
      if (result.invitation_url && ticket === generation && identity === authGeneration && owner) {
        userAccessState = null; $('refresh').disabled = false;
        $('view').replaceChildren(invitationCard(result), element('p', 'The invitation was created, but the user list could not refresh. Copy the link before trying Refresh.', 'notice'));
        message(error.message, true); return;
      }
      throw error;
    }
    if (ticket !== generation || identity !== authGeneration || !owner) return;
    $('view').replaceChildren(renderUsers(users.users, result.invitation_url ? result : null));
    message(result.invitation_url ? 'Invitation created. Copy the link before leaving this page.' : 'Access saved. The change applies to current sessions too.');
  } catch (error) { if (matrix && userAccessState === matrix && error.code === 'reauthentication_required') matrix.requiresVerification = true; message(error.message, true); }
  finally { button.disabled = false; if (matrix && userAccessState === matrix) { matrix.busy = false; matrix.update(); } }
}
function renderUsers(users, invitation = null) {
  const fragment = document.createDocumentFragment();
  if (invitation) fragment.append(invitationCard(invitation));
  const create = element('details', undefined, 'user-create'); create.open = !users.length && !invitation;
  create.append(element('summary', 'Invite a user'));
  const form = element('form', undefined, 'user-form'), nameLabel = element('label', 'Username'), username = element('input');
  username.type = 'text'; username.required = true; username.minLength = 3; username.maxLength = 32; username.pattern = '[a-z][a-z0-9_-]{2,31}'; username.autocomplete = 'off'; username.spellcheck = false;
  nameLabel.append(username); const choices = permissionChoices();
  const invite = element('button', 'Create invitation', 'primary'); invite.type = 'submit';
  form.append(nameLabel, element('p', '3–32 lowercase letters, numbers, underscores or hyphens; start with a letter. “maintainer” is reserved.', 'muted'), choices.fieldset,
    element('p', 'No pages are selected by default. Support lookup grants access to the raw records for a supplied support ID; Reports includes submitted technical context.', 'muted'), invite);
  form.addEventListener('submit', event => { event.preventDefault(); return changeUser('/api/admin/users', { username: username.value.trim(), permissions: choices.read() }, invite); });
  create.append(form); fragment.append(create);
  if (!users.length) fragment.append(element('div', 'No additional users yet. The owner retains access to every page.', 'empty'));
  const editor = element('form', undefined, 'access-editor'), wrapper = element('div', undefined, 'access-table-wrap');
  wrapper.tabIndex = 0; wrapper.setAttribute('role', 'region'); wrapper.setAttribute('aria-label', 'User permissions table; scroll horizontally on small screens');
  const matrix = element('table', undefined, 'access-table'), head = element('thead'), headings = element('tr'), rows = element('tbody');
  const caption = element('caption', 'User access. Checked permissions allow read access to the page and its API.'); caption.className = 'visually-hidden'; matrix.append(caption);
  const userHeading = element('th', 'User'); userHeading.scope = 'col'; userHeading.className = 'access-user'; headings.append(userHeading);
  for (const label of ['Allow sign-in', ...Object.values(permissionNames)]) {
    const cell = element('th', undefined, 'access-permission'); cell.scope = 'col'; cell.append(element('span', label)); headings.append(cell);
  }
  const actionsHeading = element('th', 'Passkey / invitation'); actionsHeading.scope = 'col'; actionsHeading.className = 'access-actions'; headings.append(actionsHeading); head.append(headings); matrix.append(head, rows);
  const controls = [], rowActions = [];
  const state = { busy: false, requiresVerification: false, verifying: false, dirtyCount: () => controls.filter(control => control.changed()).length, update: () => {} };
  userAccessState = state;
  function checkCell(name, label, checked, disabled = false) {
    const cell = element('td', undefined, 'access-check'), input = element('input'); input.type = 'checkbox'; input.checked = checked; input.disabled = disabled;
    input.setAttribute('aria-label', `${name}: ${label}`); cell.append(input); return { cell, input };
  }
  const ownerRow = element('tr', undefined, 'access-owner'), ownerName = element('th', undefined, 'access-user'); ownerName.scope = 'row';
  ownerName.append(element('strong', 'maintainer'), element('small', 'Owner · full access')); ownerRow.append(ownerName);
  for (const label of ['Allow sign-in', ...Object.values(permissionNames)]) ownerRow.append(checkCell('maintainer', label, true, true).cell);
  ownerRow.append(element('td', 'Owner account', 'access-actions access-owner-note')); rows.append(ownerRow);
  for (const user of users) {
    const row = element('tr'), name = element('th', undefined, 'access-user'); name.scope = 'row';
    name.append(element('strong', user.username), element('small', !user.enabled ? 'Access disabled' : user.enrolled ? 'Passkey ready' : 'Awaiting passkey'));
    if (user.invitation_expires_at) name.title = `Invitation expires ${time(user.invitation_expires_at)} UTC. The original link cannot be retrieved.`;
    row.append(name);
    const enabledCell = checkCell(user.username, 'Allow sign-in', user.enabled); row.append(enabledCell.cell);
    const permissionInputs = Object.entries(permissionNames).map(([permission, label]) => {
      const choice = checkCell(user.username, label, user.permissions.includes(permission)); choice.input.value = permission; row.append(choice.cell); return choice.input;
    });
    const original = { enabled: user.enabled, permissions: [...user.permissions] };
    const read = () => ({ enabled: enabledCell.input.checked, permissions: permissionInputs.filter(input => input.checked).map(input => input.value) });
    const changed = () => { const value = read(); return value.enabled !== original.enabled || value.permissions.length !== original.permissions.length || value.permissions.some(permission => !original.permissions.includes(permission)); };
    controls.push({ id: user.id, row, original, read, changed, inputs: [enabledCell.input, ...permissionInputs] });
    for (const input of [enabledCell.input, ...permissionInputs]) input.addEventListener('change', () => state.update());
    const actions = element('td', undefined, 'access-actions');
    const reset = element('button', user.enrolled ? 'Reset passkey…' : 'New invitation', 'quiet'); reset.type = 'button';
    reset.setAttribute('aria-label', `${user.enrolled ? 'Reset passkey' : 'New invitation'} for ${user.username}`);
    reset.addEventListener('click', () => {
      if (!owner || state.busy || state.dirtyCount()) return;
      if (user.enrolled && !confirm(`Reset the passkey for ${user.username}? Their existing passkey and sessions will stop working. Give the new invitation only to this user.`)) return;
      if (!user.enrolled && user.invitation_expires_at && !confirm(`Replace the pending invitation for ${user.username}? The previous link will stop working.`)) return;
      return changeUser(`/api/admin/users/${encodeURIComponent(user.id)}/invite`, { reset: user.enrolled }, reset);
    });
    if (!user.enabled) reset.title = 'Resetting keeps access disabled. Enable sign-in after replacing the old passkey, when ready.';
    rowActions.push(reset); actions.append(reset); row.append(actions); rows.append(row);
  }
  wrapper.append(matrix); editor.append(wrapper);
  const footer = element('div', undefined, 'access-footer'), status = element('span', undefined, 'access-draft-status'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
  const buttons = element('div', undefined, 'user-actions'), discard = element('button', 'Discard changes', 'quiet'), save = element('button', 'Save access', 'primary'), verify = element('button', 'Verify passkey', 'primary'); discard.type = 'button'; save.type = 'submit'; verify.type = 'button';
  buttons.append(discard, verify, save); footer.append(status, buttons); editor.append(footer);
  state.update = () => {
    const count = state.dirtyCount();
    status.textContent = state.verifying ? 'Follow the passkey prompt on your device…' : state.busy ? 'Saving changes…' : count ? `${count} ${count === 1 ? 'user has' : 'users have'} unsaved changes` : 'No unsaved changes';
    save.disabled = state.busy || state.requiresVerification || !count; discard.disabled = state.busy || !count; invite.disabled = state.busy || state.requiresVerification || !!count;
    verify.hidden = !state.requiresVerification; verify.disabled = state.busy;
    for (const button of rowActions) button.disabled = state.busy || state.requiresVerification || !!count;
    for (const control of controls) { control.row.classList.toggle('access-dirty', control.changed()); for (const input of control.inputs) input.disabled = state.busy; }
    $('refresh').disabled = state.busy;
  };
  discard.addEventListener('click', () => {
    if (state.busy || userAccessState !== state) return;
    for (const control of controls) { control.inputs[0].checked = control.original.enabled; for (const input of control.inputs.slice(1)) input.checked = control.original.permissions.includes(input.value); }
    state.update(); message('Unsaved access changes discarded.');
  });
  verify.addEventListener('click', async () => {
    if (!owner || userAccessState !== state || currentView !== 'users' || state.busy) return;
    const ticket = generation, identity = authGeneration;
    const current = () => ticket === generation && identity === authGeneration && userAccessState === state && owner;
    let verificationSent = false;
    state.busy = true; state.verifying = true; state.update(); message('');
    try {
      const library = window.SimpleWebAuthnBrowser;
      if (!library?.browserSupportsWebAuthn()) throw new Error('This browser does not support passkeys. Use a current browser on the HTTPS Launchpad site.');
      const options = await request('/auth/authenticate-options', {}); if (!current()) return;
      const response = await library.startAuthentication({ optionsJSON: options }); if (!current()) return;
      verificationSent = true; await request('/auth/authenticate-verify', { response }); if (!current()) return;
      const session = await request('/auth/session'); if (!current()) return;
      if (!session.authenticated || session.owner !== true) { showWorkspace(session); message('Sign in with the owner passkey to manage access.', true); return; }
      state.requiresVerification = false; expirePrivateDisplay(); message('Passkey verified. Your edits are still here; press Save access when ready.');
    } catch (error) {
      if (current()) {
        if (verificationSent) { showLogin(); message('Your account could not be verified. Private pages have been cleared; sign in again to continue.', true); }
        else message(error.name === 'NotAllowedError' ? 'The passkey prompt was cancelled. Your edits are still here.' : error.message, true);
      }
    }
    finally { if (userAccessState === state) { state.busy = false; state.verifying = false; state.update(); } }
  });
  editor.addEventListener('submit', async event => {
    event.preventDefault();
    if (!owner || userAccessState !== state || currentView !== 'users' || state.busy || state.requiresVerification || !state.dirtyCount()) return;
    const changes = controls.filter(control => control.changed()).map(control => ({ id: control.id, ...control.read(), expected: { enabled: control.original.enabled, permissions: [...control.original.permissions] } }));
    const ticket = generation, identity = authGeneration; state.busy = true; state.update(); message('');
    try {
      const result = await request('/api/admin/users/access', { users: changes });
      if (ticket !== generation || identity !== authGeneration || userAccessState !== state || !owner) return;
      const invitedUser = invitation && result.users.find(user => user.id === invitation.user?.id);
      const retainedInvitation = invitedUser && !invitedUser.enrolled && invitedUser.invitation_expires_at === invitation.expires_at && Date.parse(invitation.expires_at) > Date.now() ? { ...invitation, user: invitedUser } : null;
      $('view').replaceChildren(renderUsers(result.users, retainedInvitation)); message('Access saved for all changed users.' + (invitation && !retainedInvitation ? ' The displayed invitation is no longer active.' : ''));
    } catch (error) {
      if (ticket === generation && identity === authGeneration && userAccessState === state) { if (error.code === 'reauthentication_required') state.requiresVerification = true; message(`${error.message} Save not confirmed; your edits are still shown.`, true); }
    } finally { if (userAccessState === state) { state.busy = false; state.update(); } }
  });
  editor.append(element('p', 'Save access applies all changed rows together. Passkey actions are separate; save or discard your edits before using them.', 'access-help'));
  fragment.append(editor); state.update();
  return fragment;
}
function updateEnrollmentMode() {
  const invited = !!invitationToken;
  $('invitation-notice').hidden = !invited;
  $('enroll-field').hidden = !enrollment || invited; $('enrollment').required = enrollment && !invited; $('enrollment').value = '';
  $('login-title').textContent = invited ? 'Create your invited passkey' : enrollment ? 'Set up the owner passkey' : 'Welcome back';
  $('login-submit').textContent = enrollment ? 'Create passkey' : 'Sign in with a passkey';
  $('enroll-toggle').textContent = enrollment ? 'Back to sign-in' : 'Owner setup';
}
async function boot() {
  captureInvitation();
  if (document.body.dataset.page === 'privacy') {
    try { const data = await request('/privacy/info'); $('operator').textContent = `Operated by ${data.operator}.`; $('contact').textContent = data.contact; }
    catch { $('operator').textContent = 'Contact the Launchpad project maintainer for operator information and deletion requests.'; }
    return;
  }
  window.addEventListener('beforeunload', event => {
    if (!userAccessState?.busy && !userAccessState?.dirtyCount()) return;
    event.preventDefault(); event.returnValue = '';
  });
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault(); const button = $('login-submit'), attempt = ++signInAttempt; button.disabled = true; message('Follow the passkey prompt on your device.');
    try {
      const library = window.SimpleWebAuthnBrowser;
      if (!library?.browserSupportsWebAuthn()) throw new Error('This browser does not support passkeys. Use a current browser on the HTTPS Launchpad site.');
      const kind = enrollment ? 'register' : 'authenticate';
      const options = await request(`/auth/${kind}-options`, enrollment ? { enrollment_code: invitationToken || $('enrollment').value.trim() } : {});
      if (attempt !== signInAttempt) return;
      const response = kind === 'register' ? await library.startRegistration({ optionsJSON: options }) : await library.startAuthentication({ optionsJSON: options });
      if (attempt !== signInAttempt) return;
      await request(`/auth/${kind}-verify`, { response });
      if (attempt !== signInAttempt) return;
      $('enrollment').value = ''; invitationToken = ''; enrollment = false; updateEnrollmentMode();
      const session = await request('/auth/session');
      if (attempt === signInAttempt && showWorkspace(session)) await load();
    } catch (error) { message(error.name === 'NotAllowedError' ? 'The passkey prompt was cancelled or timed out. Try again when ready.' : error.message, true); }
    finally { button.disabled = false; }
  });
  $('enroll-toggle').addEventListener('click', () => { signInAttempt++; enrollment = !enrollment; invitationToken = ''; updateEnrollmentMode(); message(''); if (enrollment) $('enrollment').focus(); });
  $('sign-out').addEventListener('click', async () => {
    if (!leaveUserAccess()) return;
    showLogin(); const identity = authGeneration;
    try { await request('/auth/logout', {}); if (identity === authGeneration) message('Signed out.'); }
    catch { if (identity === authGeneration) message('Private pages are hidden, but the server could not confirm sign-out. Reconnect and sign out again to end the session.', true); }
  });
  document.querySelectorAll('[data-view]').forEach(button => button.addEventListener('click', () => {
    if (!canView(button.dataset.view)) return;
    if (!leaveUserAccess()) return;
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
  $('report-status-form').addEventListener('submit', async event => { event.preventDefault(); if (!owner || !selectedReport) return; try { selectedReport = await request(`/api/admin/reports/${encodeURIComponent(selectedReport.reference)}/status`, { status: $('report-status').value }); $('report-message').textContent = 'Status saved.'; await load(); } catch (error) { $('report-message').textContent = error.message; } });
  if (invitationToken) { enrollment = true; updateEnrollmentMode(); showLogin(); return; }
  try { if (showWorkspace(await request('/auth/session'))) await load(); } catch (error) { showLogin(); message('The service could not be reached. Reload to try again.', true); }
}
boot();
