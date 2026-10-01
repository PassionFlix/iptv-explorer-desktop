// Production UI orchestration with an in-memory bridge. No WebView, images, HTTP or provider.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');
const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
function production(name) {
  const match = app.match(new RegExp(`^  (?:async )?function ${name}\\([^]*?^  }`, 'm'));
  assert.ok(match, name); return match[0];
}
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }
function fixture(functions) {
  const calls = [], rendered = [], elements = new Map();
  const providers = [{ key: 'first', type: 'xtream' }, { key: 'second', type: 'xtream' }, { key: 'stalker', type: 'stalker' }];
  const state = { activeProviderKey: 'first', app: { activeProviderKey: 'first', providers }, page: 'home', catalogs: new Map(), search: {} };
  const context = {
    state, catalogSessions: new Set(), catalogRefreshBusy: false, completedIndex: null,
    activeProvider: () => providers.find(p => p.key === state.activeProviderKey),
    $: selector => { if (!elements.has(selector)) elements.set(selector, { textContent: '', disabled: false }); return elements.get(selector); },
    rpc: async (method, params) => { calls.push({ method, params }); return method === 'app.getState' ? state.app : {}; },
    renderProviderSelector() {}, renderProviderSettings() {}, navigate() {},
    renderHome: async () => rendered.push('home'), renderHomeMedia: () => rendered.push('media'),
    loadCatalogShell: async () => rendered.push('catalog'), renderIndexStatus() {}, runSearch: async () => rendered.push('search'),
    loadCatalogStatus: async () => {}, startCatalogSession() {}, pollIndex: async () => {}, applyCatalogRefresh: async () => {},
    window: { clearTimeout() {}, setTimeout: () => { throw new Error('Unexpected polling'); } }
  };
  runInNewContext(functions.map(production).join('\n'), context);
  return { context, calls, rendered, elements, state };
}

test('startup renders local UI without waiting for network session refresh', async () => {
  const f = fixture(['refreshApp', 'startCatalogSession']); const pending = deferred();
  f.context.rpc = async (method, params) => {
    f.calls.push({ method, params });
    if (method === 'app.getState') return f.state.app;
    assert.deepEqual(f.rendered, ['home']);
    return pending.promise;
  };
  await f.context.refreshApp();
  assert.deepEqual(f.calls.map(call => call.method), ['app.getState', 'catalog.session']);
  pending.resolve({ state: 'updated' });
});

test('returning home starts at most one session refresh per Xtream provider, never for Stalker', async () => {
  const f = fixture(['startCatalogSession']);
  await f.context.startCatalogSession(); await f.context.startCatalogSession();
  f.state.activeProviderKey = 'second'; await f.context.startCatalogSession();
  f.state.activeProviderKey = 'first'; await f.context.startCatalogSession();
  f.state.activeProviderKey = 'stalker'; await f.context.startCatalogSession();
  assert.deepEqual(f.calls.map(call => call.params.providerKey), ['first', 'second']);
});

test('manual refresh is only explicit and ignores a double click while pending', async () => {
  const f = fixture(['refreshCatalogManual']); const pending = deferred();
  f.context.rpc = (method, params) => { f.calls.push({ method, params }); return pending.promise; };
  assert.equal(f.calls.length, 0);
  const first = f.context.refreshCatalogManual(); await f.context.refreshCatalogManual();
  assert.equal(f.calls.length, 1); assert.equal(f.calls[0].method, 'catalog.refresh');
  assert.equal(f.elements.get('#refresh-catalog').disabled, true);
  pending.resolve({ state: 'updated', updated: true }); await first;
  assert.equal(f.context.catalogRefreshBusy, false);
});

test('failed background refresh keeps local UI and schedules no network retry', async () => {
  const f = fixture(['applyCatalogRefresh']);
  await f.context.applyCatalogRefresh('first', { state: 'retained', updated: false });
  assert.equal(f.calls.length, 0); assert.equal(f.rendered.length, 0);
  assert.match(f.elements.get('#catalog-refresh-state').textContent, /Aucun nouvel essai automatique/);
});

test('catalog status is local and never initiates a refresh', async () => {
  const f = fixture(['loadCatalogStatus']);
  await f.context.loadCatalogStatus();
  assert.deepEqual(f.calls.map(call => call.method), ['catalog.status']);
  assert.match(f.elements.get('#catalog-refreshed-at').textContent, /jamais/);
});

test('index completion refreshes home locally and discards stale provider responses', { timeout: 1000 }, async () => {
  const f = fixture(['pollIndex']); const pending = deferred(), entered = deferred();
  f.context.rpc = async (method, params) => {
    f.calls.push({ method, params });
    if (method === 'index.status') return { job: { id: 1, status: 'completed' } };
    entered.resolve(); return pending.promise;
  };
  const task = f.context.pollIndex(); await entered.promise;
  f.state.activeProviderKey = 'second'; pending.resolve({}); await task;
  assert.deepEqual(f.calls.map(call => call.method), ['index.status', 'home.content']);
  assert.equal(f.rendered.length, 0);
});
