// Vellum client behaviour. Progressive enhancement only: every action also works server-side,
// and the server re-validates everything (INV-082).
(function () {
  "use strict";

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const csrf = () => $('meta[name="csrf-token"]')?.content || "";
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };

  async function api(url, opts = {}) {
    const init = { credentials: "same-origin", headers: { "Accept": "application/json" }, ...opts };
    if (init.method && init.method !== "GET") {
      init.headers["X-CSRF-TOKEN"] = csrf();
      if (init.body && typeof init.body !== "string" && !(init.body instanceof FormData)) {
        init.headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(init.body);
      }
    }
    const res = await fetch(url, init);
    if (res.status === 401) { location.href = "/login?expired=1&returnUrl=" + encodeURIComponent(location.pathname + location.search); throw new Error("unauthorized"); }
    const type = res.headers.get("content-type") || "";
    return type.includes("json") ? res.json() : res.text();
  }
  window.vl = { api, esc };

  // ---------------------------------------------------------------- toasts
  function toast(message, kind = "ok") {
    let host = $(".vl-toasts");
    if (!host) { host = document.createElement("div"); host.className = "vl-toasts"; host.setAttribute("aria-live", "polite"); document.body.appendChild(host); }
    const icon = { ok: "bi-check-circle-fill", bad: "bi-exclamation-circle-fill", info: "bi-info-circle-fill" }[kind] || "bi-info-circle-fill";
    const el = document.createElement("div");
    el.className = "vl-toast " + kind;
    el.setAttribute("role", "status");
    el.innerHTML = `<i class="bi ${icon}"></i><div>${esc(message)}</div><button class="close" aria-label="Dismiss"><i class="bi bi-x"></i></button>`;
    el.querySelector(".close").onclick = () => el.remove();
    host.appendChild(el);
    setTimeout(() => el.remove(), kind === "bad" ? 9000 : 4500);
  }
  window.vl.toast = toast;
  $$("[data-toast]").forEach((n) => toast(n.dataset.toast, n.dataset.kind || "ok"));

  // ---------------------------------------------------------------- theme
  $$("[data-theme-set]").forEach((b) => b.addEventListener("click", async (e) => {
    e.preventDefault();
    const pref = b.dataset.themeSet;
    try { localStorage.setItem("vl-theme", pref); } catch (_) { }
    const dark = pref === "dark" || (pref === "auto" && matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.setAttribute("data-bs-theme", dark ? "dark" : "light");
    document.documentElement.setAttribute("data-theme-pref", pref);
    $$("[data-theme-set]").forEach((x) => x.classList.toggle("active", x.dataset.themeSet === pref));
    api("/api/me/theme", { method: "POST", body: { theme: pref } }).catch(() => { });
  }));
  const pref = document.documentElement.getAttribute("data-theme-pref");
  $$("[data-theme-set]").forEach((x) => x.classList.toggle("active", x.dataset.themeSet === pref));

  // ---------------------------------------------------------------- favorites
  $$("[data-fav-kind]").forEach((b) => b.addEventListener("click", async (e) => {
    e.preventDefault();
    const r = await api("/api/favorites/toggle", { method: "POST", body: { kind: b.dataset.favKind, refKey: b.dataset.favKey, label: b.dataset.favLabel } });
    b.classList.toggle("on", r.favorite);
    const i = b.querySelector(".bi");
    if (i) { i.classList.toggle("bi-star-fill", r.favorite); i.classList.toggle("bi-star", !r.favorite); }
    b.setAttribute("aria-pressed", r.favorite ? "true" : "false");
    toast(r.favorite ? "Added to favorites" : "Removed from favorites", "info");
  }));

  // ---------------------------------------------------------------- clickable rows
  document.addEventListener("click", (e) => {
    const tr = e.target.closest("tr[data-href]");
    if (!tr || e.target.closest("a,button,input,label,select,textarea")) return;
    if (e.ctrlKey || e.metaKey) window.open(tr.dataset.href, "_blank"); else location.href = tr.dataset.href;
  });
  $$("[data-check-all]").forEach((all) => all.addEventListener("change", () => {
    $$(all.dataset.checkAll).forEach((c) => { c.checked = all.checked; });
  }));

  // ---------------------------------------------------------------- command palette (Ctrl+K)
  const palette = $("#vl-palette");
  if (palette) {
    const modal = bootstrap.Modal.getOrCreateInstance(palette);
    const input = $("input", palette);
    const results = $(".vl-palette-results", palette);
    let items = [];
    let focus = 0;
    const groups = { app: "Applications", view: "Views", record: "Records", action: "Actions" };
    const actions = JSON.parse($("#vl-palette-actions")?.textContent || "[]");

    function render() {
      if (!items.length) { results.innerHTML = `<div class="vl-picker-none py-4">No matches. Press Enter to search everything.</div>`; return; }
      let html = "", last = "";
      items.forEach((it, i) => {
        if (it.kind !== last) { html += `<div class="vl-palette-group">${groups[it.kind] || it.kind}</div>`; last = it.kind; }
        html += `<a class="vl-palette-item${i === focus ? " focused" : ""}" href="${esc(it.href)}" data-i="${i}">
          <span class="vl-app-tile xs" style="--app:${esc(it.color || "#78716c")}"><i class="bi ${esc(it.icon || "bi-dot")}"></i></span>
          <span class="main"><div>${esc(it.label)}</div>${it.sub ? `<div class="sub">${esc(it.sub)}</div>` : ""}</span>
          ${i === focus ? '<span class="vl-kbd">↵</span>' : ""}</a>`;
      });
      results.innerHTML = html;
      results.querySelector(".focused")?.scrollIntoView({ block: "nearest" });
    }
    const load = debounce(async () => {
      const q = input.value.trim();
      const local = actions.filter((a) => !q || a.label.toLowerCase().includes(q.toLowerCase())).slice(0, q ? 6 : 5);
      try {
        const r = await api("/api/search/quick?q=" + encodeURIComponent(q));
        items = [...r.items.filter((x) => x.kind === "app"), ...local, ...r.items.filter((x) => x.kind !== "app")];
      } catch { items = local; }
      focus = 0; render();
    }, 140);
    input.addEventListener("input", load);
    input.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown") { e.preventDefault(); focus = Math.min(items.length - 1, focus + 1); render(); }
      else if (e.key === "ArrowUp") { e.preventDefault(); focus = Math.max(0, focus - 1); render(); }
      else if (e.key === "Enter") {
        e.preventDefault();
        if (items[focus]) location.href = items[focus].href;
        else if (input.value.trim()) location.href = "/search?q=" + encodeURIComponent(input.value.trim());
      }
    });
    palette.addEventListener("shown.bs.modal", () => { input.value = ""; input.focus(); load(); });
    document.addEventListener("keydown", (e) => {
      const typing = e.target.closest("input,textarea,select,[contenteditable]");
      if ((e.key === "k" && (e.ctrlKey || e.metaKey)) || (e.key === "/" && !typing)) { e.preventDefault(); modal.show(); }
    });
    $$("[data-open-palette]").forEach((b) => b.addEventListener("click", () => modal.show()));
  }

  // ---------------------------------------------------------------- notifications
  const notifBtn = $("#vl-notif-toggle");
  if (notifBtn) {
    const list = $("#vl-notif-list");
    const icons = {
      Assignment: ["bi-inbox-fill", "tone-info"], Info: ["bi-info-circle-fill", "tone-neutral"], Reminder: ["bi-alarm-fill", "tone-warn"],
      Escalation: ["bi-arrow-up-circle-fill", "tone-bad"], SafetyCritical: ["bi-exclamation-octagon-fill", "tone-bad"],
    };
    async function loadNotifs() {
      const r = await api("/api/notifications");
      const badge = $("#vl-notif-count");
      if (badge) { badge.textContent = r.unread > 99 ? "99+" : r.unread; badge.hidden = r.unread === 0; }
      if (!r.items.length) { list.innerHTML = `<div class="vl-empty py-4"><div class="vl-empty-icon"><i class="bi bi-bell-slash"></i></div><p>You're all caught up.</p></div>`; return; }
      list.innerHTML = r.items.map((n) => {
        const [ic, tone] = icons[n.kind] || icons.Info;
        const ack = n.kind === "SafetyCritical" && !n.ack ? `<button class="btn btn-sm btn-danger mt-2" data-ack="${n.id}"><i class="bi bi-check2"></i>Acknowledge</button>` : "";
        return `<a class="vl-notif${n.read ? "" : " unread"}" href="${esc(n.url || "#")}" data-read="${n.id}">
          <span class="ico ${tone}"><i class="bi ${ic}"></i></span>
          <span class="min-w-0"><div class="t">${esc(n.title)}</div>${n.body ? `<div class="b">${esc(n.body)}</div>` : ""}<div class="a">${esc(n.ago)}</div>${ack}</span></a>`;
      }).join("");
    }
    notifBtn.addEventListener("show.bs.dropdown", loadNotifs);
    list?.addEventListener("click", async (e) => {
      const ack = e.target.closest("[data-ack]");
      if (ack) { e.preventDefault(); e.stopPropagation(); await api(`/api/notifications/${ack.dataset.ack}/ack`, { method: "POST" }); toast("Alert acknowledged"); loadNotifs(); return; }
      const a = e.target.closest("[data-read]");
      if (a) api(`/api/notifications/${a.dataset.read}/read`, { method: "POST" }).catch(() => { });
    });
    $("#vl-notif-readall")?.addEventListener("click", async (e) => { e.preventDefault(); e.stopPropagation(); await api("/api/notifications/read-all", { method: "POST" }); loadNotifs(); });
  }

  // ---------------------------------------------------------------- session timeout (SEC-401)
  const timeoutModalEl = $("#vl-timeout");
  if (timeoutModalEl) {
    const tm = bootstrap.Modal.getOrCreateInstance(timeoutModalEl, { backdrop: "static", keyboard: false });
    const counter = $("[data-countdown]", timeoutModalEl);
    let left = 9999, shown = false, tick;
    async function check() {
      try {
        const s = await api("/api/session/status");
        left = s.secondsLeft;
        if (left <= 0) { location.href = "/login?expired=1&returnUrl=" + encodeURIComponent(location.pathname + location.search); return; }
        if (left <= 120 && !shown) { shown = true; tm.show(); countdown(); }
      } catch { }
    }
    function countdown() {
      clearInterval(tick);
      tick = setInterval(() => {
        left--;
        if (counter) counter.textContent = `${Math.floor(left / 60)}:${String(Math.max(0, left % 60)).padStart(2, "0")}`;
        if (left <= 0) { clearInterval(tick); location.href = "/login?expired=1&returnUrl=" + encodeURIComponent(location.pathname + location.search); }
      }, 1000);
    }
    $("[data-stay]", timeoutModalEl)?.addEventListener("click", async () => {
      await api("/api/session/ping", { method: "POST" });
      clearInterval(tick); shown = false; tm.hide(); toast("You're still signed in.", "info");
    });
    setInterval(check, 60000);
    setTimeout(check, 5000);
  }

  // ---------------------------------------------------------------- autogrow textareas
  function grow(t) { t.style.height = "auto"; t.style.height = Math.min(t.scrollHeight + 2, 560) + "px"; }
  document.addEventListener("input", (e) => { if (e.target.matches("textarea.vl-autogrow")) grow(e.target); });
  $$("textarea.vl-autogrow").forEach(grow);

  // ---------------------------------------------------------------- conditional fields (VisibleWhen)
  function fieldValues(form, key) {
    const els = $$(`[name="f.${CSS.escape(key)}"]`, form);
    const vals = [];
    els.forEach((el) => {
      if ((el.type === "radio" || el.type === "checkbox") && !el.checked) return;
      if (el.value) vals.push((el.dataset.key || el.selectedOptions?.[0]?.dataset.key || el.value).toLowerCase());
    });
    const ro = $(`[data-ro-field="${CSS.escape(key)}"]`, form);
    if (ro && !els.length && ro.dataset.roValue) vals.push(...ro.dataset.roValue.toLowerCase().split("|"));
    return vals;
  }
  function evalCond(cond, form) {
    return cond.split("||").some((any) => any.split("&&").every((c) => {
      c = c.trim(); if (!c) return true;
      const neg = c.includes("!=");
      const [k, v] = c.split(neg ? "!=" : "=");
      const vals = fieldValues(form, k.replace(/^!/, "").trim());
      if (v === undefined) return k.startsWith("!") ? vals.length === 0 : vals.length > 0;
      const hit = vals.includes(v.trim().toLowerCase());
      return neg ? !hit : hit;
    }));
  }
  function applyConditions(form) {
    $$("[data-visible-when]", form).forEach((el) => {
      const show = evalCond(el.dataset.visibleWhen, form);
      el.hidden = !show;
    });
  }
  $$("form[data-record-form]").forEach((form) => {
    applyConditions(form);
    form.addEventListener("change", () => applyConditions(form));
    form.addEventListener("input", debounce(() => applyConditions(form), 120));
  });

  // ---------------------------------------------------------------- people & record pickers
  const directory = JSON.parse($("#vl-directory")?.textContent || "[]");
  const dirById = new Map(directory.map((u) => [String(u.id), u]));
  const avatar = (u) => `<span class="vl-avatar vl-avatar-xs" style="--h:${esc(u.hue)}">${esc(u.initials)}</span>`;
  const highlight = (text, q) => {
    if (!q) return esc(text);
    const i = text.toLowerCase().indexOf(q.toLowerCase());
    return i < 0 ? esc(text) : esc(text.slice(0, i)) + "<mark>" + esc(text.slice(i, i + q.length)) + "</mark>" + esc(text.slice(i + q.length));
  };

  function initPicker(root) {
    if (root.dataset.ready) return;
    root.dataset.ready = "1";
    const kind = root.dataset.picker;           // users | records
    const name = root.dataset.name;
    const multi = root.dataset.multi === "true";
    const allowed = root.dataset.allowed ? new Set(root.dataset.allowed.split(",").filter(Boolean)) : null;
    const box = $(".vl-picker-box", root);
    const input = $("input[type=text]", root);
    const menu = $(".vl-picker-menu", root);
    let options = [], focus = 0;

    const selected = () => $$(`input[type=hidden][name="${CSS.escape(name)}"]`, root).map((h) => h.value);
    function addToken(id, label, html) {
      if (!multi) { $$(".vl-token", box).forEach((t) => t.remove()); }
      if (selected().includes(String(id))) return;
      const t = document.createElement("span");
      t.className = "vl-token" + (kind === "records" ? " record" : "");
      t.innerHTML = `${html || ""}<span class="t">${esc(label)}</span><button type="button" class="x" aria-label="Remove ${esc(label)}">×</button><input type="hidden" name="${esc(name)}" value="${esc(id)}">`;
      box.insertBefore(t, input);
      root.dispatchEvent(new Event("change", { bubbles: true }));
      if (!multi) input.placeholder = "";
    }
    box.addEventListener("click", (e) => {
      const x = e.target.closest(".x");
      if (x) { x.closest(".vl-token").remove(); root.dispatchEvent(new Event("change", { bubbles: true })); input.focus(); return; }
      input.focus();
    });
    function renderMenu(q) {
      if (!options.length) { menu.innerHTML = `<div class="vl-picker-none">${q ? "No matches" : "Start typing to search"}</div>`; return; }
      menu.innerHTML = options.map((o, i) => `<button type="button" class="vl-picker-opt${i === focus ? " focused" : ""}" data-i="${i}">${o.html || ""}<span class="min-w-0"><div>${highlight(o.label, q)}</div>${o.sub ? `<div class="sub">${esc(o.sub)}</div>` : ""}</span></button>`).join("");
    }
    const search = debounce(async () => {
      const q = input.value.trim();
      const chosen = new Set(selected());
      if (kind === "users") {
        options = directory.filter((u) => (!allowed || allowed.has(String(u.id))) && !chosen.has(String(u.id)) &&
          (!q || u.name.toLowerCase().includes(q.toLowerCase()) || (u.title || "").toLowerCase().includes(q.toLowerCase())))
          .slice(0, 30).map((u) => ({ id: u.id, label: u.name, sub: u.title, html: avatar(u) }));
      } else {
        const r = await api(`/api/lookup/records?q=${encodeURIComponent(q)}&forms=${encodeURIComponent(root.dataset.forms || "")}`);
        options = r.filter((x) => !chosen.has(x.id)).map((x) => ({ id: x.id, label: `${x.number}${x.revision ? " rev " + x.revision : ""} · ${x.title}`, sub: `${x.form}${x.phase ? " · " + x.phase : ""}`, html: `<i class="bi ${esc(x.icon)} text-body-tertiary"></i>` }));
      }
      focus = 0; renderMenu(q); menu.classList.add("show");
    }, kind === "users" ? 0 : 180);
    input.addEventListener("focus", search);
    input.addEventListener("input", search);
    input.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown") { e.preventDefault(); focus = Math.min(options.length - 1, focus + 1); renderMenu(input.value.trim()); }
      else if (e.key === "ArrowUp") { e.preventDefault(); focus = Math.max(0, focus - 1); renderMenu(input.value.trim()); }
      else if (e.key === "Enter") { e.preventDefault(); pick(focus); }
      else if (e.key === "Escape") { menu.classList.remove("show"); }
      else if (e.key === "Backspace" && !input.value) { const last = $$(".vl-token", box).pop(); if (last) { last.remove(); root.dispatchEvent(new Event("change", { bubbles: true })); } }
    });
    function pick(i) {
      const o = options[i]; if (!o) return;
      addToken(o.id, o.label, o.html);
      input.value = "";
      if (multi) search(); else menu.classList.remove("show");
    }
    menu.addEventListener("mousedown", (e) => { const b = e.target.closest("[data-i]"); if (b) { e.preventDefault(); pick(+b.dataset.i); } });
    input.addEventListener("blur", () => setTimeout(() => menu.classList.remove("show"), 120));
  }
  $$(".vl-picker[data-picker]").forEach(initPicker);
  window.vl.initPicker = initPicker;

  // ---------------------------------------------------------------- subforms
  $$("[data-subform]").forEach((sf) => {
    const key = sf.dataset.subform;
    const body = $("tbody", sf);
    const tpl = $(`template[data-row-template]`, sf);
    const max = +(sf.dataset.max || 0);
    function renumber() {
      $$("tr[data-row]", body).forEach((tr, i) => { const n = $(".row-num", tr); if (n) n.textContent = i + 1; });
      const empty = $(".vl-subform-empty", sf);
      if (empty) empty.hidden = $$("tr[data-row]", body).length > 0;
      const add = $("[data-add-row]", sf);
      if (add && max) add.disabled = $$("tr[data-row]", body).length >= max;
    }
    $("[data-add-row]", sf)?.addEventListener("click", () => {
      const idx = Date.now() % 1000000 + Math.floor(Math.random() * 1000);
      const html = tpl.innerHTML.replaceAll("__INDEX__", idx);
      body.insertAdjacentHTML("beforeend", html);
      const row = body.lastElementChild;
      $$(".vl-picker[data-picker]", row).forEach(initPicker);
      $("input,select,textarea", row)?.focus();
      renumber();
      sf.closest("form")?.dispatchEvent(new Event("input", { bubbles: true }));
    });
    sf.addEventListener("click", (e) => {
      const rm = e.target.closest("[data-remove-row]");
      if (!rm) return;
      rm.closest("tr").remove();
      renumber();
      sf.closest("form")?.dispatchEvent(new Event("input", { bubbles: true }));
    });
    renumber();
  });

  // ---------------------------------------------------------------- attachments drop zone
  $$("[data-dropzone]").forEach((dz) => {
    const input = $("input[type=file]", dz.parentElement);
    const pending = $(".vl-files[data-pending]", dz.parentElement);
    const show = () => {
      pending.innerHTML = Array.from(input.files).map((f) => `<li class="vl-file pending"><i class="bi bi-cloud-arrow-up ficon"></i><span class="min-w-0"><span class="fname">${esc(f.name)}</span><span class="fmeta">${(f.size / 1024).toFixed(1)} KB · uploads when you save</span></span></li>`).join("");
    };
    dz.addEventListener("click", () => input.click());
    dz.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); input.click(); } });
    input.addEventListener("change", show);
    ["dragenter", "dragover"].forEach((ev) => dz.addEventListener(ev, (e) => { e.preventDefault(); dz.classList.add("drag"); }));
    ["dragleave", "drop"].forEach((ev) => dz.addEventListener(ev, (e) => { e.preventDefault(); dz.classList.remove("drag"); }));
    dz.addEventListener("drop", (e) => {
      const dt = new DataTransfer();
      Array.from(input.files).forEach((f) => dt.items.add(f));
      Array.from(e.dataTransfer.files).forEach((f) => dt.items.add(f));
      input.files = dt.files;
      show();
      dz.closest("form")?.dispatchEvent(new Event("input", { bubbles: true }));
    });
  });

  // ---------------------------------------------------------------- record page
  const recordForm = $("form[data-record-form]");
  if (recordForm) {
    const recordId = recordForm.dataset.recordId;
    let dirty = false, submitting = false;
    recordForm.addEventListener("input", () => { dirty = true; });
    recordForm.addEventListener("change", () => { dirty = true; });
    recordForm.addEventListener("submit", (e) => {
      submitting = true;
      const btn = e.submitter;
      if (btn && !btn.dataset.noBusy) { btn.disabled = true; btn.insertAdjacentHTML("afterbegin", '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>'); setTimeout(() => { btn.disabled = false; }, 8000); if (btn.name) { const h = document.createElement("input"); h.type = "hidden"; h.name = btn.name; h.value = btn.value; recordForm.appendChild(h); } }
    });
    window.addEventListener("beforeunload", (e) => { if (dirty && !submitting && recordForm.dataset.editing === "true") { e.preventDefault(); e.returnValue = ""; } });

    // Ctrl+S saves (NFR-053)
    document.addEventListener("keydown", (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        const save = $("[data-save-button]");
        if (save) { e.preventDefault(); save.click(); }
      }
    });

    // Edit lock (PLT-901/902)
    if (recordId && recordForm.dataset.editing === "true") {
      const state = $("#vl-lockstate");
      const acquire = async () => {
        try {
          const r = await api(`/api/records/${recordId}/lock`, { method: "POST" });
          if (state) {
            if (r.mine) { state.className = "vl-lockstate editing"; state.innerHTML = '<span class="pulse"></span>You\'re editing'; }
            else { state.className = "vl-lockstate locked"; state.innerHTML = `<i class="bi bi-lock-fill"></i>${esc(r.holder)} is editing`; }
          }
        } catch { }
      };
      acquire();
      setInterval(acquire, 4 * 60 * 1000);
      window.addEventListener("pagehide", () => {
        if (submitting) return;
        const fd = new FormData();
        fd.append("__RequestVerificationToken", csrf());
        navigator.sendBeacon(`/api/records/${recordId}/unlock`, fd);
      });
    }

    // Remember the active tab per record
    const tabKey = "vl-tab-" + (recordId || "new");
    const errTab = $(".vl-tabs .nav-link.has-error");
    const saved = sessionStorage.getItem(tabKey);
    const target = errTab || (saved && $(`.vl-tabs [data-bs-target="${CSS.escape(saved)}"]`));
    if (target) bootstrap.Tab.getOrCreateInstance(target).show();
    $$(".vl-tabs [data-bs-toggle=tab]").forEach((t) => t.addEventListener("shown.bs.tab", () => sessionStorage.setItem(tabKey, t.dataset.bsTarget)));
    // Jump to a field from the error summary
    $$("[data-goto-field]").forEach((a) => a.addEventListener("click", (e) => {
      e.preventDefault();
      const el = document.getElementById(a.dataset.gotoField);
      const pane = el?.closest(".tab-pane");
      if (pane) bootstrap.Tab.getOrCreateInstance($(`.vl-tabs [data-bs-target="#${pane.id}"]`)).show();
      setTimeout(() => { el?.scrollIntoView({ block: "center", behavior: "smooth" }); el?.querySelector?.("input,select,textarea")?.focus(); (el?.matches?.("input,select,textarea") ? el : null)?.focus(); }, 150);
    }));
  }

  // Sticky action bar hairline
  const bar = $(".vl-actionbar");
  if (bar && "IntersectionObserver" in window) {
    const sentinel = document.createElement("div");
    bar.before(sentinel);
    new IntersectionObserver(([e]) => bar.classList.toggle("stuck", !e.isIntersecting), { rootMargin: "-120px 0px 0px 0px" }).observe(sentinel);
  }

  // ---------------------------------------------------------------- route dialog
  $$("[data-route-dialog]").forEach((dlg) => {
    const opts = $$("[data-route-option]", dlg);
    const panels = $$("[data-route-panel]", dlg);
    const submit = $("[data-route-submit]", dlg);
    const routeInput = $("input[name=routeId]", dlg);
    function select(id) {
      opts.forEach((o) => { const on = o.dataset.routeOption === id; o.classList.toggle("selected", on); o.setAttribute("aria-checked", on); });
      panels.forEach((p) => {
        const on = p.dataset.routePanel === id;
        p.hidden = !on;
        $$("input,select,textarea", p).forEach((el) => { el.disabled = !on; });
      });
      routeInput.value = id;
      const opt = opts.find((o) => o.dataset.routeOption === id);
      if (submit && opt) {
        submit.innerHTML = `${opt.dataset.sign ? '<i class="bi bi-pen"></i>' : '<i class="bi bi-send"></i>'}${esc(opt.dataset.confirm)}`;
        submit.className = "btn " + (opt.dataset.kind === "Void" ? "btn-danger" : opt.dataset.kind === "Back" ? "btn-ink" : "btn-primary");
      }
      setTimeout(() => $(`[data-route-panel="${CSS.escape(id)}"] textarea, [data-route-panel="${CSS.escape(id)}"] input[type=password]`, dlg)?.focus(), 60);
    }
    opts.forEach((o) => o.addEventListener("click", () => select(o.dataset.routeOption)));
    dlg.addEventListener("show.bs.modal", (e) => {
      const want = e.relatedTarget?.dataset.route || dlg.dataset.selected || opts[0]?.dataset.routeOption;
      if (want) select(want);
    });
    if (dlg.dataset.autoshow === "true") bootstrap.Modal.getOrCreateInstance(dlg).show();
  });

  // Generic modal auto-show (e.g. re-open an action dialog after a failed post)
  $$(".modal[data-autoshow=true]:not([data-route-dialog])").forEach((m) => bootstrap.Modal.getOrCreateInstance(m).show());

  // Tooltips
  $$('[data-bs-toggle="tooltip"]').forEach((el) => bootstrap.Tooltip.getOrCreateInstance(el));
})();

