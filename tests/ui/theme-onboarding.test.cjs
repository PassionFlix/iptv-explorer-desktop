// Local DOM/CSS regressions for theme preferences and onboarding copy. No WebView or network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const css = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.css'), 'utf8');
const html = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');

function production(name) {
  const match = app.match(new RegExp(`^  (?:async )?function ${name}\\([^]*?^  }`, 'm'));
  assert.ok(match, `Production function not found: ${name}`);
  return match[0];
}

function themeRoot(theme) {
  const root = {
    dataset: {},
    removeAttribute(name) { if (name === 'data-theme') delete this.dataset.theme; }
  };
  runInNewContext(`${production('applyThemePreference')}\napplyThemePreference(theme);`, {
    document: { documentElement: root }, theme
  });
  return root;
}

function resolvedTheme(theme, prefersLight) {
  const root = themeRoot(theme);
  if (root.dataset.theme === 'dark') return 'dark';
  if (root.dataset.theme === 'light') return 'light';
  assert.match(css, /@media\s*\(prefers-color-scheme:\s*light\)\s*{\s*html:not\(\[data-theme\]\)/);
  return prefersLight ? 'light' : 'dark';
}

test('explicit dark ignores a light system preference', () => {
  assert.equal(resolvedTheme('dark', true), 'dark');
  assert.match(css, /^:root{color-scheme:dark;/);
});

test('explicit light remains light', () => {
  assert.equal(resolvedTheme('light', false), 'light');
  assert.match(css, /html\[data-theme=light\]{color-scheme:light;/);
});

test('system with prefers light resolves to the light variables', () => {
  const root = themeRoot('system');
  assert.equal(root.dataset.theme, undefined);
  assert.equal(resolvedTheme('system', true), 'light');
});

test('system with prefers dark keeps the dark root variables', () => {
  assert.equal(themeRoot('system').dataset.theme, undefined);
  assert.equal(resolvedTheme('system', false), 'dark');
});

test('changing prefers-color-scheme remains immediately effective in system mode', () => {
  assert.equal(resolvedTheme('system', false), 'dark');
  assert.equal(resolvedTheme('system', true), 'light');
  assert.doesNotMatch(production('applyThemePreference'), /matchMedia|addEventListener/);
});

test('the persisted preference value remains system instead of a resolved color', () => {
  assert.match(app, /theme:\s*form\.elements\.theme\.value/);
  assert.match(app, /rpc\('settings\.save',\s*preferences\)/);
  assert.match(app, /applyThemePreference\(preferences\.theme\)/);
});

function classList() {
  const values = new Set();
  return {
    toggle(name, force) { force ? values.add(name) : values.delete(name); },
    contains(name) { return values.has(name); }
  };
}

function categoryFixture(type) {
  const elements = new Map();
  for (const selector of ['#onboarding-category-selection', '#onboarding-category-info'])
    elements.set(selector, { classList: classList(), textContent: '' });
  elements.set('#onboarding-categories', { replaceChildren() {} });
  elements.set('#onboarding-count', { textContent: '' });
  elements.set('#onboarding-filter', { value: '' });
  const rendered = [];
  const context = {
    state: { onboarding: { catalog: 'live', draft: { detectedType: type }, policies: { live: { items: [{ remoteId: '1' }, { remoteId: '2' }], selected: new Set(['1']), mode: 'custom' } } } },
    $: selector => elements.get(selector),
    renderPolicyRows: (...args) => rendered.push(args)
  };
  runInNewContext(production('renderOnboardingCategories'), context);
  context.renderOnboardingCategories();
  return { elements, rendered };
}

test('Xtream onboarding replaces the confusing empty category selection with local-sync information', () => {
  const fixture = categoryFixture('xtream');
  assert.equal(fixture.elements.get('#onboarding-category-selection').classList.contains('hidden'), true);
  assert.equal(fixture.elements.get('#onboarding-category-info').classList.contains('hidden'), false);
  assert.match(fixture.elements.get('#onboarding-category-info').textContent, /synchronisés localement lors de l’activation/);
  assert.equal(fixture.elements.get('#onboarding-count').textContent, '');
  assert.equal(fixture.rendered.length, 0);
});

test('Stalker onboarding preserves its available category selection', () => {
  const fixture = categoryFixture('stalker');
  assert.equal(fixture.elements.get('#onboarding-category-selection').classList.contains('hidden'), false);
  assert.equal(fixture.elements.get('#onboarding-category-info').classList.contains('hidden'), true);
  assert.equal(fixture.elements.get('#onboarding-count').textContent, '1 / 2');
  assert.equal(fixture.rendered.length, 1);
});

test('Xtream onboarding omits misleading zero catalog counters while Stalker keeps real counters', () => {
  function node(tag, className = '', textContent = '') {
    return { tag, className, textContent, children: [], append(...children) { this.children.push(...children); } };
  }
  const context = { node, document: { createTextNode: textContent => ({ textContent }) } };
  const diagnosticSource = app.match(/^  function renderDiagnostic.*$/m);
  assert.ok(diagnosticSource, 'Production function not found: renderDiagnostic');
  runInNewContext(diagnosticSource[0], context);
  const data = { authenticated: true, account: 'Active', live: 3, vod: 4, series: 5, latencyMs: 12 };

  const xtream = JSON.stringify(context.renderDiagnostic({ ...data, type: 'Xtream', live: 0, vod: 0, series: 0 }, true));
  const stalker = JSON.stringify(context.renderDiagnostic({ ...data, type: 'Stalker / MAG' }, true));

  assert.doesNotMatch(xtream, /Live|Films|Séries/);
  assert.match(xtream, /Catalogue local après activation/);
  assert.match(stalker, /Live/);
  assert.match(stalker, /Films/);
  assert.match(stalker, /Séries/);
});

test('onboarding copy accurately distinguishes minimal Xtream validation from Stalker categories', () => {
  const context = {};
  runInNewContext(production('onboardingExplanation'), context);
  assert.match(context.onboardingExplanation('xtream'), /contrôle minimal.*catalogue sera chargé localement/s);
  assert.match(context.onboardingExplanation('stalker'), /charge les catégories disponibles/);
  assert.doesNotMatch(html, /authentification puis charge uniquement les catégories/);
});

test('the Xtream wizard adds no category or catalog provider call', () => {
  const start = app.indexOf("$('#test-provider').addEventListener");
  const end = app.indexOf('function renderDiagnostic', start);
  const handler = app.slice(start, end);
  const calls = [...handler.matchAll(/rpc\('([^']+)'/g)].map(match => match[1]);
  assert.deepEqual(calls, ['providers.addDraft', 'providers.test']);
  assert.doesNotMatch(handler, /categories\.|catalog\.|get_series_info/i);
});
