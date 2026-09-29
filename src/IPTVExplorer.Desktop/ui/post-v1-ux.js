(() => {
  'use strict';

  function installStyles() {
    const style = document.createElement('style');
    style.id = 'post-v1-ux-styles';
    style.textContent = `
      #dashboard{display:grid;grid-template-columns:minmax(0,1fr);gap:12px}
      #dashboard .dashboard-hero{padding:15px 18px;margin:0;min-height:0;border-radius:13px;display:flex;align-items:center;justify-content:space-between;gap:16px}
      #dashboard .dashboard-hero h2{font-size:20px;margin:0 0 3px}
      #dashboard .dashboard-hero .eyebrow{margin-bottom:4px}
      #dashboard .dashboard-hero::after{display:none}
      #dashboard .stats{display:none!important}
      #dashboard .panel{padding:14px 17px;margin:0;border-radius:13px}
      #dashboard .panel .panel-head{align-items:center;margin-bottom:8px}
      #dashboard .panel h3{margin:0;font-size:15px}
      #dashboard .index-progress{margin-top:6px}
      .home-launchers{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:10px}
      .home-launcher{appearance:none;display:flex;align-items:center;gap:13px;min-height:68px;padding:13px 15px;border:1px solid var(--line);border-radius:13px;background:linear-gradient(145deg,var(--panel),var(--panel2));color:var(--text);font:inherit;text-align:left;cursor:pointer;transition:border-color .15s ease,transform .15s ease,background .15s ease}
      .home-launcher:hover{border-color:#6659b7;transform:translateY(-1px);background:linear-gradient(145deg,#172238,#121d30)}
      .home-launcher-icon{display:grid;place-items:center;width:38px;height:38px;flex:0 0 38px;border-radius:11px;background:#1b2940;color:#bfb4ff;font-size:18px}
      .home-launcher strong{display:block;font-size:14px;line-height:1.2}.home-launcher small{display:block;margin-top:4px;color:var(--muted);font-size:11px}
      .home-search{display:flex;align-items:center;gap:9px;padding:10px 12px;border:1px solid var(--line);border-radius:13px;background:var(--panel)}
      .home-search span{color:var(--muted);font-size:18px}.home-search input{flex:1;min-width:0;border:0!important;background:transparent!important;padding:5px 2px!important;outline:none;box-shadow:none!important}.home-search button{white-space:nowrap}
      .update-banner{display:none;align-items:center;justify-content:space-between;gap:18px;padding:13px 16px;border:1px solid #31525d;border-radius:13px;background:linear-gradient(90deg,#102a31,var(--panel));margin-bottom:12px}
      .update-banner.visible{display:flex}.update-banner strong{display:block}.update-banner small{display:block;color:var(--muted);margin-top:3px}.update-banner-actions{display:flex;gap:8px;flex-wrap:wrap}
      #detail-content .episode.has-download{display:grid!important;grid-template-columns:minmax(0,1fr) auto!important;align-items:center!important;gap:8px 14px!important;padding:11px 10px!important}
      #detail-content .episode.has-download>span{min-width:0;overflow-wrap:anywhere}
      #detail-content .episode-actions{grid-column:2;display:flex!important;align-items:center;justify-content:flex-end;gap:7px;margin-left:0!important;flex-wrap:nowrap!important}
      #detail-content .episode-actions button{white-space:nowrap}
      #detail-content .episode.has-download>.desktop-download-progress{grid-column:1/-1!important;width:100%!important;min-width:0;margin:2px 0 4px!important;padding:11px 13px!important;border-radius:11px!important}
      #detail-content .desktop-download-header{min-width:0}
      #detail-content .desktop-download-header strong{min-width:0;max-width:calc(100% - 70px)}
      #detail-content .desktop-download-cancel{min-height:34px;padding:7px 11px}
      @media(max-width:900px){.home-launchers{grid-template-columns:1fr}.home-search{align-items:stretch}.home-search input{width:100%}}
      @media(max-width:650px){#detail-content .episode.has-download{grid-template-columns:1fr!important}#detail-content .episode-actions{grid-column:1;justify-content:flex-start;flex-wrap:wrap!important}.update-banner{align-items:flex-start;flex-direction:column}.home-search{flex-wrap:wrap}.home-search input{flex-basis:calc(100% - 30px)}.home-search button{width:100%}}
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
    const stats = dashboard?.querySelector('#dashboard-stats');
    if (!dashboard || !hero || dashboard.querySelector('.home-launchers')) return;
    if (stats) stats.setAttribute('aria-hidden', 'true');

    const launchers = document.createElement('div');
    launchers.className = 'home-launchers';
    const items = [
      ['live', '◉', 'Live', 'Regarder les chaînes'],
      ['vod', '▶', 'Films', 'Parcourir les films'],
      ['series', '▤', 'Séries', 'Voir les séries']
    ];
    for (const [page, icon, title, subtitle] of items) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'home-launcher';
      const iconNode = document.createElement('span'); iconNode.className = 'home-launcher-icon'; iconNode.textContent = icon;
      const copy = document.createElement('span');
      const strong = document.createElement('strong'); strong.textContent = title;
      const small = document.createElement('small'); small.textContent = subtitle;
      copy.append(strong, small); button.append(iconNode, copy);
      button.addEventListener('click', () => navigate(page));
      launchers.append(button);
    }

    const search = document.createElement('form');
    search.className = 'home-search';
    const searchIcon = document.createElement('span'); searchIcon.textContent = '⌕'; searchIcon.setAttribute('aria-hidden', 'true');
    const input = document.createElement('input'); input.type = 'search'; input.minLength = 3; input.placeholder = 'Rechercher un film ou une série…'; input.setAttribute('aria-label', 'Recherche globale');
    const submit = document.createElement('button'); submit.type = 'submit'; submit.className = 'secondary'; submit.textContent = 'Rechercher';
    search.append(searchIcon, input, submit);
    search.addEventListener('submit', event => {
      event.preventDefault();
      const query = input.value.trim();
      navigate('search');
      const globalSearch = document.querySelector('#global-search');
      if (!globalSearch) return;
      globalSearch.value = query;
      if (query.length >= 3) document.querySelector('#search-form')?.requestSubmit(); else globalSearch.focus();
    });

    hero.after(launchers, search);
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
