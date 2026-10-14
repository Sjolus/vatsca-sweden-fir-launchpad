import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createContext, runInContext } from 'node:vm';

test('late private responses cannot repopulate the backoffice after logout', async () => {
  const nodes = new Map();
  const node = id => {
    if (!nodes.has(id)) nodes.set(id, { value: '', hidden: false, children: [],
      classList: { toggle() {} }, addEventListener() {},
      replaceChildren(...children) { this.children = children; },
      close() { this.open = false; }, showModal() { this.open = true; } });
    return nodes.get(id);
  };
  let resolveReport;
  const pendingReport = new Promise(resolve => { resolveReport = resolve; });
  const context = createContext({
    document: { body: { dataset: {} }, getElementById: node, querySelectorAll: () => [] },
    fetch: path => path === '/auth/session'
      ? Promise.resolve({ ok: true, json: async () => ({ signed_in: false }) }) : pendingReport,
    setTimeout: () => 1, clearTimeout() {}, URLSearchParams,
  });
  runInContext(readFileSync(new URL('../public/app.js', import.meta.url), 'utf8').replace(/boot\(\);\s*$/, ''), context);
  await new Promise(resolve => setImmediate(resolve));
  context.showWorkspace({authenticated:true,username:'maintainer',owner:true,permissions:[]});
  const opening = context.openReport({ reference: 'LP-synthetic' });
  context.showLogin();
  resolveReport({ ok: true, json: async () => ({ reference: 'LP-synthetic', payload: { support_id: 'synthetic-private-value' } }) });
  await opening;
  assert.equal(node('report-dialog').open, false);
  assert.equal(node('workspace').hidden, true);
  assert.equal(node('report-details').children.length, 0);
  assert.equal(runInContext('selectedReport', context), null);
});
