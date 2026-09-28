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
        mediaType: 'vod',
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

  function formatBytes(value) {
    const bytes = Number(value || 0);
    if (bytes < 1024) return `${bytes.toFixed(0)} o`;
    const units = ['Ko', 'Mo', 'Go', 'To'];
    let size = bytes / 1024;
    let unit = 0;
    while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit += 1; }
    return `${size >= 100 ? size.toFixed(0) : size >= 10 ? size.toFixed(1) : size.toFixed(2)} ${units[unit]}`;
  }

  function delay(milliseconds) {
    return new Promise(resolve => window.setTimeout(resolve, milliseconds));
  }

  function createProgressPanel(parent, downloadId, fileName, downloadButton, originalLabel) {
    parent.querySelector('.desktop-download-progress')?.remove();

    const panel = document.createElement('section');
    panel.className = 'desktop-download-progress';

    const header = document.createElement('div');
    header.className = 'desktop-download-header';
    const title = document.createElement('strong');
    title.textContent = fileName || 'Téléchargement';
    const percentage = document.createElement('span');
    percentage.textContent = 'Préparation…';
    header.append(title, percentage);

    const track = document.createElement('div');
    track.className = 'desktop-download-track indeterminate';
    const fill = document.createElement('span');
    track.append(fill);

    const meta = document.createElement('div');
    meta.className = 'desktop-download-meta';
    meta.textContent = 'Connexion au fournisseur…';

    const cancel = actionButton('Annuler', 'secondary desktop-download-cancel', async () => {
      if (cancel.disabled) return;
      cancel.disabled = true;
      cancel.textContent = 'Annulation…';
      try {
        await rpc('media.download.cancel', { downloadId });
      } catch (error) {
        showToast(error.message, true);
      }
    });

    panel.append(header, track, meta, cancel);
    parent.append(panel);

    return { panel, percentage, track, fill, meta, cancel, downloadButton, originalLabel };
  }

  function renderDownloadStatus(view, status) {
    const total = Number(status.totalBytes || 0);
    const received = Number(status.bytesReceived || 0);
    const speed = Number(status.bytesPerSecond || 0);
    const hasTotal = total > 0;
    const percent = hasTotal ? Math.min(100, Math.max(0, Math.round(received * 100 / total))) : null;

    if (hasTotal) {
      view.track.classList.remove('indeterminate');
      view.fill.style.width = `${percent}%`;
      view.percentage.textContent = `${percent} %`;
      view.meta.textContent = `${formatBytes(received)} / ${formatBytes(total)}${speed > 0 ? ` · ${formatBytes(speed)}/s` : ''}`;
    } else {
      view.track.classList.add('indeterminate');
      view.fill.style.width = '';
      view.percentage.textContent = status.status === 'starting' ? 'Préparation…' : 'En cours…';
      view.meta.textContent = `${formatBytes(received)}${speed > 0 ? ` · ${formatBytes(speed)}/s` : ''}`;
    }

    if (status.status === 'completed') {
      view.track.classList.remove('indeterminate');
      view.fill.style.width = '100%';
      view.percentage.textContent = '100 %';
      view.meta.textContent = `Terminé · ${formatBytes(received)}`;
      view.cancel.disabled = true;
      view.cancel.textContent = 'Terminé';
    } else if (status.status === 'cancelled') {
      view.percentage.textContent = 'Annulé';
      view.meta.textContent = 'Téléchargement annulé.';
      view.cancel.disabled = true;
      view.cancel.textContent = 'Annulé';
    } else if (status.status === 'failed') {
      view.percentage.textContent = 'Échec';
      view.meta.textContent = status.error || 'Le téléchargement a échoué.';
      view.cancel.disabled = true;
      view.cancel.textContent = 'Échec';
    }
  }

  async function monitorDownload(downloadId, view) {
    try {
      while (true) {
        const status = await rpc('media.download.status', { downloadId });
        renderDownloadStatus(view, status);
        if (['completed', 'cancelled', 'failed'].includes(status.status)) {
          view.downloadButton.disabled = false;
          view.downloadButton.textContent = view.originalLabel;
          if (status.status === 'completed') showToast(`Téléchargement terminé : ${status.fileName}`);
          if (status.status === 'failed') showToast(status.error || 'Le téléchargement a échoué.', true);
          return;
        }
        await delay(500);
      }
    } catch (error) {
      view.downloadButton.disabled = false;
      view.downloadButton.textContent = view.originalLabel;
      view.percentage.textContent = 'Échec';
      view.meta.textContent = error.message;
      view.cancel.disabled = true;
      showToast(error.message, true);
    }
  }

  function wireDownload(button, reference, progressHost) {
    button.addEventListener('click', async () => {
      if (button.disabled) return;
      button.disabled = true;
      const original = button.textContent;
      button.textContent = 'Choisir la destination…';
      try {
        const result = await rpc('media.download.start', reference);
        if (result?.cancelled) {
          button.disabled = false;
          button.textContent = original;
          return;
        }
        if (!result?.started || !result.downloadId) throw new Error('Impossible de démarrer le téléchargement.');

        button.textContent = 'Téléchargement…';
        const view = createProgressPanel(progressHost, result.downloadId, result.fileName, button, original);
        monitorDownload(result.downloadId, view);
      } catch (error) {
        button.disabled = false;
        button.textContent = original;
        showToast(error.message, true);
      }
    });
  }

  function wireCopy(button, reference) {
    button.addEventListener('click', async () => {
      if (button.disabled) return;
      button.disabled = true;
      try {
        await rpc('media.copyLink', reference);
        showToast('Lien copié dans le presse-papiers.');
      } catch (error) {
        showToast(error.message, true);
      } finally {
        button.disabled = false;
      }
    });
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

    const download = actionButton('↓ Télécharger', 'secondary', () => {});
    download.replaceWith(download);
    wireDownload(download, currentVod, copy);

    const copyLink = actionButton('⧉ Copier le lien', 'secondary', () => {});
    wireCopy(copyLink, currentVod);
    actions.append(download, copyLink);
  }

  function decorateEpisode(actionsHost, reference) {
    if (!actionsHost || actionsHost.querySelector('.desktop-episode-download')) return;
    if (!reference?.providerKey || !reference.mediaId || !reference.episodeId) return;

    const row = actionsHost.closest('.episode') || actionsHost;
    row.classList.add('has-download');

    const download = actionButton('Télécharger', 'secondary desktop-episode-download', () => {});
    wireDownload(download, reference, row);

    const copyLink = actionButton('Copier le lien', 'secondary desktop-episode-copy', () => {});
    wireCopy(copyLink, reference);
    actionsHost.append(download, copyLink);
  }

  window.IPTVMediaActions = Object.freeze({ decorateEpisode });

  function initialize() {
    const style = document.createElement('style');
    style.textContent = `
      .desktop-media-actions{display:flex;align-items:center;gap:10px;flex-wrap:wrap;margin-top:18px}
      .desktop-media-actions .primary,.desktop-media-actions .secondary{margin:0}
      .desktop-download-progress{margin-top:16px;padding:14px 16px;border:1px solid #30445f;border-radius:14px;background:#0b1423;display:grid;gap:9px}
      .desktop-download-header{display:flex;align-items:center;justify-content:space-between;gap:16px;color:#f8fafc}
      .desktop-download-header strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
      .desktop-download-header span{font-variant-numeric:tabular-nums;color:#8ee9df;white-space:nowrap}
      .desktop-download-track{height:8px;border-radius:999px;background:#17243a;overflow:hidden;position:relative}
      .desktop-download-track span{display:block;height:100%;width:0;border-radius:inherit;background:linear-gradient(90deg,#7c5cff,#30d2c3);transition:width .25s ease}
      .desktop-download-track.indeterminate span{width:35%;position:absolute;animation:iptv-download-indeterminate 1.2s ease-in-out infinite}
      .desktop-download-meta{color:#9fb0c7;font-size:13px;font-variant-numeric:tabular-nums}
      .desktop-download-cancel{justify-self:start}
      .episode.has-download{flex-wrap:wrap;gap:8px}
      .episode-actions{display:flex;align-items:center;gap:7px;margin-left:auto;flex-wrap:wrap;justify-content:flex-end}
      .episode-actions .play-small{margin-left:0}
      .episode.has-download>.desktop-download-progress{flex:1 0 100%;width:100%;margin-top:4px}
      @keyframes iptv-download-indeterminate{0%{left:-35%}100%{left:100%}}
    `;
    document.head.append(style);

    const content = document.querySelector('#detail-content');
    if (content) new MutationObserver(decorateVodActions).observe(content, { childList: true, subtree: true });
    document.querySelector('#detail-dialog')?.addEventListener('close', () => { currentVod = null; });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
