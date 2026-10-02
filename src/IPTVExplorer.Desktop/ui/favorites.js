(() => {
  'use strict';

  let currentVod = null;
  let currentSeries = null;
  let cacheProvider = null;
  let activeTab = 'live';
  const favorites = new Map();

  function rpc(method, params = {}) {
    return window.iptvRpc(method, params);
  }

  function key(providerKey, mediaType, mediaId) {
    return `${providerKey}\u0000${mediaType}\u0000${mediaId}`;
  }

  function providerKey() {
    return document.querySelector('#provider-select')?.value || '';
  }

  function showToast(message, error = false) {
    const toast = document.querySelector('#toast');
    if (!toast) return;
    toast.textContent = message;
    toast.style.background = error ? '#8c3242' : '#202c40';
    toast.classList.add('show');
    window.setTimeout(() => toast.classList.remove('show'), 3200);
  }

  function actionButton(label, className) {
    const value = document.createElement('button');
    value.type = 'button';
    value.className = className;
    value.textContent = label;
    return value;
  }

  function favoriteButton(reference, compact = false) {
    const button = actionButton('☆', compact ? 'favorite-toggle compact' : 'secondary favorite-toggle');
    button.dataset.favoriteKey = key(reference.providerKey, reference.mediaType, reference.mediaId);
    button.setAttribute('aria-label', `Ajouter ${reference.title || 'ce contenu'} aux favoris`);
    button.addEventListener('click', async event => {
      event.stopPropagation();
      if (button.disabled) return;
      button.disabled = true;
      const favoriteKey = key(reference.providerKey, reference.mediaType, reference.mediaId);
      const remove = favorites.has(favoriteKey);
      try {
        if (remove) {
          await rpc('favorites.remove', reference);
          favorites.delete(favoriteKey);
          showToast('Retiré des favoris.');
        } else {
          await rpc('favorites.set', reference);
          favorites.set(favoriteKey, { ...reference });
          showToast('Ajouté aux favoris.');
        }
        syncFavoriteButtons();
        if (document.querySelector('#favorites')?.classList.contains('active')) renderFavoritesPage();
      } catch (error) {
        showToast(error.message, true);
      } finally {
        button.disabled = false;
      }
    });
    updateFavoriteButton(button);
    return button;
  }

  function updateFavoriteButton(button) {
    const selected = favorites.has(button.dataset.favoriteKey || '');
    button.textContent = selected ? '★' : '☆';
    button.classList.toggle('is-favorite', selected);
    button.setAttribute('aria-pressed', String(selected));
    button.title = selected ? 'Retirer des favoris' : 'Ajouter aux favoris';
  }

  function syncFavoriteButtons() {
    document.querySelectorAll('.favorite-toggle').forEach(updateFavoriteButton);
  }

  async function refreshFavoriteCache(force = false) {
    const selectedProvider = providerKey();
    if (!selectedProvider) {
      cacheProvider = null;
      favorites.clear();
      syncFavoriteButtons();
      return;
    }
    if (!force && cacheProvider === selectedProvider) return;
    const requestedProvider = selectedProvider;
    try {
      const items = await rpc('favorites.list', { providerKey: requestedProvider });
      if (providerKey() !== requestedProvider) return;
      favorites.clear();
      for (const item of items || []) favorites.set(key(item.providerKey, item.mediaType, item.mediaId), item);
      cacheProvider = requestedProvider;
      syncFavoriteButtons();
      decorateLiveFavorites();
      decorateDetailFavorites();
      if (document.querySelector('#favorites')?.classList.contains('active')) renderFavoritesPage();
    } catch (error) {
      cacheProvider = null;
      showToast(error.message, true);
    }
  }

  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const result = response?.ok ? response.result : null;
    if (!result || typeof result !== 'object' || !('id' in result) || !('title' in result)) return;
    const selectedProvider = providerKey();
    if ('seasons' in result) {
      currentSeries = {
        providerKey: selectedProvider,
        mediaType: 'series',
        mediaId: String(result.id ?? ''),
        title: result.title || 'Série',
        imageUrl: result.poster || null
      };
      currentVod = null;
      queueMicrotask(decorateDetailFavorites);
    } else if ('duration' in result) {
      currentVod = {
        providerKey: selectedProvider,
        mediaType: 'vod',
        mediaId: String(result.id ?? ''),
        title: result.title || 'Film',
        imageUrl: result.poster || null,
        extension: result.extension || null
      };
      currentSeries = null;
      queueMicrotask(decorateDetailFavorites);
    }
  });

  function decorateDetailFavorites() {
    const content = document.querySelector('#detail-content');
    const layout = content?.querySelector('.detail-layout');
    const copy = layout?.children?.[1];
    if (!copy) return;

    if (currentVod?.providerKey && currentVod.mediaId) {
      const actions = copy.querySelector('.desktop-media-actions');
      if (actions && !actions.querySelector('.favorite-detail-toggle')) {
        const button = favoriteButton(currentVod);
        button.classList.add('favorite-detail-toggle');
        actions.append(button);
      }
    }

    if (currentSeries?.providerKey && currentSeries.mediaId && !copy.querySelector('.favorite-series-actions')) {
      const actions = document.createElement('div');
      actions.className = 'desktop-media-actions favorite-series-actions';
      const button = favoriteButton(currentSeries);
      button.classList.add('favorite-detail-toggle');
      actions.append(button);
      copy.append(actions);
    }
    syncFavoriteButtons();
  }

  function parseLiveCard(card) {
    const selectedProvider = providerKey();
    const mediaId = card.dataset.mediaId || '';
    if (!selectedProvider || !mediaId) return null;
    return {
      providerKey: selectedProvider,
      mediaType: 'live',
      mediaId,
      title: card.dataset.title || card.querySelector('strong')?.textContent || 'Chaîne Live',
      imageUrl: card.querySelector('.channel-logo img')?.src || null,
      categoryId: card.dataset.categoryId || document.querySelector('#live .category-select')?.value || null
    };
  }

  function decorateLiveFavorites() {
    document.querySelectorAll('#live .live-card').forEach(card => {
      if (card.querySelector('.favorite-live-toggle')) return;
      const reference = parseLiveCard(card);
      if (!reference) return;
      const star = favoriteButton(reference, true);
      star.classList.add('favorite-live-toggle');
      card.append(star);
    });
    syncFavoriteButtons();
  }

  function installFavoritesPage() {
    if (document.querySelector('#favorites')) return;
    const nav = document.querySelector('.sidebar nav');
    const main = document.querySelector('main');
    if (!nav || !main) return;

    const navButton = actionButton('Favoris', 'nav favorite-nav');
    navButton.innerHTML = '<span>★</span>Favoris';
    navButton.dataset.page = 'favorites';
    navButton.addEventListener('click', async () => {
      window.iptvNavigate('favorites');
      await refreshFavoriteCache(true);
      renderFavoritesPage();
    });
    nav.append(navButton);

    const page = document.createElement('section');
    page.id = 'favorites';
    page.className = 'page favorites-page';
    page.innerHTML = `
      <div class="favorites-toolbar">
        <div><p class="eyebrow">BIBLIOTHÈQUE LOCALE</p><h2>Mes favoris</h2><p class="muted">Chaînes, films et séries enregistrés uniquement sur ce PC.</p></div>
        <div class="favorites-tabs" role="tablist" aria-label="Type de favoris">
          <button type="button" class="secondary active" data-favorite-tab="live">Live</button>
          <button type="button" class="secondary" data-favorite-tab="vod">Films</button>
          <button type="button" class="secondary" data-favorite-tab="series">Séries</button>
        </div>
      </div>
      <div id="favorite-items" class="favorite-grid"></div>
      <p id="favorite-empty" class="empty-catalog hidden">Aucun favori dans cette section.</p>`;
    main.append(page);

    page.querySelectorAll('[data-favorite-tab]').forEach(button => button.addEventListener('click', () => {
      activeTab = button.dataset.favoriteTab;
      page.querySelectorAll('[data-favorite-tab]').forEach(value => value.classList.toggle('active', value === button));
      renderFavoritesPage();
    }));
  }

  function poster(item) {
    const holder = document.createElement('div');
    holder.className = 'favorite-poster';
    if (item.imageUrl) {
      const image = document.createElement('img');
      image.loading = 'lazy';
      image.referrerPolicy = 'no-referrer';
      image.alt = '';
      image.src = item.imageUrl;
      image.addEventListener('error', () => { image.remove(); holder.textContent = (item.title || '?').slice(0, 1).toUpperCase(); }, { once: true });
      holder.append(image);
    } else holder.textContent = (item.title || '?').slice(0, 1).toUpperCase();
    return holder;
  }

  function renderFavoritesPage() {
    const grid = document.querySelector('#favorite-items');
    const empty = document.querySelector('#favorite-empty');
    if (!grid || !empty) return;
    grid.replaceChildren();
    const selectedProvider = providerKey();
    const items = [...favorites.values()].filter(item => item.providerKey === selectedProvider && item.mediaType === activeTab);
    empty.classList.toggle('hidden', items.length !== 0);
    if (!selectedProvider) {
      empty.textContent = 'Aucun fournisseur actif.';
      empty.classList.remove('hidden');
      return;
    }
    empty.textContent = 'Aucun favori dans cette section.';

    for (const item of items) {
      const card = document.createElement('article');
      card.className = 'favorite-card';
      card.append(poster(item));
      const copy = document.createElement('div');
      copy.className = 'favorite-copy';
      const title = document.createElement('strong'); title.textContent = item.title;
      const type = document.createElement('small'); type.textContent = item.mediaType === 'live' ? `Live${item.mediaId ? ` · #${item.mediaId}` : ''}` : item.mediaType === 'vod' ? 'Film' : 'Série';
      const actions = document.createElement('div'); actions.className = 'favorite-card-actions';
      if (item.mediaType === 'series') {
        const detail = actionButton('Détails', 'secondary');
        detail.addEventListener('click', () => openFavoriteSeries(item));
        actions.append(detail);
      } else {
        const play = actionButton('▶ Lire', 'primary');
        play.addEventListener('click', async () => {
          try { await rpc('player.open', { providerKey: item.providerKey, mediaType: item.mediaType, mediaId: item.mediaId, extension: item.extension, categoryId: item.categoryId, title: item.title, posterUrl: item.imageUrl }); }
          catch (error) { showToast(error.message, true); }
        });
        actions.append(play);
      }
      const star = favoriteButton(item);
      star.classList.add('favorite-card-star');
      actions.append(star);
      copy.append(title, type, actions);
      card.append(copy);
      grid.append(card);
    }
  }

  async function openFavoriteSeries(item) {
    const dialog = document.querySelector('#detail-dialog');
    const content = document.querySelector('#detail-content');
    const heading = document.querySelector('#detail-title');
    if (!dialog || !content || !heading) return;
    currentSeries = { ...item };
    heading.textContent = item.title;
    content.textContent = 'Chargement…';
    dialog.showModal();
    try {
      const detail = await rpc('catalog.series.detail', { providerKey: item.providerKey, mediaId: item.mediaId });
      currentSeries = { ...item, title: detail.title || item.title, imageUrl: detail.poster || item.imageUrl };
      renderSeriesDetail(content, detail, item.providerKey);
      decorateDetailFavorites();
    } catch (error) {
      content.textContent = error.message;
    }
  }

  function renderSeriesDetail(content, detail, selectedProvider) {
    content.replaceChildren();
    const layout = document.createElement('section'); layout.className = 'detail-layout';
    const visual = document.createElement('div'); visual.className = 'detail-poster';
    if (detail.poster) { const image = document.createElement('img'); image.src = detail.poster; image.alt = ''; image.referrerPolicy = 'no-referrer'; visual.append(image); }
    const copy = document.createElement('div');
    const metadata = document.createElement('div'); metadata.className = 'metadata';
    [detail.year, detail.genre, detail.rating ? `★ ${detail.rating}` : null].filter(Boolean).forEach(value => { const span = document.createElement('span'); span.textContent = String(value); metadata.append(span); });
    const plot = document.createElement('p'); plot.className = 'plot'; plot.textContent = detail.plot || 'Aucun résumé communiqué.';
    copy.append(metadata, plot); layout.append(visual, copy); content.append(layout);
    const seasons = document.createElement('section'); seasons.className = 'series-seasons';
    const h3 = document.createElement('h3'); h3.textContent = 'Saisons et épisodes'; seasons.append(h3);
    (detail.seasons || []).forEach((season, index) => {
      const block = document.createElement('details'); block.className = 'season'; block.open = index === 0;
      const summary = document.createElement('summary'); summary.textContent = `${season.title} · ${(season.episodes || []).length} épisode(s)`; block.append(summary);
      const list = document.createElement('div'); list.className = 'episode-list';
      (season.episodes || []).forEach(episode => {
        const row = document.createElement('div'); row.className = 'episode';
        const main = document.createElement('div'); main.className = 'episode-main';
        const label = document.createElement('span'); label.className = 'episode-info'; label.textContent = `${episode.episode ? `E${String(episode.episode).padStart(2, '0')} — ` : ''}${episode.title}`;
        const actions = document.createElement('div'); actions.className = 'episode-actions';
        const play = actionButton('Lire', 'play-small');
        play.addEventListener('click', async () => {
          try { await rpc('player.open', { providerKey: selectedProvider, mediaType: 'series', mediaId: detail.id, episodeId: episode.id, extension: episode.extension, title: episode.title, posterUrl: detail.poster, seriesTitle: detail.title, season: episode.season ?? season.number, episode: episode.episode }); }
          catch (error) { showToast(error.message, true); }
        });
        actions.append(play); main.append(label, actions); row.append(main); list.append(row);
      });
      block.append(list); seasons.append(block);
    });
    content.append(seasons);
  }

  function initialize() {
    const style = document.createElement('link');
    style.rel = 'stylesheet';
    style.href = 'favorites.css';
    document.head.append(style);
    installFavoritesPage();

    const liveGrid = document.querySelector('#live .catalog-grid');
    if (liveGrid) new MutationObserver(decorateLiveFavorites).observe(liveGrid, { childList: true, subtree: true });
    const detail = document.querySelector('#detail-content');
    if (detail) new MutationObserver(decorateDetailFavorites).observe(detail, { childList: true, subtree: true });
    document.querySelector('#detail-dialog')?.addEventListener('close', () => { currentVod = null; currentSeries = null; });
    document.querySelector('#provider-select')?.addEventListener('change', () => {
      cacheProvider = null;
      favorites.clear();
      window.setTimeout(() => refreshFavoriteCache(true), 0);
    });
    refreshFavoriteCache(true);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
