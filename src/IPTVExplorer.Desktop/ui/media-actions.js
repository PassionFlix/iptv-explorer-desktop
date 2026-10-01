(() => {
  'use strict';

  let sequence = 0;
  let currentVod = null;
  let currentSeries = null;
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
    if (!result || typeof result !== 'object' || !('id' in result) || !('title' in result)) return;

    const providerKey = document.querySelector('#provider-select')?.value || '';
    if ('seasons' in result) {
      currentSeries = {
        providerKey,
        mediaType: 'series',
        mediaId: String(result.id ?? ''),
        title: result.title || document.querySelector('#detail-title')?.textContent || 'serie',
        episodes: (result.seasons || []).flatMap(season => (season.episodes || []).map(episode => ({
          episodeId: String(episode.id ?? ''),
          extension: episode.extension || null,
          season: Number(episode.season ?? season.number ?? 1),
          episode: Number(episode.episode ?? 0),
          title: episode.title || 'Episode'
        })))
      };
      currentVod = null;
      queueMicrotask(decorateSeriesActions);
      return;
    }

    if ('duration' in result) {
      currentVod = {
        providerKey,
        mediaType: 'vod',
        mediaId: String(result.id ?? ''),
        extension: result.extension || null,
        suggestedName: result.title || document.querySelector('#detail-title')?.textContent || 'video'
      };
      currentSeries = null;
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

  function actionButton(label, className) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = className;
    button.textContent = label;
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
    title.title = title.textContent;
    const percentage = document.createElement('span');
    percentage.textContent = 'Préparation…';
    header.append(title, percentage);

    const track = document.createElement('div');
    track.className = 'desktop-download-track indeterminate';
    track.setAttribute('role', 'progressbar');
    track.setAttribute('aria-label', 'Progression du téléchargement');
    track.setAttribute('aria-valuemin', '0');
    track.setAttribute('aria-valuemax', '100');
    track.setAttribute('aria-valuetext', 'Préparation');
    const fill = document.createElement('span');
    track.append(fill);

    const meta = document.createElement('div');
    meta.className = 'desktop-download-meta';
    meta.textContent = 'Connexion au fournisseur…';

    const footer = document.createElement('div');
    footer.className = 'desktop-download-footer';

    const cancel = actionButton('Annuler', 'secondary desktop-download-cancel');
    cancel.setAttribute('aria-label', `Annuler le téléchargement de ${title.textContent}`);
    cancel.addEventListener('click', async () => {
      if (cancel.disabled) return;
      cancel.disabled = true;
      cancel.textContent = 'Annulation…';
      announcement.textContent = 'Annulation du téléchargement demandée.';
      try {
        await rpc('media.download.cancel', { downloadId });
      } catch (error) {
        showToast(error.message, true);
      }
    });

    const announcement = document.createElement('div');
    announcement.className = 'visually-hidden desktop-download-announcement';
    announcement.setAttribute('aria-live', 'polite');
    announcement.textContent = 'Téléchargement en préparation.';

    footer.append(meta, cancel);
    panel.append(header, track, footer, announcement);
    parent.append(panel);

    return { panel, percentage, track, fill, meta, cancel, announcement, downloadButton, originalLabel };
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
      view.track.setAttribute('aria-valuenow', String(percent));
      view.track.removeAttribute('aria-valuetext');
      view.percentage.textContent = `${percent} %`;
      view.meta.textContent = `${formatBytes(received)} / ${formatBytes(total)}${speed > 0 ? ` · ${formatBytes(speed)}/s` : ''}`;
    } else {
      view.track.classList.add('indeterminate');
      view.fill.style.width = '';
      view.track.removeAttribute('aria-valuenow');
      view.track.setAttribute('aria-valuetext', status.status === 'starting' ? 'Préparation' : 'En cours');
      view.percentage.textContent = status.status === 'starting' ? 'Préparation…' : 'En cours…';
      view.meta.textContent = `${formatBytes(received)}${speed > 0 ? ` · ${formatBytes(speed)}/s` : ''}`;
    }

    if (status.status === 'completed') {
      view.track.classList.remove('indeterminate');
      view.fill.style.width = '100%';
      view.track.setAttribute('aria-valuenow', '100');
      view.track.removeAttribute('aria-valuetext');
      view.percentage.textContent = '100 %';
      view.meta.textContent = `Terminé · ${formatBytes(received)}`;
      view.cancel.disabled = true;
      view.cancel.textContent = 'Terminé';
      view.announcement.textContent = 'Téléchargement terminé.';
    } else if (status.status === 'cancelled') {
      view.track.removeAttribute('aria-valuenow');
      view.track.setAttribute('aria-valuetext', 'Annulé');
      view.percentage.textContent = 'Annulé';
      view.meta.textContent = 'Téléchargement annulé.';
      view.cancel.disabled = true;
      view.cancel.textContent = 'Annulé';
      view.announcement.textContent = 'Téléchargement annulé.';
    } else if (status.status === 'failed') {
      view.track.removeAttribute('aria-valuenow');
      view.track.setAttribute('aria-valuetext', 'Échec');
      view.percentage.textContent = 'Échec';
      view.meta.textContent = status.error || 'Le téléchargement a échoué.';
      view.cancel.disabled = true;
      view.cancel.textContent = 'Échec';
      view.announcement.textContent = 'Échec du téléchargement.';
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
      view.cancel.textContent = 'Échec';
      view.track.removeAttribute('aria-valuenow');
      view.track.setAttribute('aria-valuetext', 'Échec');
      view.announcement.textContent = 'Échec du téléchargement.';
      showToast(error.message, true);
    }
  }

  function wireDownload(button, reference, progressHost) {
    button.addEventListener('click', async event => {
      event.stopPropagation();
      if (button.disabled) return;
      button.disabled = true;
      const original = button.textContent;
      button.textContent = 'Choisir…';
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
    button.addEventListener('click', async event => {
      event.stopPropagation();
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

    const download = actionButton('↓ Télécharger', 'secondary');
    wireDownload(download, currentVod, copy);
    const copyLink = actionButton('⧉ Copier le lien', 'secondary');
    wireCopy(copyLink, currentVod);
    actions.append(download, copyLink);
  }

  function decorateSeriesActions() {
    if (!currentSeries?.providerKey || !currentSeries.mediaId || !currentSeries.episodes?.length) return;
    const rows = [...document.querySelectorAll('#detail-content .episode')];
    if (!rows.length) return;

    rows.forEach((row, index) => {
      if (row.querySelector('.desktop-episode-download')) return;
      const episode = currentSeries.episodes[index];
      if (!episode?.episodeId) return;

      const play = row.querySelector('button.play-small');
      if (!play) return;
      let actions = row.querySelector('.episode-actions');
      if (!actions) {
        actions = document.createElement('div');
        actions.className = 'episode-actions';
        play.replaceWith(actions);
        actions.append(play);
      }
      row.classList.add('has-download');

      const seasonNumber = String(Math.max(1, episode.season || 1)).padStart(2, '0');
      const episodeNumber = episode.episode > 0 ? `E${String(episode.episode).padStart(2, '0')}` : '';
      const reference = {
        providerKey: currentSeries.providerKey,
        mediaType: 'series',
        mediaId: currentSeries.mediaId,
        episodeId: episode.episodeId,
        extension: episode.extension,
        suggestedName: `${currentSeries.title} - S${seasonNumber}${episodeNumber} - ${episode.title}`
      };

      const download = actionButton('Télécharger', 'secondary desktop-episode-download');
      wireDownload(download, reference, row);
      const copyLink = actionButton('Copier le lien', 'secondary desktop-episode-copy');
      wireCopy(copyLink, reference);
      actions.append(download, copyLink);
    });
  }

  function initialize() {
    const content = document.querySelector('#detail-content');
    if (content) new MutationObserver(() => { decorateVodActions(); decorateSeriesActions(); }).observe(content, { childList: true, subtree: true });
    document.querySelector('#detail-dialog')?.addEventListener('close', () => { currentVod = null; currentSeries = null; });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
