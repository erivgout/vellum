// Vellum Insights: chart-builder and alert-editor form interactivity.
// Data-attribute hooks only (strict CSP: no inline script). Charts themselves are server-rendered SVG.
//   [data-ins-builder]  chart builder form (live preview into [data-ins-preview], POST to data-preview-url)
//   [data-ins-alert]    alert template form
//   #ins-catalog        JSON island: { apps:[{key,name,views:[{key,name,category,forms}]}], forms:{key:[{key,label,kind}]}, system:[...] }
//   [data-show="expr"]  show when expr holds; inputs inside hidden groups are disabled so they aren't posted
(() => {
  "use strict";
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const island = $("#ins-catalog");
  const form = $("[data-ins-builder]") || $("[data-ins-alert]");
  if (!island || !form) return;

  const catalog = JSON.parse(island.textContent || "{}");
  const apps = new Map((catalog.apps || []).map((a) => [a.key, a]));
  const appSel = $("[data-ins-app]", form);
  const viewSel = $("[data-ins-view]", form);

  function columnsFor(appKey, viewKey) {
    const view = apps.get(appKey)?.views.find((v) => v.key === viewKey);
    if (!view) return [];
    const cols = (catalog.system || []).slice();
    const seen = new Set(cols.map((c) => c.key));
    for (const f of view.forms) {
      for (const c of (catalog.forms || {})[f] || []) {
        if (!seen.has(c.key)) { seen.add(c.key); cols.push(c); }
      }
    }
    return cols;
  }

  function fillViews() {
    const app = apps.get(appSel.value);
    const current = viewSel.value;
    viewSel.textContent = "";
    if (!app) return;
    const groups = new Map();
    for (const v of app.views) {
      if (!groups.has(v.category)) groups.set(v.category, []);
      groups.get(v.category).push(v);
    }
    for (const [label, views] of groups) {
      const og = document.createElement("optgroup");
      og.label = label;
      for (const v of views) og.append(new Option(v.name, v.key, false, v.key === current));
      viewSel.append(og);
    }
    if (!app.views.some((v) => v.key === current)) viewSel.value = (app.views.find((v) => v.key === "open") || app.views[0]).key;
  }

  const accepts = {
    group: (c) => c.kind !== "number",
    number: (c) => c.kind === "number",
    date: (c) => c.kind === "date",
    match: (c) => c.kind !== "number" && c.kind !== "date",
    all: () => true,
  };

  function fillColumns() {
    const cols = columnsFor(appSel.value, viewSel.value);
    for (const sel of $$("select[data-columns]", form)) {
      const keep = new Set($$("option", sel).filter((o) => o.selected && o.value).map((o) => o.value));
      const list = cols.filter(accepts[sel.dataset.columns] || accepts.all);
      sel.textContent = "";
      if (sel.dataset.blank !== undefined) sel.append(new Option(sel.dataset.blank, ""));
      else if (sel.dataset.columns === "number") sel.append(new Option("Choose…", ""));
      for (const c of list) {
        const o = new Option(c.label, c.key, false, keep.has(c.key));
        o.dataset.kind = c.kind;
        sel.append(o);
      }
      if (!sel.multiple && sel.dataset.columns === "date" && !sel.value) sel.value = "created";
      if (!sel.multiple && sel.dataset.columns === "group" && !sel.value && sel.dataset.blank === undefined && list.length) sel.value = list[0].key;
    }
  }

  // ------------------------------------------------------------ show / hide
  function state() {
    const val = (name) => form.elements[name]?.value;
    const kind = (sel) => (sel && sel.value ? sel.selectedOptions[0]?.dataset.kind || "other" : "none");
    const type = $("[data-ins-type]:checked", form)?.value || "column";
    const cmp = kind($("[data-ins-cmp]", form));
    const limitsTypes = ["column", "bar", "line", "pareto", "stacked_column", "stacked_bar"];
    const limitsSel = $("[data-ins-limits]", form);
    const limits = limitsSel && limitsTypes.includes(type) && cmp === "none" ? limitsSel.value : "none";
    return {
      action: $("[name='Input.Action']:checked", form)?.value || "notify",
      type, cat: kind($("[data-ins-cat]", form)), cmp,
      agg: val("Def.Aggregate"), range: val("Def.Range"), loc: val("Def.LocationScope"), limits,
      lperiod: val("Def.LimitsPeriod"),
      cmpallowed: !["pareto", "gauge", "donut"].includes(type) && limits === "none",
      cmprequired: ["stacked_column", "stacked_bar", "heatmap"].includes(type),
      sortallowed: !["pareto", "gauge", "donut", "heatmap"].includes(type) && cmp === "none",
      limitsallowed: limitsTypes.includes(type) && cmp === "none",
    };
  }

  function holds(expr, s) {
    return expr.split("&").every((part) => {
      const p = part.trim();
      const ne = p.indexOf("!=");
      if (ne > 0) return String(s[p.slice(0, ne)]) !== p.slice(ne + 2);
      const eq = p.indexOf("=");
      if (eq > 0) return String(s[p.slice(0, eq)]) === p.slice(eq + 1);
      return !!s[p];
    });
  }

  function applyShow() {
    const s = state();
    for (const el of $$("[data-show]", form)) el.hidden = !holds(el.dataset.show, s);
    for (const el of $$("input, select, textarea", form)) {
      if (el.type === "hidden" && el.name === "__RequestVerificationToken") continue;
      el.disabled = !!el.closest("[data-show][hidden]");
    }
  }

  // ------------------------------------------------------------ live preview (chart builder)
  const preview = $("[data-ins-preview]");
  let timer = 0;
  let seq = 0;
  async function refresh() {
    if (!preview || !form.dataset.previewUrl) return;
    const mine = ++seq;
    preview.classList.add("is-loading");
    try {
      const token = $('meta[name="csrf-token"]')?.content;
      const res = await fetch(form.dataset.previewUrl, {
        method: "POST", body: new FormData(form), credentials: "same-origin",
        headers: token ? { "X-CSRF-TOKEN": token } : {},
      });
      if (mine !== seq) return;
      if (res.ok) preview.innerHTML = await res.text();
    } catch {
      /* keep the previous render */
    } finally {
      if (mine === seq) preview.classList.remove("is-loading");
    }
  }
  const schedule = (ms = 350) => { clearTimeout(timer); timer = setTimeout(refresh, ms); };

  form.addEventListener("change", (e) => {
    if (e.target === appSel) { fillViews(); fillColumns(); }
    else if (e.target === viewSel) fillColumns();
    applyShow();
    schedule(150);
  });
  form.addEventListener("input", (e) => {
    if (e.target.matches("input[type=text], input:not([type]), input[type=number], textarea")) schedule(600);
  });

  applyShow();
})();
