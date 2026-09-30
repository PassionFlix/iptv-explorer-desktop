// Local DOM regression for the production backdrop state machine. No browser or network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const script = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/home-media.js'), 'utf8');
const css = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.css'), 'utf8');
const html = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/index.html'), 'utf8');

class ClassList {
  constructor(value = '') { this.values = new Set(value.split(/\s+/).filter(Boolean)); }
  add(...values) { values.forEach(value => this.values.add(value)); }
  remove(...values) { values.forEach(value => this.values.delete(value)); }
  toggle(value, force) {
    const active = force === undefined ? !this.values.has(value) : force;
    active ? this.values.add(value) : this.values.delete(value);
    return active;
  }
  contains(value) { return this.values.has(value); }
}

class Element {
  constructor(className = '') {
    this.classList = new ClassList(className);
    this.attributes = new Map();
    this.children = new Map();
    this.src = '';
  }
  querySelector(selector) { return this.children.get(selector); }
  querySelectorAll(selector) { return selector === '.home-backdrop-layer' ? this.layers : []; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  removeAttribute(name) { this.attributes.delete(name); if (name === 'src') this.src = ''; }
}

function layer() {
  const value = new Element('home-backdrop-layer');
  value.children.set('.home-backdrop-fill', new Element('home-backdrop-fill'));
  value.children.set('.home-backdrop-subject', new Element('home-backdrop-subject'));
  return value;
}

function fixture({ reduced = false } = {}) {
  const layers = [layer(), layer()];
  const backdrop = new Element('home-backdrop hidden');
  backdrop.layers = layers;
  const documentListeners = new Map();
  const mediaListeners = new Map();
  const timers = new Map();
  const preloads = [];
  let timerId = 0;
  let providerCalls = 0;
  const media = {
    matches: reduced,
    addEventListener(type, callback) { mediaListeners.set(type, callback); }
  };
  const document = {
    hidden: false,
    querySelector: selector => selector === '#home-backdrop' ? backdrop : null,
    querySelectorAll: () => [],
    addEventListener(type, callback) { documentListeners.set(type, callback); }
  };
  class PreloadImage {
    set src(value) {
      this.value = value;
      preloads.push(value);
      queueMicrotask(() => value.includes('/fail.') ? this.onerror?.() : this.onload?.());
    }
    removeAttribute(name) { if (name === 'src') this.value = ''; }
  }
  const window = {
    matchMedia: () => media,
    setTimeout(callback, delay) { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    addEventListener() {},
    rpc() { providerCalls++; }
  };
  runInNewContext(script, { window, document, Image: PreloadImage, ResizeObserver: class {}, URL, Set, JSON, Promise, queueMicrotask });
  return {
    backdrop, layers, media, timers, preloads, window,
    providerCalls: () => providerCalls,
    async setBackgrounds(value) { window.iptvHome.setBackgrounds(value); await new Promise(setImmediate); },
    fireVisibility(hidden) { document.hidden = hidden; documentListeners.get('visibilitychange')(); },
    fireReduced(value) { media.matches = value; mediaListeners.get('change')(); }
  };
}

const backdropCandidate = { url: 'https://images.example.invalid/wide.jpg', kind: 'backdrop' };
const posterCandidate = { url: 'https://images.example.invalid/tall.jpg', kind: 'poster' };

test('typed backdrop uses cover mode and no subject copy', async () => {
  const f = fixture(); await f.setBackgrounds([[backdropCandidate]]);
  assert.equal(f.layers[0].getAttribute('data-kind'), 'backdrop');
  assert.ok(f.layers[0].classList.contains('is-backdrop'));
  assert.equal(f.layers[0].querySelector('.home-backdrop-fill').src, backdropCandidate.url);
  assert.equal(f.layers[0].querySelector('.home-backdrop-subject').src, '');
});

test('poster-only data renders fill and contained subject from the same preloaded URL', async () => {
  const f = fixture(); await f.setBackgrounds([[posterCandidate]]);
  assert.ok(f.layers[0].classList.contains('is-poster'));
  assert.equal(f.layers[0].querySelector('.home-backdrop-fill').src, posterCandidate.url);
  assert.equal(f.layers[0].querySelector('.home-backdrop-subject').src, posterCandidate.url);
  assert.deepEqual(f.preloads, [posterCandidate.url]);
});

test('safe backdrop keeps priority over its poster fallback', async () => {
  const f = fixture(); await f.setBackgrounds([[backdropCandidate, posterCandidate]]);
  assert.equal(f.layers[0].getAttribute('data-kind'), 'backdrop');
  assert.deepEqual(f.preloads, [backdropCandidate.url]);
});

test('failed backdrop falls back to the poster of the same media', async () => {
  const f = fixture();
  const failed = { url: 'https://images.example.invalid/fail.jpg', kind: 'backdrop' };
  await f.setBackgrounds([[failed, posterCandidate]]);
  assert.deepEqual(f.preloads, [failed.url, posterCandidate.url]);
  assert.equal(f.layers[0].getAttribute('data-kind'), 'poster');
});

test('failed poster advances to the next media', async () => {
  const f = fixture();
  const failed = { url: 'https://images.example.invalid/fail.jpg', kind: 'poster' };
  await f.setBackgrounds([[failed], [backdropCandidate]]);
  assert.deepEqual(f.preloads, [failed.url, backdropCandidate.url]);
  assert.equal(f.layers[0].getAttribute('data-kind'), 'backdrop');
});

test('legacy string arrays are ignored instead of guessed', async () => {
  const f = fixture(); await f.setBackgrounds([['https://images.example.invalid/legacy.jpg']]);
  assert.deepEqual(f.preloads, []);
  assert.ok(f.backdrop.classList.contains('hidden'));
});

test('unknown kinds and dangerous URLs are rejected locally', async () => {
  const f = fixture();
  await f.setBackgrounds([[
    { url: 'https://images.example.invalid/a.jpg', kind: 'square' },
    { url: 'https://user:pass@images.example.invalid/a.jpg', kind: 'poster' },
    { url: 'javascript:alert(1)', kind: 'backdrop' }
  ]]);
  assert.deepEqual(f.preloads, []);
  assert.ok(f.backdrop.classList.contains('hidden'));
});

test('artwork handling never invokes a bridge or provider callback', async () => {
  const f = fixture(); await f.setBackgrounds([[posterCandidate]]);
  assert.equal(f.providerCalls(), 0);
});

test('rotation retains a single 15 second timer when two media remain usable', async () => {
  const f = fixture();
  await f.setBackgrounds([[backdropCandidate], [{ url: 'https://images.example.invalid/second.jpg', kind: 'backdrop' }]]);
  assert.equal([...f.timers.values()].filter(timer => timer.delay === 15000).length, 1);
});

test('reduced motion prevents rotation and removes the opacity transition', async () => {
  const f = fixture({ reduced: true });
  await f.setBackgrounds([[backdropCandidate], [{ url: 'https://images.example.invalid/second.jpg', kind: 'poster' }]]);
  assert.equal([...f.timers.values()].filter(timer => timer.delay === 15000).length, 0);
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)[\s\S]*?\.home-backdrop-layer \{ transition: none; \}/);
});

