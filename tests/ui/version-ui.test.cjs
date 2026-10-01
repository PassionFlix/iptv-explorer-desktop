const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');

const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const html = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');
const polish = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/v1-polish.js'), 'utf8');

test('brand version is supplied by app.getState', () => {
  assert.match(app, /state\.app\s*=\s*await rpc\('app\.getState'\)/);
  assert.match(app, /brandVersion\.textContent\s*=\s*state\.app\.version\s*\?\s*`Desktop · \$\{state\.app\.version\}`/);
});

test('markup and polish script contain no release literal', () => {
  assert.doesNotMatch(html, /Desktop · 1\.0\.0/);
  assert.doesNotMatch(polish, /Desktop · 1\.0\.0/);
});

test('static markup has a safe pre-state label', () => {
  assert.match(html, /<small>Desktop<\/small>/);
});
