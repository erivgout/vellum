// Change Management, Audits and Inspections: hover tooltips for chart marks ([data-tip]).
// No inline handlers (strict CSP): one delegated listener positions a single tooltip element.
(() => {
  "use strict";
  let tip = null;
  const show = (el, x, y) => {
    if (!tip) {
      tip = document.createElement("div");
      tip.className = "vl-viz-tip";
      tip.setAttribute("role", "tooltip");
      document.body.appendChild(tip);
    }
    tip.textContent = el.dataset.tip;
    tip.hidden = false;
    const r = tip.getBoundingClientRect();
    const left = Math.min(Math.max(8, x - r.width / 2), window.innerWidth - r.width - 8);
    const top = y - r.height - 12 < 8 ? y + 16 : y - r.height - 12;
    tip.style.left = `${left}px`;
    tip.style.top = `${top}px`;
  };
  const hide = () => { if (tip) tip.hidden = true; };

  document.addEventListener("mousemove", (e) => {
    const el = e.target instanceof Element ? e.target.closest("[data-tip]") : null;
    if (el) show(el, e.clientX, e.clientY); else hide();
  });
  document.addEventListener("focusin", (e) => {
    const el = e.target instanceof Element ? e.target.closest("[data-tip]") : null;
    if (!el) return;
    const r = el.getBoundingClientRect();
    show(el, r.left + r.width / 2, r.top);
  });
  document.addEventListener("focusout", hide);
  window.addEventListener("scroll", hide, { passive: true });
})();
