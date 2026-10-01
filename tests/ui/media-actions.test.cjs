// Production media-actions.js exercised against a local DOM/RPC double. No network.
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const script = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/media-actions.js'), 'utf8');
const app = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.js'), 'utf8');
const css = readFileSync(resolve(__dirname, '../../src/IPTVExplorer.Desktop/ui/app.css'), 'utf8');

class ClassList {
  constructor(owner, value = '') { this.owner = owner; this.values = new Set(value.split(/\s+/).filter(Boolean)); }
  sync() { this.owner._className = [...this.values].join(' '); }
  add(...values) { values.forEach(value => this.values.add(value)); this.sync(); }
  remove(...values) { values.forEach(value => this.values.delete(value)); this.sync(); }
  contains(value) { return this.values.has(value); }
}

class Element {
  constructor(tag, className = '', text = '') {
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.parent = null;
    this.listeners = new Map();
    this.attributes = new Map();
    this.style = {};
    this.disabled = false;
    this.type = '';
    this.title = '';
    this.value = '';
    this._textContent = text;
    this.classList = new ClassList(this, className);
    this.classList.sync();
  }
  get className() { return this._className; }
  set className(value) { this.classList = new ClassList(this, value); this.classList.sync(); }
  get textContent() { return this._textContent; }
  set textContent(value) { this._textContent = String(value); }
  append(...children) { children.forEach(child => { child.parent = this; this.children.push(child); }); }
  replaceWith(value) {
    const index = this.parent.children.indexOf(this);
    this.parent.children.splice(index, 1, value);
    value.parent = this.parent;
    this.parent = null;
  }
  remove() {
    if (!this.parent) return;
    this.parent.children = this.parent.children.filter(child => child !== this);
    this.parent = null;
  }
  addEventListener(type, callback) {
    const callbacks = this.listeners.get(type) || [];
    callbacks.push(callback);
    this.listeners.set(type, callbacks);
  }
  dispatch(type) {
    const event = { target: this, stopPropagation() {} };
    return (this.listeners.get(type) || []).map(callback => callback(event));
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  removeAttribute(name) { this.attributes.delete(name); }
  matches(selector) {
    if (selector.startsWith('#')) return this.id === selector.slice(1);
    const [tag, ...classes] = selector.split('.');
    return (!tag || this.tagName === tag.toUpperCase()) && classes.every(value => this.classList.contains(value));
  }
  querySelectorAll(selector) {
    const matches = [];
    const visit = node => {
      node.children.forEach(child => {
        if (child.matches(selector)) matches.push(child);
        visit(child);
      });
    };
    visit(this);
    return matches;
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
}

const tick = () => new Promise(setImmediate);

function episodeRow(title = 'E03 — Épisode court') {
  const row = new Element('div', 'episode');
  const main = new Element('div', 'episode-main');
  const info = new Element('span', 'episode-info', title);
  const actions = new Element('div', 'episode-actions');
  actions.append(new Element('button', 'play-small', 'Lire'));
  main.append(info, actions);
  row.append(main);
  return row;
}

function fixture(titles = ['E03 — Épisode court']) {
  const content = new Element('div'); content.id = 'detail-content';
  const rows = titles.map(episodeRow); rows.forEach(row => content.append(row));
  const dialog = new Element('dialog'); dialog.id = 'detail-dialog';
  const toast = new Element('div'); toast.id = 'toast';
  const provider = new Element('select'); provider.id = 'provider-select'; provider.value = 'fixture-provider';
  const detailTitle = new Element('h2', '', 'Série fixture'); detailTitle.id = 'detail-title';
  const ids = new Map([[content.id, content], [dialog.id, dialog], [toast.id, toast], [provider.id, provider], [detailTitle.id, detailTitle]]);
  const outbound = [];
  let messageHandler;
  const document = {
    readyState: 'complete',
    createElement: tag => new Element(tag),
    querySelector(selector) { return selector.startsWith('#') ? ids.get(selector.slice(1)) || null : null; },
    querySelectorAll(selector) { return selector === '#detail-content .episode' ? rows : []; },
    addEventListener() {}
  };
  const window = {
    chrome: { webview: {
      addEventListener(type, callback) { if (type === 'message') messageHandler = callback; },
      postMessage(message) { outbound.push(message); }
    } },
    setTimeout() { return 1; },
    clearTimeout() {}
  };
  class MutationObserver { observe() {} }
  runInNewContext(script, { window, document, MutationObserver, Map, Promise, Error, Number, String, Math, Date, queueMicrotask });
  const respond = (request, result, ok = true, error = null) => messageHandler({ data: { id: request.id, ok, result, error } });
  return {
    content, rows, outbound, respond, messageHandler,
    async decorateSeries() {
      messageHandler({ data: { ok: true, result: {
        id: 'series-1', title: 'Série fixture',
        seasons: [{ number: 1, episodes: titles.map((title, index) => ({ id: `episode-${index + 1}`, season: 1, episode: index + 1, title, extension: 'mkv' })) }]
      } } });
      await tick();
    },
    async begin(row, result = { started: true, downloadId: 'download-1', fileName: 'episode.mkv' }) {
      row.querySelector('.desktop-episode-download').dispatch('click');
      await tick();
      const start = outbound.shift();
      respond(start, result);
      await tick();
      return { start, status: outbound.shift(), panel: row.querySelector('.desktop-download-progress') };
    },
    async render(statusRequest, status) { respond(statusRequest, status); await tick(); },
    installVod() {
      content.children = [];
      const layout = new Element('div', 'detail-layout');
      const poster = new Element('img');
      const copy = new Element('div');
      copy.append(new Element('button', 'primary', 'Lire'));
      layout.append(poster, copy); content.append(layout);
      messageHandler({ data: { ok: true, result: { id: 'vod-1', title: 'Film fixture', duration: 120, extension: 'mkv' } } });
      return { layout, copy };
    }
  };
}

test('resting episode keeps actions in one dedicated group', async () => {
  const f = fixture(); await f.decorateSeries();
  assert.equal(f.rows[0].children.length, 1);
  assert.equal(f.rows[0].children[0].className, 'episode-main');
  assert.equal(f.rows[0].querySelector('.episode-actions').children.length, 3);
});

test('episode action order is Lire, Télécharger, Copier le lien', async () => {
  const f = fixture(); await f.decorateSeries();
  assert.deepEqual(f.rows[0].querySelector('.episode-actions').children.map(button => button.textContent), ['Lire', 'Télécharger', 'Copier le lien']);
});

test('download start creates the progress panel in the selected episode only', async () => {
  const f = fixture(['Premier', 'Second']); await f.decorateSeries();
  await f.begin(f.rows[1]);
  assert.equal(f.rows[0].querySelector('.desktop-download-progress'), null);
  assert.ok(f.rows[1].querySelector('.desktop-download-progress'));
});

test('progress panel is a full-width second row with explicit header, track and footer', async () => {
  const f = fixture(); await f.decorateSeries(); const { panel } = await f.begin(f.rows[0]);
  assert.equal(f.rows[0].children[0].className, 'episode-main');
  assert.equal(f.rows[0].children[1], panel);
  assert.deepEqual(panel.children.slice(0, 3).map(child => child.className), ['desktop-download-header', 'desktop-download-track indeterminate', 'desktop-download-footer']);
  assert.match(css, /\.episode\.has-download > \.desktop-download-progress\s*\{[\s\S]*?width: 100%;/);
});

test('long file names retain a tooltip and CSS ellipsis without changing panel width', async () => {
  const fileName = 'Une série avec un nom extrêmement long - S01E03 - un épisode au titre encore plus long que la largeur disponible.mkv';
  const f = fixture(); await f.decorateSeries(); const { panel } = await f.begin(f.rows[0], { started: true, downloadId: 'long', fileName });
  const title = panel.querySelector('.desktop-download-header').children[0];
  assert.equal(title.title, fileName);
  assert.match(css, /\.desktop-download-header strong\s*\{[\s\S]*?text-overflow: ellipsis;[\s\S]*?white-space: nowrap;/);
});

test('known total sets a determinate percentage and aria-valuenow', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  await f.render(started.status, { status: 'running', bytesReceived: 43, totalBytes: 100, bytesPerSecond: 10 });
  const track = started.panel.querySelector('.desktop-download-track');
  assert.equal(track.getAttribute('aria-valuenow'), '43');
  assert.equal(track.children[0].style.width, '43%');
});

test('unknown total remains indeterminate without aria-valuenow', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  await f.render(started.status, { status: 'running', bytesReceived: 43, totalBytes: 0, bytesPerSecond: 10 });
  const track = started.panel.querySelector('.desktop-download-track');
  assert.ok(track.classList.contains('indeterminate'));
  assert.equal(track.getAttribute('aria-valuenow'), null);
  assert.equal(track.getAttribute('aria-valuetext'), 'En cours');
});

test('cancel sends media.download.cancel exactly once', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  const cancel = started.panel.querySelector('.desktop-download-cancel'); cancel.dispatch('click'); await tick();
  assert.equal(f.outbound.filter(request => request.method === 'media.download.cancel').length, 1);
});

test('cancel double click is ignored after immediate disable', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  const cancel = started.panel.querySelector('.desktop-download-cancel'); cancel.dispatch('click'); cancel.dispatch('click'); await tick();
  assert.equal(cancel.textContent, 'Annulation…');
  assert.equal(f.outbound.filter(request => request.method === 'media.download.cancel').length, 1);
});

