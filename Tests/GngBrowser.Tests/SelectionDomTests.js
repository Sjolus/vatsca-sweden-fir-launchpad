'use strict';
const assert = require('node:assert/strict');
const vm = require('node:vm');
const scripts = JSON.parse(require('node:fs').readFileSync(0, 'utf8'));
let passed = 0;
function test(name, run) {
    try { run(); passed++; }
    catch (error) { throw new Error(name + ': ' + error.message, { cause: error }); }
}
function anchor(label, options = {}) {
    return {
        textContent: label, href: options.href ?? '/download?synthetic=' + label, disabled: options.disabled ?? false,
        clicked: 0, hrefReads: 0,
        closest(selector) { assert.equal(selector, '.disabled, [disabled], [aria-disabled="true"]'); return this.disabled ? this : null; },
        getAttribute(name) { assert.equal(name, 'href'); this.hrefReads++; return this.href; },
        click() { this.clicked++; if (options.throwOnClick) throw new Error('synthetic click failure'); }
    };
}
function cell(value, links = [], colSpan = 1) {
    return { textContent: value, colSpan, rowSpan: 1, querySelectorAll(selector) { assert.equal(selector, 'a'); return links; }, links };
}
function row(options = {}) {
    const zip = anchor(options.zipLabel ?? 'zip', options);
    const seven = anchor(options.sevenLabel ?? '7z');
    return {
        cells: [cell(options.client ?? 'ES'), cell(options.name ?? 'ESAA Full_Package'), cell(options.airac ?? '2610 / 01'),
            cell(options.revision ?? '3'), cell(options.released ?? '2026-10-01 22:25:00'), cell('zip', [zip]), cell('7z', [seven])], zip, seven
    };
}
function table(rows) {
    return { rows: [{ cells: ['Client', 'Packagename', 'AIRAC', 'Version', 'Released', 'Download'].map((value, i) => cell(value, [], i === 5 ? 2 : 1)) }, ...rows] };
}
function page(rows, url = 'https://files.aero-nav.com/ESAA') {
    const context = {
        URL, window: { location: { href: url } }, tables: [table(rows)], reads: 0,
        document: { querySelectorAll(selector) { assert.equal(selector, 'table'); context.reads++; return context.tables; } }
    };
    for (const key of ['cookie', 'forms']) Object.defineProperty(context.document, key, { get() { throw new Error('Private document surface read: ' + key); } });
    for (const key of ['localStorage', 'sessionStorage', 'credentials', 'tokens']) Object.defineProperty(context.window, key, { get() { throw new Error('Private browser surface read: ' + key); } });
    return context;
}
function evaluate(script, context) {
    assert.equal(typeof script, 'string', 'Exact production script missing for this fixture');
    return JSON.parse(JSON.stringify(vm.runInNewContext(script, context, { timeout: 1000 })));
}
function inspect(context, kind = 'full') { return evaluate(scripts.inspection[kind], context); }
function download(context, identity, kind = 'full') { return evaluate(scripts.download[kind][identity], context); }
function run(context, kind = 'full') {
    const result = inspect(context, kind);
    return result.status === 'ready' ? download(context, result.identity, kind) : result;
}
function noClicks(rows) { for (const entry of rows) { assert.equal(entry.zip.clicked, 0); assert.equal(entry.seven.clicked, 0); } }

test('inspection obtains public identity without claiming or clicking the download', () => {
    const entry = row(); const context = page([entry]);
    const result = inspect(context);
    assert.deepEqual(result, { status: 'ready', version: '2610/01 rev.3', identity: '20261001222500-261001-0003' });
    assert.deepEqual(inspect(context), result);
    noClicks([entry]); assert.equal(Object.getOwnPropertySymbols(context.window).length, 0);
    assert.equal(download(context, result.identity).status, 'clicked');
    assert.equal(entry.zip.clicked, 1);
    assert.deepEqual(inspect(context), result);
    assert.equal(download(context, result.identity).status, 'already-attempted');
    assert.equal(entry.zip.clicked, 1);
});

