(() => {
  'use strict';

  let sequence = 0;
  let currentVod = null;
  const pending = new Map();

  function rpc(method, params) {
    const id = `media-${Date.now()}-${++sequence}`;
    const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
    window.chrome.webview.postMessage({ id, method, params });
    return promise;
  }

  window.chrome.webview.addEventListener('message', event => {
    const response = event.data;
    const request = pending.get(response?.id);
    if (request) {
      pending.delete(response.id);
      response.ok ? request.resolve(response.result) : request.reject(new Error(response.error || 'Opération impossible'));
      return;
    }

    const result = response?.ok ? response.result : null;
    if (result && typeof result === 'object' && 'id' in result && 'title' in result && 'duration' in result && !('seasons' in result)) {
      const providerKey = document.querySelector('#provider-select')?.value || '';
      currentVod = {
        providerKey,
        mediaId: String(result.id ?? ''),
        extension: result.extension || null,
        suggestedName: result.title || document.querySelector('#detail-title')?.textContent || 'video'
      };
      queueMicrotask(decorateVodActions);
    }
  });

  function showToast(message, error = false) {
    const toast = document.querySelector('#toast');
    if (!toast) return;
    toast.textContent = message;
    toast.style.background = error ? '#8c3242' : '#202c40';
    toast.classList.add('show');
    window.setTimeout(() => toast.classList.remove('show'), 3200);
  }

  function actionButton(label, className, action) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = className;
    button.textContent = label;
    button.addEventListener('click', action);
    return button;
  }

  function decorateVodActions() {
    if (!currentVod?.providerKey || !currentVod.mediaId) return;
    const content = document.querySelector('#detail-content');
    const layout = content?.querySelector('.detail-layout');
    if (!layout) return;
    const copy = layout.children[1];
    if (!copy || copy.querySelector('.desktop-media-actions')) return;

    const play = [...copy.querySelectorAll('button.primary')].find(value => value.textContent?.includes('Lire'));
    if (!play) return;

    const actions = document.createElement('div');
    actions.className = 'desktop-media-actions';
    play.replaceWith(actions);
    actions.append(play);

    const download = actionButton('↓ Télécharger', 'secondary', async () => {
      if (!currentVod || download.disabled) return;
      download.disabled = true;
      const original = download.textContent;
      download.textContent = 'Téléchargement…';
      try {
        const result = await rpc('media.download', currentVod);
        if (result?.saved) showToast(`Téléchargement terminé : ${result.fileName}`);
      } catch (error) {
        showToast(error.message, true);
      } finally {
        download.disabled = false;
        download.textContent = original;
      }
    });

    const copyLink = actionButton('⧉ Copier le lien', 'secondary', async () => {
      if (!currentVod || copyLink.disabled) return;
      copyLink.disabled = true;
      try {
        await rpc('media.copyLink', currentVod);
        showToast('Lien copié dans le presse-papiers.');
      } catch (error) {
        showToast(error.message, true);
      } finally {
        copyLink.disabled = false;
      }
    });

    actions.append(download, copyLink);
  }

  function initialize() {
    const style = document.createElement('style');
    style.textContent = '.desktop-media-actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-top:18px}.desktop-media-actions .primary,.desktop-media-actions .secondary{margin:0}';
    document.head.append(style);

    const content = document.querySelector('#detail-content');
    if (content) new MutationObserver(decorateVodActions).observe(content, { childList: true, subtree: true });
    document.querySelector('#detail-dialog')?.addEventListener('close', () => { currentVod = null; });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
