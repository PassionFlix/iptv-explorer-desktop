// Synthetic Live behavior regressions. No WebView, HTTP, or provider calls.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { runInNewContext } = require('node:vm');
const { test } = require('node:test');

const root = resolve(__dirname, '../..');
const app = readFileSync(resolve(root, 'src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const html = readFileSync(resolve(root, 'src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');
const css = readFileSync(resolve(root, 'src/IPTVExplorer.Desktop/ui/app.css'), 'utf8');

function productionFunction(name) {
  const match = app.match(new RegExp(`^  (?:async )?function ${name}\\([^]*?^  }`, 'm'));
  assert.ok(match, `Production function ${name} must exist`);
  return match[0];
}

test('opening Live exposes categories and performs zero catalog.live calls before selection', () => {
  assert.match(html, /Sélectionnez une catégorie Live\./);
  assert.match(app, /if \(selected\.length && catalog !== 'live'\) selectCatalogCategory/);
  assert.equal((app.match(/rpc\('catalog\.live'/g) || []).length, 1);
});

test('one category selection owns exactly one cancellable catalog.live operation', () => {
  assert.match(app, /if \(catalog === 'live'\) return selectLiveCategory\(categoryId\)/);
  assert.match(app, /const operation = rpc\('catalog\.live', \{ providerKey, categoryId \}, 'live-catalog'\)/);
  assert.match(app, /model\.loadingKey === requestKey && model\.loadingPromise/);
});

test('rapid A then B supersedes A and stale results cannot render', () => {
  assert.match(app, /model\.requestVersion \+= 1; const requestVersion = model\.requestVersion/);
  assert.match(app, /state\.page !== 'live'[^]*model\.requestVersion !== requestVersion[^]*model\.categoryId !== categoryId/);
  assert.match(app, /if \(page !== 'live'\) cancelGroup\('live-catalog'\)/);
});

test('Live to Home hides the native pane while Home media sections remain intact', () => {
  assert.match(app, /rpc\('player\.liveSurface', \{ visible: page === 'live', top:/);
  assert.match(app, /renderContinueWatching\(data\?\.continueWatching/);
  assert.match(app, /renderRecentlyAdded\(data\?\.recentlyAddedFilms/);
  assert.match(app, /renderRecentlyAdded\(data\?\.recentlyAddedSeries/);
  assert.doesNotMatch(css, /#live(?:\.live-browser)?\{display:(?:block|grid)/);
  assert.match(css, /#live\.live-browser\.active\{display:block/);
});

test('Live player.open sends only an opaque reference and never media credentials', () => {
  const block = productionFunction('openPlayer');
  assert.match(block, /providerKey: reference\.providerKey/);
  assert.match(block, /mediaType: 'live'/);
  assert.match(block, /mediaId: reference\.mediaId/);
  assert.match(block, /categoryId: reference\.categoryId/);
  assert.match(block, /extension: reference\.extension/);
  assert.doesNotMatch(block, /\b(?:url|uri|token|cookie|authorization|headers?)\b/i);
  assert.doesNotMatch(block, /title:/);
});

test('synthetic 1000-channel list keeps the DOM window bounded', () => {
  const children = [];
  const grid = { scrollTop: 0, replaceChildren(fragment) { children.splice(0, children.length, ...fragment.children); } };
  const model = { filteredLiveItems: Array.from({ length: 1000 }, (_, index) => ({ id: String(index + 1), title: `Channel ${index + 1}` })) };
  const context = {
    $: () => grid,
    state: { catalogs: new Map([['live', model]]) },
    node: () => ({ style: {} }),
    createLiveRow: item => ({ item }),
    document: { createDocumentFragment: () => ({ children: [], append(value) { this.children.push(value); } }) },
    Math
  };
  runInNewContext(productionFunction('renderLiveWindow'), context);
  context.renderLiveWindow(model);
  assert.equal(children.length, 82);
  assert.ok(children.length < model.filteredLiveItems.length);
});

test('numeric Live IDs sort before title fallback', () => {
  const context = {};
  runInNewContext(productionFunction('compareLiveItems'), context);
  const sorted = [{ id: '10', title: 'Zulu' }, { id: '2', title: 'Beta' }, { id: 'x', title: 'Alpha' }].sort(context.compareLiveItems);
  assert.deepEqual(sorted.map(item => item.id), ['2', '10', 'x']);
});
