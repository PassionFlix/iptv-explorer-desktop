(() => {
  'use strict';

  const pending = new Map();
  let sequence = 0;
  let currentProvider = '';
  let currentRequestId = null;
  let currentItems = [];
  let categories = [];
  let selectedCategoryId = '';
  let renderGeneration = 0;
  const MAX_RENDERED_CHANNELS = 800;

  function rpc(method, params = {}) {
    const id = `live-ui-${Date.now()}-${++sequence}`;
    const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    window.chrome.webview.postMessage({ id, method, params });
    return { id, promise };
  }

  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const request = pending.get(response?.id);
    if (!request) return;
    pending.delete(response.id);
    response.ok ? request.resolve(response.result) : request.reject(new Error(response.error || 'Opération impossible'));
  });

  function cancelCurrentRequest() {
    if (!currentRequestId) return;
    const request = pending.get(currentRequestId);
    pending.delete(currentRequestId);
    request?.reject(new DOMException('Superseded', 'AbortError'));
    window.chrome.webview.postMessage({
      id: `live-cancel-${Date.now()}-${++sequence}`,
      method: 'app.cancel',
      params: { requestId: currentRequestId }
    });
    currentRequestId = null;
  }

  function activeProviderKey() {
    return document.querySelector('#provider-select')?.value || '';
  }

  function setPageActive() {
    window.iptvHome?.setActive(false);
    document.querySelectorAll('.nav,.page').forEach(element => element.classList.remove('active'));
    document.querySelector('.nav[data-page="live"]')?.classList.add('active');
    document.querySelector('#live')?.classList.add('active');
    const title = document.querySelector('#page-title');
    if (title) title.textContent = 'Live';
  }

  function enhanceCard(card) {
    if (!card || card.dataset.liveCompactWired === '1') return;
    const play = card.querySelector('button.play-small');
    if (!play) return;
    card.dataset.liveCompactWired = '1';
    card.classList.add('live-row');
    card.tabIndex = 0;
    card.setAttribute('role', 'button');
    const title = card.querySelector('strong')?.textContent?.trim() || 'chaîne';
    card.setAttribute('aria-label', `Lire ${title}`);

    card.addEventListener('click', event => {
      if (event.target.closest('button')) return;
      play.click();
    });
    card.addEventListener('keydown', event => {
      if (event.target.closest('button')) return;
      if (event.key !== 'Enter' && event.key !== ' ') return;
      event.preventDefault();
      play.click();
    });
  }

  function createChannelCard(item, providerKey, categoryId) {
    const card = document.createElement('article');
    card.className = 'media-card live-card';

    const logo = document.createElement('div');
    logo.className = 'channel-logo';
    const title = String(item.title || 'Chaîne');
    if (item.imageUrl) {
      const image = document.createElement('img');
      image.loading = 'lazy';
      image.referrerPolicy = 'no-referrer';
      image.alt = '';
      image.src = item.imageUrl;
      image.addEventListener('error', () => {
        image.remove();
        logo.textContent = title.slice(0, 1).toUpperCase();
      }, { once: true });
      logo.append(image);
    } else {
      logo.textContent = title.slice(0, 1).toUpperCase();
    }

    const copy = document.createElement('strong');
    copy.textContent = `#${item.id} · ${title}`;

    const play = document.createElement('button');
    play.type = 'button';
    play.className = 'play-small';
    play.textContent = 'Lire';
    play.addEventListener('click', event => {
      event.stopPropagation();
      openLive({
        providerKey,
        mediaType: 'live',
        mediaId: String(item.id),
        extension: item.extension || null,
        categoryId: item.categoryId || categoryId || null,
        title
      });
    });

    card.append(logo, copy, play);
    enhanceCard(card);
    return card;
  }

  function numericLiveSort(left, right) {
    const leftId = /^\d+$/.test(String(left.id ?? '')) ? Number(left.id) : Number.MAX_SAFE_INTEGER;
    const rightId = /^\d+$/.test(String(right.id ?? '')) ? Number(right.id) : Number.MAX_SAFE_INTEGER;
    if (leftId !== rightId) return leftId - rightId;
    return String(left.title || '').localeCompare(String(right.title || ''), 'fr', { numeric: true, sensitivity: 'base' });
  }

  function renderChannels() {
    const section = document.querySelector('#live');
    const grid = section?.querySelector('.catalog-grid');
    const empty = section?.querySelector('.empty-catalog');
    const filter = section?.querySelector('.live-channel-filter');
    const count = section?.querySelector('.live-channel-count');
    if (!grid || !empty) return;

    const query = (filter?.value || '').trim().toLocaleLowerCase();
    const filtered = currentItems
      .filter(item => !query || String(item.title || '').toLocaleLowerCase().includes(query) || String(item.id || '').includes(query))
      .sort(numericLiveSort);
    const visible = filtered.slice(0, MAX_RENDERED_CHANNELS);
    const generation = ++renderGeneration;
    grid.replaceChildren();

    if (count) {
      count.textContent = filtered.length > MAX_RENDERED_CHANNELS
        ? `${visible.length.toLocaleString('fr-CA')} affichées sur ${filtered.length.toLocaleString('fr-CA')}`
        : `${filtered.length.toLocaleString('fr-CA')} chaîne${filtered.length > 1 ? 's' : ''}`;
    }

    if (!filtered.length) {
      empty.textContent = currentItems.length ? 'Aucune chaîne ne correspond au filtre.' : 'Aucune chaîne dans cette catégorie.';
      empty.classList.remove('hidden');
      return;
    }
    empty.classList.add('hidden');

    let index = 0;
    const providerKey = activeProviderKey();
    const categoryId = selectedCategoryId;
    const appendChunk = () => {
      if (generation !== renderGeneration) return;
      const fragment = document.createDocumentFragment();
      const end = Math.min(index + 100, visible.length);
      for (; index < end; index += 1) fragment.append(createChannelCard(visible[index], providerKey, categoryId));
      grid.append(fragment);
      if (index < visible.length) window.requestAnimationFrame(appendChunk);
    };
    appendChunk();
  }

  async function openLive(reference) {
    const { promise } = rpc('player.open', reference);
    try { await promise; }
    catch (error) {
      if (error?.name === 'AbortError') return;
      showLiveError(error.message || 'Impossible de lancer la chaîne.');
    }
  }

  function showLiveError(message) {
    const empty = document.querySelector('#live .empty-catalog');
    if (!empty) return;
    empty.textContent = message;
    empty.classList.remove('hidden');
  }

  function renderCategorySelector() {
    const section = document.querySelector('#live');
    const select = section?.querySelector('.category-select');
    if (!select) return;
    const placeholder = document.createElement('option');
    placeholder.value = '';
    placeholder.textContent = 'Sélectionner une catégorie';
    select.replaceChildren(placeholder);
    for (const category of categories) {
      const option = document.createElement('option');
      option.value = String(category.id);
      option.textContent = category.name || category.id;
      option.selected = String(category.id) === selectedCategoryId;
      select.append(option);
    }
  }

  function renderCategoryRail() {
    const section = document.querySelector('#live');
    const list = section?.querySelector('.category-list');
    const filter = section?.querySelector('.category-filter');
    if (!list) return;
    const query = (filter?.value || '').trim().toLocaleLowerCase();
    list.replaceChildren();
    categories
      .filter(category => !query || String(category.name || '').toLocaleLowerCase().includes(query))
      .slice(0, 200)
      .forEach(category => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = `category-button${String(category.id) === selectedCategoryId ? ' active' : ''}`;
        button.textContent = category.name || category.id;
        button.addEventListener('click', () => selectCategory(String(category.id)));
        list.append(button);
      });
  }

  function selectCategory(categoryId) {
    if (!categoryId) return;
    selectedCategoryId = categoryId;
    renderCategorySelector();
    renderCategoryRail();
    const channelFilter = document.querySelector('#live .live-channel-filter');
    if (channelFilter) channelFilter.value = '';
    loadCategory(categoryId);
  }

  async function loadCategory(categoryId) {
    const providerKey = activeProviderKey();
    const section = document.querySelector('#live');
    const loading = section?.querySelector('.loading-state');
    const empty = section?.querySelector('.empty-catalog');
    if (!providerKey || !categoryId || !loading || !empty) return;

    cancelCurrentRequest();
    currentItems = [];
    renderGeneration += 1;
    section.querySelector('.catalog-grid')?.replaceChildren();
    empty.classList.add('hidden');
    loading.classList.remove('hidden');

    const request = rpc('catalog.live', { providerKey, categoryId });
    currentRequestId = request.id;
    try {
      const result = await request.promise;
      if (currentRequestId !== request.id || activeProviderKey() !== providerKey || selectedCategoryId !== categoryId) return;
      currentItems = Array.isArray(result) ? result : [];
      renderChannels();
    } catch (error) {
      if (error?.name !== 'AbortError') showLiveError(error.message || 'Impossible de charger les chaînes.');
    } finally {
      if (currentRequestId === request.id) currentRequestId = null;
      loading.classList.add('hidden');
    }
  }

  async function loadCategories(force = false) {
    const providerKey = activeProviderKey();
    const section = document.querySelector('#live');
    const empty = section?.querySelector('.empty-catalog');
    if (!section || !empty) return;

    if (!providerKey) {
      currentProvider = '';
      currentItems = [];
      categories = [];
      selectedCategoryId = '';
      renderCategorySelector();
      renderCategoryRail();
      empty.textContent = 'Aucun fournisseur actif.';
      empty.classList.remove('hidden');
      return;
    }

    if (!force && currentProvider === providerKey && categories.length) return;
    currentProvider = providerKey;
    currentItems = [];
    categories = [];
    selectedCategoryId = '';
    renderGeneration += 1;
    section.querySelector('.catalog-grid')?.replaceChildren();
    renderCategorySelector();
    renderCategoryRail();
    empty.textContent = 'Chargement des catégories…';
    empty.classList.remove('hidden');

    const { promise } = rpc('categories.list', { providerKey, catalogType: 'live' });
    try {
      const data = await promise;
      if (activeProviderKey() !== providerKey) return;
      categories = Array.isArray(data?.categories)
        ? data.categories.filter(category => category.selected && category.present)
        : [];
      renderCategorySelector();
      renderCategoryRail();
      empty.textContent = categories.length
        ? 'Sélectionnez une catégorie Live.'
        : 'Aucune catégorie Live sélectionnée.';
    } catch (error) {
      showLiveError(error.message || 'Impossible de charger les catégories Live.');
    }
  }

  function prepareLiveLayout() {
    const section = document.querySelector('#live');
    const rail = section?.querySelector('.category-rail');
    const content = section?.querySelector('.catalog-content');
    const originalSelect = rail?.querySelector('.category-select');
    const originalCategoryFilter = rail?.querySelector('.category-filter');
    const grid = content?.querySelector('.catalog-grid');
    if (!section || !rail || !content || !originalSelect || !originalCategoryFilter || !grid) return false;

    section.classList.add('live-browser-owned');

    // Clone these two controls to remove the legacy app.js listeners. Live navigation is
    // intentionally owned here so no category is auto-loaded on page entry.
    const select = originalSelect.cloneNode(true);
    originalSelect.replaceWith(select);
    const categoryFilter = originalCategoryFilter.cloneNode(true);
    originalCategoryFilter.replaceWith(categoryFilter);

    select.addEventListener('change', () => {
      if (select.value) selectCategory(select.value);
    });
    categoryFilter.placeholder = 'Filtrer les catégories';
    categoryFilter.addEventListener('input', renderCategoryRail);

    if (!content.querySelector('.live-channel-tools')) {
      const channelTools = document.createElement('div');
      channelTools.className = 'live-channel-tools';
      const head = document.createElement('div');
      head.className = 'live-channel-tools-head';
      head.innerHTML = '<strong>Chaînes</strong><span class="live-channel-count">0 chaîne</span>';
      const channelFilter = document.createElement('input');
      channelFilter.className = 'live-channel-filter';
      channelFilter.type = 'search';
      channelFilter.placeholder = 'Filtrer les chaînes';
      let filterTimer = 0;
      channelFilter.addEventListener('input', () => {
        window.clearTimeout(filterTimer);
        filterTimer = window.setTimeout(renderChannels, 80);
      });
      channelTools.append(head, channelFilter);
      const refreshPanel = content.querySelector('#live-refresh-panel');
      if (refreshPanel?.nextSibling) content.insertBefore(channelTools, refreshPanel.nextSibling);
      else if (refreshPanel) content.append(channelTools);
      else content.prepend(channelTools);
    }

    return true;
  }

  let layoutPrepared = false;
  function prepareLiveLayoutOnce() {
    if (layoutPrepared) return true;
    layoutPrepared = prepareLiveLayout();
    return layoutPrepared;
  }

  async function activateLive() {
    setPageActive();
    if (!prepareLiveLayoutOnce()) return;
    await loadCategories(false);
  }

  function initialize() {
    const style = document.createElement('link');
    style.rel = 'stylesheet';
    style.href = 'live-compact.css';
    document.head.append(style);
    prepareLiveLayoutOnce();

    document.addEventListener('click', event => {
      const trigger = event.target.closest?.('.nav[data-page="live"], [data-home-page="live"]');
      if (!trigger) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      activateLive();
    }, true);

    document.querySelector('#provider-select')?.addEventListener('change', () => {
      cancelCurrentRequest();
      currentProvider = '';
      currentItems = [];
      categories = [];
      selectedCategoryId = '';
      renderGeneration += 1;
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