test('completed status remains visible with a clean terminal state', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  await f.render(started.status, { status: 'completed', bytesReceived: 100, totalBytes: 100, fileName: 'episode.mkv' });
  assert.equal(started.panel.querySelector('.desktop-download-header').children[1].textContent, '100 %');
  assert.equal(started.panel.querySelector('.desktop-download-cancel').textContent, 'Terminé');
  assert.ok(started.panel.parent);
});

test('cancelled status remains visible and labelled Annulé', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  await f.render(started.status, { status: 'cancelled', bytesReceived: 10, totalBytes: 100 });
  assert.equal(started.panel.querySelector('.desktop-download-meta').textContent, 'Téléchargement annulé.');
  assert.equal(started.panel.querySelector('.desktop-download-cancel').textContent, 'Annulé');
});

test('failed status retains the supplied error and disables the action cleanly', async () => {
  const f = fixture(); await f.decorateSeries(); const started = await f.begin(f.rows[0]);
  await f.render(started.status, { status: 'failed', bytesReceived: 10, totalBytes: 100, error: 'Échec fixture.' });
  assert.equal(started.panel.querySelector('.desktop-download-meta').textContent, 'Échec fixture.');
  assert.equal(started.panel.querySelector('.desktop-download-cancel').textContent, 'Échec');
});

