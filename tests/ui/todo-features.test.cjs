// Static regressions for local-only TODO features. No WebView, HTTP, or provider calls.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');

const root = resolve(__dirname, '../..');
const read = path => readFileSync(resolve(root, path), 'utf8');
const mainWindow = read('src/IPTVExplorer.Desktop/MainWindow.xaml.cs');
const favorites = read('src/IPTVExplorer.Desktop/ui/favorites.js');
const favoritesCss = read('src/IPTVExplorer.Desktop/ui/favorites.css');
const app = read('src/IPTVExplorer.Desktop/ui/app.js');
const appCss = read('src/IPTVExplorer.Desktop/ui/app.css');
const nativeActions = read('src/IPTVExplorer.Desktop/ui/provider-native-actions.js');
const localStats = read('src/IPTVExplorer.Desktop/ui/diagnostic-local-stats.js');

test('desktop injects auxiliary enhancers but keeps Live and category ownership in app.js', () => {
  for (const script of ['favorites.js', 'provider-native-actions.js', 'diagnostic-local-stats.js']) {
    assert.match(mainWindow, new RegExp(script.replace('.', '\\.')));
  }
  assert.doesNotMatch(mainWindow, /live-compact\.js/);
  assert.doesNotMatch(mainWindow, /category-hierarchy\.js/);
  assert.match(app, /function selectLiveCategory/);
  assert.match(app, /function categoryDepths/);
  assert.match(mainWindow, /Navigate\("https:\/\/appassets\.local\/index\.html"\)/);
});

test('all injected helpers delegate to the single app.js RPC transport', () => {
  assert.match(app, /window\.iptvRpc = rpc/);
  for (const source of [favorites, nativeActions, localStats, read('src/IPTVExplorer.Desktop/ui/media-actions.js')]) {
    assert.match(source, /window\.iptvRpc\(method, params\)/);
    assert.doesNotMatch(source, /new Map\(\)[^]*postMessage\(\{ id, method, params \}\)/);
  }
});

test('favorites stay local, provider scoped, and expose Live Films Series tabs', () => {
  assert.match(favorites, /rpc\('favorites\.list'/);
  assert.match(favorites, /rpc\('favorites\.set'/);
  assert.match(favorites, /rpc\('favorites\.remove'/);
  assert.match(favorites, /enregistrés uniquement sur ce PC/);
  assert.match(favorites, /data-favorite-tab="live"/);
  assert.match(favorites, /data-favorite-tab="vod"/);
  assert.match(favorites, /data-favorite-tab="series"/);
  assert.match(favorites, /providerKey: selectedProvider/);
  assert.doesNotMatch(favorites, /\bfetch\s*\(/);
  assert.doesNotMatch(favorites, /XMLHttpRequest/);
  assert.match(favoritesCss, /\.favorite-toggle/);
});

test('compact Live rows are keyboard playable and windowed inside app.js', () => {
  assert.match(app, /event\.key !== 'Enter'/);
  assert.match(app, /event\.key !== ' '/);
  assert.match(app, /event\.target\.closest\('button'\)/);
  assert.match(app, /windowSize = 80/);
  assert.match(appCss, /#live \.live-card\{[^}]*height:52px/);
});

test('full Stalker MAC action stays native and never reads the secret in JavaScript', () => {
  assert.match(nativeActions, /providers\.showFullMac/);
  assert.match(nativeActions, /MAC complète/);
  assert.match(nativeActions, /fenêtre Windows native/);
  assert.doesNotMatch(nativeActions, /macAddress/);
  assert.doesNotMatch(nativeActions, /password/);
  assert.doesNotMatch(nativeActions, /token/);
});

test('diagnostic content counts use local stats only and explain Stalker limits', () => {
  assert.match(localStats, /rpc\('catalog\.stats'/);
  assert.match(localStats, /Contenus disponibles localement/);
  assert.match(localStats, /Chaînes Live en cache/);
  assert.match(localStats, /ne sont pas balayés catégorie par catégorie/);
  assert.doesNotMatch(localStats, /catalog\.live/);
  assert.doesNotMatch(localStats, /catalog\.vod/);
  assert.doesNotMatch(localStats, /catalog\.series/);
});

test('category hierarchy uses persisted parent ids without title heuristics', () => {
  assert.match(app, /category\?\.parentId/);
  assert.match(app, /trail\.has\(id\)/);
  assert.match(app, /hierarchyCategories: data\.categories/);
  assert.match(app, /option\.dataset\.depth = String\(depth\)/);
  assert.doesNotMatch(app, /split\([^]*category\.name/);
});
