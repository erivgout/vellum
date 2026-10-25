// Vellum Insights: the alert create-record action editor (RPT-603).
// Data-attribute hooks only (strict CSP). Server-side validation in AlertCreateCatalog is authoritative; this mirrors it live.
//   [data-ins-create]       the editor (inside the alert form)
//   #ins-create-catalog     JSON: { apps:[{key,name,forms:[{key,name,phases:[...],fields:[...]}]}], tokens, filters, system }
//   #ins-create-state       JSON: the saved field values [{field,value}]
//   #ins-catalog            JSON: the watched apps/views/forms (for {record.KEY} and {group.KEY} tokens)
(() => {
  "use strict";
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
  const root = $("[data-ins-create]");
  const island = $("#ins-create-catalog");
  if (!root || !island) return;
  const form = root.closest("form");
  const json = (id, fallback) => { try { return JSON.parse($(id)?.textContent || "") ?? fallback; } catch { return fallback; } };
  const cat = json("#ins-create-catalog", { apps: [] });
  const saved = json("#ins-create-state", []);
  const watched = json("#ins-catalog", { apps: [], forms: {} });
  const people = json("#vl-directory", []);
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

  const appSel = $("[data-ac-app]", root);
  const formSel = $("[data-ac-form]", root);
  const phaseSel = $("[data-ac-phase]", root);
  const rowsEl = $("[data-ac-rows]", root);
  const statusEl = $("[data-ac-status]", root);
  const listsEl = $("[data-ac-lists]", root);
  let uid = 0;

  const app = () => cat.apps.find((a) => a.key === appSel.value);
  const target = () => app()?.forms.find((f) => f.key === formSel.value);
  const phase = () => { const f = target(); return f?.phases.find((p) => p.value === phaseSel.value) || f?.phases[0]; };
  const fieldOf = (key) => target()?.fields.find((f) => f.key === key);
  const isRequired = (f, p) => !!f && (f.required || (!!p && (f.requiredIn || []).includes(p.key)));

  // ------------------------------------------------------------ target selects
  function fillForms() {
    const a = app();
    const keep = formSel.value;
    formSel.textContent = "";
    for (const f of a?.forms || []) formSel.append(new Option(f.name, f.key, false, f.key === keep));
    if (!(a?.forms || []).some((f) => f.key === keep) && a?.forms.length) formSel.value = a.forms[0].key;
  }
  function fillPhases() {
    const f = target();
    const keep = phaseSel.value;
    phaseSel.textContent = "";
    for (const p of f?.phases || []) phaseSel.append(new Option(p.name, p.value, false, p.value === keep));
    if (!(f?.phases || []).some((p) => p.value === keep) && f?.phases.length) phaseSel.value = f.phases[0].value;
  }

  // ------------------------------------------------------------ the watched view (token sources)
  function matchColumns() {
    return $$("#al-match option").filter((o) => o.selected).map((o) => ({ key: o.value, label: o.textContent.trim() }));
  }
  function recordFields() {
    const a = (watched.apps || []).find((x) => x.key === $("[data-ins-app]")?.value);
    const v = a?.views.find((x) => x.key === $("[data-ins-view]")?.value);
    const seen = new Map();
    for (const fk of v?.forms || []) for (const c of (watched.forms || {})[fk] || []) if (!seen.has(c.key)) seen.set(c.key, c.label);
    return seen;
  }

  // ------------------------------------------------------------ the token language (mirrors AlertTemplate)
  const SIMPLE = new Set(["today", "due", "count", "groups", "minimum", "group", "record", "records", "alert.name", "alert.number", "report.number", "report.link"]);
  const FILTERS = new Set(["upper", "lower", "trim", "truncate", "default"]);
  function checkToken(name, match, fields) {
    if (SIMPLE.has(name)) return null;
    const today = /^today([+-])(\d{1,4})$/.exec(name);
    if (today) return +today[2] <= 3660 ? null : `{${name}}: at most 3660 days.`;
    if (name.startsWith("today")) return `{${name}}: write today+N or today-N.`;
    if (name.startsWith("group.")) return match.some((m) => m.key === name.slice(6)) ? null : `{${name}}: "${name.slice(6)}" isn't one of the match columns.`;
    if (name.startsWith("record.")) { const k = name.slice(7); return (cat.system || []).includes(k) || fields.has(k) ? null : `{${name}}: the watched forms have no field "${k}".`; }
    return `Unknown token {${name}}.`;
  }
  function checkTemplate(text, match, fields) {
    let i = 0;
    while (i < text.length) {
      const ch = text[i];
      if ((ch === "{" && text[i + 1] === "{") || (ch === "}" && text[i + 1] === "}")) { i += 2; continue; }
      if (ch !== "{") { i++; continue; }
      const close = text.indexOf("}", i + 1);
      if (close < 0) return "A token isn't closed: add } (or write {{ for a literal brace).";
      const inner = text.slice(i + 1, close);
      if (inner.includes("{")) return "Tokens can't be nested.";
      const parts = inner.split("|");
      const name = parts[0].trim();
      if (!name) return "A token is empty.";
      const bad = checkToken(name, match, fields);
      if (bad) return bad;
      for (const raw of parts.slice(1)) {
        const colon = raw.indexOf(":");
        const fname = (colon < 0 ? raw : raw.slice(0, colon)).trim().toLowerCase();
        const arg = colon < 0 ? null : raw.slice(colon + 1);
        if (!FILTERS.has(fname)) return `Unknown filter "${fname}".`;
        if (fname === "truncate" && !(/^\s*\d+\s*$/.test(arg || "") && +arg >= 1 && +arg <= 4000)) return "truncate needs a length from 1 to 4000.";
        if (fname === "default" && arg === null) return "default needs a text, e.g. |default:unknown.";
      }
      i = close + 1;
    }
    return null;
  }

  // ------------------------------------------------------------ rows
  function tokenOptions(sel) {
    const match = matchColumns();
    const fields = recordFields();
    sel.textContent = "";
    sel.append(new Option("Insert token…", ""));
    const group = (label, items) => {
      const og = document.createElement("optgroup");
      og.label = label;
      for (const [text, tok] of items) og.append(new Option(`${text}  ${tok}`, tok));
      sel.append(og);
    };
    group("Alert", [["Today", "{today}"], ["Today + 30 days", "{today+30}"], ["New record's due date", "{due}"], ["Matching records", "{count}"],
      ["Groups in the report", "{groups}"], ["Alert name", "{alert.name}"], ["Report number", "{report.number}"], ["Report link", "{report.link}"]]);
    group("Group", [["Group values", "{group}"], ...match.map((m) => [m.label, `{group.${m.key}}`])]);
    group("Matched records", [["Every match (links)", "{records}"], ["Most recent match", "{record}"], ["Number", "{record.number}"], ["Title", "{record.title}"],
      ["Phase", "{record.phase}"], ["Created", "{record.created}"], ["Created by", "{record.creator}"], ...[...fields].map(([k, l]) => [l, `{record.${k}}`])]);
  }

  function datalistFor(f) {
    if (!f) return "";
    const values = f.options || (f.kind === "user" ? people.map((p) => p.name) : null);
    if (!values || !values.length) return "";
    const id = `ac-dl-${formSel.value}-${f.key}`;
    if (!document.getElementById(id)) {
      const dl = document.createElement("datalist");
      dl.id = id;
      for (const v of values) dl.append(new Option(v));
      listsEl.append(dl);
    }
    return id;
  }

  function placeholderFor(f, required) {
    if (!f) return "";
    if (f.default) return `Default: ${f.default}`;
    if (required && f.meDefault) return "Required: the “current user” default doesn't apply to alerts";
    if (f.kind === "user") return "A username, a full name or a token";
    if (f.kind === "date") return "yyyy-mm-dd or {today+30}";
    if (f.kind === "link") return "{records}, {record} or a record number";
    if (f.options) return "An option, or a token";
    return "Text and tokens, e.g. {group}";
  }

  function metaFor(f, required) {
    if (!f) return "";
    const bits = [];
    if (required) bits.push('<span class="ins-ac-req"><i class="bi bi-asterisk" aria-hidden="true"></i>Required</span>');
    bits.push(esc(f.type) + (f.multi ? " · several allowed" : ""));
    if (f.options && f.options.length) bits.push(esc(f.options.slice(0, 4).join(", ") + (f.options.length > 4 ? ", …" : "")));
    return bits.join('<span class="sep" aria-hidden="true">·</span>');
  }

  function settableUnused(exceptKey) {
    const used = new Set($$("[data-ac-key]", rowsEl).map((x) => x.value).filter((k) => k !== exceptKey));
    return (target()?.fields || []).filter((f) => f.settable && !used.has(f.key));
  }

  function fillFieldSelect(sel) {
    const keep = sel.value;
    const p = phase();
    sel.textContent = "";
    for (const f of settableUnused(keep)) {
      const req = isRequired(f, p);
      sel.append(new Option(f.label + (req && f.visibleWhen ? " (required when shown)" : ""), f.key, false, f.key === keep));
    }
    if (keep && [...sel.options].some((o) => o.value === keep)) sel.value = keep;
  }

  function describeRow(row) {
    const key = $("[data-ac-key]", row).value;
    const f = fieldOf(key);
    const locked = row.classList.contains("is-required");
    const input = $("[data-ac-value]", row);
    const label = f?.label || key;
    $("[data-ac-meta]", row).innerHTML = metaFor(f, locked || isRequired(f, phase()));
    input.placeholder = placeholderFor(f, locked);
    input.setAttribute("aria-label", `Value for ${label}`);
    const list = datalistFor(f);
    if (list) input.setAttribute("list", list); else input.removeAttribute("list");
    $("[data-ac-token]", row).setAttribute("aria-label", `Insert a token into ${label}`);
    const rm = $("[data-ac-remove]", row);
    if (rm) rm.setAttribute("aria-label", `Remove ${label}`);
    if (f?.help) input.title = f.help; else input.removeAttribute("title");
  }

  function addRow(key, value, locked) {
    const f = fieldOf(key);
    const id = `ac-row-${++uid}`;
    const row = document.createElement("div");
    row.className = "ins-ac-row" + (locked ? " is-required" : "");
    row.innerHTML = `
      <div class="ins-ac-field">
        ${locked
          ? `<span class="ins-ac-label" id="${id}-l">${esc(f?.label || key)}</span><input type="hidden" data-ac-key value="${esc(key)}">`
          : `<select class="form-select form-select-sm" data-ac-key aria-label="Field"></select>`}
        <span class="ins-ac-meta" data-ac-meta></span>
      </div>
      <div class="ins-ac-value">
        <input class="form-control form-control-sm" type="text" data-ac-value autocomplete="off" maxlength="4000" aria-describedby="${id}-m">
        <div class="ins-ac-msg" id="${id}-m" data-ac-msg></div>
      </div>
      <div class="ins-ac-tools">
        <select class="form-select form-select-sm ins-ac-token" data-ac-token></select>
        ${locked ? '<span class="ins-ac-lock" title="Required fields can\'t be removed"><i class="bi bi-lock" aria-hidden="true"></i></span>'
                 : '<button type="button" class="btn btn-sm btn-ghost ins-ac-remove" data-ac-remove><i class="bi bi-x-lg" aria-hidden="true"></i></button>'}
      </div>`;
    rowsEl.append(row);
    if (!locked) {
      const sel = $("[data-ac-key]", row);
      sel.append(new Option(f?.label || key, key));
      fillFieldSelect(sel);
      sel.value = key;
    }
    $("[data-ac-value]", row).value = value || "";
    tokenOptions($("[data-ac-token]", row));
    describeRow(row);
    return row;
  }

  function readRows() {
    return $$(".ins-ac-row", rowsEl).map((r) => ({ field: $("[data-ac-key]", r).value, value: $("[data-ac-value]", r).value }));
  }

  function renumber() {
    $$(".ins-ac-row", rowsEl).forEach((r, i) => {
      $("[data-ac-key]", r).name = `Input.Create.Fields[${i}].Field`;
      $("[data-ac-value]", r).name = `Input.Create.Fields[${i}].Value`;
    });
    $$(".ins-ac-row select[data-ac-key]", rowsEl).forEach(fillFieldSelect);
    // Inputs inside a hidden action panel are disabled so they aren't posted (same rule as insights.js).
    for (const el of $$("input, select, textarea", root)) el.disabled = !!el.closest("[data-show][hidden]");
  }

  function rebuild(values) {
    const f = target();
    const p = phase();
    rowsEl.textContent = "";
    listsEl.textContent = "";
    if (!f) { validate(); return; }
    const given = new Map((values || []).filter((r) => r.field).map((r) => [r.field, r.value]));
    for (const fd of f.fields.filter((x) => isRequired(x, p) && x.settable && !x.visibleWhen)) {
      addRow(fd.key, given.get(fd.key) ?? "", true);
      given.delete(fd.key);
    }
    for (const [k, v] of given) if (fieldOf(k)?.settable) addRow(k, v, false);
    renumber();
    validate();
  }

  // ------------------------------------------------------------ live validation (INV-127)
  function validate() {
    const f = target();
    const p = phase();
    if (!f || !p) { statusEl.innerHTML = ""; return; }
    const match = matchColumns();
    const fields = recordFields();
    const missing = [];
    let invalid = 0;
    for (const row of $$(".ins-ac-row", rowsEl)) {
      const key = $("[data-ac-key]", row).value;
      const fd = fieldOf(key);
      const input = $("[data-ac-value]", row);
      const value = input.value.trim();
      let msg = "";
      if (!value) {
        if (row.classList.contains("is-required") && !fd?.default) { msg = "Required: enter a value or a token."; missing.push(fd?.label || key); }
      } else {
        msg = checkTemplate(value, match, fields) || "";
        if (!msg && fd?.maxLength && !value.includes("{") && value.length > fd.maxLength) msg = `At most ${fd.maxLength} characters.`;
        if (msg) invalid++;
      }
      row.classList.toggle("is-invalid", !!msg);
      input.classList.toggle("is-invalid", !!msg);
      input.setAttribute("aria-invalid", msg ? "true" : "false");
      $("[data-ac-msg]", row).textContent = msg;
    }
    const required = f.fields.filter((x) => isRequired(x, p));
    const attachments = required.filter((x) => x.attachment && !x.visibleWhen);
    const computed = required.filter((x) => x.computed);
    const conditional = required.filter((x) => x.visibleWhen && x.settable && !readRows().some((r) => r.field === x.key && r.value.trim()));

    // Someone to assign (INV-072): chosen people or groups, or the phase's own rule.
    const chosen = $$("[data-ac-assignees] input[type=hidden]", root).length + $$("[data-ac-assign-groups] option", root).filter((o) => o.selected).length;
    const autoField = p.autoField && readRows().some((r) => r.field === p.autoField && r.value.trim());
    const autoOk = !!p.auto && (!p.autoField || autoField);
    const assignHelp = $("[data-ac-assign-help]", root);
    if (assignHelp) assignHelp.textContent = p.auto ? `Active people only. Leave blank to assign ${p.auto}.` : `Active people only. ${p.name} doesn't assign anyone automatically, so choose at least one person or group.`;
    const noAssignee = chosen === 0 && !autoOk;
    const dueHelp = $("[data-ac-due-help]", root);
    if (dueHelp) dueHelp.textContent = `Weekends and holidays from the business calendar are skipped. Blank uses ${p.name}'s rule` + (p.dueDays != null ? `: ${p.dueDays} ${p.businessDays ? "business " : ""}days.` : ".");

    const problems = [];
    if (missing.length) problems.push(`Required field${missing.length === 1 ? "" : "s"} without a value: <strong>${esc(missing.join(", "))}</strong>.`);
    if (invalid) problems.push(`${invalid} value${invalid === 1 ? " has" : "s have"} a problem (see below).`);
    if (noAssignee) problems.push(`Nobody to assign: choose people or groups for ${esc(p.name)}.`);
    const notes = [];
    if (computed.length) notes.push(`Computed by the form: ${esc(computed.map((x) => x.label).join(", "))}. Checked on each record before it's kept.`);
    if (conditional.length) notes.push(`Required when shown: ${esc(conditional.map((x) => x.label).join(", "))}. Checked on each record before it's kept.`);
    const noteHtml = notes.length ? `<div class="small mt-1 text-body-secondary">${notes.join(" ")}</div>` : "";
    const reqCount = required.filter((x) => x.settable && !x.visibleWhen).length;
    if (attachments.length)
      statusEl.innerHTML = `<div class="vl-banner vl-banner-bad"><i class="bi bi-exclamation-octagon-fill" aria-hidden="true"></i><div><div class="ttl">Alerts can't create ${esc(f.name)} records</div>${esc(f.name)} requires ${esc(attachments.map((x) => x.label).join(", "))}, an attachment an alert can't add.</div></div>`;
    else if (problems.length)
      statusEl.innerHTML = `<div class="vl-banner vl-banner-warn"><i class="bi bi-exclamation-triangle-fill" aria-hidden="true"></i><div><div class="ttl">Not ready to create ${esc(f.name)} records</div><ul>${problems.map((x) => `<li>${x}</li>`).join("")}</ul>${noteHtml}</div></div>`;
    else
      statusEl.innerHTML = `<div class="vl-banner vl-banner-ok"><i class="bi bi-check-circle-fill" aria-hidden="true"></i><div><div class="ttl">Ready</div>All ${reqCount} required field${reqCount === 1 ? "" : "s"} of ${esc(f.name)} have a value.${noteHtml}</div></div>`;

    const n = readRows().filter((r) => r.value.trim()).length;
    const count = $("[data-ac-count]", root);
    if (count) count.textContent = `${n} field${n === 1 ? "" : "s"} set · ${settableUnused(null).length} more available`;
    const addBtn = $("[data-ac-add]", root);
    if (addBtn) addBtn.disabled = settableUnused(null).length === 0;
    const rf = $("[data-ac-record-fields]", root);
    if (rf) rf.textContent = [...fields.keys()].slice(0, 40).join(", ") + (fields.size > 40 ? ", …" : "") || "none";
  }

  function refreshTokens() {
    for (const sel of $$("[data-ac-token]", rowsEl)) tokenOptions(sel);
    validate();
  }

  // ------------------------------------------------------------ events
  appSel.addEventListener("change", () => { fillForms(); fillPhases(); rebuild(readRows()); });
  formSel.addEventListener("change", () => { fillPhases(); rebuild(readRows()); });
  phaseSel.addEventListener("change", () => rebuild(readRows()));
  rowsEl.addEventListener("input", (e) => { if (e.target.matches("[data-ac-value]")) validate(); });
  rowsEl.addEventListener("change", (e) => {
    const t = e.target;
    if (t.matches("select[data-ac-key]")) {
      const row = t.closest(".ins-ac-row");
      describeRow(row);
      renumber();
      validate();
    } else if (t.matches("[data-ac-token]")) {
      const tok = t.value;
      t.value = "";
      if (!tok) return;
      const input = $("[data-ac-value]", t.closest(".ins-ac-row"));
      const start = input.selectionStart ?? input.value.length;
      const end = input.selectionEnd ?? input.value.length;
      input.setRangeText(tok, start, end, "end");
      input.focus();
      validate();
    }
  });
  rowsEl.addEventListener("click", (e) => {
    const rm = e.target.closest("[data-ac-remove]");
    if (!rm) return;
    const row = rm.closest(".ins-ac-row");
    const next = row.nextElementSibling || row.previousElementSibling;
    row.remove();
    renumber();
    validate();
    ($("[data-ac-value]", next || root) || $("[data-ac-add]", root))?.focus();
  });
  $("[data-ac-add]", root)?.addEventListener("click", () => {
    const f = settableUnused(null)[0];
    if (!f) return;
    const row = addRow(f.key, "", false);
    renumber();
    validate();
    $("select[data-ac-key]", row)?.focus();
  });
  // The watched view and match columns feed {group.KEY} and {record.KEY}; insights.js refills them first.
  form.addEventListener("change", (e) => {
    if (e.target.closest("#al-match, [data-ins-app], [data-ins-view]")) setTimeout(refreshTokens, 0);
    else if (e.target.closest("[data-ac-assignees], [data-ac-assign-groups]")) validate();
    else if (e.target.name === "Input.Action") setTimeout(renumber, 0);
  });
  form.addEventListener("submit", renumber);

  rebuild(saved);
})();
