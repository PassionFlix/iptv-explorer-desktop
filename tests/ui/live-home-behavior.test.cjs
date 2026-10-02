const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');

const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const html = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');
const favorites = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/favorites.js'), 'utf8');
function source(name, script = app) {
  const match = script.match(new RegExp(`^  (?:async )?function ${name}\\([^]*?^  }`, 'm'));
  assert.ok(match, `Missing ${name}`);
  return match[0];
}
const tick = () => new Promise(setImmediate);
function deferred() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }

class Element {
  constructor(text = '') {
    this.textContent = text; this.children = []; this.style = {}; this.value = ''; this.disabled = false; this.scrollTop = 0;
    this.classes = new Set(['hidden']);
    this.classList = {
      add: (...names) => names.forEach(name => this.classes.add(name)),
      remove: (...names) => names.forEach(name => this.classes.delete(name)),
      toggle: (name, force) => { if (force === undefined ? !this.classes.has(name) : force) this.classes.add(name); else this.classes.delete(name); },
      contains: name => this.classes.has(name)
    };
  }
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = items.flatMap(item => item?.fragment ? item.children : [item]); }
  addEventListener() {}
  setAttribute() {}
  scrollTo() {}
  getBoundingClientRect() { return { top: 105 }; }
}
const node = (tag, className, text = '') => Object.assign(new Element(text), { tag, className, style: {}, dataset: {} });

test('Home RPC response makes continue, two films and two series visible; refresh does not blank them', async () => {
  const elements = new Map();
  const $ = selector => { if (!elements.has(selector)) elements.set(selector, new Element()); return elements.get(selector); };
  const calls = [];
  const data = {
    continueWatching: [{ providerKey: 'fixture', catalog: 'vod', mediaId: 'resume', title: 'À reprendre', percentage: 50, durationSeconds: 100 }],
    recentlyAddedFilms: [{ id: 'f1', title: 'Film un' }, { id: 'f2', title: 'Film deux' }],
    recentlyAddedSeries: [{ id: 's1', title: 'Série une' }, { id: 's2', title: 'Série deux' }]
  };
  const state = { app: { providerCount: 1, providers: [{ key: 'fixture', name: 'Fixture', type: 'xtream', enabled: true }] }, activeProviderKey: 'fixture', page: 'home' };
  const context = {
    $, node, state,
    displayedHomeProvider: null, activeProvider: () => state.app.providers[0],
    rpc: async method => { calls.push(method); return method === 'home.content' ? data : { index: null }; },
    renderIndexStatus() {}, imageOrPlaceholder: () => node('div'), openDetail() {}, isAbort: () => false,
    window: { iptvHome: { setBackgrounds() {}, resetCarousel() {} }, setTimeout() {} }, console
  };
  runInNewContext(['renderHome', 'renderHomeMedia', 'renderContinueWatching', 'renderRecentlyAdded'].map(name => source(name)).join('\n'), context);
  await context.renderHome(); await tick();
  for (const [section, grid, count, title] of [
    ['#continue-watching', '#continue-watching-items', 1, 'À reprendre'],
    ['#recent-films', '#recent-films-items', 2, 'Film un'],
    ['#recent-series', '#recent-series-items', 2, 'Série une']
  ]) {
    assert.equal($(section).classList.contains('hidden'), false);
    assert.equal($(grid).children.length, count);
    assert.match(JSON.stringify($(grid).children), new RegExp(title));
  }
  assert.deepEqual(calls, ['home.content', 'providers.dashboard']);
  const second = deferred(); context.rpc = method => method === 'home.content' ? second.promise : Promise.resolve({ index: null });
  const refresh = context.renderHome();
  assert.equal($('#recent-films-items').children.length, 2);
  second.resolve(data); await refresh; await tick();
  assert.equal($('#recent-series-items').children.length, 2);
});

function liveFixture() {
  const elements = new Map();
  const section = new Element(); const grid = new Element(); const empty = new Element('Sélectionnez une catégorie Live.'); const loading = new Element();
  const select = new Element(); const filter = new Element();
  section.querySelector = selector => ({ '.category-list': null, '.catalog-grid': grid, '.live-grid': grid, '.empty-catalog': empty, '.loading-state': loading, '.category-select': select })[selector] || null;
  const $ = selector => ({ '#live': section, '#live .live-grid': grid, '#live-channel-filter': filter, '#live-channel-count': elements.get('count') || elements.set('count', new Element()).get('count') })[selector] || null;
  const calls = [], pending = new Map();
  const state = { app: {}, page: 'live', activeProviderKey: 'fixture', catalogs: new Map() };
  const model = { categories: [], hierarchyCategories: [], categoryId: null, page: 1, requestVersion: 0, loadingKey: null, loadingPromise: null, liveItems: [], filteredLiveItems: [], liveRenderFrame: 0 };
  state.catalogs.set('live', model);
  const context = {
    $, node, state, console, isAbort: error => error?.name === 'AbortError', renderCatalogSelector() {}, renderCatalogCategories() {},
    rpc(method, params) { calls.push({ method, params }); const promise = deferred(); pending.set(`${method}:${params.categoryId || params.offset}`, promise); return promise.promise; },
    requestAnimationFrame: callback => { setImmediate(callback); return 1; }, cancelAnimationFrame() {},
    document: { createDocumentFragment: () => ({ fragment: true, children: [], append(item) { this.children.push(item); } }) },
    imageOrPlaceholder: () => node('div'), openPlayer() {}, Object, Math
  };
  runInNewContext(['compareLiveItems', 'selectLiveCategory', 'renderLiveItems', 'queueLiveWindow', 'renderLiveWindow', 'createLiveRow'].map(name => source(name)).join('\n'), context);
  return { context, state, model, grid, empty, loading, filter, section, calls, pending };
}
const items = count => Array.from({ length: count }, (_, index) => ({ id: String(index + 1), title: `Chaîne ${index + 1}`, categoryId: 'A' }));
const page = (list, snapshotId = 'snapshot', offset = 0, size = 200) => ({ snapshotId, total: list.length, items: list.slice(offset, offset + size), nextOffset: offset + size < list.length ? offset + size : null });