test('two simultaneous episode downloads own two separate panels', async () => {
  const f = fixture(['Premier', 'Second']); await f.decorateSeries();
  const first = await f.begin(f.rows[0], { started: true, downloadId: 'a', fileName: 'a.mkv' });
  const second = await f.begin(f.rows[1], { started: true, downloadId: 'b', fileName: 'b.mkv' });
  assert.notEqual(first.panel, second.panel);
  assert.equal(first.panel.parent, f.rows[0]);
  assert.equal(second.panel.parent, f.rows[1]);
});

test('progress for episode A never mutates episode B', async () => {
  const f = fixture(['Premier', 'Second']); await f.decorateSeries();
  const first = await f.begin(f.rows[0], { started: true, downloadId: 'a', fileName: 'a.mkv' });
  const second = await f.begin(f.rows[1], { started: true, downloadId: 'b', fileName: 'b.mkv' });
  await f.render(first.status, { status: 'running', bytesReceived: 43, totalBytes: 100 });
  assert.equal(first.panel.querySelector('.desktop-download-header').children[1].textContent, '43 %');
  assert.equal(second.panel.querySelector('.desktop-download-header').children[1].textContent, 'Préparation…');
});

test('responsive classes and source episode structure are explicit', () => {
  assert.match(app, /const main = node\('div', 'episode-main'\)/);
  assert.match(app, /node\('span', 'episode-info'/);
  assert.match(css, /@media \(max-width: 650px\)[\s\S]*?#detail-content \.episode-main\s*\{\s*grid-template-columns: 1fr;/);
  assert.match(css, /#detail-content \.episode-info\s*\{[\s\S]*?text-overflow: ellipsis;/);
});

test('VOD download still creates the generic progress panel in the film detail copy', async () => {
  const f = fixture([]); const { copy } = f.installVod(); await tick();
  const download = copy.querySelector('.desktop-media-actions').children[1];
  download.dispatch('click'); await tick();
  const start = f.outbound.shift(); f.respond(start, { started: true, downloadId: 'vod-download', fileName: 'film.mkv' }); await tick();
  assert.ok(copy.querySelector('.desktop-download-progress'));
  assert.equal(f.outbound.shift().method, 'media.download.status');
});
