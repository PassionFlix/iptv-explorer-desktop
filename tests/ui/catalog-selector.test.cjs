// Local source/DOM regressions for the Live category selector and tools. No WebView or network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');

const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const html = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');
const css = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.css'), 'utf8');

test('Live selector is the only category navigator and has no all-channels option', () => {
  const live = html.match(/<section id="live"[^]*?<\/section>/)?.[0] || '';
  assert.ok(live.indexOf('class="category-select"') >= 0);
  assert.doesNotMatch(live, /class="category-filter"|class="category-list"/);
  assert.doesNotMatch(live, /Toutes les chaînes/i);
  assert.match(css, /\.category-select-label/);
});

test('Live selector and VOD/Series rails use cancellable category loads', () => {
  assert.match(app, /category-select'\)\.forEach\(select => select\.addEventListener\('change'[^]*selectCatalogCategory/);
  assert.match(app, /category-button[^]*selectCatalogCategory\(catalog, category\.id\)/);
  assert.match(app, /rpc\('catalog\.live',[^]*'live-catalog'\)/);
  assert.match(app, /model\.categoryId = categoryId;[^]*renderCatalogSelector\('live'\)/);
});

test('provider changes clear catalog models before the next selector load', () => {
  assert.match(app, /app\.setActiveProvider[^]*state\.catalogs\.clear\(\)[^]*refreshApp\(true\)/);
  assert.match(app, /state\.catalogs\.set\(catalog,[^]*renderCatalogSelector\(catalog\)/);
});

test('Live playback forwards the safe category id for targeted Stalker command recovery', () => {
  assert.match(app, /categoryId: item\.categoryId/);
});

test('manual Stalker refresh remains in Settings without a second Live control', () => {
  assert.doesNotMatch(app, /ensureLiveRefreshControls/);
  assert.match(app, /stalker \? 'Actualiser le Live'/);
  assert.match(app, /rpc\('catalog\.refresh', \{ providerKey: provider\.key \}\)/);
});

test('Live entries display provider order with id fallback and append bounded bridge pages', () => {
  assert.match(app, /model\.liveItems\.push\(\.\.\.result\.items\)/);
  assert.match(app, /rpc\('catalog\.live\.page'/);
  assert.match(app, /item\.providerOrder \?\? item\.id/);
});

test('provider diagnostic counts are explicitly labelled as category counts', () => {
  assert.match(app, /'Catégories Live'/);
  assert.match(app, /'Catégories Films'/);
  assert.match(app, /'Catégories Séries'/);
});