for (const options of [
    { airac: '2611 / 01' }, { revision: '4' }, { released: '2026-10-01 22:25:01' }
]) test('metadata changed between inspection and click blocks stale selection: ' + JSON.stringify(options), () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    const newer = row(options); context.tables[0].rows.push(newer);
    assert.deepEqual(download(context, result.identity), { status: 'selection-changed', version: null, identity: null });
    noClicks([entry, newer]); assert.equal(Object.getOwnPropertySymbols(context.window).length, 0);
});

test('a removed inspected package cannot download its replacement', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    context.tables[0].rows.splice(1);
    assert.equal(download(context, result.identity).status, 'no-package'); noClicks([entry]);
});

test('duplicate metadata introduced after inspection still blocks clicking', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    const duplicate = row(); context.tables[0].rows.push(duplicate);
    assert.equal(download(context, result.identity).status, 'ambiguous'); noClicks([entry, duplicate]);
});

test('a ZIP link that becomes disabled after inspection waits without claiming it', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    entry.zip.disabled = true;
    assert.equal(download(context, result.identity).status, 'waiting-for-login'); noClicks([entry]);
    assert.equal(Object.getOwnPropertySymbols(context.window).length, 0);
});

test('a ZIP link changed to an external destination after inspection is rejected', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    entry.zip.href = 'https://other.example.test/download';
    assert.equal(download(context, result.identity).status, 'unsupported-link'); noClicks([entry]);
    assert.equal(Object.getOwnPropertySymbols(context.window).length, 0);
});

test('navigation between inspection and click prevents DOM reads on the new page', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    const reads = context.reads; context.window.location.href = 'https://auth.example.test/login';
    assert.equal(download(context, result.identity).status, 'wrong-page');
    assert.equal(context.reads, reads); noClicks([entry]);
});

test('metadata is available before the click causes navigation', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context);
    entry.zip.click = () => {
        assert.equal(result.status, 'ready'); assert.equal(result.identity, '20261001222500-261001-0003');
        entry.zip.clicked++; context.window.location.href = 'about:blank';
    };
    assert.equal(download(context, result.identity).status, 'clicked'); assert.equal(entry.zip.clicked, 1);
});

test('reference selection also refuses a changed release within the required version', () => {
    const entry = row(); const context = page([entry]); const result = inspect(context, 'fullReference');
    const newer = row({ released: '2026-10-01 22:25:01' }); context.tables[0].rows.push(newer);
    assert.equal(download(context, result.identity, 'fullReference').status, 'selection-changed'); noClicks([entry, newer]);
});