test('Live without selection has no catalog request, no category rail and shows prompt', async () => {
  assert.doesNotMatch(html.match(/<section id="live"[^]*?<section id="vod"/)[0], /category-list|Filtrer les catégories/);
  const f = liveFixture();
  f.context.rpc = async method => { f.calls.push({ method }); return { categories: [{ id: 'A', selected: true, present: true }] }; };
  f.context.resetLiveChannels = () => {};
  f.context.renderCatalogSelector = () => {};
  f.context.renderCatalogCategories = () => { throw Error('Live rail must not render'); };
  runInNewContext(source('loadCatalogShell'), f.context);
  await f.context.loadCatalogShell('live');
  assert.deepEqual(f.calls.map(call => call.method), ['categories.list']);
  assert.equal(f.empty.textContent, 'Sélectionnez une catégorie Live.');
});

test('one category request shows ten rows and clears loading', async () => {
  const f = liveFixture(); const request = f.context.selectLiveCategory('A');
  assert.deepEqual(f.calls.map(call => call.method), ['catalog.live']);
  f.pending.get('catalog.live:A').resolve(page(items(10)));
  await request; await tick();
  assert.equal(f.grid.children.filter(child => child.className?.includes('live-row')).length, 10);
  assert.equal(f.loading.classList.contains('hidden'), true);
  assert.equal(f.filter.disabled, false);
});

test('1000 channels arrive in local pages but DOM remains bounded', async () => {
  const f = liveFixture(), all = items(1000); const request = f.context.selectLiveCategory('A');
  f.pending.get('catalog.live:A').resolve(page(all)); await tick(); await tick();
  for (let offset = 200; offset < 1000; offset += 200) {
    f.pending.get(`catalog.live.page:${offset}`).resolve(page(all, 'snapshot', offset)); await tick(); await tick();
  }
  await request; await tick();
  assert.equal(f.model.liveItems.length, 1000);
  assert.equal(f.calls.filter(call => call.method === 'catalog.live').length, 1);
  assert.ok(f.grid.children.length <= 82);
  assert.equal(f.loading.classList.contains('hidden'), true);
});

test('rapid A then B ignores A even when its response arrives late', async () => {
  const f = liveFixture(); const a = f.context.selectLiveCategory('A'); const b = f.context.selectLiveCategory('B');
  f.pending.get('catalog.live:B').resolve(page([{ id: 'B1', title: 'B choisi' }])); await b;
  f.pending.get('catalog.live:A').resolve(page([{ id: 'A1', title: 'A tardif' }])); await a; await tick();
  assert.equal(f.model.liveItems[0].title, 'B choisi');
  assert.equal(f.loading.classList.contains('hidden'), true);
});

test('Live RPC error clears loading and category remains interactive', async () => {
  const f = liveFixture(); const first = f.context.selectLiveCategory('A');
  f.pending.get('catalog.live:A').reject(new Error('Catalogue indisponible')); await first;
  assert.equal(f.loading.classList.contains('hidden'), true);
  assert.match(f.empty.textContent, /indisponible/);
  const second = f.context.selectLiveCategory('B');
  f.pending.get('catalog.live:B').resolve(page(items(1))); await second; await tick();
  assert.equal(f.model.liveItems.length, 1);
});

test('Live to Home hides native surface and restores Home', async () => {
  const sent = [], state = { page: 'live' }, page = new Element();
  const context = {
    state, titles: { home: 'Accueil' }, cancelGroup() {}, renderHome() { sent.push('renderHome'); },
    $: selector => selector === '#live' ? new Element() : page,
    $$: () => [new Element()],
    rpc: async (method, params) => { sent.push({ method, params }); },
    isAbort: () => false, window: { iptvHome: { setActive() {} } },
    document: { body: new Element() }, console
  };
  runInNewContext(source('navigate'), context);
  context.navigate('home'); await tick();
  assert.equal(state.page, 'home');
  assert.equal(sent[0].method, 'player.liveSurface');
  assert.equal(sent[0].params.visible, false);
  assert.equal(sent[1], 'renderHome');
});

test('favorites decorator writes star text only when its state changes', () => {
  let writes = 0, value = '';
  const button = { dataset: { favoriteKey: 'fixture' }, classList: { toggle() {} }, setAttribute() {}, title: '' };
  Object.defineProperty(button, 'textContent', { get: () => value, set: next => { value = next; writes++; } });
  const context = { favorites: new Map() };
  runInNewContext(source('updateFavoriteButton', favorites), context);
  context.updateFavoriteButton(button); context.updateFavoriteButton(button);
  assert.equal(writes, 1);
});
