// Vellum Designer (Areas/Designer). Loaded on every page, so everything is keyed off designer-only data attributes.
// Designers edit a copy of a configuration component in the browser and save it to the server-side draft (DSN-003);
// the server re-validates and re-authorizes every save (INV-082). No inline script: data comes from JSON islands.
(function () {
  "use strict";

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const api = (url, opts) => window.vl.api(url, opts);
  const toast = (m, k) => window.vl && window.vl.toast(m, k);
  let uidSeq = 0;
  const uid = () => "u" + (++uidSeq);
  const strip = (o) => JSON.parse(JSON.stringify(o, (k, v) => (k.startsWith("_") ? undefined : v)));
  const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
  const typing = (el) => !!el && !!el.closest && !!el.closest("input:not([type=checkbox]):not([type=radio]):not([type=range]),textarea,select,[contenteditable]");

  function slug(name, max = 40) {
    let s = String(name || "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
    if (!s) s = "item";
    if (/^[0-9]/.test(s)) s = "n_" + s;
    return s.length > max ? s.slice(0, max).replace(/_+$/, "") : s;
  }
  function uniqueKey(base, taken) {
    let k = base, i = 2;
    while (taken.has(k.toLowerCase())) k = `${base}_${i++}`;
    return k;
  }
  const keyProblem = (k) => !k ? "is required" : !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(k) ? "may only use letters, digits, _ and -" : /^[0-9]+$/.test(k) ? "can't be only digits (INV-130)" : null;

  // =====================================================================================================
  // Shared page behaviour
  // =====================================================================================================

  // Design name suggestions from display names.
  $$("[data-dz-slug-source]").forEach((src) => {
    const target = $(src.dataset.dzSlugSource);
    if (!target) return;
    let manual = target.value.trim().length > 0;
    target.addEventListener("input", () => { manual = target.value.trim().length > 0; });
    src.addEventListener("input", () => { if (!manual) target.value = slug(src.value); });
  });

  // Icon library picker (DSN-101).
  const iconModalEl = $("#dz-icon-modal");
  let iconNames = null, iconCallback = null, iconCurrent = "";
  async function loadIcons() {
    if (iconNames) return iconNames;
    const res = await fetch("/lib/bootstrap-icons/font/bootstrap-icons.json", { credentials: "same-origin" });
    iconNames = Object.keys(await res.json());
    return iconNames;
  }
  function renderIcons(term) {
    if (!iconModalEl || !iconNames) return;
    const grid = $("[data-dz-icon-grid]", iconModalEl);
    const t = term.trim().toLowerCase().replace(/^bi-/, "");
    const all = iconNames.filter((n) => !t || n.includes(t));
    const list = all.slice(0, 300);
    grid.innerHTML = list.length
      ? list.map((n) => `<button type="button" class="dz-icon-opt${"bi-" + n === iconCurrent ? " on" : ""}" data-icon="bi-${n}" role="option" aria-selected="${"bi-" + n === iconCurrent}" title="bi-${n}"><i class="bi bi-${n}"></i><span>${n}</span></button>`).join("")
      : `<div class="vl-picker-none py-4">No icons match “${esc(term)}”.</div>`;
    $("[data-dz-icon-count]", iconModalEl).textContent = all.length > list.length ? `Showing ${list.length} of ${all.length} – refine the search` : `${all.length} icon${all.length === 1 ? "" : "s"}`;
  }
  const iconPicker = {
    async open(current, cb) {
      if (!iconModalEl) return;
      iconCurrent = current || "";
      iconCallback = cb;
      const search = $("[data-dz-icon-search]", iconModalEl);
      search.value = "";
      bootstrap.Modal.getOrCreateInstance(iconModalEl).show();
      try { await loadIcons(); renderIcons(""); } catch { $("[data-dz-icon-grid]", iconModalEl).innerHTML = `<div class="vl-picker-none py-4">The icon library couldn't be loaded.</div>`; }
      setTimeout(() => search.focus(), 200);
    },
  };
  if (iconModalEl) {
    const search = $("[data-dz-icon-search]", iconModalEl);
    search.addEventListener("input", debounce(() => renderIcons(search.value), 80));
    iconModalEl.addEventListener("click", (e) => {
      const term = e.target.closest("[data-dz-icon-term]");
      if (term) { search.value = term.dataset.dzIconTerm; renderIcons(search.value); search.focus(); return; }
      const opt = e.target.closest("[data-icon]");
      if (!opt) return;
      const cb = iconCallback;
      iconCallback = null;
      bootstrap.Modal.getOrCreateInstance(iconModalEl).hide();
      if (cb) cb(opt.dataset.icon);
    });
    search.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); const first = $("[data-icon]", iconModalEl); if (first) first.click(); } });
  }
  document.addEventListener("click", (e) => {
    const b = e.target.closest("[data-dz-icon-browse]");
    if (!b) return;
    const input = $(b.dataset.dzIconBrowse);
    if (!input) return;
    iconPicker.open(input.value, (icon) => { input.value = icon; input.dispatchEvent(new Event("input", { bubbles: true })); input.focus(); });
  });
  function setIcon(el, icon) { if (el) el.className = "bi " + icon; }
  document.addEventListener("input", (e) => {
    const t = e.target;
    if (t.matches("[data-dz-icon-input]")) {
      const icon = t.value.trim();
      $$(`[data-dz-icon-preview="#${CSS.escape(t.id)}"]`).forEach((el) => setIcon(el, icon));
      $$("[data-dz-out-tile] i, [data-dz-tile-preview] i").forEach((el) => setIcon(el, icon));
    }
    if (t.matches("[data-dz-color-input]")) {
      $$("[data-dz-out-tile], [data-dz-tile-preview]").forEach((el) => el.style.setProperty("--app", t.value));
      $$("[data-dz-swatch]").forEach((s) => s.classList.toggle("on", s.dataset.dzSwatch.toLowerCase() === t.value.toLowerCase()));
    }
    if (t.matches("[data-dz-preview-name]")) $$("[data-dz-out-name]").forEach((el) => { el.textContent = t.value || "Untitled"; });
    if (t.matches("[data-dz-preview-desc]")) $$("[data-dz-out-desc]").forEach((el) => { el.textContent = t.value; });
  });
  document.addEventListener("change", (e) => {
    if (e.target.matches("[data-dz-preview-suite]")) $$("[data-dz-out-suite]").forEach((el) => { el.textContent = e.target.value; });
  });
  document.addEventListener("click", (e) => {
    const s = e.target.closest("[data-dz-swatch]");
    if (!s) return;
    const input = $(s.dataset.dzSwatchFor);
    if (input && !input.disabled) { input.value = s.dataset.dzSwatch; input.dispatchEvent(new Event("input", { bubbles: true })); }
  });
  $$("[data-dz-color-input]").forEach((t) => $$("[data-dz-swatch]").forEach((s) => s.classList.toggle("on", s.dataset.dzSwatch.toLowerCase() === t.value.toLowerCase())));

  // "New form" buttons preselect the form type; "Copy view" preselects the source.
  document.addEventListener("click", (e) => {
    const k = e.target.closest("[data-dz-kind]");
    if (k) { const r = $(`#dz-nf-${k.dataset.dzKind}`); if (r) r.checked = true; }
    const c = e.target.closest("[data-dz-copy]");
    if (c) {
      const sel = $("[data-dz-copy-select]");
      if (sel) sel.value = c.dataset.dzCopy;
      const name = $("#dz-nv-name");
      if (name) { name.value = c.dataset.dzCopyName || ""; name.dispatchEvent(new Event("input", { bubbles: true })); }
    } else if (e.target.closest('[data-bs-target="#dz-new-view"]')) {
      const sel = $("[data-dz-copy-select]");
      if (sel) sel.value = "";
    }
  });

  // DSN-004: keep component locks alive while the editor is open.
  $$("[data-dz-heartbeat]").forEach((el) => {
    const url = el.dataset.dzHeartbeat;
    if (!url) return;
    let warned = false;
    setInterval(async () => {
      try {
        const r = await api(url, { method: "POST" });
        if (r && r.ok === false && !warned) { warned = true; toast(`${r.holder || "Another designer"} now holds the lock. Your next save will be refused.`, "bad"); }
      } catch { /* offline: the next save reports it */ }
    }, 60000);
  });

  // Unsaved-change guard for plain designer forms (settings).
  $$("form[data-dz-dirty-guard]").forEach((f) => {
    let dirty = false, submitting = false;
    f.addEventListener("input", () => { dirty = true; });
    f.addEventListener("change", () => { dirty = true; });
    f.addEventListener("submit", () => { submitting = true; });
    window.addEventListener("beforeunload", (e) => { if (dirty && !submitting) { e.preventDefault(); e.returnValue = ""; } });
  });

  // =====================================================================================================
  // Workbench: undo/redo, dirty state, keyboard shortcuts and the problems list, shared by the three designers.
  // =====================================================================================================

  function workbench(root, o) {
    const status = $("[data-dz-status]", root);
    const undoBtn = $("[data-dz-undo]", root), redoBtn = $("[data-dz-redo]", root), saveBtn = $("[data-dz-save]", root);
    const problems = $("[data-dz-problems]", root), problemCount = $("[data-dz-problems-count]", root);
    const wb = { dirty: false, saving: false, undo: [], redo: [], pending: null };

    wb.setStatus = (state) => {
      if (!status) return;
      const s = state || (wb.dirty ? "dirty" : "saved");
      status.className = "dz-status " + (s === "saved" ? "" : s);
      status.innerHTML = {
        saved: '<i class="bi bi-check2-circle"></i>Saved to draft',
        dirty: '<i class="bi bi-circle-fill" style="font-size:.5rem"></i>Unsaved changes',
        saving: '<span class="spinner-border spinner-border-sm"></span>Saving…',
        error: '<i class="bi bi-exclamation-circle"></i>Not saved',
      }[s];
      if (undoBtn) undoBtn.disabled = o.readOnly || wb.undo.length === 0;
      if (redoBtn) redoBtn.disabled = o.readOnly || wb.redo.length === 0;
      if (saveBtn) saveBtn.disabled = o.readOnly || wb.saving;
    };
    wb.snapshot = () => { wb.undo.push(o.serialize()); if (wb.undo.length > 100) wb.undo.shift(); wb.redo = []; };
    wb.changed = () => { wb.dirty = true; wb.setStatus(); if (o.onChange) o.onChange(); };
    wb.doUndo = () => { if (!wb.undo.length) return; wb.redo.push(o.serialize()); o.restore(wb.undo.pop()); wb.changed(); o.render(); };
    wb.doRedo = () => { if (!wb.redo.length) return; wb.undo.push(o.serialize()); o.restore(wb.redo.pop()); wb.changed(); o.render(); };
    if (undoBtn) undoBtn.addEventListener("click", wb.doUndo);
    if (redoBtn) redoBtn.addEventListener("click", wb.doRedo);
    if (saveBtn) saveBtn.addEventListener("click", () => o.save());
    document.addEventListener("keydown", (e) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      const k = e.key.toLowerCase();
      if (k === "s") { e.preventDefault(); if (!o.readOnly) o.save(); }
      else if (k === "z" && !e.shiftKey && !typing(document.activeElement)) { e.preventDefault(); if (!o.readOnly) wb.doUndo(); }
      else if ((k === "y" || (k === "z" && e.shiftKey)) && !typing(document.activeElement)) { e.preventDefault(); if (!o.readOnly) wb.doRedo(); }
    });
    window.addEventListener("beforeunload", (e) => { if (wb.dirty) { e.preventDefault(); e.returnValue = ""; } });

    // Property panels: one undo step per control edit (snapshot on focus, committed on the first change).
    wb.bindPanel = (panel, apply) => {
      panel.addEventListener("focusin", (e) => { if (e.target.closest("[data-p],[data-pm]")) wb.pending = o.serialize(); });
      const onEdit = (e) => {
        const el = e.target.closest("[data-p],[data-pm]");
        if (!el || o.readOnly) return;
        if (e.type === "input" && (el.type === "checkbox" || el.type === "radio" || el.tagName === "SELECT")) return;
        if (wb.pending) { wb.undo.push(wb.pending); wb.redo = []; wb.pending = null; }
        else if (e.type === "change" && (el.type === "checkbox" || el.tagName === "SELECT")) wb.snapshot();
        apply(el, e.type);
        wb.changed();
      };
      panel.addEventListener("input", onEdit);
      panel.addEventListener("change", onEdit);
    };

    wb.setIssues = (issues, onPick) => {
      if (!problems) return;
      const n = issues.length, errs = issues.filter((i) => i.isError).length;
      problemCount.textContent = n;
      problemCount.classList.toggle("hot", errs > 0);
      problems.innerHTML = n === 0
        ? `<div class="vl-picker-none py-3"><i class="bi bi-check-circle-fill text-success"></i> No problems. This component passes the publish checks.</div>`
        : `<div class="small text-body-tertiary px-2 pb-2">As of the last save or analysis. Errors block publishing (DSN-1201).</div><ul class="dz-issues">${issues.map((i, ix) =>
          `<li class="${i.isError ? "is-error" : "is-warn"}" data-issue="${ix}" tabindex="0"><i class="bi ${i.isError ? "bi-x-circle-fill" : "bi-exclamation-triangle-fill"}" aria-hidden="true"></i><span><span class="visually-hidden">${i.isError ? "Error" : "Warning"}:</span><b>${esc(i.object)}</b> ${esc(i.message)}${i.rule ? ` <span class="dz-rule">${esc(i.rule)}</span>` : ""}</span></li>`).join("")}</ul>`;
      problems.onclick = (e) => { const li = e.target.closest("[data-issue]"); if (li && onPick) onPick(issues[+li.dataset.issue]); };
      problems.onkeydown = (e) => { if (e.key === "Enter") { const li = e.target.closest("[data-issue]"); if (li && onPick) onPick(issues[+li.dataset.issue]); } };
    };
    wb.setStatus();
    return wb;
  }

  /** Read a bound control's value. data-t: int | num | bool | str (default: str, empty → null). */
  function readValue(el) {
    if (el.type === "checkbox" && el.dataset.p) return el.checked;
    const v = el.value;
    switch (el.dataset.t) {
      case "int": return v === "" ? null : parseInt(v, 10);
      case "num": return v === "" ? null : Number(v);
      case "str": return v;
      default: return v === "" ? null : v;
    }
  }
  /** data-pm="list" value="x": checkbox membership in an array property. */
  function applyMember(obj, el) {
    const list = obj[el.dataset.pm] || (obj[el.dataset.pm] = []);
    const i = list.indexOf(el.value);
    if (el.checked && i < 0) list.push(el.value);
    if (!el.checked && i >= 0) list.splice(i, 1);
  }

  // ---------------------------------------------------------------- property panel HTML helpers
  let pid = 0;
  const nid = () => "dzp" + (++pid);
  const H = {
    text(p, label, val, extra = "", hint = "") { const id = nid(); return `<div class="dz-prop"><label for="${id}">${label}</label><input class="form-control" id="${id}" data-p="${p}" value="${esc(val ?? "")}" ${extra}>${hint ? `<div class="dz-hint">${hint}</div>` : ""}</div>`; },
    area(p, label, val, extra = "", hint = "") { const id = nid(); return `<div class="dz-prop"><label for="${id}">${label}</label><textarea class="form-control" id="${id}" data-p="${p}" rows="2" ${extra}>${esc(val ?? "")}</textarea>${hint ? `<div class="dz-hint">${hint}</div>` : ""}</div>`; },
    num(p, label, val, extra = "", hint = "") { const id = nid(); return `<div class="dz-prop"><label for="${id}">${label}</label><input type="number" class="form-control" id="${id}" data-p="${p}" data-t="${/step="any"|data-t="num"/.test(extra) ? "num" : "int"}" value="${val ?? ""}" ${extra}>${hint ? `<div class="dz-hint">${hint}</div>` : ""}</div>`; },
    select(p, label, opts, val, extra = "", hint = "") {
      const id = nid();
      return `<div class="dz-prop"><label for="${id}">${label}</label><select class="form-select" id="${id}" data-p="${p}" ${extra}>${opts.map(([v, l, dis]) => `<option value="${esc(v)}"${String(v) === String(val ?? "") ? " selected" : ""}${dis ? " disabled" : ""}>${esc(l)}</option>`).join("")}</select>${hint ? `<div class="dz-hint">${hint}</div>` : ""}</div>`;
    },
    check(p, label, val, extra = "", hint = "") { const id = nid(); return `<div class="form-check form-switch"><input class="form-check-input" type="checkbox" role="switch" id="${id}" data-p="${p}" ${val ? "checked" : ""} ${extra}><label class="form-check-label" for="${id}">${label}</label>${hint ? `<div class="dz-hint">${hint}</div>` : ""}</div>`; },
    members(pm, items, selected, extra = "") {
      if (!items.length) return `<div class="dz-hint">Nothing to choose from.</div>`;
      let last = null, html = "";
      for (const it of items) {
        if (it.group && it.group !== last) { html += `<div class="grp">${esc(it.group)}</div>`; last = it.group; }
        const id = nid();
        html += `<div class="form-check"><input class="form-check-input" type="checkbox" id="${id}" data-pm="${pm}" value="${esc(it.value)}" ${selected.includes(it.value) ? "checked" : ""} ${extra}><label class="form-check-label" for="${id}">${esc(it.label)}</label></div>`;
      }
      return `<div class="dz-checklist">${html}</div>`;
    },
    group(title, icon, body) { return `<div class="dz-prop-group"><div class="dz-panel-title"><i class="bi ${icon}"></i>${title}</div>${body}</div>`; },
    key(p, val, locked, label = "Design name") {
      const id = nid();
      return `<div class="dz-prop"><label for="${id}">${label}</label><input class="form-control mono" id="${id}" data-p="${p}" data-t="str" value="${esc(val)}" ${locked ? "readonly" : ""} spellcheck="false" autocomplete="off">${locked
        ? `<div class="dz-lockkey"><i class="bi bi-lock"></i>Fixed once published: records and rules refer to it (INV-130).</div>`
        : `<div class="dz-hint">Unique, letters, digits and _ only, never all digits. Fixed after publishing.</div>`}</div>`;
    },
  };

  /** DSN-305 visibility matrix for draft documents (mirrors FormRules.StateOf). */
  function sectionState(s, p) {
    const terminal = p.type !== "Normal";
    const has = (list, k) => (list || []).some((x) => x.toLowerCase() === k.toLowerCase());
    let st;
    if (has(p.hiddenSections, s.key)) st = "hidden";
    else if (!(p.editableSections || []).length && !(p.readOnlySections || []).length) st = terminal ? "ro" : "edit";
    else if (has(p.readOnlySections, s.key)) st = "ro";
    else if (has(p.editableSections, s.key) || (p.editableSections || []).includes("*")) st = "edit";
    else if ((p.readOnlySections || []).includes("*")) st = "ro";
    else st = "hidden";
    return terminal && st === "edit" ? "ro" : st;
  }
  const stateLabel = { edit: "Editable", ro: "Read-only", hidden: "Hidden" };

  /** Simple condition evaluation (FormRules.Matches): clauses joined by && and ||. */
  function matches(cond, valuesOf) {
    if (!cond || !cond.trim()) return true;
    return cond.split("||").some((any) => any.split("&&").every((c) => {
      c = c.trim();
      if (!c) return true;
      const neg = c.includes("!=");
      const parts = c.split(neg ? "!=" : "=");
      const key = parts[0].trim();
      if (parts.length === 1) return key.startsWith("!") ? valuesOf(key.slice(1)).length === 0 : valuesOf(key).length > 0;
      const want = parts.slice(1).join("=").trim().toLowerCase();
      const hit = valuesOf(key).some((v) => String(v).toLowerCase() === want);
      return neg ? !hit : hit;
    }));
  }

  function conditionBuilder(fields, selfKey) {
    const opts = fields.filter((f) => f.key !== selfKey && f.type !== "Prompt");
    return `<div class="dz-cond" data-cond>
      <select class="form-select form-select-sm" data-cond-field aria-label="Field">${opts.map((f) => `<option value="${esc(f.key)}">${esc(f.label || f.key)}</option>`).join("")}</select>
      <select class="form-select form-select-sm" data-cond-op aria-label="Operator"><option value="=">is</option><option value="!=">is not</option><option value="set">has a value</option><option value="empty">is empty</option></select>
      <div class="full" data-cond-value></div>
      <div class="full d-flex gap-1"><button type="button" class="btn btn-sm btn-light flex-fill" data-cond-apply="set"><i class="bi bi-check2"></i>Use this rule</button><button type="button" class="btn btn-sm btn-ghost" data-cond-apply="and" title="Combine with the current rule (AND)">+ AND</button><button type="button" class="btn btn-sm btn-ghost" data-cond-apply="or" title="Alternative (OR)">+ OR</button></div>
    </div>`;
  }
  function condValueControl(field, lists) {
    if (!field) return `<input class="form-control form-control-sm" data-cond-val placeholder="Value" aria-label="Value">`;
    if (field.type === "YesNo") return `<select class="form-select form-select-sm" data-cond-val aria-label="Value"><option value="yes">Yes</option><option value="no">No</option></select>`;
    const listKey = field.keywordList || field.list;
    if ((field.type === "Keyword" || field.type === "KeywordMulti") && lists[listKey]) {
      const options = lists[listKey].options || lists[listKey];
      return `<select class="form-select form-select-sm" data-cond-val aria-label="Value">${options.map((k) => `<option value="${esc(k.key)}">${esc(k.label)}</option>`).join("")}</select>`;
    }
    return `<input class="form-control form-control-sm" data-cond-val placeholder="Value" aria-label="Value">`;
  }
  /** Wire a condition builder inside `container`; `fields` resolves keys; `setCond(expr)` stores the result. */
  function wireCondition(container, fields, lists, getCond, setCond) {
    const b = $("[data-cond]", container);
    if (!b) return;
    const fSel = $("[data-cond-field]", b), opSel = $("[data-cond-op]", b), wrap = $("[data-cond-value]", b);
    const refresh = () => {
      const f = fields.find((x) => x.key === fSel.value);
      wrap.innerHTML = opSel.value === "set" || opSel.value === "empty" ? "" : condValueControl(f, lists);
    };
    fSel.addEventListener("change", refresh);
    opSel.addEventListener("change", refresh);
    refresh();
    b.addEventListener("click", (e) => {
      const btn = e.target.closest("[data-cond-apply]");
      if (!btn) return;
      const key = fSel.value;
      if (!key) return;
      const val = $("[data-cond-val]", b)?.value?.trim() || "";
      const clause = opSel.value === "set" ? key : opSel.value === "empty" ? "!" + key : `${key}${opSel.value}${val}`;
      const cur = (getCond() || "").trim();
      const mode = btn.dataset.condApply;
      setCond(mode === "set" || !cur ? clause : `${cur} ${mode === "and" ? "&&" : "||"} ${clause}`);
    });
  }

  // =====================================================================================================
  // Form designer (DSN §3.3–3.4)
  // =====================================================================================================

  const formRoot = $("[data-dz-form-designer]");
  if (formRoot) formDesigner(formRoot);

  function formDesigner(root) {
    const data = JSON.parse($("#dz-form-data").textContent);
    const ro = !!data.readOnly;
    const types = data.types;
    const typeBy = Object.fromEntries(types.map((t) => [t.value, t]));
    const lists = Object.fromEntries(data.keywordLists.map((l) => [l.key, l]));
    const dataCount = data.data || {};
    let issues = data.issues || [];
    let form = data.form;
    const canvas = $("[data-dz-canvas]", root), left = $("[data-dz-left]", root), props = $("[data-dz-props]", root);
    const phaseSel = $("[data-dz-preview-phase]", root);
    let sel = { kind: "form" }, activeTab = null, mode = "design", leftTab = "add", previewPhase = "", previewTab = null, drag = null;

    function normField(x) {
      x._uid = x._uid || uid();
      if (x._orig === undefined) x._orig = x.key;
      x.requiredInPhases = x.requiredInPhases || [];
      x.linkForms = x.linkForms || [];
      return x;
    }
    function prepare(f) {
      if (f._orig === undefined) f._orig = f.key;
      f.parentForms = f.parentForms || [];
      f.sections.forEach((s) => { s._uid = s._uid || uid(); if (s._orig === undefined) s._orig = s.key; s.fields.forEach(normField); });
      f.detached = f.detached || [];
      f.detached.forEach(normField);
      f.workflows = f.workflows || [];
    }
    prepare(form);

    const wb = workbench(root, {
      readOnly: ro,
      serialize: () => JSON.stringify({ form, sel, activeTab }),
      restore: (s) => { const o = JSON.parse(s); form = o.form; sel = o.sel; activeTab = o.activeTab; },
      render: () => render(),
      save: () => save(),
    });

    // ------------------------------------------------------------ model helpers
    const tabs = () => { const t = []; form.sections.forEach((s) => { const n = s.tab || "General"; if (!t.includes(n)) t.push(n); }); return t; };
    const allFields = () => form.sections.flatMap((s) => s.fields).concat(form.detached);
    const mainFields = () => form.sections.filter((s) => !s.isSubform).flatMap((s) => s.fields);
    const findSection = (u) => form.sections.find((s) => s._uid === u);
    function findField(u) {
      for (const s of form.sections) { const i = s.fields.findIndex((x) => x._uid === u); if (i >= 0) return { f: s.fields[i], s, i, list: s.fields }; }
      const i = form.detached.findIndex((x) => x._uid === u);
      return i >= 0 ? { f: form.detached[i], s: null, i, list: form.detached } : null;
    }
    const phases = () => form.workflows.flatMap((w) => w.phases.map((p) => ({ p, w })));
    const takenFields = (except) => new Set(allFields().filter((x) => x !== except).map((x) => x.key.toLowerCase()));
    const takenSections = (except) => new Set(form.sections.filter((s) => s !== except).map((s) => s.key.toLowerCase()));
    const fieldLabel = (x) => x.type === "Prompt" ? (x.help ? "Prompt: " + x.help.slice(0, 40) : "Text prompt") : (x.label || x.key);
    const issuesFor = (path) => issues.filter((i) => i.path === `form:${form.key}/${path}` || i.path === `form:${form._orig}/${path}`);

    function targetSection() {
      if (sel.kind === "section") return findSection(sel.uid);
      if (sel.kind === "field") { const r = findField(sel.uid); if (r && r.s) return r.s; }
      return form.sections.find((s) => (s.tab || "General") === activeTab) || form.sections[0];
    }
    function newField(type, section) {
      const t = typeBy[type];
      const label = type === "Prompt" ? "" : t.label;
      const key = uniqueKey(type === "Prompt" ? "prompt" : slug(t.label), takenFields());
      const sub = section && section.isSubform;
      return normField({
        id: null, key, label, type, width: Math.min(t.width, sub ? 4 : 12), required: false, requiredInPhases: [], preventNull: false, unique: false, sensitive: false,
        readOnly: false, protectFromAdminUpdate: false, keywordList: null, linkForms: [], help: type === "Prompt" ? "Guidance shown on the form." : null,
        placeholder: null, defaultValue: null, min: null, max: null, decimals: type === "Currency" ? 2 : null, maxLength: null,
        currencyCode: type === "Currency" ? "USD" : null, showInHeader: false, visibleWhen: null, _auto: true,
      });
    }
    function addField(type, section, index) {
      if (ro) return;
      if (!section) { toast("Add a section first.", "info"); return; }
      wb.snapshot();
      const x = newField(type, section);
      section.fields.splice(index ?? section.fields.length, 0, x);
      activeTab = section.tab || "General";
      sel = { kind: "field", uid: x._uid };
      wb.changed(); render();
      focusProp("label");
    }
    function addSection(isSubform, tab) {
      if (ro) return;
      wb.snapshot();
      const name = isSubform ? "New subform" : "New section";
      const s = { id: null, key: uniqueKey(slug(name), takenSections()), name, description: null, tab: tab || activeTab || "General", icon: null, isSubform, rowNoun: isSubform ? "row" : null,
        maxRows: null, required: false, optional: false, collapsed: false, fields: [], _uid: uid(), _orig: undefined, _auto: true };
      s._orig = s.key;
      const tabSecs = form.sections.filter((x) => (x.tab || "General") === s.tab);
      const anchor = sel.kind === "section" ? findSection(sel.uid) : tabSecs[tabSecs.length - 1];
      const at = anchor ? form.sections.indexOf(anchor) + 1 : form.sections.length;
      form.sections.splice(at, 0, s);
      activeTab = s.tab;
      sel = { kind: "section", uid: s._uid };
      wb.changed(); render();
      focusProp("name");
    }
    function addTab() {
      if (ro) return;
      const existing = new Set(tabs().map((t) => t.toLowerCase()));
      let name = "New tab", i = 2;
      while (existing.has(name.toLowerCase())) name = `New tab ${i++}`;
      activeTab = name;
      addSection(false, name);
    }
    function moveField(u, targetList, index) {
      const src = findField(u);
      if (!src) return;
      src.list.splice(src.i, 1);
      targetList.splice(Math.max(0, Math.min(index, targetList.length)), 0, src.f);
    }
    function deleteField(u) {
      const r = findField(u);
      if (!r) return;
      const n = dataCount[r.f.key] || 0;
      if (r.f.id != null && !confirm(`Delete ${fieldLabel(r.f)}?${n ? ` ${n} record${n === 1 ? "" : "s"} hold values; they stay in the database but won't be shown.` : ""} You can detach it instead to keep it off the layout.`)) return;
      wb.snapshot();
      r.list.splice(r.i, 1);
      sel = r.s ? { kind: "section", uid: r.s._uid } : { kind: "form" };
      wb.changed(); render();
    }
    function deleteSection(u) {
      const s = findSection(u);
      if (!s) return;
      if (s.fields.length && !confirm(`Delete ${s.name} and its ${s.fields.length} field${s.fields.length === 1 ? "" : "s"}? Detach fields first to keep them.`)) return;
      wb.snapshot();
      form.sections.splice(form.sections.indexOf(s), 1);
      sel = { kind: "form" };
      if (!tabs().includes(activeTab)) activeTab = tabs()[0] || null;
      wb.changed(); render();
    }
    function moveSection(s, delta) {
      const ts = form.sections.filter((x) => (x.tab || "General") === (s.tab || "General"));
      const i = ts.indexOf(s), j = i + delta;
      if (j < 0 || j >= ts.length) return;
      wb.snapshot();
      const other = ts[j];
      const a = form.sections.indexOf(s), b = form.sections.indexOf(other);
      form.sections[a] = other; form.sections[b] = s;
      wb.changed(); render();
    }
    function moveTab(name, delta) {
      const t = tabs(), i = t.indexOf(name), j = i + delta;
      if (j < 0 || j >= t.length) return;
      wb.snapshot();
      const order = t.slice();
      order.splice(i, 1);
      order.splice(j, 0, name);
      form.sections.sort((a, b) => order.indexOf(a.tab || "General") - order.indexOf(b.tab || "General"));
      wb.changed(); render();
    }
    function renameTab(oldName, newName) {
      newName = (newName || "").trim();
      if (!newName || newName === oldName) return;
      wb.snapshot();
      form.sections.forEach((s) => { if ((s.tab || "General") === oldName) s.tab = newName; });
      activeTab = newName;
      wb.changed(); render();
    }
    function focusProp(p) { setTimeout(() => { const el = $(`[data-p="${p}"]`, props); if (el) { el.focus(); if (el.select) el.select(); } }, 30); }

    // ------------------------------------------------------------ rendering
    function render() {
      if (!activeTab || !tabs().includes(activeTab)) activeTab = tabs()[0] || "General";
      renderLeft(); renderCanvas(); renderProps(); syncTop();
      wb.setIssues(issues, pickIssue);
    }
    function syncTop() {
      const n = $("[data-dz-form-name]", root); if (n) n.textContent = form.name || form.key;
      const i = $("[data-dz-form-icon]", root); if (i) i.className = "bi " + (form.icon || "bi-file-earmark-text");
      phaseSel.hidden = mode !== "preview";
      const opts = [["", "New record (all editable)"], ...phases().map(({ p, w }) => [`${w.key}/${p.key}`, `${form.workflows.length > 1 ? w.name + " › " : ""}${p.name}${p.type !== "Normal" ? " (" + p.type.toLowerCase() + ")" : ""}`])];
      phaseSel.innerHTML = opts.map(([v, l]) => `<option value="${esc(v)}"${v === previewPhase ? " selected" : ""}>Preview: ${esc(l)}</option>`).join("");
    }

    function renderLeft() {
      $$("[data-dz-left-tab]", root).forEach((b) => { const on = b.dataset.dzLeftTab === leftTab; b.classList.toggle("active", on); b.setAttribute("aria-selected", on); });
      if (leftTab === "outline") { left.innerHTML = outlineHtml(); return; }
      const ts = targetSection();
      left.innerHTML = `
        <div class="dz-panel-title"><i class="bi bi-input-cursor"></i>Fields</div>
        <div class="dz-toolbox">${types.map((t) => `<button type="button" class="dz-tool" draggable="${!ro}" data-new-type="${t.value}" ${ro ? "disabled" : ""} title="Drag onto a section, or click to add to ${esc(ts ? ts.name : "the form")}"><i class="bi ${t.icon}"></i>${esc(t.label)}</button>`).join("")}</div>
        <div class="dz-hint">Drag a field onto the layout, or click to add it to <b>${esc(ts ? ts.name : "a new section")}</b>.</div>
        <div class="dz-panel-title mt-3"><i class="bi bi-layout-split"></i>Layout</div>
        <div class="dz-toolbox">
          <button type="button" class="dz-tool wide" data-act="add-section" ${ro ? "disabled" : ""}><i class="bi bi-card-heading"></i>Section</button>
          <button type="button" class="dz-tool wide" data-act="add-subform" ${ro ? "disabled" : ""}><i class="bi bi-table"></i>Subform (repeating rows)</button>
          <button type="button" class="dz-tool wide" data-act="add-tab" ${ro ? "disabled" : ""}><i class="bi bi-window-plus"></i>Tab</button>
        </div>
        <div class="dz-panel-title mt-3"><i class="bi bi-box-arrow-in-down"></i>Detached fields <span class="vl-pill-count">${form.detached.length}</span></div>
        <div class="dz-detached" data-drop-detached>${form.detached.length ? form.detached.map((x) => fieldBlock(x, null)).join("") : `<div class="dz-hint m-0">Drop a field here to take it off the layout without deleting it (DSN-302).</div>`}</div>`;
    }
    function outlineHtml() {
      const item = (kind, u, icon, label, on) => `<button type="button" class="${on ? "on" : ""}" data-pick="${kind}" data-uid="${u || ""}"><i class="bi ${icon}"></i><span class="t">${esc(label)}</span></button>`;
      return `<ul class="dz-tree" role="tree">
        <li>${item("form", "", "bi-ui-checks-grid", form.name || form.key, sel.kind === "form")}</li>
        ${tabs().map((t) => `<li class="tab">${item("tab", t, "bi-folder2", t, false)}<ul>${form.sections.filter((s) => (s.tab || "General") === t).map((s) => `<li>${item("section", s._uid, s.isSubform ? "bi-table" : "bi-card-heading", s.name || s.key, sel.kind === "section" && sel.uid === s._uid)}<ul>${s.fields.map((x) => `<li>${item("field", x._uid, typeBy[x.type]?.icon || "bi-square", fieldLabel(x), sel.kind === "field" && sel.uid === x._uid)}</li>`).join("")}</ul></li>`).join("")}</ul></li>`).join("")}
        ${form.detached.length ? `<li class="tab">${item("tab", "", "bi-box-arrow-in-down", "Detached", false)}<ul>${form.detached.map((x) => `<li>${item("field", x._uid, typeBy[x.type]?.icon || "bi-square", fieldLabel(x), sel.kind === "field" && sel.uid === x._uid)}</li>`).join("")}</ul></li>` : ""}
      </ul>`;
    }

    function fieldBlock(x, s) {
      const t = typeBy[x.type] || { icon: "bi-square", label: x.type };
      const sub = s && s.isSubform;
      const w = Math.max(1, Math.min(x.width || 6, sub ? 10 : 12));
      const badges = [
        x.showInHeader && ["bi-layout-text-sidebar-reverse", "Shown in the record header"], x.sensitive && ["bi-shield-lock", "Sensitive (SEC-520)"],
        x.readOnly && ["bi-lock", "Read-only (computed)"], x.visibleWhen && ["bi-eye", "Visible when " + x.visibleWhen],
        x.requiredInPhases.length && ["bi-asterisk", "Required in " + x.requiredInPhases.join(", ")], x.unique && ["bi-fingerprint", "Unique"],
        x.preventNull && ["bi-slash-circle", "Prevent null"], (dataCount[x.key] || 0) > 0 && ["bi-database", `${dataCount[x.key]} records hold values`],
      ].filter(Boolean);
      const bad = issuesFor(`field:${x.key}`).some((i) => i.isError);
      const on = sel.kind === "field" && sel.uid === x._uid;
      const label = fieldLabel(x);
      return `<div class="dz-fld${x.type === "Prompt" ? " is-prompt" : ""}${on ? " is-selected" : ""}${x.id == null ? " is-new" : ""}${bad ? " has-issue" : ""}" style="--w:${w}" data-fld="${x._uid}" draggable="${!ro}" tabindex="0" role="button" aria-pressed="${on}" aria-label="${esc(label)}: ${esc(t.label)} field, ${w} columns${x.required ? ", required" : ""}. Alt+arrow keys move it.">
        <div class="dz-fld-top"><i class="bi ${bad ? "bi-exclamation-triangle-fill" : t.icon}" aria-hidden="true"></i><span class="dz-fld-label">${esc(label)}${x.required ? '<span class="req" aria-hidden="true">*</span>' : ""}</span></div>
        <div class="dz-fld-meta"><span class="mono">${esc(x.key)}</span> · ${esc(t.label)}${x.keywordList ? " · " + esc(x.keywordList) : ""}</div>
        <div class="dz-fld-badges">${badges.map((b) => `<i class="bi ${b[0]}" title="${esc(b[1])}"></i>`).join("")}</div>
        ${ro || !s ? "" : `<div class="dz-fld-width"><button type="button" data-act="narrow" aria-label="Narrower" tabindex="-1">−</button><span>${w}</span><button type="button" data-act="widen" aria-label="Wider" tabindex="-1">+</button></div>`}
      </div>`;
    }

    function sectionHtml(s) {
      const on = sel.kind === "section" && sel.uid === s._uid;
      const bad = issuesFor(`section:${s.key}`).some((i) => i.isError);
      const meta = [`<span class="mono">${esc(s.key)}</span>`];
      if (s.isSubform) meta.push(`<span>· subform${s.maxRows ? `, up to ${s.maxRows}` : ""}${s.required ? ", at least one row" : ""}</span>`);
      if (s.optional) meta.push("<span>· optional</span>");
      if (s.id == null) meta.push('<span class="vl-kw vl-kw-violet">New</span>');
      if (bad) meta.push('<span class="vl-kw vl-kw-danger">Problem</span>');
      return `<section class="dz-sec${on ? " is-selected" : ""}" data-sec="${s._uid}">
        <header class="dz-sec-head" data-select-sec="${s._uid}" tabindex="0" role="button" aria-pressed="${on}" aria-label="${esc(s.name)} ${s.isSubform ? "subform" : "section"}. Alt+arrow keys move it.">
          ${ro ? "" : `<span class="dz-grip" draggable="true" data-sec-grip="${s._uid}" title="Drag to move the section"><i class="bi bi-grip-vertical" aria-hidden="true"></i></span>`}
          <span class="vl-section-icon"><i class="bi ${esc(s.icon || (s.isSubform ? "bi-table" : "bi-card-text"))}"></i></span>
          <div class="min-w-0"><div class="dz-sec-name">${esc(s.name || "(unnamed section)")}</div><div class="dz-sec-meta">${meta.join("")}</div></div>
          ${ro ? "" : `<div class="dz-sec-tools">
            <button type="button" class="btn btn-sm btn-ghost btn-icon" data-act="sec-up" data-u="${s._uid}" aria-label="Move ${esc(s.name)} up"><i class="bi bi-arrow-up"></i></button>
            <button type="button" class="btn btn-sm btn-ghost btn-icon" data-act="sec-down" data-u="${s._uid}" aria-label="Move ${esc(s.name)} down"><i class="bi bi-arrow-down"></i></button>
            <button type="button" class="btn btn-sm btn-ghost btn-icon text-danger" data-act="sec-delete" data-u="${s._uid}" aria-label="Delete ${esc(s.name)}"><i class="bi bi-trash3"></i></button></div>`}
        </header>
        <div class="dz-grid${s.isSubform ? " is-sub" : ""}" data-drop-fields="${s._uid}">
          ${s.fields.map((x) => fieldBlock(x, s)).join("") || `<div class="dz-empty-grid">${ro ? "No fields." : "Drop fields here."}</div>`}
          ${ro ? "" : `<button type="button" class="dz-add-field" data-act="add-field" data-u="${s._uid}"><i class="bi bi-plus-lg"></i> Add ${s.isSubform ? "column" : "field"}</button>`}
        </div>
      </section>`;
    }

    function renderCanvas() {
      canvas.classList.toggle("dz-readonly", ro);
      if (mode === "preview") { renderPreview(); return; }
      const t = tabs();
      const count = (name) => form.sections.filter((s) => (s.tab || "General") === name).reduce((n, s) => n + s.fields.length, 0);
      canvas.innerHTML = `
        <div class="dz-tabbar" role="tablist" aria-label="Form tabs">
          ${t.map((name) => `<button type="button" class="dz-tab${name === activeTab ? " active" : ""}" data-tab="${esc(name)}" draggable="${!ro}" role="tab" aria-selected="${name === activeTab}" title="Double-click or F2 to rename. Alt+←/→ to move.">${esc(name)} <span class="vl-pill-count">${count(name)}</span></button>`).join("")}
          ${ro ? "" : `<button type="button" class="dz-tab-add" data-act="add-tab"><i class="bi bi-plus-lg"></i> Tab</button>`}
        </div>
        <div data-drop-sections>
          ${form.sections.filter((s) => (s.tab || "General") === activeTab).map(sectionHtml).join("") || `<div class="card"><div class="vl-empty"><div class="vl-empty-icon"><i class="bi bi-card-heading"></i></div><h3>No sections yet</h3><p>Add a section, then drag fields into it.</p></div></div>`}
        </div>
        ${ro ? "" : `<div class="dz-add-row"><button type="button" data-act="add-section"><i class="bi bi-card-heading"></i>Add section</button><button type="button" data-act="add-subform"><i class="bi bi-table"></i>Add subform</button></div>`}`;
    }

    // ------------------------------------------------------------ preview (renders like the record page)
    function renderPreview() {
      const ph = previewPhase ? phases().find(({ p, w }) => `${w.key}/${p.key}` === previewPhase)?.p : null;
      const stateOf = (s) => (ph ? sectionState(s, ph) : "edit");
      const visible = form.sections.filter((s) => stateOf(s) !== "hidden");
      const ptabs = [];
      visible.forEach((s) => { const n = s.tab || "General"; if (!ptabs.includes(n)) ptabs.push(n); });
      if (!previewTab || !ptabs.includes(previewTab)) previewTab = ptabs[0];
      const hidden = form.sections.length - visible.length;
      canvas.innerHTML = `<div class="dz-preview-frame">
        <div class="dz-preview-banner"><i class="bi bi-eye"></i>${ph ? `Previewing phase <span class="vl-chip ${ph.type === "Normal" ? "vl-chip-open" : ph.type === "Completed" ? "vl-chip-done" : "vl-chip-void"}">${esc(ph.name)}</span>` : "Previewing a <b>new record</b>"}
          ${hidden ? `<span class="text-body-tertiary">· ${hidden} section${hidden === 1 ? "" : "s"} hidden in this phase</span>` : ""}<span class="ms-auto text-body-tertiary">Rules apply as you fill it in. Nothing is saved.</span></div>
        <ul class="nav vl-tabs" role="tablist">${ptabs.map((t) => `<li class="nav-item"><button type="button" class="nav-link${t === previewTab ? " active" : ""}" data-ptab="${esc(t)}">${esc(t)}</button></li>`).join("")}</ul>
        ${visible.filter((s) => (s.tab || "General") === previewTab).map((s) => pvSection(s, stateOf(s))).join("") || `<div class="vl-empty"><p>Nothing is shown in this phase.</p></div>`}
      </div>`;
      pvApply();
    }
    function pvSection(s, state) {
      const editable = state === "edit";
      const head = `<div class="card-header"><span class="vl-section-icon"><i class="bi ${esc(s.icon || (s.isSubform ? "bi-table" : "bi-card-text"))}"></i></span><div class="min-w-0"><h3 class="card-title-sm">${esc(s.name)}</h3>${s.description ? `<p class="vl-section-desc">${esc(s.description)}</p>` : ""}</div>${editable ? "" : '<span class="vl-section-state"><i class="bi bi-lock"></i>Read-only</span>'}</div>`;
      if (s.isSubform) {
        const cols = s.fields.filter((x) => x.type !== "Prompt");
        return `<section class="card vl-section">${head}<div class="card-body"><div class="vl-subform"><div class="table-responsive"><table class="table align-middle">
          <thead><tr><th class="row-num">#</th>${cols.map((x) => `<th style="width:${(x.width || 3) * 8}%">${esc(x.label)}${x.required && editable ? '<span class="text-danger"> *</span>' : ""}</th>`).join("")}${editable ? '<th class="row-tools"></th>' : ""}</tr></thead>
          <tbody><tr><td class="row-num">1</td>${cols.map((x) => `<td>${editable && !x.readOnly ? pvControl(x, "pv-" + x._uid, true) : '<div class="vl-value empty">–</div>'}</td>`).join("")}${editable ? '<td class="row-tools"><button type="button" class="btn btn-sm btn-ghost btn-icon text-danger" tabindex="-1" aria-label="Remove row"><i class="bi bi-x-lg"></i></button></td>' : ""}</tr></tbody></table></div>
          ${editable ? `<div class="border-top p-2 bg-surface-2"><button type="button" class="btn btn-sm btn-light" tabindex="-1"><i class="bi bi-plus-lg"></i>Add ${esc(s.rowNoun || "row")}</button>${s.maxRows ? `<span class="vl-sr-hint ms-2">Up to ${s.maxRows}</span>` : ""}</div>` : ""}</div></div></section>`;
      }
      return `<section class="card vl-section">${head}<div class="card-body"><div class="row g-3">${s.fields.map((x) => {
        const w = x.width || 6;
        const cls = w < 12 ? `col-md-${Math.min(12, w + (w <= 4 ? 2 : 0))} col-xl-${w}` : "";
        const fe = editable && !x.readOnly;
        const id = "pv-" + x._uid;
        if (x.type === "Prompt") return `<div class="col-12" ${x.visibleWhen ? `data-pv-when="${esc(x.visibleWhen)}"` : ""}><div class="vl-prompt"><i class="bi bi-info-circle-fill"></i><div>${esc(x.help)}</div></div></div>`;
        const req = (x.required || (previewPhase && x.requiredInPhases.includes(previewPhase.split("/")[1]))) && fe;
        return `<div class="col-12 ${cls} vl-field ${fe ? "" : "vl-field-ro"}" ${x.visibleWhen ? `data-pv-when="${esc(x.visibleWhen)}"` : ""}>
          <label class="form-label" for="${id}">${esc(x.label)}${req ? '<span class="req" aria-hidden="true">*</span>' : ""}${x.sensitive ? ' <i class="bi bi-shield-lock text-body-tertiary" title="Sensitive"></i>' : ""}${x.help ? ` <i class="bi bi-question-circle vl-help" title="${esc(x.help)}"></i>` : ""}</label>
          ${fe ? pvControl(x, id, false) : `<div class="vl-value empty" id="${id}">–</div>`}</div>`;
      }).join("")}</div></div></section>`;
    }
    function pvDefault(x) {
      const d = x.defaultValue || "";
      if (x.type === "Date" && d.startsWith("$today")) { const n = parseInt(d.slice(7) || "0", 10) || 0; const dt = new Date(); dt.setDate(dt.getDate() + n); return dt.toISOString().slice(0, 10); }
      return d.startsWith("$") ? "" : d;
    }
    function pvControl(x, id, compact) {
      const k = `data-pv-key="${esc(x.key)}"`;
      const ph = esc(x.placeholder || "");
      const def = pvDefault(x);
      switch (x.type) {
        case "TextArea": return `<textarea class="form-control" id="${id}" rows="${compact ? 1 : 3}" placeholder="${ph}" ${k}>${esc(def)}</textarea>`;
        case "Number": return `<input class="form-control tabular" type="number" id="${id}" value="${esc(def)}" ${x.min != null ? `min="${x.min}"` : ""} ${x.max != null ? `max="${x.max}"` : ""} placeholder="${ph}" ${k}>`;
        case "Currency": return `<div class="input-group"><span class="input-group-text">${esc(x.currencyCode || "USD")}</span><input class="form-control tabular" type="number" step="0.01" id="${id}" ${k}></div>`;
        case "Date": return `<input class="form-control tabular" type="date" id="${id}" value="${esc(def)}" ${k}>`;
        case "DateTime": return `<input class="form-control tabular" type="datetime-local" id="${id}" ${k}>`;
        case "Url": return `<input class="form-control" type="url" id="${id}" placeholder="https://" ${k}>`;
        case "Keyword": {
          const l = lists[x.keywordList];
          return `<select class="form-select" id="${id}" ${k}><option value="">${l ? "Choose…" : "No keyword list chosen"}</option>${(l ? l.options : []).map((o) => `<option value="${esc(o.key)}" data-key="${esc(o.key)}"${o.key === def ? " selected" : ""}>${esc(o.label)}</option>`).join("")}</select>`;
        }
        case "KeywordMulti": {
          const l = lists[x.keywordList];
          return `<div class="d-flex flex-wrap gap-1 pt-1" role="group" aria-label="${esc(x.label)}">${(l ? l.options : []).map((o, i) => `<input type="checkbox" class="btn-check" id="${id}-${i}" data-key="${esc(o.key)}" ${k} autocomplete="off"><label class="btn btn-sm btn-light" for="${id}-${i}">${esc(o.label)}</label>`).join("") || '<span class="text-body-tertiary small">No keyword list chosen</span>'}</div>`;
        }
        case "YesNo": return `<div class="vl-seg" role="radiogroup" aria-label="${esc(x.label)}"><input type="radio" class="btn-check" name="${id}" id="${id}-y" value="yes" ${k} ${def === "yes" ? "checked" : ""}><label for="${id}-y">Yes</label><input type="radio" class="btn-check" name="${id}" id="${id}-n" value="no" ${k} ${def === "no" ? "checked" : ""}><label for="${id}-n">No</label></div>`;
        case "User": case "UserMulti": return `<div class="vl-picker"><div class="vl-picker-box">${x.defaultValue === "$me" ? '<span class="vl-token"><span class="vl-avatar vl-avatar-xs" style="--h:200">ME</span><span class="t">You</span></span>' : ""}<input type="text" id="${id}" placeholder="${ph || (x.type === "UserMulti" ? "Add people…" : "Search people…")}" aria-label="${esc(x.label)}" ${k}></div></div>`;
        case "Link": return `<div class="vl-picker"><div class="vl-picker-box"><input type="text" id="${id}" placeholder="Search by number or title…" aria-label="${esc(x.label)}" ${k}></div></div>`;
        case "Attachment": return `<div class="vl-drop" id="${id}"><i class="bi bi-cloud-arrow-up"></i><span><strong>Drop files here</strong> or click to browse</span></div>`;
        default: return `<input class="form-control" id="${id}" value="${esc(def)}" placeholder="${ph}" ${x.maxLength ? `maxlength="${x.maxLength}"` : ""} ${k}>`;
      }
    }
    function pvValues(key) {
      const els = $$(`[data-pv-key="${CSS.escape(key)}"]`, canvas);
      const vals = [];
      els.forEach((el) => {
        if (el.type === "radio" || el.type === "checkbox") { if (el.checked) vals.push(el.dataset.key || el.value); }
        else if (el.tagName === "SELECT") { if (el.value) vals.push(el.value); }
        else if (el.value) vals.push(el.value);
      });
      return vals;
    }
    function pvApply() { $$("[data-pv-when]", canvas).forEach((el) => el.classList.toggle("dz-pv-hidden", !matches(el.dataset.pvWhen, pvValues))); }

    // ------------------------------------------------------------ properties panel
    function renderProps() {
      pid = 0;
      const head = (icon, title, sub) => `<div class="dz-props-head"><span class="vl-section-icon"><i class="bi ${icon}"></i></span><div class="min-w-0"><div class="t">${esc(title)}</div><div class="k">${sub}</div></div></div>`;
      let html = "";
      if (sel.kind === "field") {
        const r = findField(sel.uid);
        if (!r) { sel = { kind: "form" }; return renderProps(); }
        html = head(typeBy[r.f.type]?.icon || "bi-square", fieldLabel(r.f), `${esc(typeBy[r.f.type]?.label)} field`) + `<div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${fieldProps(r)}</fieldset></div>`;
      } else if (sel.kind === "section") {
        const s = findSection(sel.uid);
        if (!s) { sel = { kind: "form" }; return renderProps(); }
        html = head(s.icon || (s.isSubform ? "bi-table" : "bi-card-heading"), s.name || s.key, s.isSubform ? "Subform" : "Section") + `<div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${sectionProps(s)}</fieldset></div>`;
      } else {
        html = head(form.icon || "bi-ui-checks-grid", form.name || form.key, "Form settings") + `<div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${formProps()}</fieldset>${formExtras()}</div>`;
      }
      props.innerHTML = html;
      if (sel.kind === "field") {
        const r = findField(sel.uid);
        wireCondition(props, mainFields(), lists, () => r.f.visibleWhen, (expr) => { wb.snapshot(); r.f.visibleWhen = expr; wb.changed(); render(); });
      }
    }

    function fieldProps({ f: x, s }) {
      const n = dataCount[x.key] || 0;
      const t = x.type;
      const locked = x.id != null;
      const out = [];
      if (n > 0) out.push(`<div class="dz-note"><i class="bi bi-database"></i> ${n} record${n === 1 ? " holds a value" : "s hold values"}. The type is fixed (DSN-403).</div>`);
      for (const i of issuesFor(`field:${x.key}`)) out.push(`<div class="dz-note ${i.isError ? "bad" : "warn"}"><i class="bi ${i.isError ? "bi-x-circle" : "bi-exclamation-triangle"}"></i> ${esc(i.message)}</div>`);
      const secOpts = form.sections.map((z) => [z._uid, `${z.tab} › ${z.name}${z.isSubform ? " (subform)" : ""}`]).concat([["__detached", "Detached (not on the layout)"]]);
      let basics = "";
      if (t === "Prompt") basics += H.area("help", "Text", x.help, 'rows="3"');
      else basics += H.text("label", "Label", x.label, 'maxlength="200"');
      basics += H.key("key", x.key, locked);
      basics += H.select("type", "Type", types.map((ty) => [ty.value, ty.label]), t, `data-rerender ${n > 0 ? "disabled" : ""}`);
      if (s) basics += `<div class="dz-prop"><span class="dz-prop-label">Width (${s.isSubform ? "of 10" : "of 12"} columns)</span><div class="dz-range"><input type="range" min="1" max="${s.isSubform ? 10 : 12}" step="1" data-p="width" data-t="int" value="${Math.min(x.width, s.isSubform ? 10 : 12)}" aria-label="Width"><output>${Math.min(x.width, s.isSubform ? 10 : 12)}</output></div></div>`;
      basics += H.select("_section", "Section", secOpts, s ? s._uid : "__detached", "data-rerender");
      out.push(H.group("Basics", "bi-sliders", basics));

      let content = "";
      if (t === "Keyword" || t === "KeywordMulti") {
        content += H.select("keywordList", "Keyword list", [["", "Choose a list…"], ...data.keywordLists.map((l) => [l.key, `${l.name} (${l.key})`])], x.keywordList || "", "data-rerender");
        const l = lists[x.keywordList];
        if (l) content += `<div class="dz-kwprev">${l.options.slice(0, 12).map((o) => `<span class="vl-kw${o.color ? (o.color.startsWith("#") ? " vl-kw-hex" : " vl-kw-" + esc(o.color)) : ""}"${o.color && o.color.startsWith("#") ? ` style="--kw:${esc(o.color)}"` : ""}>${esc(o.label)}</span>`).join("")}${l.options.length > 12 ? `<span class="small text-body-tertiary">+${l.options.length - 12}</span>` : ""}</div><div class="dz-hint">Stored by stable keyword ID (INV-061). Edit lists in the Administration Center.</div>`;
      }
      if (t === "Text" || t === "TextArea") content += H.num("maxLength", "Maximum length", x.maxLength, 'min="1"');
      if (t === "Number" || t === "Currency") {
        content += `<div class="dz-prop-row">${H.num("min", "Minimum", x.min, 'step="any"')}${H.num("max", "Maximum", x.max, 'step="any"')}</div>`;
        content += H.num("decimals", "Decimal places", x.decimals, 'min="0" max="30"');
        if (t === "Currency") content += H.text("currencyCode", "Currency code", x.currencyCode, 'maxlength="3"');
      }
      if (t === "Link") content += `<div class="dz-prop"><span class="dz-prop-label">Records it can link to</span>${H.members("linkForms", data.forms.map((fm) => ({ value: fm.key, label: fm.name, group: fm.app })), x.linkForms)}<div class="dz-hint">None selected = any form.</div></div>`;
      if (content) out.push(H.group(typeBy[t].label + " settings", typeBy[t].icon, content));

      if (t !== "Prompt") {
        let rules = H.check("required", "Required when sending forward", x.required);
        const normal = phases().filter(({ p }) => p.type === "Normal");
        if (normal.length) rules += `<div class="dz-prop mt-2"><span class="dz-prop-label">Required only in phases (DSN-410)</span>${H.members("requiredInPhases", normal.map(({ p, w }) => ({ value: p.key, label: p.name, group: form.workflows.length > 1 ? w.name : null })), x.requiredInPhases)}</div>`;
        rules += H.check("preventNull", "Prevent empty values on every save (PLT-503)", x.preventNull);
        rules += H.check("unique", "Unique across the form (PLT-410)", x.unique);
        out.push(H.group("Validation", "bi-check2-square", rules));

        let help = H.area("help", "Help text", x.help, 'maxlength="2000"');
        if (!["Keyword", "KeywordMulti", "YesNo", "Attachment", "Date", "DateTime"].includes(t)) help += H.text("placeholder", "Placeholder", x.placeholder);
        const defHint = { Keyword: "A keyword key, e.g. medium.", YesNo: "yes or no.", Date: "$today or $today+7.", User: "$me = the person creating the record." }[t] || "A literal value.";
        if (t !== "Attachment" && t !== "Link") help += H.text("defaultValue", "Default value", x.defaultValue, "", defHint);
        out.push(H.group("Help and defaults", "bi-chat-left-text", help));

        let beh = H.check("readOnly", "Read-only (computed by the system)", x.readOnly);
        beh += H.check("sensitive", "Sensitive: managers and listed readers only (SEC-520)", x.sensitive);
        if (!s || !s.isSubform) beh += H.check("showInHeader", "Show in the record header", x.showInHeader);
        beh += H.check("protectFromAdminUpdate", "Protect from administrative updates (PLT-408)", x.protectFromAdminUpdate);
        out.push(H.group("Behavior", "bi-toggles", beh));
      }
      if (!s || !s.isSubform) {
        out.push(H.group("Visible when", "bi-eye", `${conditionBuilder(mainFields(), x.key)}<div class="dz-prop mt-2"><label for="dzp-vw">Rule</label><input class="form-control mono" id="dzp-vw" data-p="visibleWhen" value="${esc(x.visibleWhen || "")}" placeholder="Always shown" spellcheck="false"></div><div class="dz-hint">Field rules apply only while the field is shown (PLT-506).</div>`));
      }
      out.push(`<div class="dz-actions mt-1">
        <button type="button" class="btn btn-sm btn-light" data-act="fld-up"><i class="bi bi-arrow-up"></i>Earlier</button>
        <button type="button" class="btn btn-sm btn-light" data-act="fld-down"><i class="bi bi-arrow-down"></i>Later</button>
        ${s ? '<button type="button" class="btn btn-sm btn-light" data-act="fld-detach"><i class="bi bi-box-arrow-in-down"></i>Detach</button>' : '<button type="button" class="btn btn-sm btn-light" data-act="fld-attach"><i class="bi bi-box-arrow-up"></i>Put back on the layout</button>'}
        <button type="button" class="btn btn-sm btn-outline-danger" data-act="fld-delete"><i class="bi bi-trash3"></i>Delete</button></div>`);
      return out.join("");
    }

    function sectionProps(s) {
      const locked = s.id != null;
      const out = [];
      for (const i of issuesFor(`section:${s.key}`)) out.push(`<div class="dz-note ${i.isError ? "bad" : "warn"}">${esc(i.message)}</div>`);
      let b = H.text("name", "Name", s.name, 'maxlength="200"') + H.key("key", s.key, locked);
      b += `<div class="dz-prop"><label for="dzp-tab">Tab</label><input class="form-control" id="dzp-tab" data-p="tab" data-t="str" value="${esc(s.tab)}" list="dzp-tabs"><datalist id="dzp-tabs">${tabs().map((t) => `<option value="${esc(t)}"></option>`).join("")}</datalist></div>`;
      b += `<div class="dz-prop"><label for="dzp-sicon">Icon</label><div class="input-group"><span class="input-group-text"><i class="bi ${esc(s.icon || "bi-card-text")}"></i></span><input class="form-control mono" id="dzp-sicon" data-p="icon" value="${esc(s.icon || "")}" placeholder="bi-card-text"><button type="button" class="btn btn-light" data-act="pick-icon" aria-label="Browse icons"><i class="bi bi-grid-3x3-gap"></i></button></div></div>`;
      b += H.area("description", "Description", s.description, 'maxlength="2000"');
      out.push(H.group("Section", "bi-card-heading", b));
      let o = H.check("optional", "Optional: users can show or hide it (PLT-304)", s.optional) + H.check("collapsed", "Collapsed by default", s.collapsed);
      if (s.isSubform) {
        o += `<div class="dz-prop-row mt-2">${H.text("rowNoun", "Row name", s.rowNoun, 'placeholder="row"')}${H.num("maxRows", "Maximum rows", s.maxRows, 'min="1"')}</div>`;
        o += H.check("required", "At least one complete row before sending forward (PLT-504)", s.required);
      }
      out.push(H.group(s.isSubform ? "Subform" : "Options", s.isSubform ? "bi-table" : "bi-toggles", o));
      if (form.workflows.length) {
        const rows = phases().map(({ p, w }) => { const st = sectionState(s, p); return `<li><span class="flex-grow-1">${form.workflows.length > 1 ? esc(w.name) + " › " : ""}${esc(p.name)}</span><span class="vl-kw ${st === "edit" ? "vl-kw-success" : st === "ro" ? "vl-kw-info" : ""}">${stateLabel[st]}</span></li>`; }).join("");
        out.push(H.group("Visibility by phase (DSN-305)", "bi-grid-3x3", `<ul class="dz-mini-list">${rows}</ul><div class="dz-hint">Change it in the workflow designer's visibility matrix: ${form.workflows.map((w) => `<a href="${data.urls.workflows}${esc(w.key)}">${esc(w.name)}</a>`).join(", ")}.</div>`));
      }
      out.push(`<div class="dz-actions"><button type="button" class="btn btn-sm btn-light" data-act="sec-up" data-u="${s._uid}"><i class="bi bi-arrow-up"></i>Up</button><button type="button" class="btn btn-sm btn-light" data-act="sec-down" data-u="${s._uid}"><i class="bi bi-arrow-down"></i>Down</button><button type="button" class="btn btn-sm btn-light" data-act="add-field" data-u="${s._uid}"><i class="bi bi-plus-lg"></i>Field</button><button type="button" class="btn btn-sm btn-outline-danger" data-act="sec-delete" data-u="${s._uid}"><i class="bi bi-trash3"></i>Delete</button></div>`);
      return out.join("");
    }

    function numberExample() {
      const year = form.numberIncludesYear ? new Date().getFullYear() + "-" : "";
      return `${form.numberPrefix || ""}${year}${"42".padStart(Math.max(1, form.numberPadding || 5), "0")}${form.numberSuffix || ""}`;
    }
    function formProps() {
      const out = [];
      for (const i of issuesFor("").concat(issues.filter((i) => i.path === `form:${form.key}`))) out.push(`<div class="dz-note ${i.isError ? "bad" : "warn"}">${esc(i.message)}</div>`);
      let b = H.text("name", "Name", form.name, 'maxlength="200"') + H.key("key", form.key, form.id != null);
      b += `<div class="dz-prop"><label for="dzp-ficon">Icon</label><div class="input-group"><span class="input-group-text"><i class="bi ${esc(form.icon)}"></i></span><input class="form-control mono" id="dzp-ficon" data-p="icon" value="${esc(form.icon)}"><button type="button" class="btn btn-light" data-act="pick-icon" aria-label="Browse icons"><i class="bi bi-grid-3x3-gap"></i></button></div></div>`;
      b += H.area("description", "Description", form.description, 'maxlength="2000"');
      out.push(H.group("Form", "bi-ui-checks-grid", b));
      let k = H.check("isActivity", "Activity form: created from a parent record (PLT-205)", form.isActivity, "data-rerender");
      if (form.isActivity) k += `<div class="dz-prop mt-1"><span class="dz-prop-label">Parent forms</span>${H.members("parentForms", data.forms.filter((fm) => fm.key !== form.key && !fm.isActivity).map((fm) => ({ value: fm.key, label: fm.name, group: fm.app })), form.parentForms)}<div class="dz-hint">None selected = any form in this application.</div></div>`;
      k += H.check("isNonWorkflow", "Profile form without a workflow (PLT-203)", form.isNonWorkflow, form.workflows.length ? 'disabled title="A form with workflows can\'t be non-workflow (INV-132)"' : "");
      k += H.check("hiddenFromNew", "Hidden from the New menu", form.hiddenFromNew);
      k += H.check("disabled", "Disabled: no new records", form.disabled);
      out.push(H.group("Kind", "bi-diagram-2", k));
      let num = `<div class="dz-prop-row">${H.text("numberPrefix", "Prefix", form.numberPrefix, 'data-t="str"')}${H.text("numberSuffix", "Suffix", form.numberSuffix, 'data-t="str"')}</div>`;
      num = num.replace(/data-p="numberPrefix"/, 'data-p="numberPrefix" data-t="str"').replace(/data-p="numberSuffix"/, 'data-p="numberSuffix" data-t="str"');
      num += `<div class="dz-prop-row">${H.num("numberPadding", "Digits", form.numberPadding, 'min="1" max="12"')}<div class="dz-prop"><span class="dz-prop-label">&nbsp;</span>${H.check("numberIncludesYear", "Include year", form.numberIncludesYear)}</div></div>`;
      num += `<div class="dz-prop"><span class="dz-prop-label">Example</span><span class="dz-numex" data-dz-numex>${esc(numberExample())}</span></div>`;
      num += `<div class="dz-prop"><label for="dzp-title">Title template</label><input class="form-control mono" id="dzp-title" data-p="titleTemplate" data-t="str" value="${esc(form.titleTemplate)}"><select class="form-select form-select-sm mt-1" data-act-insert aria-label="Insert a field into the title"><option value="">Insert a field…</option>${mainFields().filter((x) => x.type !== "Prompt").map((x) => `<option value="${esc(x.key)}">${esc(x.label)}</option>`).join("")}<option value="number">Record number</option></select></div>`;
      out.push(H.group("Numbering and title", "bi-123", num));
      return out.join("");
    }
    function formExtras() {
      const wfs = form.workflows.map((w) => `<li><i class="bi bi-diagram-3 text-body-tertiary"></i><a class="flex-grow-1" href="${data.urls.workflows}${esc(w.key)}">${esc(w.name)}</a><span class="small text-body-tertiary">${w.phases.length} phases</span></li>`).join("");
      return H.group("Workflows", "bi-diagram-3", `${wfs ? `<ul class="dz-mini-list">${wfs}</ul>` : `<div class="dz-hint">No workflow yet.</div>`}${ro || form.isNonWorkflow ? "" : `<button type="button" class="btn btn-sm btn-light mt-2" data-act="new-workflow"><i class="bi bi-plus-lg"></i>Add a workflow</button>`}`);
    }

    // Apply a property edit to the selected object.
    wb.bindPanel(props, (el, evt) => {
      const target = sel.kind === "field" ? findField(sel.uid)?.f : sel.kind === "section" ? findSection(sel.uid) : form;
      if (!target) return;
      if (el.dataset.pm) { applyMember(target, el); render(); return; }
      const p = el.dataset.p;
      const v = readValue(el);
      if (p === "_section") {
        const r = findField(sel.uid);
        const dest = v === "__detached" ? form.detached : findSection(v)?.fields;
        if (dest && dest !== r.list) { moveField(sel.uid, dest, dest.length); const sec = findSection(v); if (sec) activeTab = sec.tab || "General"; }
        render(); return;
      }
      if (p === "key") {
        target.key = (v || "").trim();
        target._auto = false;
        const prob = keyProblem(target.key);
        el.classList.toggle("is-invalid", !!prob);
        el.title = prob ? "Design name " + prob : "";
      } else if ((p === "label" || p === "name") && sel.kind !== "form" && target._auto && target.id == null) {
        target[p] = v ?? "";
        if (v) target.key = uniqueKey(slug(v), sel.kind === "field" ? takenFields(target) : takenSections(target));
        const keyEl = $('[data-p="key"]', props);
        if (keyEl) keyEl.value = target.key;
      } else if (p === "width") {
        target.width = v || 1;
        const out = el.parentElement.querySelector("output");
        if (out) out.textContent = target.width;
      } else if (p === "type") {
        target.type = v;
        if (!["Keyword", "KeywordMulti"].includes(v)) target.keywordList = null;
        if (v === "Prompt") { target.width = 12; target.label = target.label || ""; }
      } else {
        target[p] = v ?? (["numberPrefix", "numberSuffix", "titleTemplate", "label", "name", "tab"].includes(p) ? "" : null);
      }
      if (p === "tab") activeTab = target.tab || "General";
      if (p.startsWith("number")) { const ex = $("[data-dz-numex]", props); if (ex) ex.textContent = numberExample(); }
      const icon = el.closest(".input-group")?.querySelector(".input-group-text .bi");
      if (p === "icon" && icon) icon.className = "bi " + (v || "bi-card-text");
      renderLeft(); renderCanvas(); syncTop();
      if (el.hasAttribute("data-rerender") && evt === "change") renderProps();
      if (p === "label" || p === "name") { const t = $(".dz-props-head .t", props); if (t) t.textContent = v || target.key; }
    });
    props.addEventListener("change", (e) => {
      if (!e.target.matches("[data-act-insert]") || !e.target.value) return;
      wb.snapshot();
      form.titleTemplate = ((form.titleTemplate || "").trim() + " {" + e.target.value + "}").trim();
      e.target.value = "";
      wb.changed(); renderProps();
    });

    // ------------------------------------------------------------ actions (clicks)
    function act(name, el) {
      const fr = sel.kind === "field" ? findField(sel.uid) : null;
      switch (name) {
        case "add-section": return addSection(false);
        case "add-subform": return addSection(true);
        case "add-tab": return addTab();
        case "add-field": return addField("Text", findSection(el.dataset.u) || targetSection());
        case "sec-up": case "sec-down": { const s = findSection(el.dataset.u); if (s) moveSection(s, name === "sec-up" ? -1 : 1); return; }
        case "sec-delete": return deleteSection(el.dataset.u);
        case "narrow": case "widen": {
          const fu = el.closest("[data-fld]")?.dataset.fld;
          const r = fu && findField(fu);
          if (!r) return;
          wb.snapshot();
          const max = r.s && r.s.isSubform ? 10 : 12;
          r.f.width = Math.max(1, Math.min(max, (r.f.width || 6) + (name === "widen" ? 1 : -1)));
          sel = { kind: "field", uid: fu };
          wb.changed(); render(); return;
        }
        case "fld-up": case "fld-down": {
          if (!fr) return;
          const j = fr.i + (name === "fld-up" ? -1 : 1);
          if (j < 0 || j >= fr.list.length) return;
          wb.snapshot(); fr.list.splice(fr.i, 1); fr.list.splice(j, 0, fr.f); wb.changed(); render(); return;
        }
        case "fld-detach": if (fr) { wb.snapshot(); moveField(fr.f._uid, form.detached, form.detached.length); wb.changed(); render(); } return;
        case "fld-attach": if (fr) { const t = targetSection() || form.sections[0]; if (!t) return toast("Add a section first.", "info"); wb.snapshot(); moveField(fr.f._uid, t.fields, t.fields.length); activeTab = t.tab; wb.changed(); render(); } return;
        case "fld-delete": if (fr) deleteField(fr.f._uid); return;
        case "pick-icon": {
          const target = sel.kind === "section" ? findSection(sel.uid) : form;
          iconPicker.open(target.icon, (icon) => { wb.snapshot(); target.icon = icon; wb.changed(); render(); });
          return;
        }
        case "new-workflow": {
          const go = () => $("#dz-new-workflow").submit();
          if (wb.dirty) { save().then((ok) => { if (ok) { wb.dirty = false; go(); } }); } else go();
          return;
        }
      }
    }
    root.addEventListener("click", (e) => {
      const a = e.target.closest("[data-act]");
      if (a && root.contains(a) && !a.disabled) { e.stopPropagation(); if (!ro || a.dataset.act === "noop") act(a.dataset.act, a); return; }
      const lt = e.target.closest("[data-dz-left-tab]");
      if (lt) { leftTab = lt.dataset.dzLeftTab; renderLeft(); return; }
      const pick = e.target.closest("[data-pick]");
      if (pick) {
        const k = pick.dataset.pick, u = pick.dataset.uid;
        if (k === "form") sel = { kind: "form" };
        else if (k === "tab") { if (u) activeTab = u; }
        else if (k === "section") { sel = { kind: "section", uid: u }; activeTab = findSection(u)?.tab || activeTab; }
        else if (k === "field") { sel = { kind: "field", uid: u }; const r = findField(u); if (r && r.s) activeTab = r.s.tab; }
        if (mode === "preview") setMode("design");
        render(); return;
      }
      const nt = e.target.closest("[data-new-type]");
      if (nt && !ro) { const ts = targetSection(); const fr = sel.kind === "field" ? findField(sel.uid) : null; addField(nt.dataset.newType, ts, fr && fr.s === ts ? fr.i + 1 : undefined); return; }
      const tab = e.target.closest("[data-tab]");
      if (tab && canvas.contains(tab)) { activeTab = tab.dataset.tab; render(); return; }
      const ptab = e.target.closest("[data-ptab]");
      if (ptab) { previewTab = ptab.dataset.ptab; renderPreview(); return; }
      const f = e.target.closest("[data-fld]");
      if (f) { sel = { kind: "field", uid: f.dataset.fld }; render(); restoreFocus(`[data-fld="${f.dataset.fld}"]`); return; }
      const sh = e.target.closest("[data-select-sec]");
      if (sh) { sel = { kind: "section", uid: sh.dataset.selectSec }; render(); restoreFocus(`[data-select-sec="${sh.dataset.selectSec}"]`); return; }
      if (e.target === canvas || e.target.closest("[data-drop-sections]") === e.target) { sel = { kind: "form" }; render(); }
    });
    function restoreFocus(selector) { const el = $(selector, root); if (el) el.focus({ preventScroll: true }); }

    canvas.addEventListener("dblclick", (e) => { const t = e.target.closest("[data-tab]"); if (t && !ro) inlineRename(t); });
    function inlineRename(btn) {
      const old = btn.dataset.tab;
      const input = document.createElement("input");
      input.className = "form-control form-control-sm";
      input.style.width = Math.max(120, btn.offsetWidth) + "px";
      input.value = old;
      input.setAttribute("aria-label", "Tab name");
      btn.replaceWith(input);
      input.focus(); input.select();
      let done = false;
      const finish = (commit) => { if (done) return; done = true; if (commit) renameTab(old, input.value); else render(); };
      input.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); finish(true); } if (e.key === "Escape") finish(false); });
      input.addEventListener("blur", () => finish(true));
    }

    // Keyboard: select with Enter/Space, move with Alt+arrows, delete with Delete.
    root.addEventListener("keydown", (e) => {
      const f = e.target.closest && e.target.closest("[data-fld]");
      const sh = e.target.closest && e.target.closest("[data-select-sec]");
      const tb = e.target.closest && e.target.closest("[data-tab]");
      if (f) {
        const r = findField(f.dataset.fld);
        if (!r) return;
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); sel = { kind: "field", uid: r.f._uid }; render(); restoreFocus(`[data-fld="${r.f._uid}"]`); }
        else if (e.altKey && ["ArrowUp", "ArrowLeft", "ArrowDown", "ArrowRight"].includes(e.key) && !ro) {
          e.preventDefault();
          const j = r.i + (e.key === "ArrowUp" || e.key === "ArrowLeft" ? -1 : 1);
          if (j < 0 || j >= r.list.length) return;
          wb.snapshot(); r.list.splice(r.i, 1); r.list.splice(j, 0, r.f); sel = { kind: "field", uid: r.f._uid }; wb.changed(); render(); restoreFocus(`[data-fld="${r.f._uid}"]`);
        } else if ((e.key === "Delete" || e.key === "Backspace") && !ro) { e.preventDefault(); deleteField(r.f._uid); }
      } else if (sh) {
        const s = findSection(sh.dataset.selectSec);
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); sel = { kind: "section", uid: s._uid }; render(); restoreFocus(`[data-select-sec="${s._uid}"]`); }
        else if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown") && !ro) { e.preventDefault(); moveSection(s, e.key === "ArrowUp" ? -1 : 1); restoreFocus(`[data-select-sec="${s._uid}"]`); }
      } else if (tb && canvas.contains(tb)) {
        if (e.key === "F2" && !ro) { e.preventDefault(); inlineRename(tb); }
        else if (e.altKey && (e.key === "ArrowLeft" || e.key === "ArrowRight") && !ro) { e.preventDefault(); moveTab(tb.dataset.tab, e.key === "ArrowLeft" ? -1 : 1); restoreFocus(`[data-tab="${CSS.escape(tb.dataset.tab)}"]`); }
      }
    });

    // ------------------------------------------------------------ drag and drop (HTML5)
    function clearMarks() { $$(".drop-before,.drop-after,.drop-active,.drop-target,.dragging", root).forEach((el) => el.classList.remove("drop-before", "drop-after", "drop-active", "drop-target", "dragging")); }
    root.addEventListener("dragstart", (e) => {
      if (ro) { e.preventDefault(); return; }
      const t = e.target;
      if (t.matches && t.matches("[data-new-type]")) drag = { kind: "new", type: t.dataset.newType };
      else if (t.matches && t.matches("[data-fld]")) { drag = { kind: "field", uid: t.dataset.fld }; t.classList.add("dragging"); }
      else if (t.matches && t.matches("[data-sec-grip]")) { drag = { kind: "section", uid: t.dataset.secGrip }; t.closest(".dz-sec")?.classList.add("dragging"); }
      else if (t.matches && t.matches("[data-tab]")) drag = { kind: "tab", name: t.dataset.tab };
      else return;
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", drag.kind);
    });
    root.addEventListener("dragend", () => { drag = null; clearMarks(); });
    function gridIndex(container, e) {
      const items = $$("[data-fld]", container).filter((el) => !el.classList.contains("dragging"));
      for (let i = 0; i < items.length; i++) {
        const r = items[i].getBoundingClientRect();
        if (e.clientY < r.top) return { i, el: items[i] };
        if (e.clientY <= r.bottom && e.clientX < r.left + r.width / 2) return { i, el: items[i] };
      }
      return { i: items.length, el: null };
    }
    root.addEventListener("dragover", (e) => {
      if (!drag) return;
      const grid = e.target.closest("[data-drop-fields],[data-drop-detached]");
      const tab = e.target.closest("[data-tab]");
      const sec = e.target.closest(".dz-sec");
      $$(".drop-before,.drop-after,.drop-active,.drop-target", root).forEach((el) => el.classList.remove("drop-before", "drop-after", "drop-active", "drop-target"));
      if ((drag.kind === "field" || drag.kind === "new") && grid) {
        if (drag.kind === "new" && grid.matches("[data-drop-detached]")) return;
        e.preventDefault();
        grid.classList.add("drop-active");
        const { el } = gridIndex(grid, e);
        if (el) el.classList.add("drop-before");
      } else if (tab && canvas.contains(tab) && drag.kind !== "new") {
        e.preventDefault();
        tab.classList.add(drag.kind === "tab" ? "drop-before" : "drop-target");
      } else if (drag.kind === "section" && sec) {
        e.preventDefault();
        const r = sec.getBoundingClientRect();
        sec.classList.add(e.clientY < r.top + r.height / 2 ? "drop-before" : "drop-after");
      }
    });
    root.addEventListener("drop", (e) => {
      if (!drag) return;
      e.preventDefault();
      const d = drag;
      drag = null;
      const grid = e.target.closest("[data-drop-fields],[data-drop-detached]");
      const tab = e.target.closest("[data-tab]");
      const sec = e.target.closest(".dz-sec");
      if ((d.kind === "field" || d.kind === "new") && grid) {
        const { i } = gridIndex(grid, e);
        const list = grid.matches("[data-drop-detached]") ? form.detached : findSection(grid.dataset.dropFields)?.fields;
        if (!list) return clearMarks();
        if (d.kind === "new") { clearMarks(); addField(d.type, findSection(grid.dataset.dropFields), i); return; }
        wb.snapshot(); moveField(d.uid, list, i); sel = { kind: "field", uid: d.uid };
      } else if (tab && canvas.contains(tab) && d.kind !== "new") {
        const name = tab.dataset.tab;
        wb.snapshot();
        if (d.kind === "tab") {
          const t = tabs().filter((x) => x !== d.name);
          t.splice(t.indexOf(name), 0, d.name);
          form.sections.sort((a, b) => t.indexOf(a.tab || "General") - t.indexOf(b.tab || "General"));
        } else if (d.kind === "section") {
          const s = findSection(d.uid);
          form.sections.splice(form.sections.indexOf(s), 1);
          s.tab = name;
          const last = form.sections.map((x, ix) => ((x.tab || "General") === name ? ix : -1)).filter((ix) => ix >= 0).pop();
          form.sections.splice(last == null ? form.sections.length : last + 1, 0, s);
        } else if (d.kind === "field") {
          const target = form.sections.find((x) => (x.tab || "General") === name);
          if (target) moveField(d.uid, target.fields, target.fields.length);
        }
        activeTab = name;
      } else if (d.kind === "section" && sec && sec.dataset.sec !== d.uid) {
        const s = findSection(d.uid), other = findSection(sec.dataset.sec);
        const r = sec.getBoundingClientRect();
        const after = e.clientY >= r.top + r.height / 2;
        wb.snapshot();
        form.sections.splice(form.sections.indexOf(s), 1);
        s.tab = other.tab;
        form.sections.splice(form.sections.indexOf(other) + (after ? 1 : 0), 0, s);
        sel = { kind: "section", uid: s._uid };
      } else { clearMarks(); return; }
      wb.changed(); render();
    });

    // ------------------------------------------------------------ preview interactions, modes, problems
    canvas.addEventListener("input", () => { if (mode === "preview") pvApply(); });
    canvas.addEventListener("change", () => { if (mode === "preview") pvApply(); });
    function setMode(m) {
      mode = m;
      $$("[data-dz-mode]", root).forEach((r) => { r.checked = r.value === m; });
      render();
    }
    $$("[data-dz-mode]", root).forEach((r) => r.addEventListener("change", () => { if (r.checked) setMode(r.value); }));
    phaseSel.addEventListener("change", () => { previewPhase = phaseSel.value; previewTab = null; renderPreview(); });

    function pickIssue(i) {
      const m = /\/(field|section):(.+)$/.exec(i.path);
      if (m && m[1] === "field") { const x = allFields().find((y) => y.key === m[2]); if (x) { sel = { kind: "field", uid: x._uid }; const r = findField(x._uid); if (r.s) activeTab = r.s.tab; } }
      else if (m && m[1] === "section") { const s = form.sections.find((y) => y.key === m[2]); if (s) { sel = { kind: "section", uid: s._uid }; activeTab = s.tab; } }
      else sel = { kind: "form" };
      if (mode === "preview") setMode("design"); else render();
    }

    // ------------------------------------------------------------ save
    async function save() {
      if (ro || wb.saving) return false;
      const bad = allFields().concat(form.sections).find((o) => keyProblem(o.key));
      if (bad) { toast(`Design name “${bad.key}” ${keyProblem(bad.key)}.`, "bad"); return false; }
      wb.saving = true; wb.setStatus("saving");
      const renames = { form: form.id == null && form._orig !== form.key ? form._orig : null, sections: {}, fields: {} };
      form.sections.forEach((s) => { if (s._orig && s._orig !== s.key) renames.sections[s._orig] = s.key; });
      allFields().forEach((x) => { if (x._orig && x._orig !== x.key) renames.fields[x._orig] = x.key; });
      try {
        const r = await api(data.urls.save, { method: "POST", body: { form: strip(form), renames } });
        if (!r || r.ok !== true) { wb.saving = false; wb.setStatus("error"); toast((r && r.error) || "The draft couldn't be saved. Reload and try again.", "bad"); return false; }
        adopt(r.form);
        issues = r.issues || [];
        wb.dirty = false; wb.saving = false;
        render();
        const errs = issues.filter((i) => i.isError).length;
        toast(errs ? `Saved to the draft. ${errs} problem${errs === 1 ? "" : "s"} must be fixed before publishing.` : "Saved to the draft. Publish to make it live.", errs ? "info" : "ok");
        if (r.redirect) { wb.dirty = false; location.href = r.redirect; }
        return true;
      } catch (err) {
        wb.saving = false; wb.setStatus("error");
        toast("The draft couldn't be saved: " + err.message, "bad");
        return false;
      }
    }
    function adopt(nf) {
      const secUid = new Map(form.sections.map((s) => [s.key.toLowerCase(), s._uid]));
      const fldUid = new Map(allFields().map((x) => [x.key.toLowerCase(), x._uid]));
      nf.sections.forEach((s) => { s._uid = secUid.get(s.key.toLowerCase()) || uid(); s.fields.forEach((x) => { x._uid = fldUid.get(x.key.toLowerCase()) || uid(); }); });
      (nf.detached || []).forEach((x) => { x._uid = fldUid.get(x.key.toLowerCase()) || uid(); });
      form = nf;
      form._orig = undefined;
      form.sections.forEach((s) => { s._orig = undefined; s.fields.forEach((x) => { x._orig = undefined; }); });
      (form.detached || []).forEach((x) => { x._orig = undefined; });
      prepare(form);
    }

    render();
  }

  // =====================================================================================================
  // Workflow designer (DSN §3.6)
  // =====================================================================================================

  const wfRoot = $("[data-dz-wf-designer]");
  if (wfRoot) wfDesigner(wfRoot);

  function wfDesigner(root) {
    const data = JSON.parse($("#dz-wf-data").textContent);
    const ro = !!data.readOnly;
    let wf = data.workflow;
    let table = data.table || [];
    let issues = data.issues || [];
    const records = data.records || {}, history = data.history || {};
    const sections = data.sections || [], fields = data.fields || [];
    const userFields = fields.filter((f) => f.type === "User" || f.type === "UserMulti");
    const dateFields = fields.filter((f) => f.type === "Date" || f.type === "DateTime");
    const lists = Object.fromEntries(Object.entries(data.keywordLists || {}).map(([k, v]) => [k, v]));
    const left = $("[data-dz-left]", root), props = $("[data-dz-props]", root), body = $("[data-dz-wf-body]", root), diagram = $("[data-dz-diagram]", root);
    let sel = { kind: "workflow" }, tab = "routes", drag = null;

    function prepare(w) {
      w.phases.forEach((p) => { p._uid = p._uid || uid(); if (p._orig === undefined) p._orig = p.key; ["editableSections", "readOnlySections", "hiddenSections", "mustCreateForms", "mustCompleteForms", "activityForms"].forEach((k) => { p[k] = p[k] || []; }); });
      w.routes.forEach((r) => { r._uid = r._uid || uid(); r.allowedPrincipals = r.allowedPrincipals || []; });
    }
    prepare(wf);

    const analyze = debounce(async () => {
      try {
        const r = await api(data.urls.analyze, { method: "POST", body: { workflow: strip(wf), renames: renames() } });
        if (r && r.ok) { diagram.innerHTML = r.svg; table = r.table; issues = r.issues; markDiagram(); renderLeft(); renderBody(); wb.setIssues(issues, pickIssue); }
      } catch { /* keep the last diagram */ }
    }, 350);
    const wb = workbench(root, {
      readOnly: ro,
      serialize: () => JSON.stringify({ wf, sel }),
      restore: (s) => { const o = JSON.parse(s); wf = o.wf; sel = o.sel; },
      render: () => render(),
      save: () => save(),
      onChange: () => analyze(),
    });

    const phaseBy = (u) => wf.phases.find((p) => p._uid === u);
    const phaseByKey = (k) => wf.phases.find((p) => p.key === k);
    const routeBy = (u) => wf.routes.find((r) => r._uid === u);
    const pName = (k) => phaseByKey(k)?.name || k;
    const typeIcon = (p) => (p.isStart ? "bi-play-fill" : { Normal: "bi-record-circle", Completed: "bi-check-circle-fill", Voided: "bi-slash-circle", Archived: "bi-archive", Obsolete: "bi-archive" }[p.type]);
    const typeTone = (p) => ({ Normal: "tone-info", Completed: "tone-ok" }[p.type] || "tone-neutral");
    const rowFor = (k) => table.find((t) => t.key === k);
    function renames() { const m = {}; wf.phases.forEach((p) => { if (p._orig && p._orig !== p.key) m[p._orig] = p.key; }); return { phases: m }; }

    function render() { renderLeft(); renderBody(); renderProps(); markDiagram(); wb.setIssues(issues, pickIssue); const n = $("[data-dz-wf-name]", root); if (n) n.textContent = wf.name; }

    function renderLeft() {
      const hasCompleted = wf.phases.some((p) => p.type === "Completed");
      left.innerHTML = `<div class="dz-left-body">
        <div class="dz-panel-title"><i class="bi bi-signpost-split"></i>Phases <span class="vl-pill-count">${wf.phases.length}</span></div>
        <ul class="dz-phase-list" data-phase-list>${wf.phases.map((p) => {
          const row = rowFor(p.key) || {};
          const flag = row.unreachable && p.type === "Normal" ? "Can't be reached" : row.deadEnd ? "Dead end" : null;
          const n = records[p.key] || 0;
          return `<li class="dz-phase-item${sel.kind === "phase" && sel.uid === p._uid ? " is-selected" : ""}" data-phase="${p._uid}" draggable="${!ro}" tabindex="0" role="button" aria-label="${esc(p.name)}, ${p.type}${p.isStart ? ", starting phase" : ""}${flag ? ", " + flag : ""}. Alt+arrow keys reorder.">
            <span class="ty ${typeTone(p)}"><i class="bi ${typeIcon(p)}"></i></span><span class="nm">${esc(p.name || p.key)}</span>
            ${flag ? `<i class="bi bi-exclamation-triangle-fill flag" title="${flag}"></i>` : ""}${n ? `<span class="vl-pill-count" title="${n} records in this phase">${n}</span>` : ""}</li>`;
        }).join("")}</ul>
        ${ro ? "" : `<div class="dz-panel-title mt-3"><i class="bi bi-plus-square"></i>Add a phase</div>
        <div class="dz-toolbox">
          <button type="button" class="dz-tool" data-add-phase="Normal"><i class="bi bi-record-circle"></i>Normal</button>
          <button type="button" class="dz-tool" data-add-phase="Completed"><i class="bi bi-check-circle"></i>Completed</button>
          <button type="button" class="dz-tool" data-add-phase="Voided"><i class="bi bi-slash-circle"></i>Voided</button>
          <button type="button" class="dz-tool" data-add-phase="Archived" ${hasCompleted ? "" : 'disabled title="Add a completed phase first (DSN-601)"'}><i class="bi bi-archive"></i>Archived</button>
          <button type="button" class="dz-tool wide" data-add-phase="Obsolete" ${hasCompleted ? "" : "disabled"}><i class="bi bi-archive"></i>Obsolete</button>
        </div>
        <div class="dz-hint">New normal and completed phases get a forward route from the selected phase. The first phase must be a starting phase.</div>`}
        <div class="dz-panel-title mt-3"><i class="bi bi-gear"></i>Workflow</div>
        <button type="button" class="dz-tool wide${sel.kind === "workflow" ? " active" : ""}" data-pick-wf><i class="bi bi-sliders"></i>Workflow settings</button>
      </div>`;
    }

    function routeRow(r, i) {
      const on = sel.kind === "route" && sel.uid === r._uid;
      const bad = issues.some((x) => x.path.endsWith(`/route:${i}`) && x.isError);
      return `<tr data-route="${r._uid}" class="${on ? "is-selected" : ""}" tabindex="0">
        <td class="fw-medium">${esc(pName(r.from))}</td>
        <td><span class="dz-kind dz-kind-${r.kind}">${r.kind === "SpecialForward" ? "Special" : r.kind}</span></td>
        <td class="fw-medium">${esc(pName(r.to))}</td>
        <td>${esc(r.label || "")}${!r.label ? '<span class="text-body-tertiary">Default label</span>' : ""}</td>
        <td>${r.requireSignature ? `<span class="vl-sig-badge"><i class="bi bi-pen-fill"></i>${esc(r.signatureMeaning || "meaning?")}</span>` : ""}</td>
        <td class="mono small">${esc(r.condition || "")}</td>
        <td class="text-end text-nowrap">${bad ? '<i class="bi bi-exclamation-triangle-fill text-danger me-1" title="Problem"></i>' : ""}${ro ? "" : `<button type="button" class="btn btn-sm btn-ghost btn-icon text-danger" data-del-route="${r._uid}" aria-label="Delete route"><i class="bi bi-trash3"></i></button>`}</td></tr>`;
    }
    function renderBody() {
      $$("[data-dz-wf-tab]", root).forEach((b) => b.classList.toggle("active", b.dataset.dzWfTab === tab));
      const rc = $("[data-dz-route-count]", root); if (rc) rc.textContent = wf.routes.length;
      const flags = table.filter((t) => (t.unreachable && t.type === "Normal") || t.deadEnd).length;
      const fc = $("[data-dz-flag-count]", root); if (fc) { fc.textContent = flags || ""; fc.classList.toggle("hot", flags > 0); }
      if (tab === "routes") {
        const order = (r) => wf.phases.findIndex((p) => p.key === r.from);
        const rows = wf.routes.map((r, i) => ({ r, i })).sort((a, b) => order(a.r) - order(b.r) || a.i - b.i);
        body.innerHTML = `<div class="dz-tablecard"><div class="vl-table-scroll"><table class="table mb-0">
          <thead><tr><th>From</th><th>Kind</th><th>To</th><th>Label</th><th>E-signature</th><th>Condition</th><th></th></tr></thead>
          <tbody>${rows.map(({ r, i }) => routeRow(r, i)).join("") || `<tr><td colspan="7" class="text-center text-body-tertiary py-4">No routes yet.</td></tr>`}</tbody></table></div>
          ${ro ? "" : `<div class="dz-tablefoot"><button type="button" class="btn btn-sm btn-light" data-add-route><i class="bi bi-plus-lg"></i>Add route</button><span>from ${esc(sel.kind === "phase" ? phaseBy(sel.uid)?.name : wf.phases[0]?.name || "the first phase")}. Back and void routes always ask for a comment.</span></div>`}</div>`;
      } else if (tab === "matrix") {
        body.innerHTML = matrixHtml();
      } else {
        body.innerHTML = `<div class="dz-tablecard"><div class="vl-table-scroll"><table class="table mb-0">
          <thead><tr><th>Phase</th><th>Type</th><th>Forward</th><th>Back</th><th>Special</th><th>Void</th><th>Flags</th><th class="text-end">Records</th></tr></thead>
          <tbody>${table.map((t) => `<tr><td class="fw-medium">${esc(t.name)}</td><td>${t.type}</td>${["forward", "back", "special", "void"].map((k) => `<td><div class="dz-targets">${(t[k] || []).map((x) => `<span>${esc(x)}</span>`).join("") || '<span class="text-body-tertiary border-0 bg-transparent">–</span>'}</div></td>`).join("")}
            <td><div class="dz-flags">${t.start ? '<span class="vl-kw vl-kw-info">Start</span>' : ""}${t.unreachable && t.type === "Normal" ? '<span class="vl-kw vl-kw-danger"><i class="bi bi-x-circle"></i>Unreachable</span>' : t.unreachable ? '<span class="vl-kw vl-kw-warning">Unreachable</span>' : ""}${t.deadEnd ? '<span class="vl-kw vl-kw-danger"><i class="bi bi-sign-dead-end"></i>Dead end</span>' : ""}${t.noCompletion ? '<span class="vl-kw vl-kw-warning">Never completes</span>' : ""}</div></td>
            <td class="text-end tabular">${t.records || 0}</td></tr>`).join("")}</tbody></table></div>
          <div class="dz-tablefoot"><i class="bi bi-info-circle"></i>DSN-612: read-only. ✎ = signed route, ⓘ = conditional route. Updates as you edit.</div></div>`;
      }
    }
    function matrixHtml() {
      if (!sections.length) return `<div class="card"><div class="vl-empty"><p>The form has no sections yet.</p></div></div>`;
      const opt = (v, l, cur) => `<option value="${v}"${v === cur ? " selected" : ""}>${l}</option>`;
      return `<div class="dz-tablecard"><div class="vl-table-scroll"><table class="table mb-0 dz-matrix">
        <thead><tr><th>Section</th>${wf.phases.map((p) => `<th class="ph">${esc(p.name)}<span class="ty">${p.type}${p.isStart ? " · start" : ""}</span>${ro ? "" : `<select class="form-select form-select-sm mt-1" data-col-all="${p._uid}" aria-label="Set every section in ${esc(p.name)}"><option value="">Set all…</option>${p.type === "Normal" ? opt("edit", "All editable", "") : ""}${opt("ro", "All read-only", "")}${opt("hidden", "All hidden", "")}${opt("default", "Default", "")}</select>`}</th>`).join("")}</tr></thead>
        <tbody>${sections.map((s) => `<tr><td class="sec">${esc(s.name)}<span class="tab">${esc(s.tab)}${s.isSubform ? " · subform" : ""}</span></td>${wf.phases.map((p) => {
          const st = sectionState(s, p);
          return `<td class="cell"><select class="form-select form-select-sm dz-cell st-${st}" data-cell data-sec="${esc(s.key)}" data-ph="${p._uid}" aria-label="${esc(s.name)} in ${esc(p.name)}" ${ro ? "disabled" : ""}>${p.type === "Normal" ? opt("edit", "Editable", st) : ""}${opt("ro", "Read-only", st)}${opt("hidden", "Hidden", st)}</select></td>`;
        }).join("")}</tr>`).join("")}</tbody></table></div>
        <div class="dz-tablefoot"><i class="bi bi-info-circle"></i>DSN-305: listed as editable → editable; read-only → read-only; not listed → hidden. Records in terminal phases are read-only at most. A section is never both editable and read-only (INV-135).</div></div>`;
    }
    /** Rewrite a phase's lists explicitly after a matrix edit (keeps DSN-305 semantics, no overlaps). */
    function setMatrix(p, secKey, state) {
      const states = sections.map((s) => ({ key: s.key, st: secKey === "*" || s.key === secKey ? state : sectionState(s, p) }));
      p.editableSections = states.filter((x) => x.st === "edit").map((x) => x.key);
      p.readOnlySections = states.filter((x) => x.st === "ro").map((x) => x.key);
      p.hiddenSections = states.filter((x) => x.st === "hidden").map((x) => x.key);
    }

    function markDiagram() {
      $$("[data-phase-key]", diagram).forEach((g) => g.classList.toggle("is-selected", sel.kind === "phase" && phaseBy(sel.uid)?.key === g.dataset.phaseKey));
      const ri = sel.kind === "route" ? wf.routes.findIndex((r) => r._uid === sel.uid) : -1;
      $$("[data-route-index]", diagram).forEach((g) => g.classList.toggle("is-selected", +g.dataset.routeIndex === ri));
    }

    // ------------------------------------------------------------ properties
    function renderProps() {
      pid = 0;
      const head = (icon, tone, title, sub) => `<div class="dz-props-head"><span class="dz-ico ${tone}"><i class="bi ${icon}"></i></span><div class="min-w-0"><div class="t">${esc(title)}</div><div class="k">${sub}</div></div></div>`;
      if (sel.kind === "phase" && phaseBy(sel.uid)) {
        const p = phaseBy(sel.uid);
        props.innerHTML = head(typeIcon(p), typeTone(p), p.name || p.key, `${p.type} phase`) + `<div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${phaseProps(p)}</fieldset></div>`;
      } else if (sel.kind === "route" && routeBy(sel.uid)) {
        const r = routeBy(sel.uid);
        props.innerHTML = head("bi-arrow-right-circle", "tone-info", `${pName(r.from)} → ${pName(r.to)}`, `${r.kind === "SpecialForward" ? "Special forward" : r.kind} route`) + `<div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${routeProps(r)}</fieldset></div>`;
        wireCondition(props, fields, lists, () => r.condition, (expr) => { wb.snapshot(); r.condition = expr; wb.changed(); render(); });
      } else {
        sel = { kind: "workflow" };
        props.innerHTML = head("bi-diagram-3", "tone-info", wf.name, "Workflow settings") + `<div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${wfProps()}</fieldset></div>`;
      }
    }
    function wfProps() {
      let b = H.text("name", "Name", wf.name, 'maxlength="200"') + H.key("key", wf.key, true);
      b += H.check("disabled", "Disabled: no new records use this workflow (INV-133)", wf.disabled);
      const normal = wf.phases.filter((p) => p.type === "Normal").length;
      b += `<dl class="vl-kv mt-3"><dt>Phases</dt><dd>${wf.phases.length} (${normal} normal)</dd><dt>Routes</dt><dd>${wf.routes.length}</dd><dt>Signed routes</dt><dd>${wf.routes.filter((r) => r.requireSignature).length}</dd><dt>Records in flight</dt><dd>${Object.values(records).reduce((a, b) => a + b, 0)}</dd></dl>`;
      const errs = issues.filter((i) => i.isError);
      return H.group("Workflow", "bi-diagram-3", b) + (errs.length ? H.group("Problems", "bi-exclamation-triangle", `<ul class="dz-issues">${errs.map((i) => `<li class="is-error"><i class="bi bi-x-circle-fill"></i><span><b>${esc(i.object)}</b> ${esc(i.message)}</span></li>`).join("")}</ul>`) : "");
    }
    function phaseProps(p) {
      const locked = p.id != null;
      const n = records[p.key] || 0, h = history[p.key] || 0;
      const out = [];
      for (const i of issues.filter((x) => x.path.endsWith(`/phase:${p.key}`))) out.push(`<div class="dz-note ${i.isError ? "bad" : "warn"}"><i class="bi ${i.isError ? "bi-x-circle" : "bi-exclamation-triangle"}"></i> ${esc(i.message)}</div>`);
      if (n) out.push(`<div class="dz-note"><i class="bi bi-files"></i> ${n} record${n === 1 ? " is" : "s are"} in this phase now. They keep their assignees and due date; changes apply from their next route (INV-071).</div>`);
      let b = H.text("name", "Name", p.name, 'maxlength="200"') + H.key("key", p.key, locked);
      b += H.select("type", "Type", [["Normal", "Normal"], ["Completed", "Completed"], ["Voided", "Voided"], ["Archived", "Archived"], ["Obsolete", "Obsolete"]], p.type, `data-rerender ${locked ? 'disabled title="A phase\'s type is fixed once published (DSN-602)"' : ""}`);
      if (p.type === "Normal") b += H.check("isStart", "Starting phase: new records begin here (PLT-602)", p.isStart);
      b += H.area("purpose", "Purpose", p.purpose, 'maxlength="2000"', "Shown to assignees on the record page.");
      out.push(H.group("Phase", "bi-signpost", b));
      if (p.type === "Normal") {
        const groups = data.groups.map((g) => [g.key, `${g.name} (${g.members} active)`]);
        let a = H.select("assignment", "Assign to", [["Any", "Anyone the router picks"], ["Field", "People in a names field (forced)"], ["Group", "Members of a group"], ["Creator", "The record's creator"]], p.assignment, "data-rerender");
        if (p.assignment === "Field" || p.assignment === "Any") a += H.select("assigneeField", p.assignment === "Field" ? "Names field" : "Preselect from field (optional)", [["", p.assignment === "Field" ? "Choose a field…" : "None"], ...userFields.map((f) => [f.key, f.label])], p.assigneeField || "");
        if (p.assignment === "Group") { a += H.select("assigneeGroup", "Group", [["", "Choose a group…"], ...groups], p.assigneeGroup || ""); a += H.check("assignAllMembers", "Assign every active member (no picking)", p.assignAllMembers); }
        a += H.select("routing", "Routing type", [["Parallel", "Parallel: any assignee completes (PLT-610)"], ["Sequential", "Sequential: one after another (PLT-611)"], ["Voting", "Voting: quorum of approvals (PLT-612)"]], p.routing, "data-rerender");
        if (p.routing === "Voting") a += H.num("quorumPercent", "Quorum (% of assignees)", p.quorumPercent, 'min="1" max="100"');
        out.push(H.group("Assignment", "bi-person-check", a));
        let d = `<div class="dz-prop-row">${H.num("dueDays", "Due in (days)", p.dueDays, 'min="0"')}<div class="dz-prop"><span class="dz-prop-label">&nbsp;</span>${H.check("businessDays", "Business days", p.businessDays)}</div></div>`;
        d += H.select("dueDateField", "Or due on the date in", [["", "No date field"], ...dateFields.map((f) => [f.key, f.label])], p.dueDateField || "", "", "The field wins when it has a value (PLT-630).");
        d += H.check("assignerCanChangeDue", "The router may change the due date", p.assignerCanChangeDue);
        out.push(H.group("Due date", "bi-calendar-check", d));
        const af = (data.activityForms || []).map((f) => ({ value: f.key, label: f.name, group: f.app }));
        if (af.length) {
          let g = `<div class="dz-prop"><span class="dz-prop-label">Can be created here</span>${H.members("activityForms", af, p.activityForms)}<div class="dz-hint">None = every allowed activity form.</div></div>`;
          g += `<div class="dz-prop"><span class="dz-prop-label">Must create at least one</span>${H.members("mustCreateForms", af, p.mustCreateForms)}</div>`;
          g += `<div class="dz-prop"><span class="dz-prop-label">Must complete before leaving (INV-075)</span>${H.members("mustCompleteForms", af, p.mustCompleteForms)}</div>`;
          out.push(H.group("Activity gates (PLT-640)", "bi-check2-all", g));
        }
      }
      const vis = sections.map((s) => { const st = sectionState(s, p); return `<li><span class="flex-grow-1">${esc(s.name)}</span><span class="vl-kw ${st === "edit" ? "vl-kw-success" : st === "ro" ? "vl-kw-info" : ""}">${stateLabel[st]}</span></li>`; }).join("");
      out.push(H.group("Sections in this phase", "bi-grid-3x3", `<ul class="dz-mini-list">${vis}</ul><button type="button" class="btn btn-sm btn-ghost mt-1" data-goto-tab="matrix"><i class="bi bi-grid-3x3"></i>Edit in the matrix</button>`));
      const outRoutes = wf.routes.filter((r) => r.from === p.key);
      out.push(H.group("Routes out", "bi-arrow-right-circle", `<ul class="dz-mini-list">${outRoutes.map((r) => `<li><span class="dz-kind dz-kind-${r.kind}">${r.kind === "SpecialForward" ? "Special" : r.kind}</span><a href="#" class="flex-grow-1" data-pick-route="${r._uid}">${esc(pName(r.to))}</a>${r.requireSignature ? '<i class="bi bi-pen-fill text-body-tertiary"></i>' : ""}</li>`).join("") || '<li class="text-body-tertiary">None yet</li>'}</ul>${ro ? "" : '<button type="button" class="btn btn-sm btn-light mt-2" data-add-route><i class="bi bi-plus-lg"></i>Add route from here</button>'}`));
      if (!ro) out.push(`<div class="dz-actions"><button type="button" class="btn btn-sm btn-light" data-move-phase="-1"><i class="bi bi-arrow-up"></i>Earlier</button><button type="button" class="btn btn-sm btn-light" data-move-phase="1"><i class="bi bi-arrow-down"></i>Later</button><button type="button" class="btn btn-sm btn-outline-danger" data-del-phase ${n || h ? `title="${n ? n + " records are in this phase" : "This phase has history"}: publishing will be blocked (DSN-601)"` : ""}><i class="bi bi-trash3"></i>Delete phase</button></div>`);
      return out.join("");
    }
    function routeProps(r) {
      const out = [];
      const ix = wf.routes.indexOf(r);
      for (const i of issues.filter((x) => x.path.endsWith(`/route:${ix}`))) out.push(`<div class="dz-note ${i.isError ? "bad" : "warn"}"><i class="bi ${i.isError ? "bi-x-circle" : "bi-exclamation-triangle"}"></i> ${esc(i.message)}</div>`);
      const phaseOpts = (filter) => wf.phases.map((p) => [p.key, `${p.name}${p.type !== "Normal" ? " (" + p.type.toLowerCase() + ")" : ""}`, filter && !filter(p)]);
      const valid = { Forward: (p) => p.type === "Normal" || p.type === "Completed", SpecialForward: (p) => p.type === "Normal" || p.type === "Completed", Back: (p) => p.type === "Normal", Void: (p) => p.type === "Voided" }[r.kind];
      let b = H.select("kind", "Kind", [["Forward", "Forward"], ["Back", "Back (needs a comment)"], ["SpecialForward", "Special forward (named users)"], ["Void", "Void (needs a reason)"]], r.kind, "data-rerender");
      b += `<div class="dz-prop-row">${H.select("from", "From", phaseOpts((p) => p.type === "Normal"), r.from, "data-rerender")}${H.select("to", "To", phaseOpts(valid), r.to, "data-rerender")}</div>`;
      b += H.text("label", "Button label", r.label, 'maxlength="200"', "Empty = “Send to …”, “Return to …” or “Void”.");
      out.push(H.group("Route", "bi-arrow-right-circle", b));
      let s = H.check("requireSignature", "Requires an electronic signature (SEC-601, DSN-606)", r.requireSignature, "data-rerender");
      if (r.requireSignature) s += H.text("signatureMeaning", "Signature meaning", r.signatureMeaning, `maxlength="200" placeholder="e.g. Approved" ${r.requireSignature && !r.signatureMeaning ? 'class="is-invalid"' : ""}`, "Required: shown on the signature manifest (21 CFR 11.50).");
      s += H.check("requireComment", "Requires a comment", r.requireComment || r.kind === "Back" || r.kind === "Void", r.kind === "Back" || r.kind === "Void" ? "disabled" : "");
      out.push(H.group("Controls", "bi-pen", s));
      out.push(H.group("Condition", "bi-funnel", `${conditionBuilder(fields)}<div class="dz-prop mt-2"><label for="dzp-rc">Rule</label><input class="form-control mono" id="dzp-rc" data-p="condition" value="${esc(r.condition || "")}" placeholder="Always offered" spellcheck="false"></div><div class="dz-hint">The route is only offered when the rule matches the record.</div>`));
      if (r.kind === "SpecialForward") {
        const items = data.groups.map((g) => ({ value: "@" + g.key, label: g.name, group: "Groups" })).concat(data.users.map((u) => ({ value: u.username, label: u.name, group: "People" })));
        out.push(H.group("Who can use it (DSN-604)", "bi-people", `${H.members("allowedPrincipals", items, r.allowedPrincipals)}<div class="dz-hint">None = administrators only.</div>`));
      }
      if (!ro) out.push(`<div class="dz-actions"><button type="button" class="btn btn-sm btn-light" data-move-route="-1"><i class="bi bi-arrow-up"></i>Earlier</button><button type="button" class="btn btn-sm btn-light" data-move-route="1"><i class="bi bi-arrow-down"></i>Later</button><button type="button" class="btn btn-sm btn-outline-danger" data-del-route="${r._uid}"><i class="bi bi-trash3"></i>Delete route</button></div>`);
      return out.join("");
    }

    wb.bindPanel(props, (el, evt) => {
      const target = sel.kind === "phase" ? phaseBy(sel.uid) : sel.kind === "route" ? routeBy(sel.uid) : wf;
      if (!target) return;
      if (el.dataset.pm) { applyMember(target, el); renderLeft(); return; }
      const p = el.dataset.p;
      let v = readValue(el);
      if (sel.kind === "phase" && p === "key") {
        const old = target.key;
        target.key = (v || "").trim();
        target._auto = false;
        wf.routes.forEach((r) => { if (r.from === old) r.from = target.key; if (r.to === old) r.to = target.key; });
        el.classList.toggle("is-invalid", !!keyProblem(target.key));
      } else if (sel.kind === "phase" && p === "name" && target._auto && target.id == null) {
        const old = target.key;
        target.name = v || "";
        if (v) target.key = uniqueKey(slug(v), new Set(wf.phases.filter((x) => x !== target).map((x) => x.key.toLowerCase())));
        wf.routes.forEach((r) => { if (r.from === old) r.from = target.key; if (r.to === old) r.to = target.key; });
        const k = $('[data-p="key"]', props); if (k) k.value = target.key;
      } else if (p === "isStart" && v) {
        target.isStart = true;
      } else if (p === "type") {
        target.type = v;
        if (v !== "Normal") target.isStart = false;
      } else {
        if (p === "quorumPercent" && v != null) v = Math.max(1, Math.min(100, v));
        target[p] = v ?? (["name", "label"].includes(p) ? (p === "name" ? "" : null) : null);
        if (p === "signatureMeaning") el.classList.toggle("is-invalid", target.requireSignature && !v);
      }
      if (sel.kind === "route" && p === "kind") {
        const to = phaseByKey(target.to);
        const want = { Void: "Voided", Back: "Normal" }[v];
        if (want && to && to.type !== want) { const alt = wf.phases.find((x) => x.type === want && x.key !== target.from) || wf.phases.find((x) => x.type === want); if (alt) target.to = alt.key; }
      }
      renderLeft(); renderBody(); markDiagram();
      if (el.hasAttribute("data-rerender") && evt === "change") renderProps();
      const n = $("[data-dz-wf-name]", root); if (n) n.textContent = wf.name;
    });

    // ------------------------------------------------------------ actions
    function addPhase(type) {
      wb.snapshot();
      const names = { Normal: "New phase", Completed: "Completed", Voided: "Voided", Archived: "Archived", Obsolete: "Obsolete" };
      const taken = new Set(wf.phases.map((x) => x.key.toLowerCase()));
      const p = { id: null, key: uniqueKey(slug(names[type]), taken), name: names[type], purpose: null, type, isStart: type === "Normal" && !wf.phases.some((x) => x.isStart),
        editableSections: [], readOnlySections: type === "Normal" ? [] : ["*"], hiddenSections: [], assignment: "Any", assigneeField: null, assigneeGroup: null,
        assignAllMembers: false, routing: "Parallel", quorumPercent: 100, dueDays: type === "Normal" ? 5 : null, dueDateField: null, businessDays: true,
        assignerCanChangeDue: true, mustCreateForms: [], mustCompleteForms: [], activityForms: [], color: null, _uid: uid(), _auto: true };
      p._orig = p.key;
      const from = sel.kind === "phase" ? phaseBy(sel.uid) : null;
      if (type === "Normal") {
        const at = from && from.type === "Normal" ? wf.phases.indexOf(from) + 1 : (wf.phases.findIndex((x) => x.type !== "Normal") + 1 || wf.phases.length + 1) - 1;
        wf.phases.splice(Math.max(0, at), 0, p);
      } else wf.phases.push(p);
      if (from && from.type === "Normal" && (type === "Normal" || type === "Completed"))
        wf.routes.push({ id: null, from: from.key, to: p.key, kind: "Forward", label: null, requireComment: false, requireSignature: false, signatureMeaning: null, condition: null, allowedPrincipals: [], _uid: uid() });
      if (type === "Voided" && !wf.phases.some((x) => x !== p && x.type === "Voided"))
        wf.phases.filter((x) => x.type === "Normal").forEach((x) => wf.routes.push({ id: null, from: x.key, to: p.key, kind: "Void", label: null, requireComment: true, requireSignature: false, signatureMeaning: null, condition: null, allowedPrincipals: [], _uid: uid() }));
      sel = { kind: "phase", uid: p._uid };
      wb.changed(); render();
      setTimeout(() => { const el = $('[data-p="name"]', props); if (el) { el.focus(); el.select(); } }, 30);
    }
    function addRoute() {
      const from = (sel.kind === "phase" && phaseBy(sel.uid)?.type === "Normal" ? phaseBy(sel.uid) : null) || wf.phases.find((p) => p.type === "Normal");
      if (!from) { toast("Add a normal phase first.", "info"); return; }
      const idx = wf.phases.indexOf(from);
      const to = wf.phases.slice(idx + 1).find((p) => p.type === "Normal" || p.type === "Completed") || wf.phases.find((p) => p !== from && (p.type === "Normal" || p.type === "Completed")) || from;
      wb.snapshot();
      const r = { id: null, from: from.key, to: to.key, kind: "Forward", label: null, requireComment: false, requireSignature: false, signatureMeaning: null, condition: null, allowedPrincipals: [], _uid: uid() };
      wf.routes.push(r);
      sel = { kind: "route", uid: r._uid };
      tab = "routes";
      wb.changed(); render();
    }
    function deletePhase(p) {
      const n = records[p.key] || 0, h = history[p.key] || 0;
      if (n && !confirm(`${n} record${n === 1 ? " is" : "s are"} in ${p.name}. Publishing will be blocked until they're moved (DSN-601). Delete it from the draft anyway?`)) return;
      if (!n && h && !confirm(`${p.name} appears in the history of ${h} record${h === 1 ? "" : "s"}, so publishing will be blocked. Remove the routes into it instead. Delete anyway?`)) return;
      wb.snapshot();
      wf.routes = wf.routes.filter((r) => r.from !== p.key && r.to !== p.key);
      wf.phases.splice(wf.phases.indexOf(p), 1);
      sel = { kind: "workflow" };
      wb.changed(); render();
    }
    function movePhase(p, delta) {
      const i = wf.phases.indexOf(p), j = i + delta;
      if (j < 0 || j >= wf.phases.length) return;
      wb.snapshot();
      wf.phases.splice(i, 1); wf.phases.splice(j, 0, p);
      wb.changed(); render();
    }
    root.addEventListener("click", (e) => {
      const t = e.target;
      const ap = t.closest("[data-add-phase]"); if (ap && !ap.disabled && !ro) return addPhase(ap.dataset.addPhase);
      if (t.closest("[data-add-route]") && !ro) return addRoute();
      const dr = t.closest("[data-del-route]");
      if (dr && !ro) { e.stopPropagation(); wb.snapshot(); wf.routes = wf.routes.filter((r) => r._uid !== dr.dataset.delRoute); if (sel.kind === "route") sel = { kind: "workflow" }; wb.changed(); return render(); }
      if (t.closest("[data-del-phase]") && !ro) { const p = phaseBy(sel.uid); if (p) deletePhase(p); return; }
      const mp = t.closest("[data-move-phase]"); if (mp && !ro) { const p = phaseBy(sel.uid); if (p) movePhase(p, +mp.dataset.movePhase); return; }
      const mr = t.closest("[data-move-route]");
      if (mr && !ro) { const r = routeBy(sel.uid); const i = wf.routes.indexOf(r), j = i + +mr.dataset.moveRoute; if (r && j >= 0 && j < wf.routes.length) { wb.snapshot(); wf.routes.splice(i, 1); wf.routes.splice(j, 0, r); wb.changed(); render(); } return; }
      const gt = t.closest("[data-goto-tab]"); if (gt) { tab = gt.dataset.gotoTab; renderBody(); return; }
      const wt = t.closest("[data-dz-wf-tab]"); if (wt) { tab = wt.dataset.dzWfTab; renderBody(); return; }
      const pr = t.closest("[data-pick-route]"); if (pr) { e.preventDefault(); sel = { kind: "route", uid: pr.dataset.pickRoute }; return render(); }
      if (t.closest("[data-pick-wf]")) { sel = { kind: "workflow" }; return render(); }
      const ph = t.closest("[data-phase]"); if (ph) { sel = { kind: "phase", uid: ph.dataset.phase }; render(); $(`[data-phase="${ph.dataset.phase}"]`, left)?.focus(); return; }
      const row = t.closest("[data-route]"); if (row && !t.closest("select,input,button")) { sel = { kind: "route", uid: row.dataset.route }; return render(); }
      const node = t.closest("[data-phase-key]"); if (node) { const p = phaseByKey(node.dataset.phaseKey); if (p) { sel = { kind: "phase", uid: p._uid }; render(); } return; }
      const edge = t.closest("[data-route-index]"); if (edge) { const r = wf.routes[+edge.dataset.routeIndex]; if (r) { sel = { kind: "route", uid: r._uid }; tab = "routes"; render(); } }
    });
    root.addEventListener("keydown", (e) => {
      const node = e.target.closest && e.target.closest("[data-phase-key]");
      if (node && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); node.dispatchEvent(new MouseEvent("click", { bubbles: true })); return; }
      const row = e.target.closest && e.target.closest("[data-route]");
      if (row && e.key === "Enter" && e.target === row) { sel = { kind: "route", uid: row.dataset.route }; render(); return; }
      const ph = e.target.closest && e.target.closest("[data-phase]");
      if (ph) {
        const p = phaseBy(ph.dataset.phase);
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); sel = { kind: "phase", uid: p._uid }; render(); $(`[data-phase="${p._uid}"]`, left)?.focus(); }
        else if (e.altKey && (e.key === "ArrowUp" || e.key === "ArrowDown") && !ro) { e.preventDefault(); sel = { kind: "phase", uid: p._uid }; movePhase(p, e.key === "ArrowUp" ? -1 : 1); $(`[data-phase="${p._uid}"]`, left)?.focus(); }
      }
    });
    body.addEventListener("change", (e) => {
      if (ro) return;
      const cell = e.target.closest("[data-cell]");
      if (cell) { const p = phaseBy(cell.dataset.ph); wb.snapshot(); setMatrix(p, cell.dataset.sec, cell.value); wb.changed(); renderBody(); renderProps(); return; }
      const all = e.target.closest("[data-col-all]");
      if (all && all.value) {
        const p = phaseBy(all.dataset.colAll);
        wb.snapshot();
        if (all.value === "default") { p.editableSections = []; p.readOnlySections = []; p.hiddenSections = []; }
        else setMatrix(p, "*", all.value);
        wb.changed(); renderBody(); renderProps();
      }
    });
    // Phase list drag and drop.
    left.addEventListener("dragstart", (e) => { const li = e.target.closest("[data-phase]"); if (!li || ro) return; drag = li.dataset.phase; e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", "phase"); });
    left.addEventListener("dragover", (e) => { if (!drag) return; const li = e.target.closest("[data-phase]"); $$(".drop-before", left).forEach((x) => x.classList.remove("drop-before")); if (li) { e.preventDefault(); li.classList.add("drop-before"); } });
    left.addEventListener("drop", (e) => {
      const li = e.target.closest("[data-phase]");
      if (!drag || !li) return;
      e.preventDefault();
      const p = phaseBy(drag), target = phaseBy(li.dataset.phase);
      drag = null;
      if (!p || p === target) return renderLeft();
      wb.snapshot();
      wf.phases.splice(wf.phases.indexOf(p), 1);
      wf.phases.splice(wf.phases.indexOf(target), 0, p);
      wb.changed(); render();
    });
    left.addEventListener("dragend", () => { drag = null; $$(".drop-before", left).forEach((x) => x.classList.remove("drop-before")); });

    function pickIssue(i) {
      const ph = /\/phase:(.+)$/.exec(i.path), rt = /\/route:(\d+)$/.exec(i.path);
      if (ph) { const p = phaseByKey(ph[1]); if (p) sel = { kind: "phase", uid: p._uid }; }
      else if (rt) { const r = wf.routes[+rt[1]]; if (r) { sel = { kind: "route", uid: r._uid }; tab = "routes"; } }
      else sel = { kind: "workflow" };
      render();
    }

    async function save() {
      if (ro || wb.saving) return false;
      const bad = wf.phases.find((p) => keyProblem(p.key));
      if (bad) { toast(`Phase design name “${bad.key}” ${keyProblem(bad.key)}.`, "bad"); return false; }
      wb.saving = true; wb.setStatus("saving");
      try {
        const r = await api(data.urls.save, { method: "POST", body: { workflow: strip(wf), renames: renames() } });
        if (!r || r.ok !== true) { wb.saving = false; wb.setStatus("error"); toast((r && r.error) || "The draft couldn't be saved.", "bad"); return false; }
        const phaseUid = new Map(wf.phases.map((p) => [p.key.toLowerCase(), p._uid]));
        const routeUids = wf.routes.map((x) => x._uid);
        const selRoute = sel.kind === "route" ? wf.routes.findIndex((x) => x._uid === sel.uid) : -1;
        wf = r.workflow;
        wf.phases.forEach((p) => { p._uid = phaseUid.get(p.key.toLowerCase()) || uid(); });
        wf.routes.forEach((x, i) => { x._uid = routeUids[i] || uid(); });
        prepare(wf);
        if (selRoute >= 0 && wf.routes[selRoute]) sel = { kind: "route", uid: wf.routes[selRoute]._uid };
        diagram.innerHTML = r.svg; table = r.table; issues = r.issues || [];
        wb.dirty = false; wb.saving = false;
        render();
        const errs = issues.filter((i) => i.isError).length;
        toast(errs ? `Saved to the draft. ${errs} problem${errs === 1 ? "" : "s"} must be fixed before publishing.` : "Saved to the draft. Publish to make it live.", errs ? "info" : "ok");
        return true;
      } catch (err) {
        wb.saving = false; wb.setStatus("error"); toast("The draft couldn't be saved: " + err.message, "bad"); return false;
      }
    }

    render();
  }

  // =====================================================================================================
  // View designer (DSN-702..706)
  // =====================================================================================================

  const viewRoot = $("[data-dz-view-designer]");
  if (viewRoot) viewDesigner(viewRoot);

  function viewDesigner(root) {
    const data = JSON.parse($("#dz-view-data").textContent);
    const ro = !!data.readOnly;
    let view = data.view;
    let issues = data.issues || [];
    const forms = data.forms || [];
    const formBy = Object.fromEntries(forms.map((f) => [f.key, f]));
    const sys = data.systemColumns || [];
    const sysBy = Object.fromEntries(sys.map((c) => [c.key, c]));
    const lists = data.keywordLists || {};
    const left = $("[data-dz-left]", root), canvas = $("[data-dz-canvas]", root), props = $("[data-dz-props]", root);
    let search = "", drag = null, preview = null, previewBusy = false;

    const norm = (v) => { v.forms = v.forms || []; v.columns = v.columns || []; v.phases = v.phases || []; v.filters = v.filters || []; return v; };
    norm(view);
    const viewForms = () => (view.forms.length ? view.forms.map((k) => formBy[k]).filter(Boolean) : forms.filter((f) => f.mine && !f.isActivity));
    function fieldsOfView() { const m = new Map(); viewForms().forEach((f) => f.fields.forEach((x) => { if (!m.has(x.key)) m.set(x.key, { ...x, form: f.name }); })); return [...m.values()]; }
    const fieldInfo = (k) => fieldsOfView().find((x) => x.key === k);
    const colLabel = (k) => sysBy[k]?.label || fieldInfo(k)?.label || k;
    const colKnown = (k) => !!sysBy[k] || !!fieldInfo(k);

    const refresh = debounce(runPreview, 450);
    const wb = workbench(root, {
      readOnly: ro,
      serialize: () => JSON.stringify(view),
      restore: (s) => { view = norm(JSON.parse(s)); },
      render: () => render(),
      save: () => save(),
      onChange: () => refresh(),
    });

    function render() { renderLeft(); renderCanvas(); renderProps(); wb.setIssues(issues, () => { }); const n = $("[data-dz-view-name]", root); if (n) n.textContent = view.name; }

    function renderLeft() {
      const q = search.toLowerCase();
      const item = (key, label, icon, sub) => {
        const used = view.columns.includes(key);
        if (q && !label.toLowerCase().includes(q) && !key.toLowerCase().includes(q)) return "";
        return `<li><button type="button" class="${used ? "used" : ""}" data-col="${esc(key)}" draggable="${!ro && !used}" ${ro ? "disabled" : ""} title="${used ? "Click to remove" : "Click or drag to add"}" aria-pressed="${used}"><i class="bi ${icon}"></i><span class="t">${esc(label)}${sub ? ` <span class="text-body-tertiary small">${esc(sub)}</span>` : ""}</span><i class="bi ${used ? "bi-check-lg" : "bi-plus-lg"} add"></i></button></li>`;
      };
      const fs = fieldsOfView();
      left.innerHTML = `<div class="dz-left-body">
        <div class="vl-search-input mb-2"><i class="bi bi-search"></i><input class="form-control form-control-sm" data-col-search placeholder="Find a column…" value="${esc(search)}" aria-label="Find a column"></div>
        <div class="dz-panel-title"><i class="bi bi-gear"></i>System columns</div>
        <ul class="dz-avail">${sys.map((c) => item(c.key, c.label, "bi-cpu", "")).join("")}</ul>
        <div class="dz-panel-title mt-3"><i class="bi bi-input-cursor"></i>Fields</div>
        <ul class="dz-avail">${fs.filter((x) => !sysBy[x.key]).map((x) => item(x.key, x.label || x.key, iconFor(x.type), viewForms().length > 1 ? x.form : "")).join("") || '<li class="dz-hint">Choose the view\'s forms first.</li>'}</ul>
        <div class="dz-hint mt-2">Drag columns into the view, or click to add and remove them (DSN-702).</div></div>`;
      const s = $("[data-col-search]", left);
      s.addEventListener("input", () => { search = s.value; const pos = s.selectionStart; renderLeft(); const n = $("[data-col-search]", left); n.focus(); n.setSelectionRange(pos, pos); });
    }
    const iconFor = (t) => ({ Text: "bi-input-cursor-text", TextArea: "bi-text-paragraph", Number: "bi-123", Currency: "bi-currency-dollar", Date: "bi-calendar3", DateTime: "bi-calendar-event", Keyword: "bi-menu-button-wide", KeywordMulti: "bi-ui-checks", YesNo: "bi-toggle-on", User: "bi-person", UserMulti: "bi-people", Link: "bi-link-45deg", Url: "bi-box-arrow-up-right", Attachment: "bi-paperclip" }[t] || "bi-square");

    function renderCanvas() {
      const cols = view.columns.length ? view.columns : ["number", "title", "phase", "due", "assignees"];
      const implicit = !view.columns.length;
      canvas.innerHTML = `
        <section class="card mb-3">
          <div class="card-header"><h2 class="card-title-sm"><i class="bi bi-layout-three-columns"></i>Columns <span class="vl-pill-count">${view.columns.length}</span></h2><span class="small text-body-tertiary ms-auto">Drag to reorder · Alt+←/→ with the keyboard</span></div>
          <div class="dz-colstrip" data-colstrip>${cols.map((k, i) => `<span class="dz-colchip${k === view.sortBy ? " is-sort" : ""}${colKnown(k) ? "" : " is-bad"}" data-chip="${esc(k)}" draggable="${!ro && !implicit}" tabindex="0" aria-label="Column ${i + 1}: ${esc(colLabel(k))}${k === view.sortBy ? ", sorted" : ""}">
            <span class="n">${i + 1}</span>${esc(colLabel(k))}${k === view.sortBy ? ` <i class="bi ${view.sortDesc ? "bi-sort-down" : "bi-sort-up"}" title="Sorted"></i>` : ""}
            ${ro || implicit ? "" : `<span class="tools"><button type="button" data-chip-move="-1" aria-label="Move left"><i class="bi bi-chevron-left"></i></button><button type="button" data-chip-move="1" aria-label="Move right"><i class="bi bi-chevron-right"></i></button><button type="button" data-chip-del aria-label="Remove"><i class="bi bi-x-lg"></i></button></span>`}</span>`).join("")}
            ${implicit ? '<span class="dz-hint align-self-center">Default columns. Add one to customize.</span>' : ""}</div>
        </section>
        <section class="card">
          <div class="card-header"><h2 class="card-title-sm"><i class="bi bi-eye"></i>Preview</h2>
            <span class="ms-auto small text-body-tertiary">${data.canPreview ? "Live data you can see (INV-081) · first 8 rows" : "Publish the application once to preview data"}</span>
            ${data.urls.live ? `<a class="btn btn-sm btn-ghost" href="${data.urls.live}" target="_blank" rel="noopener"><i class="bi bi-box-arrow-up-right"></i>Live view</a>` : ""}</div>
          <div data-preview>${previewHtml(cols)}</div>
        </section>`;
    }
    function previewHtml(cols) {
      if (!data.canPreview) return `<div class="vl-empty py-4"><div class="vl-empty-icon"><i class="bi bi-table"></i></div><p>The application isn't published yet, so there are no records.</p></div>`;
      if (!preview) return `<div class="p-3"><div class="dz-skel mb-2" style="width:40%"></div><div class="dz-skel mb-2"></div><div class="dz-skel"></div></div>`;
      if (!preview.ok) return `<div class="vl-banner vl-banner-warn m-3"><i class="bi bi-exclamation-triangle"></i><div>${esc(preview.error || "The preview couldn't run.")}</div></div>`;
      const head = preview.columns.map((c) => `<th class="${c.numeric ? "numeric" : ""}">${esc(c.label)}</th>`).join("");
      const rows = preview.rows.map((r) => `<tr>${r.cells.map((c, i) => {
        if (!c) return "<td></td>";
        const k = preview.columns[i].key;
        if (c.kind === "keyword" && c.text) return `<td><span class="vl-kw${c.color ? (c.color.startsWith("#") ? " vl-kw-hex" : " vl-kw-" + esc(c.color)) : ""}"${c.color && c.color.startsWith("#") ? ` style="--kw:${esc(c.color)}"` : ""}>${esc(c.text)}</span></td>`;
        if (c.kind === "phase") return `<td><span class="vl-chip ${c.color === "Completed" ? "vl-chip-done" : c.color === "Voided" ? "vl-chip-void" : "vl-chip-open"}">${esc(c.text)}</span></td>`;
        return `<td class="${k === "number" ? "col-number" : ""}${c.kind === "numeric" ? " numeric" : ""}">${esc(c.text) || '<span class="text-body-tertiary">–</span>'}</td>`;
      }).join("")}</tr>`).join("");
      return `<div class="d-flex align-items-baseline gap-2 px-3 pt-3"><span class="dz-preview-count">${preview.total.toLocaleString()}</span><span class="small text-body-secondary">matching record${preview.total === 1 ? "" : "s"}</span>${previewBusy ? '<span class="spinner-border spinner-border-sm text-body-tertiary ms-2"></span>' : ""}</div>
        <div class="vl-table-scroll mt-2"><table class="table vl-table vl-table-compact mb-0"><thead><tr>${head}</tr></thead><tbody>${rows || `<tr><td colspan="${preview.columns.length}" class="text-center text-body-tertiary py-4">No records match.</td></tr>`}</tbody></table></div>`;
    }
    async function runPreview() {
      if (!data.canPreview) return;
      previewBusy = true;
      const box = $("[data-preview]", canvas);
      try {
        preview = await api(data.urls.preview, { method: "POST", body: { view: strip(view) } });
      } catch (err) { preview = { ok: false, error: err.message }; }
      previewBusy = false;
      const b2 = $("[data-preview]", canvas);
      if (b2) b2.innerHTML = previewHtml(view.columns.length ? view.columns : ["number", "title", "phase", "due", "assignees"]);
      else if (box) box.innerHTML = "";
    }

    function renderProps() {
      pid = 0;
      const out = [];
      for (const i of issues) out.push(`<div class="dz-note ${i.isError ? "bad" : "warn"}"><i class="bi ${i.isError ? "bi-x-circle" : "bi-exclamation-triangle"}"></i> ${esc(i.message)}</div>`);
      let b = H.text("name", "Name", view.name, 'maxlength="200"') + H.key("key", view.key, view.id != null);
      b += `<div class="dz-prop"><label for="dzp-cat">Navigator category</label><input class="form-control" id="dzp-cat" data-p="category" data-t="str" value="${esc(view.category)}" list="dzp-cats"><datalist id="dzp-cats">${(data.categories || []).map((c) => `<option value="${esc(c)}"></option>`).join("")}</datalist><div class="dz-hint">Number prefixes set the order (PLT-1101).</div></div>`;
      b += H.check("isDefault", "Default view of the application", view.isDefault) + H.check("hidden", "Hidden from the navigator", view.hidden);
      out.push(H.group("View", "bi-table", b));
      const mine = forms.filter((f) => f.mine).map((f) => ({ value: f.key, label: f.name + (f.isActivity ? " (activity)" : ""), group: "This application" }));
      const others = forms.filter((f) => !f.mine).map((f) => ({ value: f.key, label: f.name, group: f.app }));
      out.push(H.group("Data source (INV-138)", "bi-database", `${H.members("forms", mine.concat(others), view.forms, "data-rerender")}<div class="dz-hint">None selected = every main form of this application.</div>`));
      let f = H.select("status", "Records", data.statuses.map((s) => [s.key, s.label]), view.status);
      const ph = []; const seen = new Set();
      viewForms().forEach((fm) => fm.phases.forEach((p) => { if (!seen.has(p.key)) { seen.add(p.key); ph.push({ value: p.key, label: p.name, group: viewForms().length > 1 ? fm.name : null }); } }));
      if (ph.length) f += `<div class="dz-prop"><span class="dz-prop-label">Only in phases</span>${H.members("phases", ph, view.phases)}</div>`;
      f += H.check("assignedToMe", "Assigned to me (DSN-706)", view.assignedToMe) + H.check("createdByMe", "Created by me", view.createdByMe) + H.check("overdueOnly", "Overdue only", view.overdueOnly);
      out.push(H.group("Filters", "bi-funnel", f));
      out.push(H.group("Field filters", "bi-sliders2", filtersHtml()));
      const sortable = sys.filter((c) => c.sortable).map((c) => [c.key, c.label]).concat(fieldsOfView().filter((x) => !["Link", "UserMulti", "KeywordMulti"].includes(x.type)).map((x) => [x.key, x.label]));
      let s = `<div class="dz-prop-row">${H.select("sortBy", "Sort by", sortable, view.sortBy)}${H.select("sortDesc", "Direction", [["true", "Descending"], ["false", "Ascending"]], String(view.sortDesc))}</div>`;
      s += H.num("pageSize", "Rows per page", view.pageSize, 'min="5" max="500"');
      const dates = [["", "None"], ["due", "Due"], ["created", "Created"], ["modified", "Modified"], ["effective", "Effective"]].concat(fieldsOfView().filter((x) => x.type === "Date" || x.type === "DateTime").map((x) => [x.key, x.label]));
      s += H.select("calendarField", "Calendar view by (PLT-1106)", dates, view.calendarField || "");
      out.push(H.group("Sorting and paging", "bi-sort-down", s));
      props.innerHTML = `<div class="dz-props-head"><span class="dz-ico tone-neutral"><i class="bi bi-sliders"></i></span><div class="min-w-0"><div class="t">${esc(view.name)}</div><div class="k">View settings</div></div></div><div class="dz-props-body"><fieldset ${ro ? "disabled" : ""}>${out.join("")}</fieldset></div>`;
    }
    function filtersHtml() {
      const fs = fieldsOfView();
      const rows = view.filters.map((flt, i) => {
        const fi = fs.find((x) => x.key === flt.field);
        let value = "";
        if (flt.op === "eq" || flt.op === "ne") {
          if (fi && fi.type === "YesNo") value = `<select class="form-select form-select-sm full" data-flt="${i}" data-fk="value" aria-label="Value"><option value="yes"${flt.value === "yes" ? " selected" : ""}>Yes</option><option value="no"${flt.value === "no" ? " selected" : ""}>No</option></select>`;
          else if (fi && (fi.type === "Keyword" || fi.type === "KeywordMulti") && lists[fi.list]) value = `<select class="form-select form-select-sm full" data-flt="${i}" data-fk="value" aria-label="Value"><option value="">Choose…</option>${lists[fi.list].map((k) => `<option value="${esc(k.key)}"${k.key === flt.value ? " selected" : ""}>${esc(k.label)}</option>`).join("")}</select>`;
          else value = `<input class="form-control form-control-sm full" data-flt="${i}" data-fk="value" value="${esc(flt.value || "")}" placeholder="${fi && (fi.type === "User" || fi.type === "UserMulti") ? "$me or a username" : "Value"}" aria-label="Value">`;
        }
        return `<div class="dz-filter-row"><select class="form-select form-select-sm" data-flt="${i}" data-fk="field" aria-label="Field">${fs.map((x) => `<option value="${esc(x.key)}"${x.key === flt.field ? " selected" : ""}>${esc(x.label)}</option>`).join("")}${fi ? "" : `<option selected value="${esc(flt.field)}">${esc(flt.field)} (missing)</option>`}</select>
          <select class="form-select form-select-sm" data-flt="${i}" data-fk="op" aria-label="Operator">${[["eq", "is"], ["ne", "is not"], ["set", "has a value"], ["empty", "is empty"]].map(([v, l]) => `<option value="${v}"${v === flt.op ? " selected" : ""}>${l}</option>`).join("")}</select>${value}
          ${ro ? "" : `<button type="button" class="rm" data-flt-del="${i}" aria-label="Remove filter"><i class="bi bi-x"></i></button>`}</div>`;
      }).join("");
      return `${rows || '<div class="dz-hint mb-2">No field filters.</div>'}${ro || !fs.length ? "" : '<button type="button" class="btn btn-sm btn-light" data-flt-add><i class="bi bi-plus-lg"></i>Add filter</button>'}`;
    }

    wb.bindPanel(props, (el, evt) => {
      if (el.dataset.pm) {
        applyMember(view, el);
        if (el.dataset.pm === "forms") { const valid = new Set(fieldsOfView().map((x) => x.key)); view.filters = view.filters.filter((f) => valid.has(f.field)); }
        renderLeft(); renderCanvas();
        if (el.hasAttribute("data-rerender")) renderProps();
        return;
      }
      const p = el.dataset.p;
      let v = readValue(el);
      if (p === "sortDesc") v = v === "true";
      if (p === "pageSize" && v != null) v = Math.max(5, Math.min(500, v));
      if (p === "key") { view.key = (v || "").trim(); el.classList.toggle("is-invalid", !!keyProblem(view.key)); }
      else view[p] = v ?? (p === "name" || p === "category" ? "" : null);
      if (p === "isDefault" && v) toast("This becomes the application's only default view when you save.", "info");
      renderCanvas();
      const n = $("[data-dz-view-name]", root); if (n) n.textContent = view.name;
    });
    props.addEventListener("change", (e) => {
      const el = e.target.closest("[data-flt]");
      if (!el || ro) return;
      wb.snapshot();
      const f = view.filters[+el.dataset.flt];
      f[el.dataset.fk] = el.value || null;
      if (el.dataset.fk !== "value") { if (f.op === "set" || f.op === "empty") f.value = null; renderProps(); }
      wb.changed();
    });
    props.addEventListener("input", (e) => {
      const el = e.target.closest('[data-flt][data-fk="value"]');
      if (!el || ro || el.tagName === "SELECT") return;
      view.filters[+el.dataset.flt].value = el.value || null;
      wb.changed();
    });
    props.addEventListener("click", (e) => {
      if (ro) return;
      if (e.target.closest("[data-flt-add]")) { const fs = fieldsOfView(); wb.snapshot(); view.filters.push({ field: fs[0].key, op: "eq", value: null }); wb.changed(); renderProps(); return; }
      const del = e.target.closest("[data-flt-del]");
      if (del) { wb.snapshot(); view.filters.splice(+del.dataset.fltDel, 1); wb.changed(); renderProps(); }
    });

    function toggleColumn(key, index) {
      if (ro) return;
      wb.snapshot();
      if (!view.columns.length) view.columns = ["number", "title", "phase", "due", "assignees"].filter((k) => k !== key);
      const i = view.columns.indexOf(key);
      if (i >= 0 && index === undefined) view.columns.splice(i, 1);
      else { if (i >= 0) view.columns.splice(i, 1); view.columns.splice(index ?? view.columns.length, 0, key); }
      wb.changed(); renderLeft(); renderCanvas();
    }
    function moveChip(key, delta) {
      const i = view.columns.indexOf(key), j = i + delta;
      if (i < 0 || j < 0 || j >= view.columns.length) return;
      wb.snapshot();
      view.columns.splice(i, 1); view.columns.splice(j, 0, key);
      wb.changed(); renderCanvas();
      $(`[data-chip="${CSS.escape(key)}"]`, canvas)?.focus();
    }
    root.addEventListener("click", (e) => {
      const c = e.target.closest("[data-col]");
      if (c && left.contains(c)) { toggleColumn(c.dataset.col); return; }
      const chip = e.target.closest("[data-chip]");
      if (!chip || ro) return;
      if (e.target.closest("[data-chip-del]")) { toggleColumn(chip.dataset.chip); return; }
      const mv = e.target.closest("[data-chip-move]");
      if (mv) moveChip(chip.dataset.chip, +mv.dataset.chipMove);
    });
    root.addEventListener("keydown", (e) => {
      const chip = e.target.closest && e.target.closest("[data-chip]");
      if (!chip || ro || !view.columns.length) return;
      if (e.altKey && (e.key === "ArrowLeft" || e.key === "ArrowRight")) { e.preventDefault(); moveChip(chip.dataset.chip, e.key === "ArrowLeft" ? -1 : 1); }
      else if (e.key === "Delete" || e.key === "Backspace") { e.preventDefault(); toggleColumn(chip.dataset.chip); }
    });
    root.addEventListener("dragstart", (e) => {
      if (ro) return;
      const c = e.target.closest && e.target.closest("[data-col]"), chip = e.target.closest && e.target.closest("[data-chip]");
      if (c) drag = { kind: "new", key: c.dataset.col };
      else if (chip && view.columns.length) { drag = { kind: "move", key: chip.dataset.chip }; chip.classList.add("dragging"); }
      else return;
      e.dataTransfer.effectAllowed = "move";
      e.dataTransfer.setData("text/plain", drag.key);
    });
    function chipIndex(strip, e) {
      const chips = $$("[data-chip]", strip).filter((x) => !x.classList.contains("dragging"));
      for (let i = 0; i < chips.length; i++) {
        const r = chips[i].getBoundingClientRect();
        if (e.clientY < r.top || (e.clientY <= r.bottom && e.clientX < r.left + r.width / 2)) return { i, el: chips[i] };
      }
      return { i: chips.length, el: null };
    }
    root.addEventListener("dragover", (e) => {
      if (!drag) return;
      const strip = e.target.closest("[data-colstrip]");
      $$(".drop-before,.drop-active", root).forEach((x) => x.classList.remove("drop-before", "drop-active"));
      if (!strip) return;
      e.preventDefault();
      strip.classList.add("drop-active");
      const { el } = chipIndex(strip, e);
      if (el) el.classList.add("drop-before");
    });
    root.addEventListener("drop", (e) => {
      const strip = e.target.closest("[data-colstrip]");
      if (!drag || !strip) return;
      e.preventDefault();
      const { i } = chipIndex(strip, e);
      const d = drag;
      drag = null;
      $$(".drop-before,.drop-active,.dragging", root).forEach((x) => x.classList.remove("drop-before", "drop-active", "dragging"));
      if (d.kind === "new") toggleColumn(d.key, i);
      else { wb.snapshot(); view.columns.splice(view.columns.indexOf(d.key), 1); view.columns.splice(i, 0, d.key); wb.changed(); renderCanvas(); }
    });
    root.addEventListener("dragend", () => { drag = null; $$(".drop-before,.drop-active,.dragging", root).forEach((x) => x.classList.remove("drop-before", "drop-active", "dragging")); });

    async function save() {
      if (ro || wb.saving) return false;
      if (keyProblem(view.key)) { toast(`Design name “${view.key}” ${keyProblem(view.key)}.`, "bad"); return false; }
      wb.saving = true; wb.setStatus("saving");
      try {
        const r = await api(data.urls.save, { method: "POST", body: { view: strip(view) } });
        if (!r || r.ok !== true) { wb.saving = false; wb.setStatus("error"); toast((r && r.error) || "The draft couldn't be saved.", "bad"); return false; }
        view = norm(r.view);
        issues = r.issues || [];
        wb.dirty = false; wb.saving = false;
        render();
        toast(issues.some((i) => i.isError) ? "Saved to the draft. Fix the problems before publishing." : "Saved to the draft. Publish to make it live.", issues.some((i) => i.isError) ? "info" : "ok");
        if (r.redirect) location.href = r.redirect;
        return true;
      } catch (err) { wb.saving = false; wb.setStatus("error"); toast("The draft couldn't be saved: " + err.message, "bad"); return false; }
    }

    render();
    runPreview();
  }
})();

// Workflow diagram: toggle between fitting the whole workflow and actual size. Fit is the default only while labels stay
// readable; large workflows open at actual size (scrollable).
(function () {
  const diagram = document.querySelector("[data-dz-diagram]");
  const btn = document.querySelector("[data-dz-fit]");
  if (!diagram || !btn) return;
  const setFit = (fit) => {
    diagram.classList.toggle("dz-fit", fit);
    btn.setAttribute("aria-pressed", fit ? "true" : "false");
    btn.innerHTML = fit ? '<i class="bi bi-arrows-angle-expand"></i><span>Actual size</span>' : '<i class="bi bi-arrows-angle-contract"></i><span>Fit</span>';
  };
  const natural = Number((diagram.querySelector("svg")?.getAttribute("viewBox") || "").split(/\s+/)[2]) || 0;
  setFit(!natural || diagram.clientWidth / natural >= 0.7);
  btn.addEventListener("click", () => setFit(!diagram.classList.contains("dz-fit")));
})();