test('current public disabled layout waits for login', () => {
    const full = row({ disabled: true, href: '#' });
    const update = row({ name: 'ESAA Update_Only', disabled: true, href: '#', released: '2026-10-01 22:25:54' });
    const result = run(page([full, update]));
    assert.deepEqual(result, { status: 'waiting-for-login', version: '2610/01 rev.3', identity: '20261001222500-261001-0003' });
    noClicks([full, update]); assert.equal(full.zip.hrefReads, 0);
});
test('Full selection ignores newer Update Only and clicks observed full ZIP only', () => {
    const full = row({ href: '/observed-full-endpoint?id=synthetic' });
    const update = row({ name: 'ESAA Update_Only', revision: '99' });
    assert.equal(run(page([update, full])).status, 'clicked');
    assert.equal(full.zip.clicked, 1); assert.equal(full.seven.clicked, 0); assert.equal(update.zip.clicked, 0);
});
test('Update Only selection ignores newer Full rows', () => {
    const full = row({ revision: '99' });
    const update = row({ name: 'ESAA Update_Only', released: '2026-10-01 22:25:54' });
    const result = run(page([full, update]), 'update');
    assert.equal(result.status, 'clicked'); assert.equal(result.identity, '20261001222554-261001-0003');
    assert.equal(update.zip.clicked, 1); assert.equal(full.zip.clicked, 0);
});
test('latest requested kind has no fallback to older downloadable package', () => {
    const old = row({ revision: '2' }); const latest = row({ disabled: true, href: '#' });
    assert.equal(run(page([old, latest])).status, 'waiting-for-login'); noClicks([old, latest]);
    assert.equal(old.zip.hrefReads, 0);
});
test('AIRAC then revision then release time determine order regardless of row placement', () => {
    const oldAirac = row({ airac: '2609 / 01', revision: '99', released: '2026-12-01 12:00:00' });
    const oldRevision = row({ revision: '2', released: '2026-12-01 12:00:00' });
    const oldTime = row(); const latest = row({ released: '2026-10-01 22:25:01' });
    assert.equal(run(page([oldAirac, latest, oldRevision, oldTime])).identity, '20261001222501-261001-0003');
    assert.equal(latest.zip.clicked, 1); noClicks([oldAirac, oldRevision, oldTime]);
});
test('AIRAC package sequence is ranked numerically', () => {
    const first = row({ airac: '2610 / 01', revision: '99' }); const next = row({ airac: '2610 / 02', revision: '1' });
    assert.equal(run(page([first, next])).version, '2610/02 rev.1'); assert.equal(next.zip.clicked, 1);
});
test('duplicate latest metadata is ambiguous even when one link is disabled', () => {
    const first = row(); const duplicate = row({ disabled: true });
    assert.equal(run(page([first, duplicate])).status, 'ambiguous'); noClicks([first, duplicate]);
});
test('malformed full metadata prevents choosing an apparently older row', () => {
    const valid = row(); const unknown = row({ revision: 'revision 9' });
    assert.equal(run(page([valid, unknown])).status, 'ambiguous'); noClicks([valid, unknown]);
});
for (const options of [
    { airac: '2610' }, { airac: '2615 / 01' }, { airac: '2610 / 00' }, { revision: '-1' }, { revision: '1.2' }, { revision: '10000' },
    { released: '2026-02-30 12:00:00' }, { released: '2026-10-01 24:00:00' }, { released: '2026-10-01T22:25:00Z' },
    { released: '2026-10-01 22:25:00 private-extra' }, { name: 'ESAA Full Package' }
]) test('unrecognized metadata blocks automation: ' + JSON.stringify(options), () => {
    const entry = row(options); assert.equal(run(page([entry])).status, 'ambiguous'); noClicks([entry]);
});
for (const href of ['#', '#download', '', 'javascript:synthetic()', 'http://files.aero-nav.com/download', '//other.example.test/download',
    'https://files.aero-nav.com.evil.test/download', 'https://files.aero-nav.com:8443/download', 'https://user:synthetic@files.aero-nav.com/download',
    'https://files.aero-nav.com/download#zip', 'https://other.aero-nav.com/download', '/ESAA'])
    test('unsupported observed download URL never clicked: ' + href, () => {
        const entry = row({ href }); assert.equal(run(page([entry])).status, 'unsupported-link'); noClicks([entry]);
    });
test('signed-looking query remains browser-only and does not appear in result', () => {
    const entry = row({ href: '/download?synthetic-private-value=never-return-this' });
    const result = run(page([entry])); assert.equal(result.status, 'clicked');
    assert.deepEqual(Object.keys(result).sort(), ['identity', 'status', 'version']); assert.ok(!JSON.stringify(result).includes('never-return-this'));
});
test('same-document attempts are claimed before click and cannot repeat', () => {
    const entry = row(); const context = page([entry]);
    assert.equal(run(context).status, 'clicked'); assert.equal(run(context).status, 'already-attempted');
    assert.equal(entry.zip.clicked, 1);
});
test('a throwing click does not permit automatic same-document retry', () => {
    const entry = row({ throwOnClick: true }); const context = page([entry]);
    assert.equal(run(context).status, 'unsupported-link'); assert.equal(run(context).status, 'already-attempted'); assert.equal(entry.zip.clicked, 1);
});
test('paired Update Only and Full downloads can each be claimed once on the same page', () => {
    const full = row(); const update = row({ name: 'ESAA Update_Only' }); const context = page([full, update]);
    assert.equal(run(context, 'update').status, 'clicked'); assert.equal(run(context, 'full').status, 'clicked');
    assert.equal(run(context, 'update').status, 'already-attempted'); assert.equal(run(context, 'full').status, 'already-attempted');
    assert.equal(full.zip.clicked, 1); assert.equal(update.zip.clicked, 1);
});
test('matching Full reference uses AIRAC/revision rather than Update Only timestamp', () => {
    const matching = row({ released: '2026-10-01 22:25:00' }); const newer = row({ airac: '2611 / 01', revision: '1', released: '2026-10-29 12:00:00' });
    const result = run(page([newer, matching]), 'fullReference');
    assert.equal(result.identity, '20261001222500-261001-0003'); assert.equal(matching.zip.clicked, 1); assert.equal(newer.zip.clicked, 0);
});
test('unavailable matching reference never falls back to a different version', () => {
    const other = row({ revision: '4' }); assert.equal(run(page([other]), 'fullReference').status, 'no-package'); noClicks([other]);
});
test('disabled newest matching reference does not use an older release of that version', () => {
    const old = row({ released: '2026-10-01 22:24:00' }); const latest = row({ disabled: true, href: '#' });
    assert.equal(run(page([old, latest]), 'fullReference').status, 'waiting-for-login'); noClicks([old, latest]);
});
test('waiting does not claim a download before authentication makes the link usable', () => {
    const entry = row({ disabled: true }); const context = page([entry]);
    assert.equal(run(context).status, 'waiting-for-login'); entry.zip.disabled = false;
    assert.equal(run(context).status, 'clicked'); assert.equal(entry.zip.clicked, 1);
});
for (const url of ['https://auth.example.test/authorize?synthetic=1', 'https://files.aero-nav.com/ESAA/more', 'https://files.aero-nav.com/esaa',
    'http://files.aero-nav.com/ESAA', 'https://files.aero-nav.com:8443/ESAA', 'https://user:synthetic@files.aero-nav.com/ESAA', 'about:blank', 'not a URL'])
    test('wrong page receives no DOM reads: ' + url, () => {
        const entry = row(); const context = page([entry], url); assert.equal(run(context).status, 'wrong-page');
        assert.equal(context.reads, 0); noClicks([entry]);
    });
