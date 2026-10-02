(() => {
  'use strict';

  let sequence = 0;
  let activeProvider = '';
  const pending = new Map();
  const cache = new Map();

  function rpc(method, params = {}) {
    const id = `category-tree-${Date.now()}-${++sequence}`;
    const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    window.chrome.webview.postMessage({ id, method, params });
    return promise;
  }

  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const request = pending.get(response?.id);
    if (!request) return;
    pending.delete(response.id);
    response.ok ? request.resolve(response.result) : request.reject(new Error(response.error || 'Hiérarchie indisponible'));
  });

  function depthMap(categories) {
    const byId = new Map(categories.map(category => [String(category.id), category]));
    const memo = new Map();
    function depth(id, trail = new Set()) {
      if (memo.has(id)) return memo.get(id);
      const category = byId.get(id);
      const parent = category?.parentId == null ? '' : String(category.parentId);
      if (!parent || !byId.has(parent) || trail.has(id)) { memo.set(id, 0); return 0; }
      const next = new Set(trail); next.add(id);
      const value = Math.min(4, 1 + depth(parent, next));
      memo.set(id, value);
      return value;
    }
    categories.forEach(category => depth(String(category.id)));
    return memo;
  }

  function label(category, depth) {
    return `${depth > 0 ? `${'↳ '.repeat(Math.min(depth, 2))}` : ''}${category.name || ''}`;
  }

  function decorate(catalog, categories) {
    const section = document.querySelector(`#${catalog}`);
    if (!section) return;
    const byId = new Map(categories.map(category => [String(category.id), category]));
    const depths = depthMap(categories);

    const select = section.querySelector('.category-select');
    select?.querySelectorAll('option[value]').forEach(option => {
      if (!option.value) return;
      const category = byId.get(String(option.value));
      if (!category) return;
      const depth = depths.get(String(category.id)) || 0;
      option.textContent = label(category, depth);
      option.dataset.depth = String(depth);
    });

    const filter = (section.querySelector('.category-filter')?.value || '').trim().toLocaleLowerCase();
    const visible = categories
      .filter(category => category.selected && category.present)
      .filter(category => !filter || String(category.name || '').toLocaleLowerCase().includes(filter))
      .slice(0, 200);
    const buttons = [...section.querySelectorAll('.category-list .category-button')];
    buttons.forEach((button, index) => {
      const category = visible[index];
      if (!category) return;
      const depth = depths.get(String(category.id)) || 0;
      button.textContent = label(category, depth);
      button.dataset.categoryDepth = String(depth);
      button.dataset.categoryId = String(category.id);
      if (depth > 0) button.title = `Sous-catégorie de ${byId.get(String(category.parentId))?.name || category.parentId}`;
      else button.removeAttribute('title');
    });
  }

  async function load(catalog, force = false) {
    const providerKey = document.querySelector('#provider-select')?.value || '';
    if (!providerKey) return;
    const key = `${providerKey}\u0000${catalog}`;
    if (!force && cache.has(key)) { decorate(catalog, cache.get(key)); return; }
    try {
      const result = await rpc('categories.hierarchy', { providerKey, catalogType: catalog });
      if ((document.querySelector('#provider-select')?.value || '') !== providerKey) return;
      const categories = Array.isArray(result?.categories) ? result.categories : [];
      cache.set(key, categories);
      decorate(catalog, categories);
    } catch { }
  }

  function wireCatalog(catalog) {
    const section = document.querySelector(`#${catalog}`);
    if (!section) return;
    const list = section.querySelector('.category-list');
    const select = section.querySelector('.category-select');
    if (list) new MutationObserver(() => load(catalog)).observe(list, { childList: true });
    if (select) new MutationObserver(() => load(catalog)).observe(select, { childList: true });
    section.querySelector('.category-filter')?.addEventListener('input', () => window.setTimeout(() => load(catalog), 0));
  }

  function initialize() {
    const stylesheet = document.createElement('link');
    stylesheet.rel = 'stylesheet';
    stylesheet.href = 'category-hierarchy.css';
    document.head.append(stylesheet);

    ['live', 'vod', 'series'].forEach(wireCatalog);
    const providerSelect = document.querySelector('#provider-select');
    activeProvider = providerSelect?.value || '';
    providerSelect?.addEventListener('change', () => {
      const next = providerSelect.value || '';
      if (next !== activeProvider) cache.clear();
      activeProvider = next;
      window.setTimeout(() => ['live', 'vod', 'series'].forEach(catalog => load(catalog, true)), 0);
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
