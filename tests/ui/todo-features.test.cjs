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
const liveCompact = read('src/IPTVExplorer.Desktop/ui/live-compact.js');
const liveCompactCss = read('src/IPTVExplorer.Desktop/ui/live-compact.css');
const nativeActions = read('src/IPTVExplorer.Desktop/ui/provider-native-actions.js');
const localStats = read('src/IPTVExplorer.Desktop/ui/diagnostic-local-stats.js');
const hierarchy = read('src/IPTVExplorer.Desktop/ui/category-hierarchy.js');
const hierarchyCss = read('src/IPTVExplorer.Desktop/ui/category-hierarchy.css');

test('desktop injects all local TODO enhancers before navigating the WebView', () => {
  for (const script of ['favorites.js', 'live-compact.js', 'provider-native-actions.js', 'diagnostic-local-stats.js', 'category-hierarchy.js']) {
    assert.match(mainWindow, new RegExp(script.replace('.', '\\.')));
  }
  assert.match(mainWindow, /Navigate\("https:\/\/appassets\.local\/index\.html"\)/);
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

test('Live browser owns navigation, waits for explicit group choice, and renders compact channel rows', () => {
  assert.match(liveCompact, /event\.stopImmediatePropagation\(\)/);
  assert.match(liveCompact, /Choisissez un groupe pour afficher les chaînes/);
  assert.match(liveCompact, /select\.addEventListener\('change'/);
  assert.match(liveCompact, /rpc\('catalog\.live'/);
  assert.match(liveCompact, /MAX_RENDERED_CHANNELS = 800/);
  assert.match(liveCompact, /requestAnimationFrame\(appendChunk\)/);
  assert.match(liveCompact, /className = 'live-channel-filter'/);
  assert.match(liveCompact, /play\.click\(\)/);
  assert.match(liveCompact, /event\.key !== 'Enter'/);
  assert.match(liveCompact, /event\.key !== ' '/);
  assert.match(liveCompact, /event\.target\.closest\('button'\)/);
  assert.match(liveCompactCss, /#live\.live-browser-owned \.live-grid\{display:flex;flex-direction:column/);
  assert.match(liveCompactCss, /min-height:50px/);
  assert.match(liveCompactCss, /\.category-list\{display:none!important\}/);
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
  assert.match(hierarchy, /rpc\('categories\.hierarchy'/);
  assert.match(hierarchy, /category\?\.parentId/);
  assert.match(hierarchy, /Sous-catégorie de/);
  assert.match(hierarchy, /trail\.has\(id\)/);
  assert.match(hierarchyCss, /data-category-depth="1"/);
  assert.doesNotMatch(hierarchy, /split\([^]*category\.name/);
  assert.doesNotMatch(hierarchy, /includes\([^]*category\.name/);
});
