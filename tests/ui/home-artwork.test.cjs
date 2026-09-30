// Local DOM regression: production functions, minimal DOM double, no browser or network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
function productionFunction(name) {
  const match = app.match(new RegExp(`^  function ${name}\\([^]*?^  }`, 'm'));
  assert.ok(match, `Production function ${name} must exist`);
  return match[0];
}

class Element {
  constructor(tag, className = '', text = '') {
    this.tag = tag;
    this.className = className;
    this.textContent = text;
    this.children = [];
    this.listeners = new Map();
    this.isConnected = true;
    this.classList = { toggle() {} };
  }
  append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); this.isConnected = false; }
  addEventListener(type, callback) { this.listeners.set(type, callback); }
  dispatch(type) { this.listeners.get(type)?.({ target: this }); }
}

function fixture() {
  const grid = new Element('div');
  const section = new Element('section');
  const providerCalls = [];
  const detailOpens = [];
  let resets = 0;
  const context = {
    document: { createElement: tag => new Element(tag) },
    node: (tag, cls, text) => new Element(tag, cls, text),
    $: selector => selector.endsWith('-items') ? grid : section,
    state: { page: 'home', activeProviderKey: 'fixture-provider' },
    rpc: (...args) => { providerCalls.push(args); return Promise.resolve({}); },
    openDetail: (...args) => detailOpens.push(args),
    window: { iptvHome: { resetCarousel() { resets++; } } }
  };
  runInNewContext(`${productionFunction('imageOrPlaceholder')}\n${productionFunction('renderRecentlyAdded')}`, context);
  return { grid, providerCalls, detailOpens, context, resets: () => resets };
}

test('poster DOM error keeps placeholder and never calls the provider or resets the carousel', () => {
  const f = fixture();
  f.context.renderRecentlyAdded([{ id: '1', title: 'Series fixture', imageUrl: 'https://images.example.invalid/missing.jpg' }], 'series', 'recent-series');
  const card = f.grid.children[0];
  const holder = card.children[0];
  holder.children[0].dispatch('error');
  assert.equal(holder.children.length, 0);
  assert.equal(holder.textContent, 'S');
  assert.equal(f.grid.children[0], card);
  assert.equal(holder.className, 'home-media-poster');
  assert.equal(f.resets(), 1);
  assert.equal(f.providerCalls.length, 0);
  assert.equal(f.detailOpens.length, 0);
});

test('missing poster and empty cache render a placeholder without requests', () => {
  const f = fixture();
  f.context.renderRecentlyAdded([{ id: '1', title: 'Series fixture', imageUrl: null }], 'series', 'recent-series');
  const holder = f.grid.children[0].children[0];
  assert.equal(holder.children.length, 0);
  assert.equal(holder.textContent, 'S');
  assert.equal(f.providerCalls.length, 0);
  assert.equal(f.detailOpens.length, 0);
});

test('cached poster renders locally; detail opens only on an explicit user click', () => {
  const f = fixture();
  const imageUrl = 'https://images.example.invalid/cached.jpg';
  f.context.renderRecentlyAdded([{ id: '1', title: 'Series fixture', imageUrl }], 'series', 'recent-series');
  const card = f.grid.children[0];
  assert.equal(card.children[0].children[0].src, imageUrl);
  assert.equal(f.providerCalls.length, 0);
  assert.equal(f.detailOpens.length, 0);
  card.dispatch('click');
  assert.equal(f.detailOpens.length, 1);
  assert.equal(f.detailOpens[0][2].providerKey, 'fixture-provider');
});