test('hidden documents cancel and hide the decorative backdrop', async () => {
  const f = fixture(); await f.setBackgrounds([[backdropCandidate]]);
  assert.ok(!f.backdrop.classList.contains('hidden'));
  f.fireVisibility(true);
  assert.ok(f.backdrop.classList.contains('hidden'));
});

test('markup keeps exactly two decorative crossfade containers', () => {
  assert.equal((html.match(/class="home-backdrop-layer"/g) || []).length, 2);
  assert.equal((html.match(/class="home-backdrop-fill"/g) || []).length, 2);
  assert.equal((html.match(/class="home-backdrop-subject"/g) || []).length, 2);
  assert.match(html, /id="home-backdrop"[^>]*aria-hidden="true"/);
  assert.doesNotMatch(html, /home-backdrop-(?:fill|subject)"(?![^>]*alt="")/);
});

test('backdrop CSS preserves the validated visual values and overlay', () => {
  assert.match(css, /\.home-backdrop-fill\s*\{[\s\S]*?object-fit: cover;[\s\S]*?blur\(7px\) brightness\(\.56\) saturate\(\.90\);[\s\S]*?scale\(1\.03\)/);
  assert.match(css, /\.home-backdrop-layer\.is-active\s*\{\s*opacity: \.74;/);
  assert.match(css, /transition: opacity 1\.8s ease;/);
  assert.match(css, /linear-gradient\(110deg, rgb\(5 10 19 \/ 58%\), rgb\(8 14 25 \/ 26%\) 60%, rgb\(5 10 19 \/ 42%\)\)/);
});

test('poster CSS uses a dark blurred fill, contained subject, soft mask and responsive treatment', () => {
  assert.match(css, /\.is-poster \.home-backdrop-fill\s*\{[\s\S]*?blur\(28px\) brightness\(\.34\) saturate\(\.82\);[\s\S]*?scale\(1\.12\)/);
  assert.match(css, /\.is-poster \.home-backdrop-subject\s*\{[\s\S]*?object-fit: contain;[\s\S]*?object-position: center right;[\s\S]*?mask-image:/);
  assert.match(css, /@media \(max-width: 900px\)[\s\S]*?\.home-backdrop-layer\.is-poster \.home-backdrop-subject/);
  assert.match(css, /@media \(max-width: 700px\)[\s\S]*?display: none;/);
});
