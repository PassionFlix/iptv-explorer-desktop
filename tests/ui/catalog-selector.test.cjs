// Local source/DOM regressions for the Live category selector. No WebView or network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');

const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const html = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');
const css = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.css'), 'utf8');

test('Live selector is above the existing category filter and has no all-channels option', () => {
  const live = html.match(/<section id="live"[^]*?<\/section>/)?.[0] || '';
  assert.ok(live.indexOf('class="category-select"') >= 0);
  assert.ok(live.indexOf('class="category-select"') < live.indexOf('class="category-filter"'));
  assert.doesNotMatch(live, /Toutes les chaînes/i);
  assert.match(css, /\.category-select-label/);
});

test('selector and rail both route through the same cancellable category load', () => {
  assert.match(app, /category-select'\)\.forEach\(select => select\.addEventListener\('change'[^]*selectCatalogCategory/);
  assert.match(app, /category-button[^]*selectCatalogCategory\(catalog, category\.id\)/);
  assert.match(app, /rpc\('catalog\.live',[^]*'view'\)/);
  assert.match(app, /model\.categoryId = categoryId;[^]*renderCatalogSelector\(catalog\)/);
});

test('provider changes clear catalog models before the next selector load', () => {
  assert.match(app, /app\.setActiveProvider[^]*state\.catalogs\.clear\(\)[^]*refreshApp\(true\)/);
  assert.match(app, /state\.catalogs\.set\(catalog,[^]*renderCatalogSelector\(catalog\)/);
});
