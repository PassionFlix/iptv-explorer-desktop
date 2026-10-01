(() => {
  'use strict';
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const carousels = new Map();

  document.querySelectorAll('.home-carousel').forEach(carousel => {
    const track = carousel.querySelector('.home-carousel-track');
    const previous = carousel.querySelector('[data-direction="-1"]');
    const next = carousel.querySelector('[data-direction="1"]');
    const update = () => {
      previous.disabled = track.scrollLeft <= 1;
      next.disabled = track.scrollLeft >= track.scrollWidth - track.clientWidth - 1;
    };
    const move = direction => {
      const card = track.firstElementChild;
      const stride = card ? card.getBoundingClientRect().width + parseFloat(getComputedStyle(track).gap) : 160;
      track.scrollBy({ left: direction * stride * 4, behavior: reducedMotion.matches ? 'instant' : 'smooth' });
    };
    previous.addEventListener('click', () => move(-1));
    next.addEventListener('click', () => move(1));
    track.addEventListener('scroll', update, { passive: true });
    track.addEventListener('wheel', event => {
      // Native horizontal wheels and trackpads keep their browser behavior.
      if (!event.shiftKey || event.deltaX || !event.deltaY) return;
      const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? track.clientWidth : 1);
      if ((delta < 0 && track.scrollLeft <= 0) || (delta > 0 && next.disabled)) return;
      event.preventDefault();
      track.scrollBy({ left: delta, behavior: 'instant' });
    }, { passive: false });
    track.addEventListener('keydown', event => {
      if (event.target !== track || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      if (event.key === 'Home' || event.key === 'End')
        track.scrollTo({ left: event.key === 'Home' ? 0 : track.scrollWidth, behavior: reducedMotion.matches ? 'instant' : 'smooth' });
      else move(event.key === 'ArrowLeft' ? -1 : 1);
    });
    new ResizeObserver(update).observe(track);
    carousels.set(track, update);
  });

  const backdrop = document.querySelector('#home-backdrop');
  const layers = [...backdrop.querySelectorAll('.home-backdrop-layer')];
  let images = [], nextImage = 0, currentUrl = '', activeLayer = 0;
  let homeActive = true, timer = null, generation = 0, loading = false, cancelPreload = null;
  const failed = new Set();
  const canRun = () => homeActive && !document.hidden;

  function safeUrl(value) {
    try {
      const url = new URL(value);
      return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password && !url.search && !url.hash &&
        /\.(jpe?g|png|webp|avif|gif)$/i.test(url.pathname) ? url.href : null;
    } catch { return null; }
  }

  function candidate(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value) || !['backdrop', 'poster'].includes(value.kind)) return null;
    const url = safeUrl(value.url);
    return url ? { url, kind: value.kind } : null;
  }

  function clearLayer(layer) {
    layer.classList.remove('is-active', 'is-backdrop', 'is-poster');
    layer.removeAttribute('data-kind');
    layer.querySelector('.home-backdrop-fill').removeAttribute('src');
    layer.querySelector('.home-backdrop-subject').removeAttribute('src');
  }

  function renderLayer(layer, artwork) {
    const fill = layer.querySelector('.home-backdrop-fill');
    const subject = layer.querySelector('.home-backdrop-subject');
    layer.classList.toggle('is-backdrop', artwork.kind === 'backdrop');
    layer.classList.toggle('is-poster', artwork.kind === 'poster');
    layer.setAttribute('data-kind', artwork.kind);
    fill.src = artwork.url;
    if (artwork.kind === 'poster') subject.src = artwork.url;
    else subject.removeAttribute('src');
  }

  function interrupt() {
    window.clearTimeout(timer); timer = null;
    generation++;
    cancelPreload?.(); cancelPreload = null;
    loading = false;
  }

  function preload(url) {
    return new Promise(resolve => {
      const image = new Image();
      image.referrerPolicy = 'no-referrer';
      const finish = success => {
        window.clearTimeout(timeout);
        image.onload = image.onerror = null;
        if (!success) image.removeAttribute('src');
        cancelPreload = null;
        resolve(success);
      };
      const timeout = window.setTimeout(() => finish(false), 8000);
      cancelPreload = () => finish(false);
      image.onload = () => finish(true);
      image.onerror = () => finish(false);
      image.src = url;
    });
  }

  function schedule() {
    window.clearTimeout(timer); timer = null;
    const usable = new Set(images.map(candidates => candidates.find(item => !failed.has(item.url))?.url).filter(Boolean));
    if (canRun() && !reducedMotion.matches && usable.size >= 2)
      timer = window.setTimeout(advance, 15000);
  }

  async function advance() {
    if (!canRun() || loading || images.length === 0) return;
    loading = true;
    const run = generation;
    try {
      for (let tried = 0; tried < images.length; tried++) {
        const candidates = images[nextImage];
        nextImage = (nextImage + 1) % images.length;
        for (const artwork of candidates) {
          if (failed.has(artwork.url)) continue;
          if (artwork.url === currentUrl) break;
          const ready = await preload(artwork.url);
          if (run !== generation || !canRun()) return;
          if (!ready) { failed.add(artwork.url); continue; }
          const incoming = currentUrl ? 1 - activeLayer : activeLayer;
          renderLayer(layers[incoming], artwork);
          layers[incoming].classList.add('is-active');
          layers[1 - incoming].classList.remove('is-active');
          activeLayer = incoming;
          currentUrl = artwork.url;
          backdrop.classList.remove('hidden');
          return;
        }
      }
    } finally {
      if (run === generation) { loading = false; schedule(); }
    }
  }

  function resume() {
    backdrop.classList.toggle('hidden', !canRun() || !currentUrl);
    if (!canRun()) return;
    if (currentUrl) schedule(); else void advance();
  }

  document.addEventListener('visibilitychange', () => { interrupt(); resume(); });
  reducedMotion.addEventListener('change', () => { interrupt(); resume(); });
  window.addEventListener('pagehide', interrupt);

  window.iptvHome = {
    resetCarousel(track) {
      track.scrollTo({ left: 0, behavior: 'instant' });
      requestAnimationFrame(() => carousels.get(track)?.());
    },
    setActive(active) {
      homeActive = active;
      interrupt(); resume();
    },
    setBackgrounds(candidates) {
      const next = Array.isArray(candidates) ? candidates.slice(0, 13).map(values => {
        if (!Array.isArray(values)) return [];
        const seen = new Set();
        return values.map(candidate).filter(item => item && !seen.has(item.url) && seen.add(item.url));
      }).filter(values => values.length) : [];
      if (JSON.stringify(next) === JSON.stringify(images)) return;
      interrupt();
      images = next; nextImage = 0; currentUrl = ''; activeLayer = 0; failed.clear();
      layers.forEach(clearLayer);
      resume();
    }
  };
})();
