(() => {
  'use strict';
  const pending = new Map();
  const groups = new Map();
  let sequence = 0;
  const state = { app: null, activeProviderKey: null, page: 'home', catalogs: new Map(), policyCatalog: 'live', policy: null, onboarding: null, indexTimer: null, search: { query: '', pages: { vod: 1, series: 1 } } };
  let updateReleaseUrl = '';
  const catalogSessions = new Set();
  let catalogRefreshBusy = false;
  let completedIndex = null;

  function rawPost(method, params) {
    const id = `ui-${++sequence}`;
    window.chrome.webview.postMessage({ id, method, params });
    return id;
  }
  function rpc(method, params = {}, group = null) {
    if (group && groups.has(group)) rawPost('app.cancel', { requestId: groups.get(group) });
    const id = `ui-${++sequence}`;
    if (group) groups.set(group, id);
    const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject, group }));
    window.chrome.webview.postMessage({ id, method, params });
    return promise;
  }
  window.iptvRpc = rpc;
  function cancelGroup(group) {
    const id = groups.get(group);
    if (id) rawPost('app.cancel', { requestId: id });
    groups.delete(group);
  }
  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const request = pending.get(response.id);
    if (!request) return;
    pending.delete(response.id);
    if (request.group && groups.get(request.group) !== response.id) { request.reject(new DOMException('Superseded', 'AbortError')); return; }
    if (request.group) groups.delete(request.group);
    response.ok ? request.resolve(response.result) : request.reject(new Error(response.error || 'Opération impossible'));
  });

  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  function decodeHtmlEntities(value) { const decoder = document.createElement('textarea'); decoder.innerHTML = String(value ?? ''); return decoder.value; }
  function node(tag, className, text) { const value = document.createElement(tag); if (className) value.className = className; if (text !== undefined) value.textContent = text; return value; }
  function button(text, className, action) { const value = node('button', className, text); value.type = 'button'; value.addEventListener('click', action); return value; }
  function toast(message, error = false) { const box = $('#toast'); box.textContent = message; box.style.background = error ? '#8c3242' : '#202c40'; box.classList.add('show'); window.setTimeout(() => box.classList.remove('show'), 3200); }
  function isAbort(error) { return error && error.name === 'AbortError'; }
  function activeProvider() { return state.app?.providers.find(provider => provider.key === state.activeProviderKey) || null; }
  function applyThemePreference(theme) {
    if (theme === 'dark' || theme === 'light') document.documentElement.dataset.theme = theme;
    else document.documentElement.removeAttribute('data-theme');
  }

  function ensureLiveRefreshControls() {
    const content = $('#live .catalog-content');
    if (!content || $('#live-refresh-panel')) return;
    const panel = node('div', 'panel panel-head'); panel.id = 'live-refresh-panel';
    const copy = node('div');
    copy.append(node('p', 'eyebrow', 'LIVE LOCAL'), node('p', 'muted', 'Dernière actualisation Live : jamais'));
    copy.lastChild.id = 'live-refreshed-at';
    const refresh = button('Actualiser le Live', 'secondary', refreshCatalogManual); refresh.id = 'live-refresh-catalog';
    panel.append(copy, refresh); content.prepend(panel);
  }

  async function refreshApp(goHome = false) {
    state.app = await rpc('app.getState');
    state.activeProviderKey = state.app.activeProviderKey;
    const brandVersion = $('.brand small');
    if (brandVersion) brandVersion.textContent = state.app.version ? `Desktop · ${state.app.version}` : 'Desktop';
    renderProviderSelector(); renderProviderSettings();
    if (goHome) navigate('home'); else await renderHome();
    // Render local data first; the background session task never blocks opening/navigation.
    startCatalogSession();
    loadCatalogStatus();
  }

  async function startCatalogSession() {
    const provider = activeProvider();
    if (!provider || provider.type !== 'xtream' || catalogSessions.has(provider.key)) return;
    catalogSessions.add(provider.key);
    try { await applyCatalogRefresh(provider.key, await rpc('catalog.session', { providerKey: provider.key })); }
    catch { if (state.activeProviderKey === provider.key) $('#catalog-refresh-state').textContent = 'Données locales conservées. Aucun nouvel essai automatique.'; }
  }
  async function loadCatalogStatus(preserveState = false) {
    const provider = activeProvider();
    const stalker = provider?.type === 'stalker';
    const settingsRefresh = $('#refresh-catalog'), liveRefresh = $('#live-refresh-catalog'), livePanel = $('#live-refresh-panel');
    if (settingsRefresh) { settingsRefresh.disabled = catalogRefreshBusy || !provider; settingsRefresh.textContent = stalker ? 'Actualiser le Live' : 'Actualiser le catalogue'; }
    if (liveRefresh) liveRefresh.disabled = catalogRefreshBusy || !provider || !stalker;
    if (livePanel) livePanel.classList.toggle('hidden', !stalker);
    $('#catalog-snapshot-kind').textContent = stalker ? 'LIVE STALKER' : 'CATALOGUE XTREAM';
    if (!provider) { $('#catalog-refreshed-at').textContent = 'Aucun fournisseur actif.'; if ($('#live-refreshed-at')) $('#live-refreshed-at').textContent = 'Aucun fournisseur actif.'; return; }
    try {
      const status = await rpc('catalog.status', { providerKey: provider.key });
      if (state.activeProviderKey !== provider.key) return;
      const refreshedLabel = `${stalker ? 'Dernière actualisation Live' : 'Dernière actualisation'} : ${status.refreshedAt ? new Date(status.refreshedAt).toLocaleString() : 'jamais'}`;
      $('#catalog-refreshed-at').textContent = refreshedLabel;
      if ($('#live-refreshed-at')) $('#live-refreshed-at').textContent = refreshedLabel;
      if (!preserveState) $('#catalog-refresh-state').textContent = stalker
          ? 'Navigation Live locale. Cache valide 6 heures ; aucune analyse catégorie par catégorie.'
          : 'Navigation locale. Actualisation automatique au plus une fois par session, avec un délai de 30 minutes.';
    } catch { /* Local status only; no network fallback. */ }
  }
  async function applyCatalogRefresh(providerKey, result) {
    if (state.activeProviderKey !== providerKey) return;
    const labels = { updated: 'Catalogue actualisé. Indexation locale en cours.', updatedIndexPending: 'Catalogue actualisé. Reconstruction locale disponible.', recent: 'Cache récent : aucun appel catalogue.', retained: 'Données locales conservées. Aucun nouvel essai automatique.' };
    $('#catalog-refresh-state').textContent = activeProvider()?.type === 'stalker' && result.state === 'updated'
      ? 'Live actualisé. Le cache local est prêt.'
      : labels[result.state] || 'Données locales disponibles.';
    await loadCatalogStatus(true);
    if (!result.updated || state.activeProviderKey !== providerKey) return;
    state.catalogs.clear();
    if (state.page === 'home') await renderHome();
    else if (['live', 'vod', 'series'].includes(state.page)) await loadCatalogShell(state.page);
    if (activeProvider()?.type === 'xtream') await pollIndex();
  }
  async function refreshCatalogManual() {
    const provider = activeProvider();
    if (!provider || catalogRefreshBusy) return;
    catalogRefreshBusy = true; $('#refresh-catalog').disabled = true; if ($('#live-refresh-catalog')) $('#live-refresh-catalog').disabled = true;
    if (provider.type === 'xtream') catalogSessions.add(provider.key);
    $('#catalog-refresh-state').textContent = provider.type === 'stalker'
      ? 'Actualisation volontaire du Live…'
      : 'Actualisation volontaire : Live, Films, puis Séries…';
    try { await applyCatalogRefresh(provider.key, await rpc('catalog.refresh', { providerKey: provider.key })); }
    catch { $('#catalog-refresh-state').textContent = 'Données locales conservées. Aucun nouvel essai automatique.'; }
    finally { catalogRefreshBusy = false; await loadCatalogStatus(true); }
  }
  $('#refresh-catalog').addEventListener('click', refreshCatalogManual);
  function renderProviderSelector() {
    const select = $('#provider-select'); select.replaceChildren();
    const empty = node('option', '', 'Aucun fournisseur actif'); empty.value = ''; select.append(empty);
    state.app.providers.filter(provider => provider.enabled).forEach(provider => { const option = node('option', '', provider.name); option.value = provider.key; option.selected = provider.key === state.activeProviderKey; select.append(option); });
    select.disabled = !state.app.providers.some(provider => provider.enabled);
  }
  $('#provider-select').addEventListener('change', async event => {
    try { await rpc('app.setActiveProvider', { providerKey: event.target.value || null }); state.activeProviderKey = event.target.value || null; state.catalogs.clear(); await refreshApp(true); }
    catch (error) { toast(error.message, true); }
  });

  const titles = { home: 'Accueil', live: 'Live', vod: 'Films', series: 'Séries', search: 'Recherche', settings: 'Paramètres', favorites: 'Favoris' };
  function navigate(page) {
    cancelGroup('view'); cancelGroup('detail'); cancelGroup('home-content');
    if (page !== 'live') cancelGroup('live-catalog');
    state.page = page;
    window.iptvHome.setActive(page === 'home');
    $$('.nav,.page').forEach(element => element.classList.remove('active'));
    $(`.nav[data-page="${page}"]`).classList.add('active');
    $(`#${page}`).classList.add('active'); $('#page-title').textContent = titles[page];
    rpc('player.liveSurface', { visible: page === 'live' }, 'live-surface').catch(error => { if (!isAbort(error)) console.warn('Native Live surface unavailable.'); });
    if (page === 'home') renderHome();
    if (['live', 'vod', 'series'].includes(page)) loadCatalogShell(page);
    if (page === 'settings') { renderProviderSettings(); loadPreferences(); }
  }
  window.iptvNavigate = navigate;
  $$('.nav').forEach(element => element.addEventListener('click', () => navigate(element.dataset.page)));
  $$('[data-home-page]').forEach(element => element.addEventListener('click', () => navigate(element.dataset.homePage)));
  $('#home-search-form').addEventListener('submit', event => {
    event.preventDefault();
    const query = $('#home-search').value.trim();
    navigate('search');
    $('#global-search').value = query;
    if (query.length >= 3) $('#search-form').requestSubmit();
    else $('#global-search').focus();
  });

  window.addEventListener('iptv-update-available', event => {
    const info = event.detail || {};
    const url = typeof info.url === 'string' ? info.url.trim() : '';
    if (!info.version || !url.startsWith('https://github.com/PassionFlix/iptv-explorer-desktop/releases/')) return;
    updateReleaseUrl = url;
    $('#update-title').textContent = `Mise à jour disponible — v${info.version}`;
    $('#update-message').textContent = 'Une nouvelle version stable est disponible.';
    $('#update-banner').classList.add('visible');
  });
  $('#update-download').addEventListener('click', () => { if (updateReleaseUrl) window.location.href = updateReleaseUrl; });
  $('#update-later').addEventListener('click', () => $('#update-banner').classList.remove('visible'));

  async function renderHome() {
    const zero = $('#zero-state'), dashboard = $('#dashboard');
    renderHomeMedia({});
    if (!state.app || state.app.providerCount === 0) { zero.classList.remove('hidden'); dashboard.classList.add('hidden'); return; }
    if (!state.activeProviderKey) { zero.classList.remove('hidden'); dashboard.classList.add('hidden'); zero.querySelector('h2').textContent = 'Aucun fournisseur actif'; zero.querySelector('p').textContent = 'Activez un fournisseur depuis Paramètres → Fournisseurs.'; return; }
    zero.classList.add('hidden'); dashboard.classList.remove('hidden');
    const provider = activeProvider();
    $('#dashboard-name').textContent = provider.name;
    $('#dashboard-meta').textContent = provider.type.toUpperCase();
    $('#dashboard-status').className = 'status neutral';
    $('#dashboard-status').textContent = 'Chargement…';
    rpc('home.content', { providerKey: provider.key }, 'home-content')
      .then(renderHomeMedia)
      .catch(error => { if (!isAbort(error)) console.warn('Home media sections unavailable.'); });
    try {
      const data = await rpc('providers.dashboard', { providerKey: provider.key }, 'view');
      $('#dashboard-status').className = 'status neutral'; $('#dashboard-status').textContent = provider.enabled ? 'Activé · état réseau non vérifié' : 'Désactivé';
      renderIndexStatus(data.index, $('#home-index-status'), data.indexDirty);
      if (data.index && ['queued', 'running'].includes(data.index.status)) {
        if (state.indexTimer) window.clearTimeout(state.indexTimer);
        state.indexTimer = window.setTimeout(pollIndex, 1200);
      }
    } catch (error) { if (!isAbort(error)) toast(error.message, true); }
  }

  function renderHomeMedia(data) {
    renderContinueWatching(data?.continueWatching || []);
    renderRecentlyAdded(data?.recentlyAddedFilms || [], 'vod', 'recent-films');
    renderRecentlyAdded(data?.recentlyAddedSeries || [], 'series', 'recent-series');
    window.iptvHome.setBackgrounds(data?.backgroundImages || []);
  }

  function renderContinueWatching(items) {
    const section = $('#continue-watching'), grid = $('#continue-watching-items');
    grid.replaceChildren();
    section.classList.toggle('hidden', items.length === 0);
    for (const item of items) {
      const card = node('button', 'home-media-card'); card.type = 'button';
      const visual = imageOrPlaceholder(item.posterUrl, item.title, 'home-media-poster');
      if (item.durationSeconds > 0) {
        const progress = node('span', 'home-media-progress');
        const fill = node('i'); fill.style.width = `${Math.min(100, Math.max(0, item.percentage || 0))}%`;
        progress.append(fill); visual.append(progress);
      }
      const copy = node('span', 'home-media-copy');
      copy.append(node('strong', '', item.title));
      if (item.catalog === 'series') {
        const code = item.season && item.episode ? `S${String(item.season).padStart(2, '0')}E${String(item.episode).padStart(2, '0')}` : item.episodeTitle || 'Épisode';
        copy.append(node('small', '', code));
      }
      copy.append(node('span', 'home-media-action', item.durationSeconds > 0 ? `${item.percentage} % · Reprendre` : 'Reprendre'));
      card.append(visual, copy);
      card.addEventListener('click', async () => {
        try { await rpc('player.resume', { providerKey: item.providerKey, catalogType: item.catalog, mediaId: item.mediaId }, 'player'); }
        catch (error) { if (!isAbort(error)) toast(error.message, true); }
      });
      grid.append(card);
    }
  }

  function renderRecentlyAdded(items, catalog, sectionId) {
    const section = $(`#${sectionId}`), grid = $(`#${sectionId}-items`);
    grid.replaceChildren();
    section.classList.toggle('hidden', items.length === 0);
    for (const item of items) {
      const card = node('button', 'home-media-card'); card.type = 'button';
      const reference = Object.freeze({ providerKey: state.activeProviderKey, mediaType: catalog, mediaId: item.id });
      card.append(imageOrPlaceholder(item.imageUrl, item.title, 'home-media-poster'));
      const copy = node('span', 'home-media-copy');
      copy.append(node('strong', '', item.title), node('small', '', catalog === 'series' ? 'Série' : 'Film'));
      card.append(copy);
      card.addEventListener('click', () => openDetail(catalog, { id: item.id, title: item.title, imageUrl: item.imageUrl }, reference));
      grid.append(card);
    }
    window.iptvHome.resetCarousel(grid);
  }

  async function loadCatalogShell(catalog) {
    const section = $(`#${catalog}`), list = section.querySelector('.category-list'); list.replaceChildren();
    section.querySelector('.catalog-grid').replaceChildren(); section.querySelector('.empty-catalog').classList.remove('hidden');
    section.querySelector('.empty-catalog').textContent = `Sélectionnez une catégorie ${catalog === 'live' ? 'Live' : catalog === 'vod' ? 'Films' : 'Séries'}.`;
    const categorySelect = section.querySelector('.category-select'); if (categorySelect) categorySelect.replaceChildren(Object.assign(node('option', '', 'Sélectionner une catégorie'), { value: '' }));
    if (catalog === 'live') resetLiveChannels();
    if (!state.activeProviderKey) { section.querySelector('.empty-catalog').textContent = 'Aucun fournisseur actif.'; return; }
    try {
      const data = await rpc('categories.list', { providerKey: state.activeProviderKey, catalogType: catalog }, 'view');
      const selected = data.categories.filter(category => category.selected && category.present);
      state.catalogs.set(catalog, { categories: selected, hierarchyCategories: data.categories, categoryId: null, page: 1, totalPages: 1, requestVersion: 0, loadingKey: null, loadingPromise: null, liveItems: [], filteredLiveItems: [], liveRenderFrame: 0 });
      renderCatalogSelector(catalog);
      renderCatalogCategories(catalog, '');
      if (selected.length && catalog !== 'live') selectCatalogCategory(catalog, selected[0].id);
      else section.querySelector('.empty-catalog').textContent = 'Aucune catégorie sélectionnée.';
      if (selected.length && catalog === 'live') section.querySelector('.empty-catalog').textContent = 'Sélectionnez une catégorie Live.';
    } catch (error) { if (!isAbort(error)) toast(error.message, true); }
  }
  function categoryDepths(categories) {
    const byId = new Map(categories.map(category => [String(category.id), category])), depths = new Map();
    function depth(id, trail = new Set()) {
      if (depths.has(id)) return depths.get(id);
      const category = byId.get(id), parent = category?.parentId == null ? '' : String(category.parentId);
      if (!parent || !byId.has(parent) || trail.has(id)) { depths.set(id, 0); return 0; }
      const next = new Set(trail); next.add(id);
      const value = Math.min(4, depth(parent, next) + 1); depths.set(id, value); return value;
    }
    categories.forEach(category => depth(String(category.id)));
    return depths;
  }
  function categoryLabel(category, depth) { return `${depth ? `${'↳ '.repeat(Math.min(depth, 2))}` : ''}${decodeHtmlEntities(category.name)}`; }
  function renderCatalogSelector(catalog) {
    const select = $(`#${catalog} .category-select`), model = state.catalogs.get(catalog); if (!select || !model) return;
    const placeholder = node('option', '', 'Sélectionner une catégorie'); placeholder.value = ''; select.replaceChildren(placeholder);
    const depths = categoryDepths(model.hierarchyCategories || model.categories);
    model.categories.forEach(category => { const depth = depths.get(String(category.id)) || 0, option = node('option', '', categoryLabel(category, depth)); option.value = category.id; option.selected = category.id === model.categoryId; option.dataset.depth = String(depth); select.append(option); });
  }
  function renderCatalogCategories(catalog, filter) {
    const model = state.catalogs.get(catalog), container = $(`#${catalog} .category-list`); container.replaceChildren();
    const query = filter.trim().toLocaleLowerCase();
    const depths = categoryDepths(model.hierarchyCategories || model.categories);
    model.categories.filter(category => !query || category.name.toLocaleLowerCase().includes(query)).forEach(category => {
      const depth = depths.get(String(category.id)) || 0;
      const item = button(categoryLabel(category, depth), `category-button${model.categoryId === category.id ? ' active' : ''}`, () => selectCatalogCategory(catalog, category.id));
      item.dataset.categoryDepth = String(depth); item.dataset.categoryId = String(category.id); container.append(item);
    });
  }
  $$('.catalog-page .category-filter').forEach(input => input.addEventListener('input', () => renderCatalogCategories(input.closest('.catalog-page').dataset.catalog, input.value)));
  $$('.catalog-page .category-select').forEach(select => select.addEventListener('change', () => { if (select.value) selectCatalogCategory(select.closest('.catalog-page').dataset.catalog, select.value); }));
  async function selectCatalogCategory(catalog, categoryId, page = 1) {
    if (catalog === 'live') return selectLiveCategory(categoryId);
    const model = state.catalogs.get(catalog); if (!model) return; model.categoryId = categoryId; model.page = page; renderCatalogSelector(catalog); renderCatalogCategories(catalog, $(`#${catalog} .category-filter`).value);
    const section = $(`#${catalog}`), grid = section.querySelector('.catalog-grid'), empty = section.querySelector('.empty-catalog'), loading = section.querySelector('.loading-state');
    grid.replaceChildren(); empty.classList.add('hidden'); loading.classList.remove('hidden');
    try {
      let result;
      result = await rpc(`catalog.${catalog}.page`, { providerKey: state.activeProviderKey, categoryId, page }, 'view');
      model.totalPages = result.totalPages || 1; renderCatalogItems(catalog, result.items);
      const pager = section.querySelector('.pager');
      if (pager) { pager.classList.toggle('hidden', model.totalPages <= 1); pager.querySelector('.page-info').textContent = `Page ${result.page} / ${model.totalPages} · ${result.total} éléments`; pager.querySelector('.previous-page').disabled = result.page <= 1; pager.querySelector('.next-page').disabled = result.page >= model.totalPages; }
      if (!result.items.length) { empty.textContent = 'Aucun contenu dans cette catégorie.'; empty.classList.remove('hidden'); }
    } catch (error) { if (!isAbort(error)) { empty.textContent = error.message; empty.classList.remove('hidden'); } }
    finally { loading.classList.add('hidden'); }
  }
  $$('.catalog-page .previous-page').forEach(value => value.addEventListener('click', () => { const catalog = value.closest('.catalog-page').dataset.catalog, model = state.catalogs.get(catalog); selectCatalogCategory(catalog, model.categoryId, Math.max(1, model.page - 1)); }));
  $$('.catalog-page .next-page').forEach(value => value.addEventListener('click', () => { const catalog = value.closest('.catalog-page').dataset.catalog, model = state.catalogs.get(catalog); selectCatalogCategory(catalog, model.categoryId, Math.min(model.totalPages, model.page + 1)); }));

  function imageOrPlaceholder(url, title, className = '') {
    const holder = node('div', className || 'poster');
    if (!url) { holder.textContent = title.slice(0, 1).toUpperCase(); return holder; }
    const image = document.createElement('img'); image.loading = 'lazy'; image.referrerPolicy = 'no-referrer'; image.alt = ''; image.src = url; image.addEventListener('error', () => { image.remove(); holder.textContent = title.slice(0, 1).toUpperCase(); }, { once: true }); holder.append(image); return holder;
  }
  function compareLiveItems(left, right) {
    const leftNumber = /^\d+$/.test(String(left.id ?? '')) ? Number(left.id) : Number.MAX_SAFE_INTEGER;
    const rightNumber = /^\d+$/.test(String(right.id ?? '')) ? Number(right.id) : Number.MAX_SAFE_INTEGER;
    if (leftNumber !== rightNumber) return leftNumber - rightNumber;
    return String(left.title || '').localeCompare(String(right.title || ''), 'fr', { numeric: true, sensitivity: 'base' });
  }
  function resetLiveChannels() {
    const filter = $('#live-channel-filter'), count = $('#live-channel-count');
    if (filter) { filter.value = ''; filter.disabled = true; }
    if (count) count.textContent = 'Aucune catégorie';
  }
  async function selectLiveCategory(categoryId) {
    const model = state.catalogs.get('live'); if (!model) return;
    const providerKey = state.activeProviderKey, requestKey = `${providerKey}\u0000${categoryId}`;
    if (model.loadingKey === requestKey && model.loadingPromise) return model.loadingPromise;
    model.categoryId = categoryId; model.page = 1; renderCatalogSelector('live'); renderCatalogCategories('live', $('#live .category-filter').value);
    const section = $('#live'), grid = section.querySelector('.live-grid'), empty = section.querySelector('.empty-catalog'), loading = section.querySelector('.loading-state');
    model.requestVersion += 1; const requestVersion = model.requestVersion;
    model.liveItems = []; model.filteredLiveItems = []; grid.replaceChildren(); empty.classList.add('hidden'); loading.classList.remove('hidden');
    const filter = $('#live-channel-filter'); filter.disabled = true; filter.value = '';
    $('#live-channel-count').textContent = 'Chargement…';
    const operation = rpc('catalog.live', { providerKey, categoryId }, 'live-catalog');
    model.loadingKey = requestKey; model.loadingPromise = operation;
    try {
      const items = await operation;
      if (state.page !== 'live' || state.activeProviderKey !== providerKey || model.requestVersion !== requestVersion || model.categoryId !== categoryId) return;
      model.liveItems = [...items].sort(compareLiveItems); model.filteredLiveItems = model.liveItems; filter.disabled = false;
      renderLiveItems(model);
      if (!items.length) { empty.textContent = 'Aucune chaîne dans cette catégorie.'; empty.classList.remove('hidden'); }
    } catch (error) {
      if (!isAbort(error) && model.requestVersion === requestVersion) { empty.textContent = error.message; empty.classList.remove('hidden'); }
    } finally {
      if (model.requestVersion === requestVersion) loading.classList.add('hidden');
      if (model.loadingKey === requestKey) { model.loadingKey = null; model.loadingPromise = null; }
    }
  }
  function renderLiveItems(model) {
    const filter = $('#live-channel-filter').value.trim().toLocaleLowerCase();
    model.filteredLiveItems = model.liveItems.filter(item => !filter || String(item.id).toLocaleLowerCase().includes(filter) || String(item.title || '').toLocaleLowerCase().includes(filter));
    const count = model.filteredLiveItems.length;
    $('#live-channel-count').textContent = `${count} chaîne${count === 1 ? '' : 's'}`;
    const grid = $('#live .live-grid'); grid.scrollTop = 0; queueLiveWindow(model);
  }
  function queueLiveWindow(model) {
    if (model.liveRenderFrame) cancelAnimationFrame(model.liveRenderFrame);
    model.liveRenderFrame = requestAnimationFrame(() => { model.liveRenderFrame = 0; renderLiveWindow(model); });
  }
  function renderLiveWindow(model) {
    const grid = $('#live .live-grid'); if (!grid || state.catalogs.get('live') !== model) return;
    const rowHeight = 58, windowSize = 80, overscan = 10;
    const start = Math.max(0, Math.floor(grid.scrollTop / rowHeight) - overscan), end = Math.min(model.filteredLiveItems.length, start + windowSize);
    const fragment = document.createDocumentFragment();
    const top = node('div', 'live-virtual-spacer'); top.style.height = `${start * rowHeight}px`; fragment.append(top);
    for (let index = start; index < end; index++) fragment.append(createLiveRow(model.filteredLiveItems[index], model));
    const bottom = node('div', 'live-virtual-spacer'); bottom.style.height = `${Math.max(0, model.filteredLiveItems.length - end) * rowHeight}px`; fragment.append(bottom);
    grid.replaceChildren(fragment);
  }
  function createLiveRow(item, model) {
    const reference = Object.freeze({ providerKey: state.activeProviderKey, mediaType: 'live', mediaId: item.id, extension: item.extension, categoryId: item.categoryId || model.categoryId });
    const card = node('article', 'media-card live-card live-row'); card.tabIndex = 0; card.setAttribute('role', 'button'); card.dataset.mediaId = String(item.id); card.dataset.title = String(item.title || ''); card.dataset.categoryId = String(reference.categoryId || '');
    const visual = imageOrPlaceholder(item.imageUrl, item.title, 'channel-logo');
    const copy = node('span', 'live-channel-copy'); copy.append(node('small', 'live-channel-id', `#${item.id}`), node('strong', '', item.title));
    const play = () => openPlayer(reference, item.title);
    card.addEventListener('click', event => { if (!event.target.closest('button')) play(); });
    card.addEventListener('keydown', event => { if (event.target.closest('button') || (event.key !== 'Enter' && event.key !== ' ')) return; event.preventDefault(); play(); });
    card.append(visual, copy); return card;
  }
  $('#live-channel-filter').addEventListener('input', () => { const model = state.catalogs.get('live'); if (model) renderLiveItems(model); });
  $('#live .live-grid').addEventListener('scroll', () => { const model = state.catalogs.get('live'); if (model) queueLiveWindow(model); }, { passive: true });
  function renderCatalogItems(catalog, items) {
    const grid = $(`#${catalog} .catalog-grid`), providerKey = state.activeProviderKey; grid.replaceChildren();
    const renderedItems = items;
    renderedItems.forEach(item => {
      const reference = Object.freeze({ providerKey, mediaType: catalog, mediaId: item.id, extension: item.extension, categoryId: item.categoryId });
      const card = node('article', 'media-card'); card.append(imageOrPlaceholder(item.imageUrl, item.title));
      const copy = node('div', 'media-copy'); copy.append(node('strong', '', item.title), node('small', '', [item.year, item.rating ? `★ ${item.rating}` : null].filter(Boolean).join(' · ') || 'Détails')); card.append(copy);
      card.addEventListener('click', () => openDetail(catalog, item, reference)); grid.append(card);
    });
  }

  async function openDetail(catalog, item, reference = Object.freeze({ providerKey: state.activeProviderKey, mediaType: catalog, mediaId: item.id, extension: item.extension })) {
    const dialog = $('#detail-dialog'), content = $('#detail-content'); $('#detail-title').textContent = item.title; content.replaceChildren(node('div', 'loading-state', 'Chargement…')); dialog.showModal();
    try {
      const detail = await rpc(`catalog.${catalog}.detail`, { providerKey: reference.providerKey, mediaId: reference.mediaId }, 'detail');
      content.replaceChildren(); const layout = node('section', 'detail-layout'); layout.append(imageOrPlaceholder(detail.poster, detail.title, 'detail-poster'));
      const copy = node('div'); const metadata = node('div', 'metadata'); [detail.year, detail.genre, detail.duration, detail.rating ? `★ ${detail.rating}` : null].filter(Boolean).forEach(value => metadata.append(node('span', '', String(value))));
      copy.append(metadata, node('p', 'plot', detail.plot || 'Aucun résumé communiqué.'));
      if (detail.director) copy.append(node('p', 'muted', `Réalisation : ${detail.director}`)); if (detail.cast) copy.append(node('p', 'muted', `Distribution : ${detail.cast}`));
      if (catalog === 'vod') copy.append(button('▶ Lire', 'primary', () => openPlayer({ ...reference, mediaId: detail.id, extension: detail.extension, title: detail.title, posterUrl: detail.poster }, detail.title)));
      layout.append(copy); content.append(layout);
      if (catalog === 'series') renderSeasons(content, detail, reference.providerKey);
    } catch (error) { if (!isAbort(error)) content.replaceChildren(node('p', 'form-error', error.message)); }
  }
  function renderSeasons(parent, detail, providerKey) {
    if (!detail.seasons?.length) { parent.append(node('p', 'muted', 'Aucun épisode communiqué.')); return; }
    const section = node('section', 'series-seasons'); section.append(node('h3', '', 'Saisons et épisodes'));
    detail.seasons.forEach((season, index) => {
      const block = node('details', 'season'); block.open = detail.seasons.length === 1 || index === 0;
      block.append(node('summary', '', `${season.title} · ${season.episodes.length} épisode(s)`));
      const list = node('div', 'episode-list');
      season.episodes.forEach(episode => {
        const row = node('div', 'episode'); const number = episode.episode ? `E${String(episode.episode).padStart(2, '0')} — ` : '';
        const main = node('div', 'episode-main');
        const actions = node('div', 'episode-actions');
        actions.append(button('Lire', 'play-small', () => openPlayer({ providerKey, mediaType: 'series', mediaId: detail.id, episodeId: episode.id, extension: episode.extension, title: episode.title, posterUrl: detail.poster, seriesTitle: detail.title, season: episode.season ?? season.number, episode: episode.episode }, episode.title)));
        main.append(node('span', 'episode-info', `${number}${episode.title}`), actions);
        row.append(main); list.append(row);
      });
      block.append(list); section.append(block);
    });
    parent.append(section);
  }
  async function openPlayer(reference, title) {
    const payload = reference.mediaType === 'live'
      ? { providerKey: reference.providerKey, mediaType: 'live', mediaId: reference.mediaId, categoryId: reference.categoryId, ...(reference.extension ? { extension: reference.extension } : {}) }
      : reference;
    try { await rpc('player.open', payload, 'player'); }
    catch (error) { if (!isAbort(error)) toast(error.message, true); }
  }

  $('#search-form').addEventListener('submit', async event => {
    event.preventDefault(); const query = $('#global-search').value.trim();
    if (query.length < 3) { $('#search-message').textContent = 'Saisissez au moins trois caractères.'; return; }
    if (!state.activeProviderKey) { $('#search-message').textContent = 'Aucun fournisseur actif.'; return; }
    state.search = { query, pages: { vod: 1, series: 1 } };
    await runSearch();
  });
  async function runSearch() {
    const query = state.search.query, results = $('#search-results');
    results.replaceChildren(); $('#search-message').textContent = 'Recherche…';
    try {
      const [vod, series] = await Promise.all([
        rpc('search.query', { providerKey: state.activeProviderKey, catalogType: 'vod', query, page: state.search.pages.vod, pageSize: 40 }, 'search-vod'),
        rpc('search.query', { providerKey: state.activeProviderKey, catalogType: 'series', query, page: state.search.pages.series, pageSize: 40 }, 'search-series')]);
      $('#search-message').textContent = `${vod.total + series.total} résultat(s)`; renderSearchGroup(results, 'Films', 'vod', vod); renderSearchGroup(results, 'Séries', 'series', series);
    } catch (error) { if (!isAbort(error)) $('#search-message').textContent = error.message; }
  }
  function renderSearchGroup(parent, title, catalog, result) {
    const group = node('section', 'result-group'); const heading = node('h3'); heading.append(node('span', '', title), node('span', '', `${result.total} résultat(s)`)); const grid = node('div', 'catalog-grid'); group.append(heading, grid); parent.append(group);
    const providerKey = state.activeProviderKey;
    result.items.forEach(hit => { const item = { id: hit.remoteId, title: hit.title, imageUrl: hit.imageUrl }; const reference = Object.freeze({ providerKey, mediaType: catalog, mediaId: item.id }); const card = node('article', 'media-card'); card.append(imageOrPlaceholder(item.imageUrl, item.title)); const copy = node('div', 'media-copy'); copy.append(node('strong', '', item.title)); card.append(copy); card.addEventListener('click', () => openDetail(catalog, item, reference)); grid.append(card); });
    if (result.totalPages > 1) {
      const pager = node('div', 'pager'); const previous = button('Précédent', 'secondary', () => changeSearchPage(catalog, result.page - 1)); const next = button('Suivant', 'secondary', () => changeSearchPage(catalog, result.page + 1)); previous.disabled = result.page <= 1; next.disabled = result.page >= result.totalPages; pager.append(previous, node('span', 'page-info', `Page ${result.page} / ${result.totalPages}`), next); group.append(pager);
    }
  }
  function changeSearchPage(catalog, page) { state.search.pages[catalog] = Math.max(1, page); runSearch(); }

  const onboardingDialog = $('#onboarding-dialog'), onboardingForm = $('#onboarding-form');
  function resetOnboarding() { onboardingForm.reset(); onboardingForm.elements.password.value = ''; onboardingForm.elements.macAddress.value = ''; state.onboarding = { step: 1, draft: null, catalog: 'live', policies: {} }; $('#onboarding-error').textContent = ''; $('#onboarding-diagnostic').replaceChildren(); updateCredentialFields(); setWizardStep(1); }
  $$('.open-onboarding').forEach(value => value.addEventListener('click', () => { resetOnboarding(); onboardingDialog.showModal(); }));
  onboardingForm.elements.providerType.addEventListener('change', updateCredentialFields);
  function onboardingExplanation(type) {
    type = String(type || '').toLowerCase();
    if (type === 'xtream') return 'Le test effectue un contrôle minimal. Le catalogue sera chargé localement après activation du fournisseur.';
    if (type === 'stalker') return 'Le test vérifie la session Stalker/MAG et charge les catégories disponibles.';
    return 'Le test détecte le type avec le minimum d’appels nécessaire. Un catalogue Xtream sera chargé localement après activation.';
  }
  function updateCredentialFields() { const type = onboardingForm.elements.providerType.value; $('#xtream-fields').style.display = type === 'stalker' ? 'none' : 'grid'; $('#stalker-fields').style.display = type === 'xtream' ? 'none' : 'block'; $('#onboarding-test-explanation').textContent = onboardingExplanation(type); }
  function setWizardStep(step) { state.onboarding.step = step; $$('.wizard-step').forEach(value => value.classList.toggle('active', Number(value.dataset.step) === step)); $$('#onboarding-dialog .steps span').forEach((value, index) => { value.classList.toggle('current', index + 1 === step); value.classList.toggle('done', index + 1 < step); }); $('#wizard-previous').disabled = step === 1; $('#wizard-next').classList.toggle('hidden', step >= 5); $('#save-provider').classList.toggle('hidden', step !== 5); if (step === 4) renderOnboardingCategories(); if (step === 5) $('#onboarding-summary').textContent = `${state.onboarding.draft.name} · ${state.onboarding.draft.detectedType.toUpperCase()} · ${state.onboarding.draft.credentialStatus}`; }
  $('#wizard-previous').addEventListener('click', () => setWizardStep(Math.max(1, state.onboarding.step - 1)));
  $('#wizard-next').addEventListener('click', () => { const step = state.onboarding.step; if (step === 1 && !validateStep($('.wizard-step[data-step="1"]'))) return; if (step === 2 && !validateCredentials()) return; if (step === 3 && !state.onboarding.draft?.detectedType) { $('#onboarding-error').textContent = 'Testez la connexion avant de continuer.'; return; } setWizardStep(Math.min(5, step + 1)); });
  function validateStep(container) { const invalid = [...container.querySelectorAll('[required]')].find(input => !input.reportValidity()); return !invalid; }
  function validateCredentials() { const type = onboardingForm.elements.providerType.value, username = onboardingForm.elements.username.value, password = onboardingForm.elements.password.value, mac = onboardingForm.elements.macAddress.value; if (type === 'xtream' && (!username || !password)) { $('#onboarding-error').textContent = 'Username et password sont requis.'; return false; } if (type === 'stalker' && !mac) { $('#onboarding-error').textContent = 'La MAC est requise.'; return false; } if (type === 'auto' && !((username && password) || mac)) { $('#onboarding-error').textContent = 'Renseignez des identifiants Xtream ou une MAC.'; return false; } $('#onboarding-error').textContent = ''; return true; }
  $('#test-provider').addEventListener('click', async () => {
    if (!validateStep($('.wizard-step[data-step="1"]')) || !validateCredentials()) return;
    const diagnostic = $('#onboarding-diagnostic'); diagnostic.replaceChildren(node('p', 'muted', 'Connexion en cours…')); $('#test-provider').disabled = true;
    try {
      const input = { name: onboardingForm.elements.name.value, providerType: onboardingForm.elements.providerType.value, serverUrl: onboardingForm.elements.serverUrl.value, username: onboardingForm.elements.username.value || null, password: onboardingForm.elements.password.value || null, macAddress: onboardingForm.elements.macAddress.value || null };
      const create = rpc('providers.addDraft', input); onboardingForm.elements.password.value = ''; onboardingForm.elements.macAddress.value = ''; const draft = await create;
      const tested = await rpc('providers.test', { draftId: draft.id }, 'onboarding');
      if (!tested.detectedType) throw new Error(tested.message || 'Identifiants refusés.'); state.onboarding.draft = tested;
      for (const catalog of ['live', 'vod', 'series']) { const items = tested.categories[catalog] || []; state.onboarding.policies[catalog] = { items, selected: new Set(items.map(item => item.remoteId)), mode: 'all' }; }
      $('#onboarding-test-explanation').textContent = onboardingExplanation(tested.detectedType);
      const success = String(tested.detectedType).toLowerCase() === 'xtream'
        ? 'Connexion validée. Le catalogue sera chargé localement après activation du fournisseur.'
        : 'Connexion validée. Les catégories disponibles peuvent être sélectionnées avant l’enregistrement.';
      diagnostic.replaceChildren(renderDiagnostic(tested.diagnostic, true), node('p', 'hint', success)); toast('Connexion validée.');
    } catch (error) { if (!isAbort(error)) diagnostic.replaceChildren(node('p', 'form-error', error.message)); }
    finally { $('#test-provider').disabled = false; }
  });
  function formatDiagnosticExpiration(value) {
    if (!value) return 'non fournie';
    const date = new Date(value); if (Number.isNaN(date.getTime())) return 'non fournie';
    return new Intl.DateTimeFormat('fr-FR', { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false }).format(date).replace(' à ', ' ');
  }
  function renderDiagnostic(data, onboarding = false) {
    const grid = node('div', 'diagnostic-grid'); if (!data) return grid;
    const authenticated = data.authenticated ?? data.accountOk;
    const values = [[data.type, 'Type fournisseur'], [authenticated ? 'Compte valide' : 'Compte rejeté', 'Authentification']];
    if (!authenticated && data.message) values.push([data.message, 'Diagnostic']);
    if (data.accountStatus && !/^\d+$/.test(String(data.accountStatus))) values.push([data.accountStatus, 'Statut fournisseur']);
    values.push([formatDiagnosticExpiration(data.expiresAt), 'Expiration']);
    if (data.host || data.api) values.push([[data.host, data.api].filter(Boolean).join(' · '), 'Endpoint / API']);
    if (data.maskedIdentity) values.push([data.maskedIdentity, data.identityLabel || 'Compte']);
    if (data.credentialState) values.push([data.credentialState, 'Mot de passe']);
    if (data.activeConnections !== null && data.activeConnections !== undefined) values.push([data.activeConnections, 'Connexions actives']);
    if (data.maxConnections !== null && data.maxConnections !== undefined) values.push([data.maxConnections, 'Connexions maximales']);
    if (data.allowedOutputFormats?.length) values.push([data.allowedOutputFormats.join(', '), 'Formats autorisés']);
    const xtreamOnboarding = onboarding && String(data.type).toLowerCase() === 'xtream';
    if (!xtreamOnboarding) values.push([data.live ?? data.liveCategories, 'Catégories Live'], [data.vod ?? data.vodCategories, 'Catégories Films'], [data.series ?? data.seriesCategories, 'Catégories Séries']);
    values.push([`${data.latencyMs} ms`, 'Latence']);
    values.forEach(([value, label]) => { const cell = node('span'); cell.append(node('strong', '', String(value)), document.createTextNode(` ${label}`)); grid.append(cell); });
    if (xtreamOnboarding) grid.append(node('span', 'muted', 'Catalogue local après activation'));
    if (data.protocolDetails) grid.append(node('span', 'muted', data.protocolDetails));
    return grid;
  }
  $$('.onboarding-tabs button').forEach(value => value.addEventListener('click', () => { $$('.onboarding-tabs button').forEach(buttonValue => buttonValue.classList.remove('active')); value.classList.add('active'); state.onboarding.catalog = value.dataset.onboardingCatalog; renderOnboardingCategories(); }));
  $('#onboarding-filter').addEventListener('input', renderOnboardingCategories);
  $('#onboarding-all').addEventListener('click', () => { const model = state.onboarding.policies[state.onboarding.catalog]; model.selected = new Set(model.items.map(item => item.remoteId)); model.mode = 'all'; renderOnboardingCategories(); });
  $('#onboarding-none').addEventListener('click', () => { const model = state.onboarding.policies[state.onboarding.catalog]; model.selected.clear(); model.mode = 'none'; renderOnboardingCategories(); });
  function renderOnboardingCategories() {
    const model = state.onboarding.policies[state.onboarding.catalog], container = $('#onboarding-categories');
    const xtream = String(state.onboarding.draft?.detectedType || '').toLowerCase() === 'xtream';
    $('#onboarding-category-selection').classList.toggle('hidden', xtream);
    $('#onboarding-category-info').classList.toggle('hidden', !xtream);
    $('#onboarding-category-info').textContent = xtream ? 'Connexion validée. Live, Films et Séries seront synchronisés localement lors de l’activation.' : '';
    container.replaceChildren(); $('#onboarding-count').textContent = '';
    if (xtream || !model) return;
    renderPolicyRows(container, model, $('#onboarding-filter').value);
    $('#onboarding-count').textContent = `${model.selected.size} / ${model.items.length}`;
  }
  onboardingForm.addEventListener('submit', async event => { event.preventDefault(); const policies = {}; for (const catalog of ['live', 'vod', 'series']) { const model = state.onboarding.policies[catalog]; policies[catalog] = { mode: model.mode, selectedIds: [...model.selected] }; } try { await rpc('providers.save', { draftId: state.onboarding.draft.id, enable: onboardingForm.elements.enableProvider.checked, policies }); onboardingDialog.close(); await refreshApp(true); toast('Fournisseur enregistré.'); } catch (error) { $('#onboarding-error').textContent = error.message; } });

  function renderProviderSettings() {
    const list = $('#settings-provider-list'); list.replaceChildren(); if (!state.app) return;
    if (!state.app.providers.length) { list.append(node('p', 'muted', 'Aucun fournisseur configuré.')); return; }
    state.app.providers.forEach(provider => { const card = node('article', 'provider-card'); const copy = node('div'); copy.append(node('strong', '', provider.name), node('p', '', `${provider.type.toUpperCase()} · ${provider.serverUrl}`)); const actions = node('div', 'provider-actions'); actions.append(button('Diagnostic', 'secondary', () => diagnose(provider)), button('Modifier', 'secondary', () => openEdit(provider)), button(provider.enabled ? 'Désactiver' : 'Activer', 'secondary', () => toggleProvider(provider)), button('Supprimer', 'danger-button', () => deleteProvider(provider))); card.append(copy, actions); list.append(card); });
  }
  async function diagnose(provider) { const panel = $('#diagnostic-panel'); panel.classList.remove('hidden'); panel.replaceChildren(node('p', 'muted', 'Diagnostic en cours…')); try { const result = await rpc('providers.diagnose', { providerKey: provider.key }, 'diagnostic'); panel.replaceChildren(node('h3', '', `Diagnostic · ${provider.name}`), renderDiagnostic(result)); } catch (error) { panel.replaceChildren(node('p', 'form-error', error.message)); } }
  async function toggleProvider(provider) { try { await rpc('providers.setEnabled', { providerKey: provider.key, enabled: !provider.enabled }, 'provider-action'); await refreshApp(); toast(provider.enabled ? 'Fournisseur désactivé.' : 'Fournisseur activé.'); } catch (error) { toast(error.message, true); } }
  function openEdit(provider) { const form = $('#edit-form'); form.reset(); form.elements.providerKey.value = provider.key; form.elements.name.value = provider.name; form.elements.serverUrl.value = provider.serverUrl; $('#edit-xtream').classList.toggle('hidden', provider.type !== 'xtream'); $('#edit-stalker').classList.toggle('hidden', provider.type !== 'stalker'); $('#edit-error').textContent = ''; $('#edit-dialog').showModal(); }
  $('#edit-form').addEventListener('submit', async event => { event.preventDefault(); const form = event.currentTarget; try { await rpc('providers.update', { providerKey: form.elements.providerKey.value, name: form.elements.name.value, serverUrl: form.elements.serverUrl.value, username: form.elements.username.value || null, password: form.elements.password.value || null, macAddress: form.elements.macAddress.value || null }, 'provider-action'); form.elements.password.value = ''; form.elements.macAddress.value = ''; $('#edit-dialog').close(); await refreshApp(); toast('Fournisseur mis à jour.'); } catch (error) { form.elements.password.value = ''; form.elements.macAddress.value = ''; $('#edit-error').textContent = error.message; } });
  async function deleteProvider(provider) { if (!window.confirm(`Supprimer « ${provider.name} » ? Le secret sera supprimé définitivement.`)) return; const remove = window.confirm('Supprimer également l’index local ?'); try { await rpc('providers.delete', { providerKey: provider.key, confirmed: true, removeLocalData: remove }, 'provider-action'); await refreshApp(true); toast('Fournisseur supprimé.'); } catch (error) { toast(error.message, true); } }

  $$('.settings-tabs button').forEach(value => value.addEventListener('click', () => { $$('.settings-tabs button,.settings-pane').forEach(item => item.classList.remove('active')); value.classList.add('active'); $(`#settings-${value.dataset.settings}`).classList.add('active'); if (value.dataset.settings === 'categories') loadPolicy(); if (value.dataset.settings === 'index') pollIndex(); }));
  $$('.catalog-tabs [data-policy-catalog]').forEach(value => value.addEventListener('click', () => { $$('.catalog-tabs [data-policy-catalog]').forEach(item => item.classList.remove('active')); value.classList.add('active'); state.policyCatalog = value.dataset.policyCatalog; loadPolicy(); }));
  async function loadPolicy() { const container = $('#policy-list'); container.replaceChildren(); if (!state.activeProviderKey) { container.append(node('p', 'muted', 'Aucun fournisseur actif.')); return; } try { const data = await rpc('categories.list', { providerKey: state.activeProviderKey, catalogType: state.policyCatalog }, 'policy'); state.policy = { items: data.categories, selected: new Set(data.categories.filter(item => item.selected).map(item => item.id)), mode: data.mode }; renderCurrentPolicy(); } catch (error) { if (!isAbort(error)) container.append(node('p', 'form-error', error.message)); } }
  function renderPolicyRows(container, model, filter) { const query = filter.trim().toLocaleLowerCase(); const visible = model.items.filter(item => !query || (item.name || '').toLocaleLowerCase().includes(query)); visible.slice(0, 200).forEach(item => { const row = node('label', `policy-item${item.present === false ? ' missing' : ''}`); const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; const id = item.id ?? item.remoteId; checkbox.checked = model.selected.has(id); checkbox.addEventListener('change', () => { checkbox.checked ? model.selected.add(id) : model.selected.delete(id); model.mode = 'custom'; if (container === $('#policy-list')) updatePolicyCount(); else renderOnboardingCategories(); }); row.append(checkbox, node('span', '', decodeHtmlEntities(item.name))); if (item.present === false) row.append(node('small', '', 'Disparue')); container.append(row); }); if (visible.length > 200) container.append(node('p', 'hint', `${visible.length - 200} autres catégories — utilisez la recherche pour les afficher.`)); }
  function renderCurrentPolicy() { const container = $('#policy-list'); container.replaceChildren(); if (!state.policy) return; renderPolicyRows(container, state.policy, $('#policy-filter').value); updatePolicyCount(); }
  function updatePolicyCount() { $('#policy-count').textContent = state.policy ? `${state.policy.selected.size} / ${state.policy.items.length}` : ''; }
  $('#policy-filter').addEventListener('input', renderCurrentPolicy); $('#policy-all').addEventListener('click', () => { if (!state.policy) return; state.policy.selected = new Set(state.policy.items.map(item => item.id)); state.policy.mode = 'all'; renderCurrentPolicy(); }); $('#policy-none').addEventListener('click', () => { if (!state.policy) return; state.policy.selected.clear(); state.policy.mode = 'none'; renderCurrentPolicy(); });
  $('#save-policy').addEventListener('click', async () => { if (!state.policy || !state.activeProviderKey) return; try { await rpc('categories.save', { providerKey: state.activeProviderKey, catalogType: state.policyCatalog, mode: state.policy.mode, selectedIds: [...state.policy.selected] }); state.catalogs.clear(); toast('Sélection enregistrée. Index à synchroniser.'); } catch (error) { toast(error.message, true); } });
  $('#sync-categories').addEventListener('click', async () => { if (!state.activeProviderKey) return; try { await rpc('providers.syncCategories', { providerKey: state.activeProviderKey }, 'sync'); await loadPolicy(); toast('Catégories actualisées.'); } catch (error) { if (!isAbort(error)) toast(error.message, true); } });

  async function queueIndex() { if (!state.activeProviderKey) return toast('Aucun fournisseur actif.', true); try { await rpc('index.queue', { providerKey: state.activeProviderKey }); toast('Index placé en file d’attente.'); pollIndex(); } catch (error) { toast(error.message, true); } }
  $('#build-index').addEventListener('click', queueIndex); $('#home-index').addEventListener('click', queueIndex);
  async function pollIndex() {
    if (state.indexTimer) window.clearTimeout(state.indexTimer);
    const providerKey = state.activeProviderKey; if (!providerKey) return;
    try {
      const status = await rpc('index.status', { providerKey });
      if (state.activeProviderKey !== providerKey) return;
      renderIndexStatus(status.job, $('#index-status'), status.dirty); renderIndexStatus(status.job, $('#home-index-status'), status.dirty);
      if (status.job && ['queued', 'running'].includes(status.job.status)) state.indexTimer = window.setTimeout(pollIndex, 1200);
      else if (status.job?.status === 'completed' && completedIndex !== `${providerKey}:${status.job.id}`) {
        completedIndex = `${providerKey}:${status.job.id}`;
        if (state.page === 'home') {
          const home = await rpc('home.content', { providerKey }, 'home-content');
          if (state.activeProviderKey === providerKey && state.page === 'home') renderHomeMedia(home);
        }
        else if (state.page === 'search' && state.search.query) await runSearch();
      }
    } catch (error) { $('#index-status').textContent = error.message; }
  }
  function renderIndexStatus(job, target, dirty = false) {
    if (target.id === 'home-index-status') { renderHomeIndexStatus(job, target, dirty); return; }
    target.replaceChildren();
    if (!job) { target.textContent = 'Pas construit'; return; }
    const labels = { queued: 'En attente', running: 'En cours', completed: dirty ? 'À synchroniser' : 'Prêt', failed: 'Échec', interrupted: 'Interrompu' };
    target.append(node('strong', '', labels[job.status] || job.status), node('p', 'muted', job.label || ''));
    if (job.total > 0) {
      const track = node('div', 'progress-track'); const fill = node('span');
      fill.style.width = `${Math.min(100, Math.round(job.current * 100 / job.total))}%`;
      track.append(fill);
      target.append(track, node('p', '', `${job.current.toLocaleString()} / ${job.total.toLocaleString()} · Films ${job.vodItems.toLocaleString()} · Séries ${job.seriesItems.toLocaleString()}`));
    }
    if (job.error) target.append(node('p', 'form-error', job.error));
  }
  function renderHomeIndexStatus(job, target, dirty) {
    const badge = $('#home-index-state'), rebuild = $('#home-index');
    target.replaceChildren();
    if (!job) {
      badge.className = 'status neutral'; badge.textContent = 'Pas construit';
      rebuild.textContent = 'Construire l’index';
      target.append(node('p', 'muted', 'Aucun index local disponible.'));
      return;
    }

    const labels = { queued: 'En attente', running: 'En cours', completed: dirty ? 'À synchroniser' : 'Prêt', failed: 'Échec', interrupted: 'Interrompu' };
    const tones = { queued: 'warning', running: 'warning', completed: dirty ? 'warning' : 'success', failed: 'error', interrupted: 'warning' };
    badge.className = `status ${tones[job.status] || 'neutral'}`;
    badge.textContent = labels[job.status] || job.status;
    rebuild.textContent = 'Reconstruire l’index';

    const current = Number(job.current || 0), total = Number(job.total || 0);
    const vodItems = Number(job.vodItems || 0), seriesItems = Number(job.seriesItems || 0);
    target.append(node('strong', 'home-index-count', `${current.toLocaleString()} contenus`));
    target.append(node('p', 'muted home-index-breakdown', `${vodItems.toLocaleString()} films · ${seriesItems.toLocaleString()} séries`));
    if (total > 0) {
      const track = node('div', 'progress-track'); const fill = node('span');
      fill.style.width = `${Math.min(100, Math.round(current * 100 / total))}%`;
      track.append(fill); target.append(track);
    }
    if (job.status !== 'completed' && job.label) target.append(node('p', 'muted home-index-label', job.label));
    if (job.error) target.append(node('p', 'form-error', job.error));
  }

  async function loadPreferences() { try { const preferences = await rpc('settings.get'); const form = $('#preferences-form'); for (const key of ['interfaceLanguage', 'theme', 'audioLanguage', 'secondaryAudioLanguage', 'subtitleLanguage']) if (form.elements[key]) form.elements[key].value = preferences[key] || 'auto'; form.elements.automaticForcedSubtitles.checked = preferences.automaticForcedSubtitles; applyThemePreference(preferences.theme); } catch { } }
  $('#preferences-form').addEventListener('submit', async event => { event.preventDefault(); const form = event.currentTarget; const preferences = { activeProviderKey: state.activeProviderKey, interfaceLanguage: form.elements.interfaceLanguage.value, theme: form.elements.theme.value, audioLanguage: form.elements.audioLanguage.value || 'auto', secondaryAudioLanguage: form.elements.secondaryAudioLanguage.value || 'auto', subtitleLanguage: form.elements.subtitleLanguage.value || 'auto', automaticForcedSubtitles: form.elements.automaticForcedSubtitles.checked }; try { await rpc('settings.save', preferences); applyThemePreference(preferences.theme); toast('Préférences enregistrées.'); } catch (error) { toast(error.message, true); } });

  $$('.close-dialog').forEach(value => value.addEventListener('click', () => value.closest('dialog').close()));
  ensureLiveRefreshControls();
  refreshApp().then(loadPreferences).catch(error => toast(error.message, true));
})();
