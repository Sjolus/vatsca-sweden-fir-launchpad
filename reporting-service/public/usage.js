'use strict';
// These views describe observations from opt-in installations, never a controller census.
const usageNames = {
  launchpad: 'Launchpad', euroscope: 'EuroScope', gng: 'Swedish GNG', trackaudio: 'TrackAudio', vacs: 'VACS', vatis: 'vATIS', vatefs: 'VatEFS', vatiris: 'VATIRIS', maintenance: 'Maintenance', saaman: 'SAAMAN',
  topsky: 'TopSky', rdf: 'RDF', vatefs_plugin: 'VatEFS plugin', euroscope_topsky: 'TopSky', euroscope_rdf: 'RDF', euroscope_vatefs: 'VatEFS plugin',
  afv_euroscope_bridge: 'AFV EuroScope Bridge', check_updates: 'Check for updates', controller_profile_review: 'Review controller settings', gng_package_review: 'Review GNG package', gng_cleanup_review: 'Review GNG cleanup', gng_restore_review: 'Review GNG recovery', setup_guide: 'Open setup', install_or_update: 'Install or update tools', adopt: 'Use existing apps', font_check: 'Check fonts',
  update_check: 'Check for updates', gng_review: 'GNG package review', profile_review: 'Controller-profile review', software_management: 'Install or update tools', setup: 'Setup', cleanup: 'Cleanup',
  cached: 'Cached package', cache: 'Cached package', download: 'AeroNav download', downloaded: 'AeroNav download', import: 'Imported ZIP', imported: 'Imported ZIP',
  existing: 'Use existing installations', fresh: 'Start with a fresh setup', undecided: 'Not chosen',
  file_version: 'File version', product_version: 'Product version', registered_msi: 'Registered MSI', msi: 'Registered MSI', package: 'Package metadata',
  configured_app: 'Configured application', package_metadata: 'Package metadata', plugin_file: 'Plugin file', standard: 'Standard', larger: 'Larger', largest: 'Largest', dark: 'Dark', light: 'Light',
  arrival_load_balancer: 'Arrival Load Balancer', cdm: 'CDM', ground_radar: 'Ground Radar', maestro: 'MAESTRO', vch: 'VCH', vatcan_bookings: 'VATCAN Bookings',
  preferences: 'Preferences only', other: 'Other observed versions',
  apps: 'Apps', setup_applications: 'Setup · Applications', setup_gng: 'Setup · GNG', controller_profile: 'Controller settings', activity_recovery: 'Activity & recovery',
  configure_sign_in: 'Configure sign-in', configure_sweden_profile: 'Configure Swedish profile', configure_plugin_profile: 'Configure plugin profile',
  install: 'Install', update: 'Update', repair: 'Repair', replace: 'Replace', remove: 'Remove', restore: 'Restore', restore_cleanup: 'Restore cleanup', prerequisite: 'Install prerequisite', save: 'Save settings', check: 'Check', launch: 'Launch',
  completed: 'Completed', partial: 'Partly completed', blocked: 'Blocked', failed: 'Failed', cancelled: 'Cancelled', unknown: 'Result unknown', incomplete: 'No result received',
  already_configured: 'Already configured', client_running: 'Client running', busy: 'Another task is running', missing_configuration: 'Configuration missing', unsupported_installation: 'Unsupported installation', conflict: 'Conflicting configuration',
  rate_limited: 'Rate limited', access_denied: 'Access denied', timeout: 'Timed out', network_unavailable: 'Network unavailable', save_failed: 'Could not save', verification_failed: 'Verification failed', unexpected: 'Unexpected problem',
  github: 'GitHub', aero_nav: 'AeroNav', under_1s: 'Under 1 second', under_10s: '1–10 seconds', under_1m: '10–60 seconds', under_5m: '1–5 minutes', over_5m: '5 minutes or more',
  last_24h: 'Last 24 hours', one_to_seven_days: '1–7 days old', older_than_seven_days: 'Over 7 days old',
};
const usageLabel = value => usageNames[value] ?? title(value);
const number = value => Number.isFinite(value) ? value.toLocaleString('en-GB') : '—';
const share = (value, total) => total > 0 && Number.isFinite(value) ? `${number(value)} / ${number(total)} · ${Math.round(value / total * 100)}%` : number(value);
function usagePanel(heading, description) {
  const panel = element('section', undefined, 'usage-panel');
  const header = element('div', undefined, 'usage-panel-heading'); header.append(element('h2', heading));
  panel.append(header);
  if (description) panel.append(element('p', description, 'panel-note'));
  return panel;
}
function usageCard(label, value, note) {
  const card = element('article', undefined, 'card usage-card');
  card.append(element('span', label), element('strong', value), element('small', note)); return card;
}
function usageWindow(data, inventory = false) {
  const window = data.window ?? {}, note = element('div', undefined, 'scope-note');
  const start = window.start ? String(window.start).slice(0, 10) : '—', asOf = window.as_of ?? (window.end_exclusive ? new Date(new Date(window.end_exclusive).valueOf() - 1).toISOString() : null);
  const end = asOf ? String(asOf).slice(0, 10) : '—', updated = asOf ? `${String(asOf).slice(11, 16)} UTC` : 'the latest refresh';
  note.append(element('span', `${start} – ${end} · ${window.days ?? '—'} UTC days, including today. Today is partial; updated ${updated}.`));
  if (!window.coverage_complete) note.append(element('strong', 'Partial coverage: the service does not have observations for the entire selected period.', 'coverage-warning'));
  if (data.filters?.application && !inventory) note.append(element('span', `Usage cohort: installations with ${usageLabel(data.filters.application)} activity. Sessions, views and preferences describe those installations; launches and tasks describe the selected application.`));
  if (data.filters?.launchpad_version) note.append(element('span', `Only observations from Launchpad ${data.filters.launchpad_version} are included.`));
  return note;
}
function distribution(rows, key, denominator, { value = 'installations', label = 'Installations', empty = 'No observations in this period.' } = {}) {
  if (!rows?.length) return element('div', empty, 'empty');
  const list = element('ul', undefined, 'distribution');
  for (const row of rows) {
    const item = element('li'), heading = element('div', undefined, 'distribution-heading'), amount = Number(row[value] ?? 0);
    heading.append(element('span', usageLabel(row[key])), element('strong', denominator > 0 ? share(amount, denominator) : number(amount)));
    const bar = element('meter'); bar.min = 0; bar.max = Math.max(1, denominator || Math.max(...rows.map(item => Number(item[value] ?? 0)))); bar.value = amount;
    bar.setAttribute('aria-label', `${usageLabel(row[key])}: ${number(amount)} ${label.toLowerCase()}`);
    item.append(heading, bar); list.append(item);
  }
  return list;
}
function dailyUsage(rows) {
  const panel = usagePanel('Everyday use', 'Installations with recorded usage each day, including sessions kept open overnight. Daily counts overlap.');
  if (!rows?.length) { panel.append(element('div', 'No daily observations yet.', 'empty')); return panel; }
  const chart = element('div', undefined, `daily-chart${rows.length > 7 ? ' month' : ''}`), max = Math.max(1, ...rows.map(row => row.active_installations));
  chart.setAttribute('role', 'img'); chart.setAttribute('aria-label', rows.map(row => `${row.date}${row.partial ? ' (today, partial)' : ''}: ${number(row.active_installations)} active installations`).join('; '));
  for (const row of rows) {
    const day = element('div', undefined, 'chart-day'), bar = document.createElementNS('http://www.w3.org/2000/svg', 'svg'), rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    bar.setAttribute('viewBox', '0 0 40 100'); bar.setAttribute('preserveAspectRatio', 'none'); bar.setAttribute('aria-hidden', 'true');
    const height = row.active_installations / max * 100; rect.setAttribute('x', '3'); rect.setAttribute('y', String(100 - height)); rect.setAttribute('width', '34'); rect.setAttribute('height', String(height)); rect.setAttribute('rx', '2'); bar.append(rect);
    day.append(element('strong', number(row.active_installations)), bar, element('span', row.partial ? 'Today*' : row.date.slice(5))); chart.append(day);
  }
  panel.append(chart);
  if (rows.some(row => row.partial)) panel.append(element('p', '* Today is partial and includes observations received so far.', 'panel-note'));
  const detail = element('details'); detail.append(element('summary', 'Daily sessions and launch actions'), table(rows, [{ key: 'date', label: 'Date · UTC', render: (value, row) => `${value}${row.partial ? ' · Today, partial' : ''}` }, { key: 'active_installations', label: 'Active installations', render: number }, { key: 'sessions', label: 'Sessions', render: number }, { key: 'app_launches', label: 'Launch actions', render: number }])); panel.append(detail);
  return panel;
}
function preferencePanel(data) {
  const prefs = data.display_preferences ?? {}, panel = usagePanel('Display preferences', `Latest observation per responding installation: ${number(prefs.responding_installations)}. No preference observation: ${number(prefs.missing_installations)}.`);
  if (!prefs.responding_installations) { panel.append(element('div', 'No display preference observations yet.', 'empty')); return panel; }
  const choices = [['compact_layout', 'Layout'], ['text_scale', 'Text size'], ['always_on_top', 'Always on top'], ['theme', 'Theme']], grid = element('div', undefined, 'preferences-grid');
  for (const [key, heading] of choices) {
    const choice = element('section'); choice.append(element('h3', heading));
    const rows = (prefs[key] ?? []).map(row => ({ ...row, value: key === 'compact_layout' ? row.value === true || row.value === 'true' ? 'Compact' : row.value === false || row.value === 'false' ? 'Expanded' : row.value : key === 'always_on_top' ? row.value === true || row.value === 'true' ? 'On' : row.value === false || row.value === 'false' ? 'Off' : row.value : row.value }));
    choice.append(distribution(rows, 'value', prefs.responding_installations)); grid.append(choice);
  }
  panel.append(grid);
  return panel;
}
const taskOutcomes = ['completed', 'partial', 'blocked', 'failed', 'cancelled', 'unknown', 'incomplete'];
function taskResults(tasks) {
  const data = tasks ?? {}, summary = data.summary ?? {}, panel = usagePanel('What gets done', 'Recorded results of setup, configuration, updates and other requested tasks.');
  if (!summary.started) {
    panel.append(element('p', 'No correlated task starts in this selection. Older clients may report results without a matching start.', 'empty'));
  } else {
    const counts = new Map((data.outcomes ?? []).map(row => [row.outcome, row.attempts])), strip = element('div', undefined, 'task-outcomes');
    for (const outcome of taskOutcomes) {
      const count = counts.get(outcome) ?? 0;
      if (!count) continue;
      const item = element('div', undefined, `task-outcome task-${outcome}`);
      item.append(element('strong', number(count)), element('span', usageLabel(outcome))); strip.append(item);
    }
    panel.append(strip, element('p', `${number(summary.started)} task starts from ${number(summary.installations)} installations · ${number(summary.matched)} have a matched result.`, 'panel-note'));
    panel.append(table(data.by_task, [
      { key: 'operation', label: 'Task', render: (value, row) => `${usageLabel(row.application)} · ${usageLabel(value)}` },
      { key: 'started', label: 'Started', render: number },
      { key: 'installations', label: 'Installations', render: number },
      { key: 'completed', label: 'Results', render: (_, row) => {
        const result = element('div', undefined, 'task-result-row');
        for (const outcome of taskOutcomes) if (row[outcome]) result.append(element('span', `${number(row[outcome])} ${usageLabel(outcome).toLowerCase()}`, `task-result task-${outcome}`));
        return result;
      } },
    ]));
  }
  const coverage = element('details');
  coverage.append(element('summary', 'Coverage and timing'), element('p', `${number(summary.incomplete ?? 0)} starts have no matching result. ${number(summary.unmatched_finishes ?? 0)} results have no matching start. ${number(summary.unlinked_records ?? 0)} records lack correlation fields.`, 'panel-note'),
    element('p', 'A missing result can mean an unfinished task, delayed reporting or a window boundary. It is not counted as a failure. Only usage records within these filters are paired; automatic errors are a separate sample.', 'panel-note'));
  if (data.durations?.length) coverage.append(table(data.durations, [{ key: 'duration_bucket', label: 'Time to result', render: usageLabel }, { key: 'attempts', label: 'Matched tasks', render: number }]));
  if (data.reasons?.length) coverage.append(table(data.reasons, [{ key: 'reason', label: 'Reason', render: usageLabel }, { key: 'outcome', label: 'Result', render: usageLabel }, { key: 'attempts', label: 'Matched tasks', render: number }]));
  panel.append(coverage); return panel;
}
function renderErrorGroups(rows) {
  const stack=(first,second,className='')=>{
    const node=element('div',undefined,`error-context ${className}`.trim());node.append(element('span',first));
    if(second)node.append(element('small',second));return node;
  };
  return table(rows,[
    {key:'application',label:'Application',render:usageLabel},
    {key:'error_code',label:'Problem',render:(value,row)=>stack(usageLabel(value),row.reason && row.reason!==value?usageLabel(row.reason):null)},
    {key:'operation',label:'Context',render:(value,row)=>stack(title(value),row.stage && row.stage!==value?title(row.stage):null)},
    {key:'service',label:'Service',render:(value,row)=>stack(value?usageLabel(value):'Not reported',row.http_status?`HTTP ${row.http_status}`:null,'error-service')},
    {key:'launchpad_version',label:'Launchpad'},
    {key:'affected_installations',label:'Installations',render:number},
    {key:'count',label:'Records',render:number},
    {key:'last_seen',label:'Seen · UTC',render:(value,row)=>stack(`Last ${time(value)}`,row.first_seen && row.first_seen!==value?`First ${time(row.first_seen)}`:null)},
  ]);
}
function renderUsageDashboard(data) {
  const fragment = document.createDocumentFragment(), cohort = data.cohort ?? {}, total = cohort.active_installations ?? 0;
  fragment.append(usageWindow(data));
  const headline = element('div', undefined, 'cards usage-cards');
  const returnNote = cohort.returning_available ? `Also seen in the preceding ${data.window.days} complete UTC days.` : cohort.returning_reason === 'retention' ? 'The preceding period falls outside raw-data retention. Use 7 days.' : 'A full preceding period is not available yet.';
  headline.append(usageCard('Active installations', number(total), 'Distinct support IDs with recorded usage.'), usageCard('Recorded sessions', number(cohort.sessions), 'Distinct sessions with activity in this period, not app starts or running time.'), usageCard('Returning installations', cohort.returning_available ? share(cohort.returning_installations, total) : 'Unavailable', returnNote), usageCard('Application start requests', number(cohort.app_launches), `${number(cohort.app_launch_installations)} installations requested a tool to start.`));
  fragment.append(headline);
  if (!total) fragment.append(element('p', 'No usage observations for this selection. Inventory may still be available separately. No observations does not mean nobody uses Launchpad.', 'notice'));
  const first = element('div', undefined, 'usage-grid'); first.append(dailyUsage(data.daily));
  const releases = usagePanel('Launchpad versions in use', 'Latest observed version per active installation within these filters. This does not identify the latest published release.'); releases.append(distribution(data.releases, 'version', total)); first.append(releases); fragment.append(first);
  fragment.append(taskResults(data.tasks));
  const second = element('div', undefined, 'usage-grid equal');
  const apps = usagePanel('Applications started from Launchpad', 'Start requests recorded. Running or connected status is not verified.');
  apps.append(table(data.applications, [{ key: 'application', label: 'Application', render: usageLabel }, { key: 'launches', label: 'Start requests', render: number }, { key: 'installations', label: `Installations · of ${number(total)}`, render: value => share(value, total) }], { empty: 'No application start observations yet.' }));
  const views = usagePanel('Where people go', 'Page opens, counted once per navigation. Opening a page is not a completed task.');
  views.append(table(data.views ?? [], [{ key: 'view', label: 'Page', render: value => value === 'preferences' ? 'App settings' : usageLabel(value) }, { key: 'opens', label: 'Opens', render: number }, { key: 'installations', label: 'Installations', render: number }], { empty: 'No navigation observations yet. These arrive from newer clients.' })); second.append(apps, views); fragment.append(second);
  const third = element('div', undefined, 'usage-grid equal');
  const features = usagePanel('Actions used', 'Review and check actions. These counts do not imply completion.');
  features.append(table(data.features, [{ key: 'feature', label: 'Action', render: usageLabel }, { key: 'uses', label: 'Uses', render: number }, { key: 'installations', label: `Installations · of ${number(total)}`, render: value => share(value, total) }], { empty: 'No feature observations yet.' }));
  const gng = usagePanel('GNG package sources', 'How packages reached review. Preparation is separate from installation.');
  gng.append(table(data.gng_sources, [{ key: 'source', label: 'Source', render: usageLabel }, { key: 'packages', label: 'Prepared', render: number }, { key: 'installations', label: 'Installations', render: number }], { empty: 'No package preparation observations yet.' }));
  third.append(features, gng); fragment.append(third, preferencePanel(data));
  if (data.setup_routes?.length) {
    const setup = element('details', undefined, 'legacy-setup');
    setup.append(element('summary', 'Earlier setup-guide choices'), element('p', 'Routes reported by the earlier wizard. These are choices, not completed setups.', 'panel-note'), table(data.setup_routes, [{ key: 'route', label: 'Route', render: usageLabel }, { key: 'attempts', label: 'Choices', render: number }, { key: 'installations', label: 'Installations', render: number }])); fragment.append(setup);
  }
  fragment.append(element('p', 'Usage is optional and may be sampled or delayed. Support IDs approximate installations, not individual people. Public submissions are unverified.', 'scope-note'));
  return fragment;
}
const inventoryStates = [['known', 'Version known'], ['unknown', 'Version unknown'], ['absent', 'Not detected'], ['unavailable', 'Unavailable']];
const inventoryMarks = { euroscope: 'ES', gng: 'SE', trackaudio: 'TA', vacs: 'VA', vatis: 'AT', vatefs: 'EF', topsky: 'TS', rdf: 'RD', vatefs_plugin: 'EF', afv_euroscope_bridge: 'AF' };
function inventorySvg(tag, attributes = {}) {
  const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, String(value));
  return node;
}
function inventoryCoverage(component, total) {
  const name = usageLabel(component.component), svg = inventorySvg('svg', { viewBox: '0 0 100 8', preserveAspectRatio: 'none', class: 'inventory-coverage', role: 'img',
    'aria-label': `${name}, of ${number(total)} inventory installations: ${inventoryStates.map(([key, label]) => `${label} ${number(component[key])}`).join(', ')}.` });
  let x = 0;
  for (const [key] of inventoryStates) {
    const width = total > 0 ? Math.max(0, Math.min(100, component[key] / total * 100)) : 0;
    svg.append(inventorySvg('rect', { x, y: 0, width, height: 8, class: `inventory-state-${key}` }));
    x += width;
  }
  return svg;
}
function inventoryVersionRows(rows, total, name) {
  const list = element('ul', undefined, 'inventory-versions');
  for (const row of rows) {
    const item = element('li'), heading = element('div', undefined, 'inventory-version-heading');
    heading.append(element('span', usageLabel(row.version), 'inventory-version'), element('span', share(row.installations, total), 'inventory-version-count'));
    const svg = inventorySvg('svg', { viewBox: '0 0 100 4', preserveAspectRatio: 'none', class: 'inventory-version-bar', role: 'img',
      'aria-label': `${name}, ${usageLabel(row.version)}: ${share(row.installations, total)} of inventory installations.` });
    svg.append(inventorySvg('rect', { width: 100, height: 4, class: 'inventory-version-track' }),
      inventorySvg('rect', { width: total > 0 ? Math.max(0, Math.min(100, row.installations / total * 100)) : 0, height: 4, class: 'inventory-version-fill' }));
    item.append(heading, svg); list.append(item);
  }
  return list;
}
function inventoryComponentCard(component, total) {
  const plugin = component.source === 'plugin_file', name = usageLabel(component.component), detected = component.known + component.unknown;
  const card = element('article', undefined, `inventory-component${plugin ? ' inventory-plugin' : ''}`);
  const heading = element('header', undefined, 'inventory-component-heading'), identity = element('div', undefined, 'inventory-identity');
  const mark = element('span', inventoryMarks[component.component] ?? 'LP', 'inventory-mark'); mark.setAttribute('aria-hidden', 'true');
  const label = element('div'); label.append(element('h3', name), element('span', usageLabel(component.source), 'inventory-source'));
  identity.append(mark, label);
  const count = element('div', undefined, 'inventory-detected');
  count.setAttribute('role', 'group');
  count.append(element('strong', number(detected)), element('span', ` / ${number(total)}`), element('small', 'detected'));
  count.setAttribute('aria-label', `${name}: detected in ${number(detected)} of ${number(total)} inventory installations.`);
  heading.append(identity, count); card.append(heading, inventoryCoverage(component, total));
  const states = element('dl', undefined, 'inventory-state-list');
  for (const [key, text] of inventoryStates) {
    const pair = element('div'), term = element('dt'), dot = element('span', undefined, `inventory-dot inventory-state-${key}`);
    dot.setAttribute('aria-hidden', 'true'); term.append(dot, document.createTextNode(text));
    pair.append(term, element('dd', number(component[key]))); states.append(pair);
  }
  card.append(states);
  const versions = element('div', undefined, 'inventory-version-section'), versionHeading = element('div', undefined, 'inventory-version-label');
    versionHeading.append(element('h4', plugin ? 'Versions read from DLL' : 'Observed versions'), element('span', 'Share of sample'));
  versions.append(versionHeading);
  const rows = component.versions ?? [];
  if (rows.length) {
    versions.append(inventoryVersionRows(rows.slice(0, 3), total, name));
    if (rows.length > 3) {
      const more = element('details', undefined, 'inventory-more');
      more.append(element('summary', `Show ${number(rows.length - 3)} more version groups`), inventoryVersionRows(rows.slice(3), total, name));
      versions.append(more);
    }
  } else {
    const explanation = plugin
      ? component.unknown > 0 ? 'DLL detected, but no readable version was reported. Its age cannot be determined.'
        : component.absent === total ? 'Not found in the checked locations.'
        : component.unavailable === total ? 'Could not reliably inspect the configured locations. Plugin presence is unknown.' : 'No readable DLL versions in these snapshots.'
      : component.unknown > 0 ? 'Detected, but no readable version was reported.' : component.absent === total
        ? 'Not detected in these snapshots.' : component.unavailable === total ? 'No observation available for this component.' : 'No readable versions in these snapshots.';
    versions.append(element('p', explanation, 'inventory-no-version'));
  }
  card.append(versions);
  return card;
}
const dllVersion = version => version ? version.replace(/-(alpha|beta|rc)\./, ' $1 ') : 'Version unknown';
// Keep each server observation intact: several binaries or locations can belong to one installation.
function dllInventoryGroups(rows, { query = '', role = 'all' } = {}) {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean), groups = new Map();
  for (const row of rows) {
    if (role !== 'all' && row.role !== role) continue;
    const text = [row.product, row.product === 'unknown' ? 'Unidentified DLL' : usageLabel(row.product), row.version, dllVersion(row.version), row.file_version, row.sha256].join(' ').toLowerCase();
    if (!words.every(word => text.includes(word))) continue;
    if (!groups.has(row.product)) groups.set(row.product, []);
    groups.get(row.product).push(row);
  }
  return [...groups].sort(([a], [b]) => (a === 'unknown') - (b === 'unknown') || usageLabel(a).localeCompare(usageLabel(b)))
    .map(([product, items]) => ({ product, rows: items.sort((a, b) =>
      (a.role === 'staged') - (b.role === 'staged') || b.installations - a.installations ||
      String(a.version ?? '').localeCompare(String(b.version ?? ''), 'en', { numeric: true }) ||
      a.sha256.localeCompare(b.sha256) || String(a.version_source).localeCompare(String(b.version_source)) ||
      String(a.file_version ?? '').localeCompare(String(b.file_version ?? ''))) }));
}
function dllBuild(row, total, distinguishHash) {
  const details = element('details', undefined, `dll-build dll-${row.role}`), summary = element('summary');
  const version = element('span', undefined, 'dll-version');
  version.append(element('strong', dllVersion(row.version), row.version ? '' : 'dll-unknown-version'));
  if (row.product === 'unknown' || distinguishHash) version.append(element('small', row.sha256.slice(0, 12), 'dll-short-hash'));
  const location = element('span', row.role === 'staged' ? 'Staged' : 'Current', `dll-location dll-location-${row.role}`);
  const count = element('span', undefined, 'dll-share');
  count.append(element('span', share(row.installations, total)));
  const bar = inventorySvg('svg', { viewBox: '0 0 100 4', preserveAspectRatio: 'none', class: 'dll-bar', 'aria-hidden': 'true' });
  bar.append(inventorySvg('rect', { width: 100, height: 4, class: 'inventory-version-track' }),
    inventorySvg('rect', { width: total ? Math.max(0, Math.min(100, row.installations / total * 100)) : 0, height: 4, class: 'inventory-version-fill' }));
  count.append(bar);
  const expand = element('span', '›', 'dll-expand'); expand.setAttribute('aria-hidden', 'true');
  summary.append(version, location, count, expand);
  summary.setAttribute('aria-label', `${row.product === 'unknown' ? 'Unidentified DLL' : usageLabel(row.product)}, ${dllVersion(row.version)}, ${row.role}, ${number(row.installations)} of ${number(total)} installations. Binary details ${row.sha256.slice(0, 12)}.`);
  const info = element('div', undefined, 'dll-build-info');
  info.append(element('p', row.version ? row.version_source === 'registration' ? 'EuroScope plugin registration' : row.version_source === 'embedded' ? 'Embedded version label' : 'Windows version resource' : 'No readable version; age is unknown'));
  if (row.file_version) info.append(element('p', `File version: ${row.file_version}`));
  info.append(element('p', `${number(row.copies)} file ${row.copies === 1 ? 'copy' : 'copies'} across ${number(row.installations)} ${row.installations === 1 ? 'installation' : 'installations'}.`), element('span', 'SHA-256', 'dll-hash-label'), element('code', row.sha256));
  details.append(summary, info); return details;
}
function dllInventoryPanel(dlls, total) {
  const panel = usagePanel('All DLLs found', 'Compare versions by plugin. Expand a version for its source, file copies and fingerprint.');
  panel.className += ' inventory-dll-panel';
  const rows = dlls.rows ?? [], coverage = element('details', undefined, 'dll-coverage');
  coverage.append(element('summary', `Scan coverage · ${number(dlls.shared_installations)} of ${number(total)} installations shared DLL details`));
  const scans = element('div', undefined, 'inventory-scan-summary');
  for (const [key, label] of [['complete_installations', 'Complete scans'], ['partial_installations', 'Partial scans'], ['unavailable_installations', 'Unavailable'], ['not_shared_installations', 'Not shared']]) {
    const item = element('span'); item.append(element('strong', number(dlls[key])), document.createTextNode(` ${label}`)); scans.append(item);
  }
  coverage.append(scans, element('p', `Latest snapshot per installation · ${number(dlls.shared_installations)} / ${number(total)} shared detailed DLL observations. Not shared includes older clients and installations without this optional consent.`, 'panel-note'),
    element('p', `Current locations: ${number(dlls.current_installations)} installations · Staged locations: ${number(dlls.staged_installations)}. These counts can overlap. Current and staged describe file locations, not whether a plugin is loaded. DLL version labels can differ from package releases.`, 'panel-note'),
    element('p', 'One installation counts once per binary observation. Multiple copies do not increase that count; versions and locations can overlap. Bars use the full inventory sample. Filenames and paths are not collected.', 'panel-note'));
  panel.append(coverage);
  if (dlls.partial_installations || dlls.unavailable_installations) panel.append(element('p', `Partial or unavailable scans cannot establish that an unlisted DLL is absent. Unreadable files: ${number(dlls.unreadable_files)}.`, 'notice'));
  if (dlls.product_filter) panel.append(element('p', `Showing DLLs recognized as ${usageLabel(dlls.product_filter)}; unidentified DLLs remain visible under All applications. Scan counts still describe the whole inventory sample.`, 'panel-note'));
  if (!dlls.shared_installations) { panel.append(element('div', 'No detailed DLL observations were shared in these snapshots. The component summaries below may still be available from older clients.', 'empty')); return panel; }
  if (dlls.omitted_groups) panel.append(element('p', `Showing ${number(rows.length)} of ${number(dlls.total_groups)} binary groups; ${number(dlls.omitted_groups)} additional groups are not displayed. Search and location filters cover the displayed groups only.`, 'notice'));
  const toolbar = element('div', undefined, 'dll-toolbar'), searchLabel = element('label', 'Find a DLL', 'dll-search-label'), search = element('input');
  search.id = 'dll-search'; search.type = 'search'; search.placeholder = 'Plugin, version or fingerprint'; search.maxLength = 100; search.value = '';
  searchLabel.append(search);
  const locationLabel = element('label', 'Location'), location = element('select'); location.id = 'dll-location';
  for (const [value, label] of [['all', 'All locations'], ['current', 'Current'], ['staged', 'Staged']]) {
    const option = element('option', label); option.value = value; location.append(option);
  }
  location.value = 'all'; locationLabel.append(location);
  const reset = element('button', 'Reset filters', 'quiet'); reset.type = 'button';
  toolbar.append(searchLabel, locationLabel, reset);
  const result = element('p', undefined, 'dll-results'); result.id = 'dll-results'; result.setAttribute('role', 'status'); result.setAttribute('aria-live', 'polite');
  const grid = element('div', undefined, 'dll-groups');
  function draw() {
    const groups = dllInventoryGroups(rows, { query: search.value, role: location.value });
    const found = groups.reduce((sum, group) => sum + group.rows.length, 0);
    result.textContent = `${number(found)} of ${number(rows.length)} binary entries · ${number(groups.length)} ${groups.length === 1 ? 'group' : 'groups'} · Counts show installations, not copies.`;
    reset.disabled = !search.value && location.value === 'all'; grid.replaceChildren();
    if (!found) {
      grid.append(element('div', rows.length ? 'No DLLs match these filters. Try another name, version or fingerprint, or reset the filters.' : dlls.product_filter ? 'No matching DLL was reported in these snapshots; partial or unavailable scans do not prove absence.' : 'No readable DLL fingerprint was reported. Check scan coverage above before drawing conclusions.', 'empty'));
    }
    for (const group of groups) {
      const section = element('section', undefined, `dll-group${group.product === 'unknown' ? ' dll-unidentified' : ''}`), heading = element('div', undefined, 'dll-group-heading');
      const builds = new Set(group.rows.map(row => row.sha256)).size;
      heading.append(element('h3', group.product === 'unknown' ? 'Unidentified DLLs' : usageLabel(group.product)), element('span', `${number(builds)} ${builds === 1 ? 'binary' : 'binaries'}`));
      section.append(heading);
      const labels = element('div', undefined, 'dll-column-labels'); labels.setAttribute('aria-hidden', 'true');
      labels.append(element('span', 'Version'), element('span', 'Location'), element('span', `Installations · of ${number(total)}`), element('span', ''));
      section.append(labels);
      for (const row of group.rows) {
        const repeated = group.rows.filter(item => item.version === row.version && item.role === row.role).length > 1;
        section.append(dllBuild(row, total, repeated));
      }
      grid.append(section);
    }
  }
  search.addEventListener('input', draw); location.addEventListener('change', draw);
  reset.addEventListener('click', () => { search.value = ''; location.value = 'all'; draw(); search.focus(); });
  panel.append(toolbar, result, grid, element('p', 'Current = other configured locations. Staged = GNG update folders. Neither confirms that a plugin is loaded.', 'panel-note'));
  draw(); return panel;
}
function renderInventory(data) {
  const fragment = document.createDocumentFragment(), inventory = data.inventory ?? {}, total = inventory.snapshot_installations ?? 0, components = inventory.components ?? [];
  fragment.append(usageWindow(data, true));
  const hero = element('section', undefined, 'inventory-intro'), sample = element('div', undefined, 'inventory-sample');
  sample.append(element('span', 'Inventory installations', 'eyebrow'), element('strong', number(total)), element('span', 'One snapshot per installation', 'muted'));
  const context = element('div', undefined, 'inventory-context');
  context.append(element('h2', 'Shared software snapshot'), element('p', 'Every chart uses the same sample of installations that chose to share their software inventory.'),
    element('p', inventory.latest_snapshot_at ? `Latest snapshot · ${time(inventory.latest_snapshot_at)} UTC` : 'No snapshot in this selection', 'inventory-freshness'));
  const help = element('details', undefined, 'inventory-help');
  help.append(element('summary', 'How to read these observations'), element('p', 'This is an independent opt-in sample, including inventory-only installations. Each installation contributes its latest snapshot in the selected period. Software presence does not prove it is enabled, running or being used.'),
    element('p', 'Version unknown means detected without a readable version; it does not establish age. Not detected means no matching component was found in the checked locations. Unavailable means presence could not be determined; it is not absence. Optional detailed DLL observations include unidentified files as fingerprints, with no filenames, paths or file contents. Versions are ranked by frequency, not by release date.'));
  context.append(element('p', 'Not a live session view: snapshots do not report whether EuroScope is running or connected.', 'inventory-session-note'));
  if (inventory.freshness?.length) {
    const freshness = element('div', undefined, 'inventory-age');
    for (const age of ['last_24h', 'one_to_seven_days', 'older_than_seven_days']) {
      const count = inventory.freshness.find(row => row.age === age)?.installations ?? 0;
      const item = element('span'); item.append(element('strong', number(count)), document.createTextNode(` ${usageLabel(age)}`)); freshness.append(item);
    }
    context.append(freshness);
  }
  context.append(help); hero.append(sample, context); fragment.append(hero);
  if (data.filters?.application) fragment.append(element('p', `Showing components for ${usageLabel(data.filters.application)}. The inventory denominator still includes every matching snapshot, including explicit absence and unavailable observations.`, 'scope-note'));
  if (!total || (!components.length && !inventory.dll_inventory)) { fragment.append(element('div', total ? 'The selected application has no component in the fixed software inventory. Choose All applications to view the available observations.' : 'No software or plugin observations for this selection. Try a wider period or different filters.', 'empty')); return fragment; }
  for (const [plugin, name, description] of [[false, 'Applications & GNG', 'Installed tools and the Swedish sector package'], [true, 'EuroScope plugins', 'DLL version labels can differ from package releases. Detection does not prove a plugin is enabled or loaded.']]) {
    if (plugin && inventory.dll_inventory) fragment.append(dllInventoryPanel(inventory.dll_inventory, total));
    const selected = components.filter(component => (component.source === 'plugin_file') === plugin);
    if (!selected.length) continue;
    const section = element('section', undefined, `inventory-section${plugin ? ' inventory-plugin-section' : ''}`), heading = element('div', undefined, 'inventory-section-heading');
    const label = element('div'); label.append(element('h2', name), element('p', description));
    heading.append(label, element('span', `${selected.length} component${selected.length === 1 ? '' : 's'}`, 'inventory-section-count'));
    const grid = element('div', undefined, 'inventory-component-grid');
    for (const component of selected) grid.append(inventoryComponentCard(component, total));
    section.append(heading, grid);
    if (plugin && inventory.dll_inventory?.shared_installations) {
      const legacy = element('details', undefined, 'inventory-legacy');
      legacy.append(element('summary', 'Recognized component summaries · includes older clients'), element('p', 'These four component checks predate detailed DLL reporting. They cover the full inventory sample and are separate from the fingerprint list above.', 'panel-note'), section); fragment.append(legacy);
    } else fragment.append(section);
  }
  return fragment;
}