test('trailing-slash ESAA page and query remain valid', () => {
    const entry = row(); assert.equal(run(page([entry], 'https://files.aero-nav.com/ESAA/?synthetic=1')).status, 'clicked');
});
test('other clients and regions cannot supply the chosen package', () => {
    const otherClient = row({ client: 'VRC' }); const otherRegion = row({ name: 'EKDK Full_Package' });
    assert.equal(run(page([otherClient, otherRegion])).status, 'no-package'); noClicks([otherClient, otherRegion]);
});
test('multiple matching package tables are ambiguous', () => {
    const first = row(); const other = row(); const context = page([first]); context.tables.push(table([other]));
    assert.equal(run(context).status, 'ambiguous'); noClicks([first, other]);
});
test('unexpected header columns do not trigger a click', () => {
    const entry = row(); const context = page([entry]); context.tables[0].rows[0].cells[3].textContent = 'Other version';
    assert.equal(run(context).status, 'no-package'); noClicks([entry]);
});
test('unexpected header spans are ambiguous', () => {
    const entry = row(); const context = page([entry]); context.tables[0].rows[0].cells[5].colSpan = 1;
    assert.equal(run(context).status, 'ambiguous'); noClicks([entry]);
});
test('mixed data column widths are ambiguous', () => {
    const entry = row(); entry.cells.pop(); assert.equal(run(page([entry])).status, 'ambiguous'); noClicks([entry]);
});
for (const options of [{ zipLabel: '7z', sevenLabel: 'zip' }, { zipLabel: 'download' }, { sevenLabel: 'other' }])
    test('unrecognized archive column layout is not guessed: ' + JSON.stringify(options), () => {
        const entry = row(options); assert.equal(run(page([entry])).status, 'ambiguous'); noClicks([entry]);
    });
test('multiple ZIP anchors require manual choice', () => {
    const entry = row(); entry.cells[5].links.push(anchor('zip')); assert.equal(run(page([entry])).status, 'ambiguous'); noClicks([entry]);
});
test('unrelated table is ignored while recognized package header selects the package', () => {
    const entry = row(); const context = page([entry]); context.tables.unshift({ rows: [{ cells: [cell('unrelated')] }] });
    assert.equal(run(context).status, 'clicked'); assert.equal(entry.zip.clicked, 1);
});
test('metadata read bounds fail safely', () => {
    const entry = row({ released: 'x'.repeat(129) }); assert.equal(run(page([entry])).status, 'ambiguous'); noClicks([entry]);
});
process.stdout.write(JSON.stringify({ passed }));
