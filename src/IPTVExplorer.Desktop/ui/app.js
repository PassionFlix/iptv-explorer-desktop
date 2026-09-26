(() => {
  'use strict';
  const pending = new Map();
  const groups = new Map();
  let sequence = 0;
  const state = { app: null, activeProviderKey: null, page: 'home', catalogs: new Map(), policyCatalog: 'live', policy: null, onboarding: null, indexTimer: null, search: { query: '', pages: { vod: 1, series: 1 } } };

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
  function node(tag, className, text) { const value = document.createElement(tag); if (className) value.className = className; if (text !== undefined) value.textContent = text; return value; }
  function button(text, className, action) { const value = node('button', className, text); value.type = 'button'; value.addEventListener('click', action); return value; }
  function toast(message, error = false) { const box = $('#toast'); box.textContent = message; box.style.background = error ? '#8c3242' : '#202c40'; box.classList.add('show'); window.setTimeout(() => box.classList.remove('show'), 3200); }
  function isAbort(error) { return error && error.name === 'AbortError'; }
  function formatDate(value) { if (!value) return 'Non communiquée'; const date = new Date(value); return Number.isNaN(date.valueOf()) ? 'Non communiquée' : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date); }
  function activeProvider() { return state.app?.providers.find(provider => provider.key === state.activeProviderKey) || null; }

  async function refreshApp(goHome = false) {
    state.app = await rpc('app.getState');
    state.activeProviderKey = state.app.activeProviderKey;
    renderProviderSelector(); renderProviderSettings();
    if (goHome) navigate('home'); else await renderHome();
  }
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

  const titles = { home: 'Accueil', live: 'Live', vod: 'Films', series: 'Séries', search: 'Recherche', settings: 'Paramètres' };
  function navigate(page) {
    cancelGroup('view'); cancelGroup('detail');
    state.page = page;
    $$('.nav,.page').forEach(element => element.classList.remove('active'));
    $(`.nav[data-page="${page}"]`).classList.add('active');
    $(`#${page}`).classList.add('active'); $('#page-title').textContent = titles[page];
    if (page === 'home') renderHome();
    if (['live', 'vod', 'series'].includes(page)) loadCatalogShell(page);
    if (page === 'settings') { renderProviderSettings(); loadPreferences(); }
  }
  $$('.nav').forEach(element => element.addEventListener('click', () => navigate(element.dataset.page)));

  async function renderHome() {
    const zero = $('#zero-state'), dashboard = $('#dashboard');
    if (!state.app || state.app.providerCount === 0) { zero.classList.remove('hidden'); dashboard.classList.add('hidden'); return; }
    if (!state.activeProviderKey) { zero.classList.remove('hidden'); dashboard.classList.add('hidden'); zero.querySelector('h2').textContent = 'Aucun fournisseur actif'; zero.querySelector('p').textContent = 'Activez un fournisseur depuis Paramètres → Fournisseurs.'; return; }
    zero.classList.add('hidden'); dashboard.classList.remove('hidden');
    const provider = activeProvider(); $('#dashboard-name').textContent = provider.name; $('#dashboard-meta').textContent = `${provider.type.toUpperCase()} · ${provider.serverUrl}`;
    try {
      const data = await rpc('providers.dashboard', { providerKey: provider.key }, 'view');
      $('#dashboard-status').className = `status ${data.available ? 'success' : 'error'}`; $('#dashboard-status').textContent = data.available ? 'Disponible' : 'Indisponible';
      const stats = $('#dashboard-stats'); stats.replaceChildren();
      addStat(stats, 'Compte', data.account?.status || 'Indisponible'); addStat(stats, 'Expiration', formatDate(data.account?.expiresAt));
      for (const summary of data.categories) addStat(stats, summary.catalog === 'vod' ? 'Films' : summary.catalog === 'series' ? 'Séries' : 'Live', `${summary.selected} / ${summary.total}`);
      renderIndexStatus(data.index, $('#home-index-status'), data.indexDirty);
    } catch (error) { if (!isAbort(error)) toast(error.message, true); }
  }
  function addStat(parent, label, value) { const card = node('div', 'stat'); card.append(node('span', '', label), node('strong', '', String(value))); parent.append(card); }

  async function loadCatalogShell(catalog) {
    const section = $(`#${catalog}`), list = section.querySelector('.category-list'); list.replaceChildren();
    section.querySelector('.catalog-grid').replaceChildren(); section.querySelector('.empty-catalog').classList.remove('hidden');
    if (!state.activeProviderKey) { section.querySelector('.empty-catalog').textContent = 'Aucun fournisseur actif.'; return; }
    try {
      const data = await rpc('categories.list', { providerKey: state.activeProviderKey, catalogType: catalog }, 'view');
      const selected = data.categories.filter(category => category.selected && category.present);
      state.catalogs.set(catalog, { categories: selected, categoryId: null, page: 1, totalPages: 1 });
      renderCatalogCategories(catalog, '');
      if (selected.length) selectCatalogCategory(catalog, selected[0].id);
      else section.querySelector('.empty-catalog').textContent = 'Aucune catégorie sélectionnée.';
    } catch (error) { if (!isAbort(error)) toast(error.message, true); }
  }
  function renderCatalogCategories(catalog, filter) {
    const model = state.catalogs.get(catalog), container = $(`#${catalog} .category-list`); container.replaceChildren();
    const query = filter.trim().toLocaleLowerCase();
    model.categories.filter(category => !query || category.name.toLocaleLowerCase().includes(query)).slice(0, 200).forEach(category => {
      const item = button(category.name, `category-button${model.categoryId === category.id ? ' active' : ''}`, () => selectCatalogCategory(catalog, category.id)); container.append(item);
    });
  }
  $$('.catalog-page .category-filter').forEach(input => input.addEventListener('input', () => renderCatalogCategories(input.closest('.catalog-page').dataset.catalog, input.value)));
  async function selectCatalogCategory(catalog, categoryId, page = 1) {
    const model = state.catalogs.get(catalog); if (!model) return; model.categoryId = categoryId; model.page = page; renderCatalogCategories(catalog, $(`#${catalog} .category-filter`).value);
    const section = $(`#${catalog}`), grid = section.querySelector('.catalog-grid'), empty = section.querySelector('.empty-catalog'), loading = section.querySelector('.loading-state');
    grid.replaceChildren(); empty.classList.add('hidden'); loading.classList.remove('hidden');
    try {
      let result;
      if (catalog === 'live') result = { items: await rpc('catalog.live', { providerKey: state.activeProviderKey, categoryId }, 'view'), page: 1, totalPages: 1, total: 0 };
      else result = await rpc(`catalog.${catalog}.page`, { providerKey: state.activeProviderKey, categoryId, page }, 'view');
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
  function renderCatalogItems(catalog, items) {
    const grid = $(`#${catalog} .catalog-grid`), providerKey = state.activeProviderKey; grid.replaceChildren();
    items.forEach(item => {
      const reference = Object.freeze({ providerKey, mediaType: catalog, mediaId: item.id, extension: item.extension });
      if (catalog === 'live') {
        const card = node('article', 'media-card live-card'); const visual = imageOrPlaceholder(item.imageUrl, item.title, 'channel-logo');
        const title = node('strong', '', item.title); const play = button('Lire', 'play-small', event => { event.stopPropagation(); openPlayer(reference, item.title); });
        card.append(visual, title, play); grid.append(card); return;
      }
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
      if (catalog === 'vod') copy.append(button('▶ Lire', 'primary', () => openPlayer({ ...reference, mediaId: detail.id, extension: detail.extension }, detail.title)));
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
        row.append(node('span', '', `${number}${episode.title}`), button('Lire', 'play-small', () => openPlayer({ providerKey, mediaType: 'series', mediaId: detail.id, episodeId: episode.id, extension: episode.extension }, episode.title))); list.append(row);
      });
      block.append(list); section.append(block);
    });
    parent.append(section);
  }
  async function openPlayer(reference, title) {
    $('#player-title').textContent = title; $('#player-dialog').showModal();
    try { await rpc('player.open', reference, 'player'); }
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
  function updateCredentialFields() { const type = onboardingForm.elements.providerType.value; $('#xtream-fields').style.display = type === 'stalker' ? 'none' : 'grid'; $('#stalker-fields').style.display = type === 'xtream' ? 'none' : 'block'; }
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
      diagnostic.replaceChildren(renderDiagnostic(tested.diagnostic)); toast('Connexion validée.');
    } catch (error) { if (!isAbort(error)) diagnostic.replaceChildren(node('p', 'form-error', error.message)); }
    finally { $('#test-provider').disabled = false; }
  });
  function renderDiagnostic(data) { const grid = node('div', 'diagnostic-grid'); if (!data) return grid; [[data.type, 'Type détecté'], [data.authenticated ? '✓' : '✕', `Compte ${data.account}`], [data.live, 'Live'], [data.vod, 'Films'], [data.series, 'Séries'], [`${data.latencyMs} ms`, 'Latence']].forEach(([value, label]) => { const cell = node('span'); cell.append(node('strong', '', String(value)), document.createTextNode(` ${label}`)); grid.append(cell); }); if (data.protocolDetails) grid.append(node('span', 'muted', data.protocolDetails)); return grid; }
  $$('.onboarding-tabs button').forEach(value => value.addEventListener('click', () => { $$('.onboarding-tabs button').forEach(buttonValue => buttonValue.classList.remove('active')); value.classList.add('active'); state.onboarding.catalog = value.dataset.onboardingCatalog; renderOnboardingCategories(); }));
  $('#onboarding-filter').addEventListener('input', renderOnboardingCategories);
  $('#onboarding-all').addEventListener('click', () => { const model = state.onboarding.policies[state.onboarding.catalog]; model.selected = new Set(model.items.map(item => item.remoteId)); model.mode = 'all'; renderOnboardingCategories(); });
  $('#onboarding-none').addEventListener('click', () => { const model = state.onboarding.policies[state.onboarding.catalog]; model.selected.clear(); model.mode = 'none'; renderOnboardingCategories(); });
  function renderOnboardingCategories() { const model = state.onboarding.policies[state.onboarding.catalog], container = $('#onboarding-categories'); container.replaceChildren(); if (!model) return; renderPolicyRows(container, model, $('#onboarding-filter').value); $('#onboarding-count').textContent = `${model.selected.size} / ${model.items.length}`; }
  onboardingForm.addEventListener('submit', async event => { event.preventDefault(); const policies = {}; for (const catalog of ['live', 'vod', 'series']) { const model = state.onboarding.policies[catalog]; policies[catalog] = { mode: model.mode, selectedIds: [...model.selected] }; } try { await rpc('providers.save', { draftId: state.onboarding.draft.id, enable: onboardingForm.elements.enableProvider.checked, policies }); onboardingDialog.close(); await refreshApp(true); toast('Fournisseur enregistré.'); } catch (error) { $('#onboarding-error').textContent = error.message; } });

  function renderProviderSettings() {
    const list = $('#settings-provider-list'); list.replaceChildren(); if (!state.app) return;
    if (!state.app.providers.length) { list.append(node('p', 'muted', 'Aucun fournisseur configuré.')); return; }
    state.app.providers.forEach(provider => { const card = node('article', 'provider-card'); const copy = node('div'); copy.append(node('strong', '', provider.name), node('p', '', `${provider.type.toUpperCase()} · ${provider.serverUrl}`)); const actions = node('div', 'provider-actions'); actions.append(button('Diagnostic', 'secondary', () => diagnose(provider)), button('Modifier', 'secondary', () => openEdit(provider)), button(provider.enabled ? 'Désactiver' : 'Activer', 'secondary', () => toggleProvider(provider)), button('Supprimer', 'danger-button', () => deleteProvider(provider))); card.append(copy, actions); list.append(card); });
  }
  async function diagnose(provider) { const panel = $('#diagnostic-panel'); panel.classList.remove('hidden'); panel.replaceChildren(node('p', 'muted', 'Diagnostic en cours…')); try { const result = await rpc('providers.diagnose', { providerKey: provider.key }, 'diagnostic'); panel.replaceChildren(node('h3', '', `Diagnostic · ${provider.name}`), renderDiagnostic({ type: result.type, authenticated: result.accountOk, account: result.message, live: result.liveCategories, vod: result.vodCategories, series: result.seriesCategories, latencyMs: result.latencyMs })); } catch (error) { panel.replaceChildren(node('p', 'form-error', error.message)); } }
  async function toggleProvider(provider) { try { await rpc('providers.setEnabled', { providerKey: provider.key, enabled: !provider.enabled }, 'provider-action'); await refreshApp(); toast(provider.enabled ? 'Fournisseur désactivé.' : 'Fournisseur activé.'); } catch (error) { toast(error.message, true); } }
  function openEdit(provider) { const form = $('#edit-form'); form.reset(); form.elements.providerKey.value = provider.key; form.elements.name.value = provider.name; form.elements.serverUrl.value = provider.serverUrl; $('#edit-xtream').classList.toggle('hidden', provider.type !== 'xtream'); $('#edit-stalker').classList.toggle('hidden', provider.type !== 'stalker'); $('#edit-error').textContent = ''; $('#edit-dialog').showModal(); }
  $('#edit-form').addEventListener('submit', async event => { event.preventDefault(); const form = event.currentTarget; try { await rpc('providers.update', { providerKey: form.elements.providerKey.value, name: form.elements.name.value, serverUrl: form.elements.serverUrl.value, username: form.elements.username.value || null, password: form.elements.password.value || null, macAddress: form.elements.macAddress.value || null }, 'provider-action'); form.elements.password.value = ''; form.elements.macAddress.value = ''; $('#edit-dialog').close(); await refreshApp(); toast('Fournisseur mis à jour.'); } catch (error) { form.elements.password.value = ''; form.elements.macAddress.value = ''; $('#edit-error').textContent = error.message; } });
  async function deleteProvider(provider) { if (!window.confirm(`Supprimer « ${provider.name} » ? Le secret sera supprimé définitivement.`)) return; const remove = window.confirm('Supprimer également l’index local ?'); try { await rpc('providers.delete', { providerKey: provider.key, confirmed: true, removeLocalData: remove }, 'provider-action'); await refreshApp(true); toast('Fournisseur supprimé.'); } catch (error) { toast(error.message, true); } }

  $$('.settings-tabs button').forEach(value => value.addEventListener('click', () => { $$('.settings-tabs button,.settings-pane').forEach(item => item.classList.remove('active')); value.classList.add('active'); $(`#settings-${value.dataset.settings}`).classList.add('active'); if (value.dataset.settings === 'categories') loadPolicy(); if (value.dataset.settings === 'index') pollIndex(); }));
  $$('.catalog-tabs [data-policy-catalog]').forEach(value => value.addEventListener('click', () => { $$('.catalog-tabs [data-policy-catalog]').forEach(item => item.classList.remove('active')); value.classList.add('active'); state.policyCatalog = value.dataset.policyCatalog; loadPolicy(); }));
  async function loadPolicy() { const container = $('#policy-list'); container.replaceChildren(); if (!state.activeProviderKey) { container.append(node('p', 'muted', 'Aucun fournisseur actif.')); return; } try { const data = await rpc('categories.list', { providerKey: state.activeProviderKey, catalogType: state.policyCatalog }, 'policy'); state.policy = { items: data.categories, selected: new Set(data.categories.filter(item => item.selected).map(item => item.id)), mode: data.mode }; renderCurrentPolicy(); } catch (error) { if (!isAbort(error)) container.append(node('p', 'form-error', error.message)); } }
  function renderPolicyRows(container, model, filter) { const query = filter.trim().toLocaleLowerCase(); const visible = model.items.filter(item => !query || (item.name || '').toLocaleLowerCase().includes(query)); visible.slice(0, 200).forEach(item => { const row = node('label', `policy-item${item.present === false ? ' missing' : ''}`); const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; const id = item.id ?? item.remoteId; checkbox.checked = model.selected.has(id); checkbox.addEventListener('change', () => { checkbox.checked ? model.selected.add(id) : model.selected.delete(id); model.mode = 'custom'; if (container === $('#policy-list')) updatePolicyCount(); else renderOnboardingCategories(); }); row.append(checkbox, node('span', '', item.name)); if (item.present === false) row.append(node('small', '', 'Disparue')); container.append(row); }); if (visible.length > 200) container.append(node('p', 'hint', `${visible.length - 200} autres catégories — utilisez la recherche pour les afficher.`)); }
  function renderCurrentPolicy() { const container = $('#policy-list'); container.replaceChildren(); if (!state.policy) return; renderPolicyRows(container, state.policy, $('#policy-filter').value); updatePolicyCount(); }
  function updatePolicyCount() { $('#policy-count').textContent = state.policy ? `${state.policy.selected.size} / ${state.policy.items.length}` : ''; }
  $('#policy-filter').addEventListener('input', renderCurrentPolicy); $('#policy-all').addEventListener('click', () => { if (!state.policy) return; state.policy.selected = new Set(state.policy.items.map(item => item.id)); state.policy.mode = 'all'; renderCurrentPolicy(); }); $('#policy-none').addEventListener('click', () => { if (!state.policy) return; state.policy.selected.clear(); state.policy.mode = 'none'; renderCurrentPolicy(); });
  $('#save-policy').addEventListener('click', async () => { if (!state.policy || !state.activeProviderKey) return; try { await rpc('categories.save', { providerKey: state.activeProviderKey, catalogType: state.policyCatalog, mode: state.policy.mode, selectedIds: [...state.policy.selected] }); state.catalogs.clear(); toast('Sélection enregistrée. Index à synchroniser.'); } catch (error) { toast(error.message, true); } });
  $('#sync-categories').addEventListener('click', async () => { if (!state.activeProviderKey) return; try { await rpc('providers.syncCategories', { providerKey: state.activeProviderKey }, 'sync'); await loadPolicy(); toast('Catégories actualisées.'); } catch (error) { if (!isAbort(error)) toast(error.message, true); } });

  async function queueIndex() { if (!state.activeProviderKey) return toast('Aucun fournisseur actif.', true); try { await rpc('index.queue', { providerKey: state.activeProviderKey }); toast('Index placé en file d’attente.'); pollIndex(); } catch (error) { toast(error.message, true); } }
  $('#build-index').addEventListener('click', queueIndex); $('#home-index').addEventListener('click', queueIndex);
  async function pollIndex() { if (state.indexTimer) window.clearTimeout(state.indexTimer); if (!state.activeProviderKey) return; try { const status = await rpc('index.status', { providerKey: state.activeProviderKey }); renderIndexStatus(status.job, $('#index-status'), status.dirty); renderIndexStatus(status.job, $('#home-index-status'), status.dirty); if (status.job && ['queued', 'running'].includes(status.job.status)) state.indexTimer = window.setTimeout(pollIndex, 1200); } catch (error) { $('#index-status').textContent = error.message; } }
  function renderIndexStatus(job, target, dirty = false) { target.replaceChildren(); if (!job) { target.textContent = 'Pas construit'; return; } const labels = { queued: 'En attente', running: 'En cours', completed: dirty ? 'À synchroniser' : 'Prêt', failed: 'Échec', interrupted: 'Interrompu' }; target.append(node('strong', '', labels[job.status] || job.status), node('p', 'muted', job.label || '')); if (job.total > 0) { const track = node('div', 'progress-track'); const fill = node('span'); fill.style.width = `${Math.min(100, Math.round(job.current * 100 / job.total))}%`; track.append(fill); target.append(track, node('p', '', `${job.current.toLocaleString()} / ${job.total.toLocaleString()} · Films ${job.vodItems.toLocaleString()} · Séries ${job.seriesItems.toLocaleString()}`)); } if (job.error) target.append(node('p', 'form-error', job.error)); }

  async function loadPreferences() { try { const preferences = await rpc('settings.get'); const form = $('#preferences-form'); for (const key of ['interfaceLanguage', 'theme', 'audioLanguage', 'secondaryAudioLanguage', 'subtitleLanguage']) if (form.elements[key]) form.elements[key].value = preferences[key] || 'auto'; form.elements.automaticForcedSubtitles.checked = preferences.automaticForcedSubtitles; document.documentElement.dataset.theme = preferences.theme === 'system' ? '' : preferences.theme; } catch { } }
  $('#preferences-form').addEventListener('submit', async event => { event.preventDefault(); const form = event.currentTarget; const preferences = { activeProviderKey: state.activeProviderKey, interfaceLanguage: form.elements.interfaceLanguage.value, theme: form.elements.theme.value, audioLanguage: form.elements.audioLanguage.value || 'auto', secondaryAudioLanguage: form.elements.secondaryAudioLanguage.value || 'auto', subtitleLanguage: form.elements.subtitleLanguage.value || 'auto', automaticForcedSubtitles: form.elements.automaticForcedSubtitles.checked }; try { await rpc('settings.save', preferences); document.documentElement.dataset.theme = preferences.theme === 'system' ? '' : preferences.theme; toast('Préférences enregistrées.'); } catch (error) { toast(error.message, true); } });

  $$('.close-dialog').forEach(value => value.addEventListener('click', () => value.closest('dialog').close()));
  refreshApp().then(loadPreferences).catch(error => toast(error.message, true));
})();
