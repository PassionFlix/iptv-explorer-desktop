(() => {
  'use strict';

  function installStyles() {
    const style = document.createElement('style');
    style.id = 'post-v1-ux-styles';
    style.textContent = `
      #dashboard{display:grid;grid-template-columns:minmax(0,1fr);gap:14px}
      #dashboard .dashboard-hero{padding:18px 20px;margin:0;min-height:0;border-radius:14px}
      #dashboard .dashboard-hero h2{font-size:23px;margin:0 0 4px}
      #dashboard .dashboard-hero .eyebrow{margin-bottom:5px}
      #dashboard .dashboard-hero::after{display:none}
      #dashboard .stats{grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin:0}
      #dashboard .stat{min-height:82px;padding:14px 16px;border-radius:12px}
      #dashboard .stat strong{font-size:20px;margin:4px 0 0;overflow:hidden;text-overflow:ellipsis}
      #dashboard .panel{padding:17px 20px;margin:0;border-radius:14px}
      #dashboard .panel h3{margin-bottom:0}
      .home-shortcuts{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:10px;margin:0}
      .home-shortcut{display:flex;align-items:center;gap:12px;min-height:72px;padding:14px 16px;border:1px solid var(--line);border-radius:13px;background:linear-gradient(145deg,var(--panel),var(--panel2));color:var(--text);font:inherit;text-align:left;cursor:pointer}
      .home-shortcut:hover{border-color:color-mix(in srgb,var(--accent) 40%,var(--line));transform:translateY(-1px)}
      .home-shortcut-icon{display:grid;place-items:center;width:36px;height:36px;flex:0 0 36px;border-radius:10px;background:color-mix(in srgb,var(--accent) 16%,var(--panel2));color:#c8bdff;font-size:18px}
      .home-shortcut strong{display:block;font-size:14px}.home-shortcut small{display:block;margin-top:3px;color:var(--muted);font-size:11px}
      .update-banner{display:none;align-items:center;justify-content:space-between;gap:18px;padding:13px 16px;border:1px solid color-mix(in srgb,var(--accent2) 35%,var(--line));border-radius:13px;background:linear-gradient(90deg,color-mix(in srgb,var(--accent2) 9%,var(--panel)),var(--panel));margin-bottom:14px}
      .update-banner.visible{display:flex}.update-banner strong{display:block}.update-banner small{display:block;color:var(--muted);margin-top:3px}.update-banner-actions{display:flex;gap:8px;flex-wrap:wrap}
      #detail-content .episode.has-download{display:grid!important;grid-template-columns:minmax(0,1fr) auto!important;align-items:center!important;gap:8px 14px!important;padding:11px 10px!important}
      #detail-content .episode.has-download>span{min-width:0;overflow-wrap:anywhere}
      #detail-content .episode-actions{grid-column:2;display:flex!important;align-items:center;justify-content:flex-end;gap:7px;margin-left:0!important;flex-wrap:nowrap!important}
      #detail-content .episode-actions button{white-space:nowrap}
      #detail-content .episode.has-download>.desktop-download-progress{grid-column:1/-1!important;width:100%!important;min-width:0;margin:2px 0 4px!important;padding:11px 13px!important;border-radius:11px!important}
      #detail-content .desktop-download-header{min-width:0}
      #detail-content .desktop-download-header strong{min-width:0;max-width:calc(100% - 70px)}
      #detail-content .desktop-download-cancel{min-height:34px;padding:7px 11px}
      @media(max-width:900px){#dashboard .stats,.home-shortcuts{grid-template-columns:repeat(2,minmax(0,1fr))}}
      @media(max-width:650px){#detail-content .episode.has-download{grid-template-columns:1fr!important}#detail-content .episode-actions{grid-column:1;justify-content:flex-start;flex-wrap:wrap!important}#dashboard .stats,.home-shortcuts{grid-template-columns:1fr}.update-banner{align-items:flex-start;flex-direction:column}}
    `;
    document.head.append(style);
  }

  function navigate(page) {
    const button = document.querySelector(`.nav[data-page="${page}"]`);
    if (button) button.click();
  }

  function enhanceHome() {
    const dashboard = document.querySelector('#dashboard');
    const hero = dashboard?.querySelector('.dashboard-hero');
    if (!dashboard || !hero || dashboard.querySelector('.home-shortcuts')) return;
    const shortcuts = document.createElement('div');
    shortcuts.className = 'home-shortcuts';
    const items = [
      ['live', '◉', 'Live', 'Regarder les chaînes'],
      ['vod', '▶', 'Films', 'Parcourir les films'],
      ['series', '▤', 'Séries', 'Voir les séries'],
      ['search', '⌕', 'Recherche', 'Chercher dans l’index']
    ];
    for (const [page, icon, title, subtitle] of items) {
      const button = document.createElement('button');
      button.type = 'button'; button.className = 'home-shortcut';
      button.innerHTML = `<span class="home-shortcut-icon">${icon}</span><span><strong>${title}</strong><small>${subtitle}</small></span>`;
      button.addEventListener('click', () => navigate(page));
      shortcuts.append(button);
    }
    hero.after(shortcuts);
  }

  function ensureUpdateBanner() {
    let banner = document.querySelector('#update-banner');
    if (banner) return banner;
    banner = document.createElement('div');
    banner.id = 'update-banner'; banner.className = 'update-banner';
    const home = document.querySelector('#home');
    home?.prepend(banner);
    return banner;
  }

  window.addEventListener('iptv-update-available', event => {
    const info = event.detail || {};
    const banner = ensureUpdateBanner();
    if (!banner || !info.version || !info.url) return;
    banner.replaceChildren();
    const copy = document.createElement('div');
    const title = document.createElement('strong'); title.textContent = `Mise à jour disponible — v${info.version}`;
    const meta = document.createElement('small'); meta.textContent = `Version installée : v${info.currentVersion || '—'} · Une nouvelle version stable est disponible.`;
    copy.append(title, meta);
    const actions = document.createElement('div'); actions.className = 'update-banner-actions';
    const download = document.createElement('button'); download.type = 'button'; download.className = 'primary'; download.textContent = 'Télécharger la mise à jour';
    download.addEventListener('click', () => { window.location.href = info.url; });
    const later = document.createElement('button'); later.type = 'button'; later.className = 'secondary'; later.textContent = 'Plus tard'; later.addEventListener('click', () => banner.classList.remove('visible'));
    actions.append(download, later); banner.append(copy, actions); banner.classList.add('visible');
  });

  function init() { installStyles(); enhanceHome(); ensureUpdateBanner(); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once: true }); else init();
})();
