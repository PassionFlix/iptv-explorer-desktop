(() => {
  'use strict';

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

  function enhanceGrid() {
    document.querySelectorAll('#live .live-card').forEach(enhanceCard);
  }

  function initialize() {
    const style = document.createElement('link');
    style.rel = 'stylesheet';
    style.href = 'live-compact.css';
    document.head.append(style);
    const grid = document.querySelector('#live .catalog-grid');
    if (!grid) return;
    enhanceGrid();
    new MutationObserver(enhanceGrid).observe(grid, { childList: true, subtree: true });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})();
