'use strict';
// These views describe observations from opt-in installations, never a controller census.
const usageNames = {
  launchpad: 'Launchpad', euroscope: 'EuroScope', gng: 'Swedish GNG', trackaudio: 'TrackAudio', vacs: 'VACS', vatis: 'vATIS', vatefs: 'VatEFS', vatiris: 'VATIRIS', maintenance: 'Maintenance',
  topsky: 'TopSky', rdf: 'RDF', vatefs_plugin: 'VatEFS plugin', euroscope_topsky: 'TopSky', euroscope_rdf: 'RDF', euroscope_vatefs: 'VatEFS plugin',
  afv_euroscope_bridge: 'AFV EuroScope Bridge', check_updates: 'Check for updates', controller_profile_review: 'Controller-profile review', gng_package_review: 'GNG package review', gng_cleanup_review: 'GNG cleanup review', gng_restore_review: 'GNG restore review', setup_guide: 'Setup guide', install_or_update: 'Install or update tools', adopt: 'Adopt existing paths', font_check: 'Font check',
  update_check: 'Check for updates', gng_review: 'GNG package review', profile_review: 'Controller-profile review', software_management: 'Install or update tools', setup: 'Setup guide', cleanup: 'GNG cleanup review',
  cached: 'Cached package', cache: 'Cached package', download: 'AeroNav download', downloaded: 'AeroNav download', import: 'Imported ZIP', imported: 'Imported ZIP',
  existing: 'Use existing installations', fresh: 'Start with a fresh setup', undecided: 'Not chosen',
  file_version: 'File version', product_version: 'Product version', registered_msi: 'Registered MSI', msi: 'Registered MSI', package: 'Package metadata',
  configured_app: 'Configured application', package_metadata: 'Package metadata', plugin_file: 'Plugin file metadata', standard: 'Standard', larger: 'Larger', largest: 'Largest', dark: 'Dark', light: 'Light',
  preferences: 'Preferences only', other: 'Other observed versions',
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
  const start = window.start ? String(window.start).slice(0, 10) : '—', end = window.end_exclusive ? new Date(new Date(window.end_exclusive).valueOf() - 86400_000).toISOString().slice(0, 10) : '—';
  note.append(element('span', `${start} – ${end} · ${window.days ?? '—'} full UTC days. Today is excluded.`));
  if (!window.coverage_complete) note.append(element('strong', 'Partial coverage: the service does not have observations for the entire selected period.', 'coverage-warning'));
  if (data.filters?.application && !inventory) note.append(element('span', `Usage cohort: installations with ${usageLabel(data.filters.application)} activity. Sessions, features and preferences describe those installations; launch counts describe the selected application.`));
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
  chart.setAttribute('role', 'img'); chart.setAttribute('aria-label', rows.map(row => `${row.date}: ${number(row.active_installations)} active installations`).join('; '));
  for (const row of rows) {
    const day = element('div', undefined, 'chart-day'), bar = document.createElementNS('http://www.w3.org/2000/svg', 'svg'), rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    bar.setAttribute('viewBox', '0 0 40 100'); bar.setAttribute('preserveAspectRatio', 'none'); bar.setAttribute('aria-hidden', 'true');
    const height = row.active_installations / max * 100; rect.setAttribute('x', '3'); rect.setAttribute('y', String(100 - height)); rect.setAttribute('width', '34'); rect.setAttribute('height', String(height)); rect.setAttribute('rx', '2'); bar.append(rect);
    day.append(element('strong', number(row.active_installations)), bar, element('span', row.date.slice(5))); chart.append(day);
  }
  panel.append(chart);
  const detail = element('details'); detail.append(element('summary', 'Daily sessions and launch actions'), table(rows, [{ key: 'date', label: 'Date · UTC' }, { key: 'active_installations', label: 'Active installations', render: number }, { key: 'sessions', label: 'Sessions', render: number }, { key: 'app_launches', label: 'Launch actions', render: number }])); panel.append(detail);
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
function inventorySummary(data) {
  const inventory = data.inventory ?? {}, panel = usagePanel('Software & plugin observations', 'Separate inventory consent. This sample includes installations that do not share usage.');
  panel.append(element('strong', number(inventory.snapshot_installations), 'summary-number'), element('p', 'installations shared a full inventory in this period', 'muted'));
  panel.append(element('p', inventory.latest_snapshot_at ? `Latest snapshot: ${time(inventory.latest_snapshot_at)} UTC.` : 'No inventory snapshots received for this selection.', 'panel-note'));
  const button = element('button', 'View software & plugin versions'); button.addEventListener('click', () => document.querySelector('[data-view="inventory"]').click()); panel.append(button);
  return panel;
}
function renderUsageDashboard(data) {
  const fragment = document.createDocumentFragment(), cohort = data.cohort ?? {}, total = cohort.active_installations ?? 0;
  fragment.append(usageWindow(data));
  const headline = element('div', undefined, 'cards usage-cards');
  const returnNote = cohort.returning_available ? `Also active in the preceding ${data.window.days} days.` : cohort.returning_reason === 'retention' ? 'The preceding period falls outside raw-data retention. Use 7 days.' : 'A full preceding period is not available yet.';
  headline.append(usageCard('Active installations', number(total), 'Distinct support IDs with recorded usage.'), usageCard('Recorded sessions', number(cohort.sessions), 'Distinct sessions with activity in this period, not app starts or running time.'), usageCard('Returning installations', cohort.returning_available ? share(cohort.returning_installations, total) : 'Unavailable', returnNote), usageCard('Application launch actions', number(cohort.app_launches), `${number(cohort.app_launch_installations)} installations used Launchpad to open a tool.`));
  fragment.append(headline);
  if (!total) fragment.append(element('p', 'No usage observations for this selection. Inventory may still be available separately. No observations does not mean nobody uses Launchpad.', 'notice'));
  const first = element('div', undefined, 'usage-grid'); first.append(dailyUsage(data.daily));
  const releases = usagePanel('Launchpad versions in use', 'Latest observed version per active installation within these filters. This does not identify the latest published release.'); releases.append(distribution(data.releases, 'version', total)); first.append(releases); fragment.append(first);
  const second = element('div', undefined, 'usage-grid equal');
  const apps = usagePanel('Applications opened through Launchpad', 'Explicit launch actions only. Running time and network connections are not measured.');
  apps.append(table(data.applications, [{ key: 'application', label: 'Application', render: usageLabel }, { key: 'launches', label: 'Launch actions', render: number }, { key: 'installations', label: `Installations · of ${number(total)}`, render: value => share(value, total) }], { empty: 'No application launch observations yet.' }));
  const features = usagePanel('Which features get used?', 'Installations count once per feature. Uses count recorded actions, not completed outcomes.');
  features.append(table(data.features, [{ key: 'feature', label: 'Feature', render: usageLabel }, { key: 'uses', label: 'Uses', render: number }, { key: 'installations', label: `Installations · of ${number(total)}`, render: value => share(value, total) }], { empty: 'No feature observations yet.' })); second.append(apps, features); fragment.append(second);
  const third = element('div', undefined, 'usage-grid thirds');
  const gng = usagePanel('GNG package sources', 'How packages reached review. Preparation is separate from installation.');
  gng.append(table(data.gng_sources, [{ key: 'source', label: 'Source', render: usageLabel }, { key: 'packages', label: 'Prepared', render: number }, { key: 'installations', label: 'Installations', render: number }], { empty: 'No package preparation observations yet.' }));
  const setup = usagePanel('Setup choices', 'Chosen routes, including unfinished guides. A choice is not a completed installation.');
  setup.append(table(data.setup_routes, [{ key: 'route', label: 'Route', render: usageLabel }, { key: 'attempts', label: 'Choices', render: number }, { key: 'installations', label: 'Installations', render: number }], { empty: 'No setup route observations yet.' }));
  third.append(gng, setup, inventorySummary(data)); fragment.append(third, preferencePanel(data));
  fragment.append(element('p', 'Usage is optional and may be sampled or delayed. Support IDs approximate installations, not individual people. Public submissions are unverified.', 'scope-note'));
  return fragment;
}
function renderInventory(data) {
  const fragment = document.createDocumentFragment(), inventory = data.inventory ?? {}, total = inventory.snapshot_installations ?? 0;
  fragment.append(usageWindow(data, true), cards([['Inventory installations', total], ['Latest snapshot · UTC', time(inventory.latest_snapshot_at)]]));
  fragment.append(element('p', 'This is an independent opt-in sample, including inventory-only installations. Each installation contributes its latest full snapshot in the selected period. Software presence does not prove it is enabled, running or being used.', 'notice'));
  if (data.filters?.application) fragment.append(element('p', `Showing components for ${usageLabel(data.filters.application)}. The inventory denominator still includes every matching snapshot, including explicit absence and unavailable observations.`, 'scope-note'));
  if (!total || !inventory.components?.length) { fragment.append(element('div', total ? 'The selected application has no component in the fixed software inventory. Choose All applications to view the available observations.' : 'No software or plugin observations for this selection.', 'empty')); return fragment; }
  const grid = element('div', undefined, 'inventory-grid');
  for (const component of inventory.components) {
    const panel = usagePanel(usageLabel(component.component), `Version source: ${usageLabel(component.source)}. Latest observation per installation.`);
    const metadata = element('dl', undefined, 'inventory-counts');
    for (const [label, value] of [['Version known', component.known], ['Present · version unknown', component.unknown], ['Absent', component.absent], ['Unavailable', component.unavailable]]) metadata.append(element('dt', label), element('dd', number(value)));
    panel.append(metadata);
    panel.append(table(component.versions, [{ key: 'version', label: 'Observed version' }, { key: 'installations', label: `Installations · of ${number(total)}`, render: value => share(value, total) }], { empty: 'No readable version metadata in this sample.' }));
    grid.append(panel);
  }
  fragment.append(grid, element('p', 'Unknown means the component was detected without usable version metadata. Absent is an explicit observation. Unavailable means it could not be determined; it is not absence. Only recognized components are inventoried, with no paths or file contents.', 'scope-note'));
  return fragment;
}
