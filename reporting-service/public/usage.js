'use strict';
// These views describe observations from opt-in installations, never a controller census.
const usageNames = {
  launchpad: 'Launchpad', euroscope: 'EuroScope', gng: 'Swedish GNG', trackaudio: 'TrackAudio', vacs: 'VACS', vatis: 'vATIS', vatefs: 'VatEFS', vatiris: 'VATIRIS', maintenance: 'Maintenance', saaman: 'SAAMAN',
  topsky: 'TopSky', rdf: 'RDF', vatefs_plugin: 'VatEFS plugin', euroscope_topsky: 'TopSky', euroscope_rdf: 'RDF', euroscope_vatefs: 'VatEFS plugin',
  afv_euroscope_bridge: 'AFV EuroScope Bridge', check_updates: 'Check for updates', controller_profile_review: 'Controller-profile review', gng_package_review: 'GNG package review', gng_cleanup_review: 'GNG cleanup review', gng_restore_review: 'GNG restore review', setup_guide: 'Setup guide', install_or_update: 'Install or update tools', adopt: 'Adopt existing paths', font_check: 'Font check',
  update_check: 'Check for updates', gng_review: 'GNG package review', profile_review: 'Controller-profile review', software_management: 'Install or update tools', setup: 'Setup guide', cleanup: 'GNG cleanup review',
  cached: 'Cached package', cache: 'Cached package', download: 'AeroNav download', downloaded: 'AeroNav download', import: 'Imported ZIP', imported: 'Imported ZIP',
  existing: 'Use existing installations', fresh: 'Start with a fresh setup', undecided: 'Not chosen',
  file_version: 'File version', product_version: 'Product version', registered_msi: 'Registered MSI', msi: 'Registered MSI', package: 'Package metadata',
  configured_app: 'Configured application', package_metadata: 'Package metadata', plugin_file: 'Plugin file', standard: 'Standard', larger: 'Larger', largest: 'Largest', dark: 'Dark', light: 'Light',
  arrival_load_balancer: 'Arrival Load Balancer', cdm: 'CDM', ground_radar: 'Ground Radar', maestro: 'MAESTRO', vch: 'VCH', vatcan_bookings: 'VATCAN Bookings',
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
  const start = window.start ? String(window.start).slice(0, 10) : '—', asOf = window.as_of ?? (window.end_exclusive ? new Date(new Date(window.end_exclusive).valueOf() - 1).toISOString() : null);
  const end = asOf ? String(asOf).slice(0, 10) : '—', updated = asOf ? `${String(asOf).slice(11, 16)} UTC` : 'the latest refresh';
  note.append(element('span', `${start} – ${end} · ${window.days ?? '—'} UTC days, including today. Today is partial; updated ${updated}.`));
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
function inventorySummary(data) {
  const inventory = data.inventory ?? {}, panel = usagePanel('Software & plugin observations', 'Separate inventory consent. This sample includes installations that do not share usage.');
  panel.append(element('strong', number(inventory.snapshot_installations), 'summary-number'), element('p', 'installations shared a software inventory in this period', 'muted'));
  panel.append(element('p', inventory.latest_snapshot_at ? `Latest snapshot: ${time(inventory.latest_snapshot_at)} UTC.` : 'No inventory snapshots received for this selection.', 'panel-note'));
  const button = element('button', 'View software & plugin versions'); button.addEventListener('click', () => document.querySelector('[data-view="inventory"]').click()); panel.append(button);
  return panel;
}
function renderUsageDashboard(data) {
  const fragment = document.createDocumentFragment(), cohort = data.cohort ?? {}, total = cohort.active_installations ?? 0;
  fragment.append(usageWindow(data));
  const headline = element('div', undefined, 'cards usage-cards');
  const returnNote = cohort.returning_available ? `Also seen in the preceding ${data.window.days} complete UTC days.` : cohort.returning_reason === 'retention' ? 'The preceding period falls outside raw-data retention. Use 7 days.' : 'A full preceding period is not available yet.';
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
  third.append(gng, setup); if (data.inventory) third.append(inventorySummary(data)); fragment.append(third, preferencePanel(data));
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
function dllInventoryPanel(dlls, total) {
  const panel = usagePanel('All DLLs found', 'Files observed in the configured EuroScope locations, including unrecognized DLLs. Filenames and paths are not collected.');
  panel.className += ' inventory-dll-panel';
  const scans = element('div', undefined, 'inventory-scan-summary');
  for (const [key, label] of [['complete_installations', 'Complete scans'], ['partial_installations', 'Partial scans'], ['unavailable_installations', 'Unavailable'], ['not_shared_installations', 'Not shared']]) {
    const item = element('span'); item.append(element('strong', number(dlls[key])), document.createTextNode(` ${label}`)); scans.append(item);
  }
  panel.append(scans, element('p', `Latest snapshot per installation · ${number(dlls.shared_installations)} / ${number(total)} shared detailed DLL observations. Not shared includes older clients and installations without this optional consent.`, 'panel-note'));
  if (dlls.partial_installations || dlls.unavailable_installations) panel.append(element('p', `Partial or unavailable scans cannot establish that an unlisted DLL is absent. Unreadable files: ${number(dlls.unreadable_files)}.`, 'panel-note'));
  if (dlls.product_filter) panel.append(element('p', `Showing DLLs recognized as ${usageLabel(dlls.product_filter)}; unidentified DLLs remain visible under All applications. Scan counts still describe the whole inventory sample.`, 'panel-note'));
  if (!dlls.shared_installations) { panel.append(element('div', 'No detailed DLL observations were shared in these snapshots. The component summaries below may still be available from older clients.', 'empty')); return panel; }
  const rows = dlls.rows ?? [];
  panel.append(element('p', `Current locations: ${number(dlls.current_installations)} installations · Staged locations: ${number(dlls.staged_installations)}. These counts can overlap. Current and staged describe file locations, not whether a plugin is loaded. DLL version labels can differ from package releases.`, 'panel-note'));
  panel.append(table(rows, [
    { key: 'product', label: 'DLL / fingerprint', render: (product, row) => {
      const cell = element('div', undefined, 'inventory-dll-identity');
      cell.append(element('strong', product === 'unknown' ? `Unidentified DLL · ${row.sha256.slice(0, 12)}` : usageLabel(product)));
      const fingerprint = element('details'); fingerprint.append(element('summary', product === 'unknown' ? 'Full fingerprint' : `SHA-256 · ${row.sha256.slice(0, 12)}`), element('code', row.sha256)); cell.append(fingerprint); return cell;
    } },
    { key: 'version', label: 'Version read from DLL', render: (version, row) => {
      const cell = element('div', undefined, 'inventory-dll-version');
      cell.append(element('strong', version ? version.replace(/-(alpha|beta|rc)\./, ' $1 ') : 'Version unknown'));
      if (version) cell.append(element('small', row.version_source === 'registration' ? 'EuroScope plugin registration'
        : row.version_source === 'embedded' ? 'Embedded version label' : 'Windows version resource'));
      if (row.file_version) cell.append(element('small', `File version: ${row.file_version}`));
      if (!version && !row.file_version) cell.append(element('small', 'No readable version; age is unknown'));
      return cell;
    } },
    { key: 'role', label: 'Location', render: value => value === 'staged' ? 'Staged' : 'Current' },
    { key: 'installations', label: `Installations · of ${number(total)}`, render: (value, row) => {
      const cell = element('div', undefined, 'inventory-dll-count'); cell.append(element('strong', share(value, total)), element('small', `${number(row.copies)} file copies`)); return cell;
    } }
  ], { empty: dlls.product_filter ? 'No matching DLL was reported in these snapshots; partial or unavailable scans do not prove absence.' : 'No readable DLL fingerprint was reported. Check scan coverage above before drawing conclusions.' }));
  if (dlls.omitted_groups) panel.append(element('p', `Showing ${number(rows.length)} of ${number(dlls.total_groups)} binary groups; ${number(dlls.omitted_groups)} additional groups are not displayed.`, 'notice'));
  panel.append(element('p', 'One installation counts once per fingerprint and location. Multiple copies do not increase that count; rows and locations can overlap.', 'panel-note'));
  return panel;
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
