(() => {
  'use strict';

  let sequence = 0;
  let providers = [];
  let activeDiagnostic = null;
  const pending = new Map();
  const stats = new Map();

  function rpc(method, params = {}) {
    const id = `catalog-stats-${Date.now()}-${++sequence}`;
    const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    window.chrome.webview.postMessage({ id, method, params });
    return promise;
  }

  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const request = pending.get(response?.id);
    if (!request) return;
    pending.delete(response.id);
    response.ok ? request.resolve(response.result) : request.reject(new Error(response.error || 'Statistiques indisponibles'));
  });

  function formatCount(value) {
    return Number.isFinite(Number(value)) ? Number(value).toLocaleString('fr-CA') : 'Non compté';
  }

  function cell(value, label) {
    const span = document.createElement('span');
    const strong = document.createElement('strong'); strong.textContent = value;
    span.append(strong, document.createTextNode(` ${label}`));
    return span;
  }

  function appendStats() {
    const provider = activeDiagnostic;
    const data = provider ? stats.get(provider.key) : null;
    const panel = document.querySelector('#diagnostic-panel');
    if (!provider || !data || !panel || panel.querySelector('.local-catalog-stats')) return;
    const heading = panel.querySelector('h3')?.textContent || '';
    if (!heading.includes(provider.name)) return;

    const section = document.createElement('section');
    section.className = 'local-catalog-stats';
    const title = document.createElement('h4'); title.textContent = 'Contenus disponibles localement';
    const grid = document.createElement('div'); grid.className = 'diagnostic-grid';
    grid.append(
      cell(formatCount(data.live), data.providerType === 'stalker' ? 'Chaînes Live en cache' : 'Chaînes Live'),
      cell(formatCount(data.vod), data.completeSnapshot ? 'Films' : 'Films globaux'),
      cell(formatCount(data.series), data.completeSnapshot ? 'Séries' : 'Séries globales'));

    if (data.indexedVod !== null || data.indexedSeries !== null) {
      grid.append(
        cell(formatCount(data.indexedVod), 'Films indexés localement'),
        cell(formatCount(data.indexedSeries), 'Séries indexées localement'));
    }

    const note = document.createElement('p'); note.className = 'hint';
    note.textContent = data.completeSnapshot
      ? 'Comptage issu du snapshot local complet. Aucun appel fournisseur supplémentaire.'
      : 'Stalker/MAG : le total Live vient du cache local. Les totaux globaux Films/Séries ne sont pas balayés catégorie par catégorie ; les nombres indexés, s’ils existent, reflètent uniquement l’index local.';
    section.append(title, grid, note);
    panel.append(section);
  }

  async function beginDiagnostic(provider) {
    activeDiagnostic = provider;
    try {
      const data = await rpc('catalog.stats', { providerKey: provider.key });
      stats.set(provider.key, data);
      appendStats();
    } catch {
      // Provider diagnostic itself remains available even if local counters cannot be read.
    }
  }

  function decorateCards() {
    const cards = [...document.querySelectorAll('#settings-provider-list .provider-card')];
    cards.forEach((card, index) => {
      if (card.dataset.catalogStatsWired === '1') return;
      const provider = providers[index];
      if (!provider) return;
      const diagnostic = [...card.querySelectorAll('button')].find(button => button.textContent.trim() === 'Diagnostic');
      if (!diagnostic) return;
      card.dataset.catalogStatsWired = '1';
      diagnostic.addEventListener('click', () => beginDiagnostic(provider));
    });
  }

  async function loadProviders() {
    try {
      const state = await rpc('app.getState');
      providers = Array.isArray(state?.providers) ? state.providers : [];
      decorateCards();
    } catch { }
  }

  function initialize() {
    const stylesheet = document.createElement('link');
    stylesheet.rel = 'stylesheet';
    stylesheet.href = 'diagnostic-local-stats.css';
    document.head.append(stylesheet);

    const list = document.querySelector('#settings-provider-list');
    const panel = document.querySelector('#diagnostic-panel');
    if (list) new MutationObserver(() => { loadProviders(); }).observe(list, { childList: true });
    if (panel) new MutationObserver(appendStats).observe(panel, { childList: true, subtree: true });
    loadProviders();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
