// Quality suite client behaviour (progressive enhancement only; the server enforces every rule).
(function () {
  "use strict";

  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  // ------------------------------------------------------------------ conditional sections and tabs
  // A section whose fields are all conditional and currently hidden (VisibleWhen) is hidden too, e.g. NCR
  // supplier details for finished product (NC-001) or the 8D disciplines on a non-8D CAPA (CAPA-006).
  // A tab whose sections are all hidden disappears from the tab bar.
  function syncSections(form) {
    $$(".vl-section", form).forEach((sec) => {
      const fields = $$(":scope > .card-body > .row > .vl-field", sec);
      if (!fields.length || !fields.some((f) => f.hasAttribute("data-visible-when") && f.dataset.visibleWhen)) return;
      const hide = fields.every((f) => f.hidden);
      if (sec.hidden !== hide) sec.hidden = hide;
    });
    $$(".tab-content > .tab-pane", form).forEach((pane) => {
      const sections = $$(":scope > .vl-section", pane);
      if (!sections.length) return;
      const btn = form.querySelector(`[data-bs-target="#${CSS.escape(pane.id)}"]`);
      const item = btn?.closest(".nav-item");
      if (!item) return;
      const hide = sections.every((s) => s.hidden);
      if (item.hidden !== hide) item.hidden = hide;
      if (hide && btn.classList.contains("active")) {
        const first = $$(".vl-tabs .nav-item:not([hidden]) .nav-link", form)[0];
        if (first && window.bootstrap) window.bootstrap.Tab.getOrCreateInstance(first).show();
      }
    });
  }

  $$("form[data-record-form]").forEach((form) => {
    let queued = false;
    const run = () => { queued = false; syncSections(form); };
    const queue = () => { if (!queued) { queued = true; requestAnimationFrame(run); } };
    new MutationObserver(queue).observe(form, { subtree: true, attributes: true, attributeFilter: ["hidden"] });
    queue();
  });

  // ------------------------------------------------------------------ reporting-clock countdowns (CMP-009)
  function fmt(ms) {
    const overdue = ms < 0;
    let m = Math.floor(Math.abs(ms) / 60000);
    const d = Math.floor(m / 1440); m -= d * 1440;
    const h = Math.floor(m / 60); m -= h * 60;
    const text = d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
    return overdue ? `Overdue ${text}` : `${text} left`;
  }
  function tick() {
    $$("[data-vlq-deadline]").forEach((el) => {
      const [y, mo, d] = el.dataset.vlqDeadline.split("-").map(Number);
      if (!y) return;
      const end = new Date(y, mo - 1, d, 23, 59, 59);
      const text = fmt(end.getTime() - Date.now());
      if (el.textContent !== text) el.textContent = text;
      el.title = `Due by the end of ${end.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short", year: "numeric" })}`;
    });
  }
  if (document.querySelector("[data-vlq-deadline]")) {
    tick();
    setInterval(tick, 30000);
  }
})();
