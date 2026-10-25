// Vellum PDF engine: conversion progress on record pages (NFR-005) and the in-browser viewer (DOC-081).
// The viewer uses vendored pdf.js (wwwroot/lib/pdfjs), loaded on demand; it works under the strict CSP (no eval, no wasm).
(() => {
  'use strict';

  // ------------------------------------------------------------------ conversion status

  const statusCard = document.querySelector('[data-pdf-status]');
  const recordForm = document.querySelector('[data-record-form]');
  if (statusCard?.dataset.pdfStatus || (recordForm && document.querySelector('[data-pdf-pending]:not([data-pdf-pending=""])'))) {
    const url = statusCard?.dataset.pdfStatus || `/pdf/status/${recordForm.dataset.recordId}`;
    let tries = 0;
    const tick = async () => {
      tries++;
      try {
        const res = await fetch(url, { headers: { Accept: 'application/json' }, credentials: 'same-origin' });
        if (res.ok) {
          const data = await res.json();
          if (data.pending === 0) {
            const key = 'vl-pdf-reload:' + url;
            const last = Number(sessionStorage.getItem(key) || 0);
            if (recordForm?.dataset.editing !== 'true' && Date.now() - last > 5000) {
              sessionStorage.setItem(key, String(Date.now()));
              location.reload();
            } else {
              document.querySelectorAll('[data-pdf-pending]:not([data-pdf-pending=""])').forEach((el) => {
                el.classList.remove('pending', 'vl-kw-info');
                el.classList.add('done', 'vl-kw-success');
                el.textContent = 'PDF ready · reload to view';
              });
            }
            return;
          }
        }
      } catch { /* offline or signed out: try again later */ }
      if (tries < 120) setTimeout(tick, Math.min(10000, 2000 + tries * 250));
    };
    setTimeout(tick, 2000);
  }

  // ------------------------------------------------------------------ rules editor preview (Administration › PDF renditions)

  const rulesForm = document.querySelector('[data-pdf-rules]');
  if (rulesForm) {
    const preview = rulesForm.querySelector('[data-pdf-preview]');
    const phaseLabel = rulesForm.querySelector('[data-pdf-preview-phase]');
    const color = rulesForm.querySelector('[data-pdf-color]');
    const opacity = rulesForm.querySelector('[data-pdf-opacity]');
    const sample = { number: 'SOP-00012', revision: 'B', rev: 'B', title: 'Cleanroom gowning and entry', effective_date: '12 Mar 2026', effective: '12 Mar 2026',
      today: new Date().toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }), status: 'Effective', org: 'Acme Medical Devices', form: 'Controlled Document' };
    const show = (input) => {
      if (!input || !preview) return;
      const pos = input.closest('tr')?.querySelector('[data-pdf-pos]')?.value || 'Diagonal';
      const text = input.value.trim().replace(/\{([^}]+)\}/g, (m, k) => (k === 'phase' ? input.dataset.phase : sample[k.trim().toLowerCase()]) ?? m);
      preview.querySelectorAll('[data-pdf-band]').forEach((el) => { el.hidden = true; el.textContent = ''; });
      const slot = preview.querySelector(`[data-pdf-band="${pos === 'Header' ? 'top' : pos === 'Footer' ? 'bottom' : 'diagonal'}"]`);
      if (slot && text) { slot.textContent = text; slot.hidden = false; }
      if (phaseLabel) phaseLabel.textContent = input.dataset.phase || '';
    };
    const style = () => {
      if (!preview) return;
      preview.style.setProperty('--pdf-color', color.value);
      preview.style.setProperty('--pdf-opacity', String(Math.max(Number(opacity.value), 8) / 100));
      const ct = rulesForm.querySelector('[data-pdf-color-text]');
      if (ct) ct.textContent = color.value;
      const ot = rulesForm.querySelector('[data-pdf-opacity-text]');
      if (ot) ot.textContent = `${opacity.value}%`;
    };
    let last = [...rulesForm.querySelectorAll('[data-pdf-wm]')].find((i) => i.value.trim()) || null;
    rulesForm.querySelectorAll('[data-pdf-wm]').forEach((i) => {
      i.addEventListener('focus', () => { last = i; show(i); });
      i.addEventListener('input', () => { last = i; show(i); });
    });
    rulesForm.querySelectorAll('[data-pdf-pos]').forEach((s) => s.addEventListener('change', () => show(s.closest('tr').querySelector('[data-pdf-wm]'))));
    color?.addEventListener('input', style);
    opacity?.addEventListener('input', style);
    style();
    show(last);
  }

  // ------------------------------------------------------------------ viewer

  const root = document.querySelector('[data-pdf-viewer]');
  if (!root) return;
  const cfg = JSON.parse(document.getElementById('vl-pdf-config')?.textContent || '{}');
  const scroll = root.querySelector('[data-pdf-scroll]');
  const host = root.querySelector('[data-pdf-host]');
  const stateEl = root.querySelector('[data-pdf-state]');
  const pageNo = root.querySelector('[data-pdf-pageno]');
  const pagesEl = root.querySelector('[data-pdf-pages]');
  const scaleSel = root.querySelector('[data-pdf-scale]');
  const printBtn = root.querySelector('[data-pdf-print]');
  const printHost = document.querySelector('[data-pdf-print-host]');
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  // Print rules hide everything but these, so they live directly under <body>.
  document.body.classList.add('vl-pdf-page');
  if (printHost) document.body.append(printHost);
  const noPrint = document.querySelector('.vl-pdf-noprint');
  if (noPrint) document.body.append(noPrint);

  let pdfjs = null;
  let doc = null;
  let pages = [];       // { el, page, base, rendered, task }
  let scale = 1;
  let mode = 'width';
  let current = 1;

  const fail = (msg) => {
    stateEl.innerHTML = '';
    const icon = document.createElement('i');
    icon.className = 'bi bi-exclamation-octagon text-danger me-1';
    stateEl.append(icon, document.createTextNode(msg));
    stateEl.hidden = false;
  };

  const fitScale = () => {
    const first = pages[0]?.base;
    if (!first) return 1;
    const w = (scroll.clientWidth - 40) / first.width;
    if (mode === 'width') return Math.max(0.25, Math.min(w, 3));
    if (mode === 'page') return Math.max(0.25, Math.min(w, (scroll.clientHeight - 40) / first.height));
    return Number(mode) || 1;
  };

  const layout = () => {
    scale = fitScale();
    for (const p of pages) {
      const vp = p.page.getViewport({ scale });
      p.el.style.width = `${Math.floor(vp.width)}px`;
      p.el.style.height = `${Math.floor(vp.height)}px`;
      p.el.style.setProperty('--total-scale-factor', String(scale));
      if (p.rendered) { p.task?.cancel?.(); p.rendered = false; p.el.querySelector('canvas')?.remove(); p.el.querySelector('.textLayer')?.remove(); }
    }
    pages.forEach((p) => io.unobserve(p.el));
    pages.forEach((p) => io.observe(p.el));
  };

  const render = async (p) => {
    if (p.rendered) return;
    p.rendered = true;
    const vp = p.page.getViewport({ scale });
    const canvas = document.createElement('canvas');
    canvas.width = Math.floor(vp.width * dpr);
    canvas.height = Math.floor(vp.height * dpr);
    canvas.style.width = `${Math.floor(vp.width)}px`;
    canvas.style.height = `${Math.floor(vp.height)}px`;
    canvas.setAttribute('aria-hidden', 'true');
    p.el.prepend(canvas);
    try {
      p.task = p.page.render({ canvas, canvasContext: canvas.getContext('2d'), viewport: vp, transform: dpr !== 1 ? [dpr, 0, 0, dpr, 0, 0] : null });
      await p.task.promise;
      // Selectable, screen-reader readable text over the canvas.
      const layer = document.createElement('div');
      layer.className = 'textLayer';
      p.el.append(layer);
      const tl = new pdfjs.TextLayer({ textContentSource: p.page.streamTextContent(), container: layer, viewport: vp });
      await tl.render();
    } catch (e) {
      if (e?.name !== 'RenderingCancelledException') console.warn('PDF page render failed', e);
    }
  };

  const io = new IntersectionObserver((entries) => {
    for (const en of entries) {
      if (!en.isIntersecting) continue;
      const p = pages.find((x) => x.el === en.target);
      if (p) render(p);
    }
  }, { root: scroll, rootMargin: '800px 0px' });

  const track = new IntersectionObserver((entries) => {
    for (const en of entries) {
      if (en.isIntersecting && en.intersectionRatio >= 0.35) {
        current = Number(en.target.dataset.page);
        if (document.activeElement !== pageNo) pageNo.value = String(current);
      }
    }
  }, { root: scroll, threshold: [0.35, 0.6] });

  const go = (n) => {
    const p = pages[Math.max(1, Math.min(pages.length, n)) - 1];
    if (p) p.el.scrollIntoView({ block: 'start', behavior: 'smooth' });
  };

  root.querySelector('[data-pdf-prev]')?.addEventListener('click', () => go(current - 1));
  root.querySelector('[data-pdf-next]')?.addEventListener('click', () => go(current + 1));
  pageNo?.addEventListener('change', () => go(Number(pageNo.value)));
  pageNo?.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); go(Number(pageNo.value)); } });
  scaleSel?.addEventListener('change', () => { mode = scaleSel.value; layout(); });
  root.querySelectorAll('[data-pdf-zoom]').forEach((b) => b.addEventListener('click', () => {
    const next = b.dataset.pdfZoom === 'in' ? scale * 1.2 : scale / 1.2;
    mode = String(Math.max(0.25, Math.min(4, Math.round(next * 100) / 100)));
    if (scaleSel) {
      let opt = [...scaleSel.options].find((o) => o.value === mode);
      if (!opt) { opt = new Option(`${Math.round(Number(mode) * 100)}%`, mode); opt.dataset.custom = '1'; scaleSel.querySelectorAll('[data-custom]').forEach((o) => o.remove()); scaleSel.add(opt); }
      scaleSel.value = mode;
    }
    layout();
  }));
  let resizeTimer = 0;
  window.addEventListener('resize', () => {
    if (mode !== 'width' && mode !== 'page') return;
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(layout, 150);
  });

  // Printing: render every page as an image into a print-only container (DOC-081: only when permitted).
  const preparePrint = async () => {
    if (!cfg.canPrint || !doc || !printHost) return;
    printBtn.disabled = true;
    const label = printBtn.innerHTML;
    printHost.innerHTML = '';
    const urls = [];
    try {
      for (let i = 1; i <= doc.numPages; i++) {
        printBtn.textContent = `Preparing ${i}/${doc.numPages}…`;
        const page = await doc.getPage(i);
        const vp = page.getViewport({ scale: 2 });
        const c = document.createElement('canvas');
        c.width = Math.floor(vp.width);
        c.height = Math.floor(vp.height);
        await page.render({ canvas: c, canvasContext: c.getContext('2d'), viewport: vp }).promise;
        const blob = await new Promise((res) => c.toBlob(res, 'image/png'));
        const url = URL.createObjectURL(blob);
        urls.push(url);
        const img = new Image();
        img.src = url;
        img.alt = '';
        printHost.append(img);
      }
      await Promise.all([...printHost.querySelectorAll('img')].map((img) => img.decode().catch(() => {})));
      window.print();
    } finally {
      printBtn.innerHTML = label;
      printBtn.disabled = false;
      setTimeout(() => { urls.forEach(URL.revokeObjectURL); printHost.innerHTML = ''; }, 1000);
    }
  };
  printBtn?.addEventListener('click', preparePrint);
  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'p') {
      e.preventDefault();
      if (cfg.canPrint) preparePrint();
    }
  });

  (async () => {
    try {
      pdfjs = await import(cfg.lib);
      pdfjs.GlobalWorkerOptions.workerSrc = cfg.worker;
      doc = await pdfjs.getDocument({
        url: cfg.src, withCredentials: true, standardFontDataUrl: cfg.fonts, cMapUrl: cfg.cmaps, cMapPacked: true,
        wasmUrl: cfg.wasm, useWasm: false, isEvalSupported: false, enableXfa: false,
      }).promise;
    } catch (e) {
      console.warn('PDF load failed', e);
      fail(e?.name === 'PasswordException' ? 'This PDF is password-protected and can\'t be shown.' : 'The PDF couldn\'t be shown. Reload the page, or ask Document Control to check the rendition.');
      return;
    }
    pagesEl.textContent = String(doc.numPages);
    pageNo.max = String(doc.numPages);
    for (let i = 1; i <= doc.numPages; i++) {
      const page = await doc.getPage(i);
      const el = document.createElement('div');
      el.className = 'vl-pdfv-sheet';
      el.dataset.page = String(i);
      el.setAttribute('role', 'group');
      el.setAttribute('aria-label', `Page ${i} of ${doc.numPages}`);
      el.style.setProperty('--scale-round-x', '1px');
      el.style.setProperty('--scale-round-y', '1px');
      host.append(el);
      pages.push({ el, page, base: page.getViewport({ scale: 1 }), rendered: false, task: null });
      track.observe(el);
    }
    stateEl.hidden = true;
    if (printBtn) printBtn.disabled = false;
    layout();
  })();
})();
