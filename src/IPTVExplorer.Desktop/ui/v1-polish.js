(() => {
  'use strict';

  function installStylesheet() {
    if (document.querySelector('link[data-iptv-polish]')) return;
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = 'v1-polish.css';
    link.dataset.iptvPolish = 'true';
    document.head.appendChild(link);
  }

  function installBranding() {
    const mark = document.querySelector('.brand-mark');
    if (mark && !mark.querySelector('img')) {
      mark.textContent = '';
      const image = document.createElement('img');
      image.src = 'assets/iptv-explorer.svg';
      image.alt = '';
      image.setAttribute('aria-hidden', 'true');
      mark.appendChild(image);
    }

    const subtitle = document.querySelector('.brand small');
    if (subtitle) subtitle.textContent = 'Desktop · 1.0.0';

    const orb = document.querySelector('.orb');
    if (orb && !orb.querySelector('img')) {
      orb.textContent = '';
      const image = document.createElement('img');
      image.src = 'assets/iptv-explorer.svg';
      image.alt = '';
      image.setAttribute('aria-hidden', 'true');
      orb.appendChild(image);
    }
  }

  function installAutomaticIndexPolling() {
    const status = document.querySelector('#home-index-status');
    const button = document.querySelector('#home-index');
    if (!status || !button) return;

    const ensurePolling = () => {
      const text = status.textContent || '';
      const active = text.includes('En attente') || text.includes('En cours');
      if (active && status.dataset.autoPolling !== 'true') {
        status.dataset.autoPolling = 'true';
        button.click();
      } else if (!active) {
        delete status.dataset.autoPolling;
      }
    };

    const observer = new MutationObserver(ensurePolling);
    observer.observe(status, { childList: true, subtree: true, characterData: true });
    ensurePolling();
  }

  function initialize() {
    installStylesheet();
    installBranding();
    installAutomaticIndexPolling();
    document.documentElement.classList.add('desktop-polished');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initialize, { once: true });
  } else {
    initialize();
  }
})();
