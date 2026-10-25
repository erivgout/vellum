// Administration Center behaviour (Areas/Admin). Progressive enhancement only: the server validates and enforces
// everything (SEC-519). Hooks are data-adm-* attributes so nothing here touches other modules.
(function () {
  "use strict";
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  if (!document.querySelector(".adm")) return;
  const toast = (m, k) => window.vl?.toast?.(m, k);
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // ------------------------------------------------------------ password fields
  document.addEventListener("click", (e) => {
    const reveal = e.target.closest("[data-adm-reveal]");
    if (reveal) {
      const input = $(reveal.dataset.admReveal);
      if (!input) return;
      const show = input.type === "password";
      input.type = show ? "text" : "password";
      reveal.setAttribute("aria-label", show ? "Hide password" : "Show password");
      reveal.querySelector(".bi")?.classList.toggle("bi-eye-slash", show);
      reveal.querySelector(".bi")?.classList.toggle("bi-eye", !show);
      return;
    }
    const gen = e.target.closest("[data-adm-genpw]");
    if (gen) {
      const input = $(gen.dataset.admGenpw);
      if (!input) return;
      input.value = generatePassword(Math.max(16, (+gen.dataset.admMin || 12) + 4));
      input.type = "text";
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.focus();
      input.select();
      toast("Generated a strong temporary password. Copy it now; it's never shown again.", "info");
    }
  });

  function generatePassword(length) {
    const sets = ["ABCDEFGHJKLMNPQRSTUVWXYZ", "abcdefghijkmnopqrstuvwxyz", "23456789", "!#$%&*+-=?@^_~"];
    const all = sets.join("");
    const rnd = (n) => { const a = new Uint32Array(1); crypto.getRandomValues(a); return a[0] % n; };
    const chars = sets.map((s) => s[rnd(s.length)]);
    while (chars.length < length) chars.push(all[rnd(all.length)]);
    for (let i = chars.length - 1; i > 0; i--) { const j = rnd(i + 1); [chars[i], chars[j]] = [chars[j], chars[i]]; }
    return chars.join("");
  }

  // ------------------------------------------------------------ system preferences (SAF-010/011)
  const settings = $("form[data-adm-settings]");
  if (settings) {
    const value = (el) => (el.type === "checkbox" ? String(el.checked) : el.value);
    const fields = $$("input:not([type=hidden]):not([type=password]),select,textarea", settings).filter((el) => el.closest(".adm-setting"));
    const initial = new Map(fields.map((el) => [el, value(el)]));
    const panel = $("[data-adm-sensitive-panel]", settings);
    const summary = $("[data-adm-sensitive-summary]", settings);
    const reason = $("#s-reason", settings);
    const pw = $("#s-pw", settings);
    const defaultSummary = summary?.textContent || "";
    // After a failed save the form shows unsaved values; the server lists them so they still count as changes.
    const pending = (panel?.dataset.admPending || "").split("|").filter(Boolean);
    const pendingKeys = new Set((panel?.dataset.admPendingKeys || "").split(" ").filter(Boolean));
    function refresh() {
      const sensitive = [...pending];
      $$(".adm-setting", settings).forEach((row) => {
        row.classList.remove("is-changed", "is-sensitive");
        if (pendingKeys.has(row.id)) row.classList.add("is-changed");
        if (pendingKeys.has(row.id) && $("[data-adm-sensitive]", row)) row.classList.add("is-sensitive");
      });
      fields.forEach((el) => {
        if (value(el) === initial.get(el)) return;
        const row = el.closest(".adm-setting");
        row.classList.add("is-changed");
        if (el.dataset.admSensitive) { row.classList.add("is-sensitive"); sensitive.push(el.dataset.admSensitive); }
      });
      const on = sensitive.length > 0;
      panel?.classList.toggle("active", on);
      if (reason) reason.required = on;
      if (pw) pw.required = on;
      if (summary) summary.textContent = on
        ? `You changed ${sensitive.length === 1 ? "a compliance-sensitive setting" : sensitive.length + " compliance-sensitive settings"} (${[...new Set(sensitive)].join(", ")}). Give a reason and confirm with your password.`
        : defaultSummary;
    }
    settings.addEventListener("input", refresh);
    settings.addEventListener("change", refresh);
    // Keep the banner preview in step with the environment name and text.
    const preview = $(".adm-banner-preview", settings);
    const envName = $("#s-envname", settings), banner = $("#s-banner", settings);
    if (preview && envName && banner) {
      const spans = $$("span", preview);
      const sync = () => { spans[0].textContent = `${envName.value || "Environment"} environment`; spans[2].textContent = banner.value || banner.placeholder; };
      envName.addEventListener("input", sync);
      banner.addEventListener("input", sync);
    }
    refresh();
  }

  // ------------------------------------------------------------ keyword rename warning (ADM-607) and colors
  $$("form[data-adm-rename]").forEach((form) => {
    const label = $("[data-adm-kw-label]", form);
    const warning = $("[data-adm-rename-warning]", form);
    const confirm = $("[data-adm-confirm-rename]", form);
    if (!label || !warning) return;
    const sticky = !warning.hidden && label.value !== label.dataset.original;
    const update = () => {
      const renamed = label.value.trim() !== label.dataset.original;
      if (confirm) { warning.hidden = !(renamed || sticky); confirm.required = renamed; }
    };
    label.addEventListener("input", update);
    update();
  });

  const variants = ["success", "warning", "danger", "info", "violet", "secondary", "dark"];
  $$("select[data-adm-color]").forEach((select) => {
    const form = select.closest("form");
    const custom = $("[data-adm-custom-color]", form);
    const hex = custom ? $("input", custom) : null;
    const preview = $("[data-adm-kw-preview]", form);
    const label = $("[data-adm-kw-label]", form);
    const render = () => {
      if (custom) custom.hidden = select.value !== "custom";
      if (!preview) return;
      const text = esc(label?.value || "Keyword");
      const c = select.value === "custom" ? (hex?.value || "") : select.value;
      preview.innerHTML = /^#[0-9a-f]{6}$/i.test(c)
        ? `<span class="vl-kw vl-kw-hex" style="--kw:${c}">${text}</span>`
        : `<span class="vl-kw${variants.includes(c) ? " vl-kw-" + c : ""}">${text}</span>`;
    };
    select.addEventListener("change", render);
    hex?.addEventListener("input", render);
    label?.addEventListener("input", render);
  });

  // ------------------------------------------------------------ keys derived from names (groups, keywords)
  $$("[data-adm-slug-source]").forEach((src) => {
    const target = $(src.dataset.admSlugSource);
    if (!target || target.readOnly) return;
    let touched = target.value.length > 0;
    target.addEventListener("input", () => { touched = target.value.length > 0; });
    src.addEventListener("input", () => {
      if (touched) return;
      const sep = target.id === "add-key" ? "_" : "-";
      target.placeholder = src.value.toLowerCase().trim().replace(/[^a-z0-9]+/g, sep).replace(new RegExp(`^\\${sep}+|\\${sep}+$`, "g"), "").slice(0, 60) || "from the name";
    });
  });

  // ------------------------------------------------------------ grant form: person or group
  $$("form[data-adm-grant]").forEach((form) => {
    const sync = () => {
      const kind = $("[data-adm-kind]:checked", form)?.value || "user";
      $$("[data-adm-kind-panel]", form).forEach((p) => { p.hidden = p.dataset.admKindPanel !== kind; });
    };
    $$("[data-adm-kind]", form).forEach((r) => r.addEventListener("change", sync));
    sync();
  });

  // ------------------------------------------------------------ long-running posts (run now, verify chains)
  $$("form[data-adm-busy]").forEach((form) => form.addEventListener("submit", (e) => {
    const btn = e.submitter || $("button", form);
    if (!btn || btn.disabled) return;
    setTimeout(() => {
      btn.disabled = true;
      btn.insertAdjacentHTML("afterbegin", '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>');
    }, 0);
  }));
})();