// Copy-to-clipboard buttons
document.addEventListener("click", async (e) => {
  const b = e.target.closest("[data-copy]");
  if (!b) return;
  e.preventDefault();
  try { await navigator.clipboard.writeText(b.dataset.copy); window.vl.toast("Link copied", "info"); } catch { window.vl.toast("Couldn't copy the link", "bad"); }
});
// Print buttons
document.addEventListener("click", (e) => { if (e.target.closest("[data-print]")) { e.preventDefault(); window.print(); } });
// Confirmation prompts
document.addEventListener("click", (e) => {
  const b = e.target.closest("[data-confirm]");
  if (b && b.dataset.confirm && !window.confirm(b.dataset.confirm)) { e.preventDefault(); e.stopImmediatePropagation(); }
}, true);
// Auto-submit filters
document.addEventListener("change", (e) => { if (e.target.matches("[data-autosubmit]")) e.target.form?.requestSubmit(); });
// Inline acknowledgment of safety-critical alerts (notifications page)
document.addEventListener("click", async (e) => {
  const b = e.target.closest("[data-ack-inline]");
  if (!b) return;
  e.preventDefault();
  b.disabled = true;
  await window.vl.api(`/api/notifications/${b.dataset.ackInline}/ack`, { method: "POST" });
  b.outerHTML = '<span class="vl-kw vl-kw-success">Acknowledged</span>';
  window.vl.toast("Alert acknowledged. Your acknowledgment is recorded.");
});
