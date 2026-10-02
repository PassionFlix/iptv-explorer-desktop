(() => {
  'use strict';

  let sequence = 0;
  let cachedProviders = [];
  let loadPending = null;
  const pending = new Map();

  function rpc(method, params = {}) {
    const id = `native-provider-${Date.now()}-${++sequence}`;
    const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    window.chrome.webview.postMessage({ id, method, params });
    return promise;
  }

  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const request = pending.get(response?.id);
    if (!request) return;
    pending.delete(response.id);
    response.ok ? request.resolve(response.result) : request.reject(new Error(response.error || 'Opération impossible'));
  });

  function showToast(message, error = false) {
    const toast = document.querySelector('#toast');
    if (!toast) return;
    toast.textContent = message;
    toast.style.background = error ? '#8c3242' : '#202c40';
    toast.classList.add('show');
    window.setTimeout(() => toast.classList.remove('show'), 3200);
  }

  async function loadProviders(force = false) {
    if (!force && cachedProviders.length) return cachedProviders;
    if (loadPending) return loadPending;
    loadPending = rpc('app.getState')
      .then(state => {
        cachedProviders = Array.isArray(state?.providers) ? state.providers : [];
        return cachedProviders;
      })
      .finally(() => { loadPending = null; });
    return loadPending;
  }

  async function decorateProviderCards(force = false) {
    const list = document.querySelector('#settings-provider-list');
    if (!list) return;
    const cards = [...list.querySelectorAll('.provider-card')];
    if (!cards.length) return;

    let providers;
    try { providers = await loadProviders(force); }
    catch { return; }
    if (!providers.length) return;

    cards.forEach((card, index) => {
      if (card.dataset.nativeProviderActions === '1') return;
      const provider = providers[index];
      if (!provider) return;
      card.dataset.nativeProviderActions = '1';
      if (String(provider.type || '').toLowerCase() !== 'stalker') return;

      const actions = card.querySelector('.provider-actions');
      if (!actions) return;
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'secondary';
      button.textContent = 'MAC complète';
      button.title = 'Afficher la MAC complète dans une fenêtre Windows native';
      button.addEventListener('click', async () => {
        if (button.disabled) return;
        button.disabled = true;
        try {
          await rpc('providers.showFullMac', { providerKey: provider.key });
        } catch (error) {
          showToast(error.message, true);
        } finally {
          button.disabled = false;
        }
      });
      actions.prepend(button);
    });
  }

  function initialize() {
    const list = document.querySelector('#settings-provider-list');
    if (!list) return;
    decorateProviderCards(true);
    new MutationObserver(() => {
      cachedProviders = [];
      decorateProviderCards(true);
    }).observe(list, { childList: true });
    document.querySelector('#provider-select')?.addEventListener('change', () => { cachedProviders = []; });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
