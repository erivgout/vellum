// Adverse Event Reporting client behaviour (progressive enhancement only; the server enforces every rule).
(function () {
  "use strict";

  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  // ------------------------------------------------------------------ certificate fields reject ( + < > ; " / \  (INT-902)
  $$("[data-emdr-cert-form]").forEach((form) => {
    const forbidden = form.dataset.forbidden || "";
    const check = (input) => {
      const bad = [...new Set([...input.value].filter((ch) => forbidden.includes(ch)))];
      const msg = bad.length ? `Remove ${bad.map((c) => `“${c}”`).join(" ")}: certificate fields can't contain ( + < > ; " / \\` : "";
      input.setCustomValidity(msg);
      input.classList.toggle("is-invalid", !!msg);
      const fb = document.getElementById(input.getAttribute("aria-describedby") || "");
      if (fb) fb.textContent = msg;
      return !msg;
    };
    const fields = $$("[data-emdr-cert-field]", form);
    fields.forEach((f) => f.addEventListener("input", () => check(f)));
    form.addEventListener("submit", (e) => {
      const ok = fields.map(check).every(Boolean);
      if (!ok) {
        e.preventDefault();
        fields.find((f) => f.classList.contains("is-invalid"))?.focus();
      }
    });
  });

  // ------------------------------------------------------------------ FDA endpoint defaults follow the account type
  const defaultsEl = document.getElementById("emdr-defaults");
  if (defaultsEl) {
    const defaults = JSON.parse(defaultsEl.textContent || "{}");
    const routing = document.querySelector("[data-emdr-default=routing]");
    const esg = document.querySelector("[data-emdr-default=esg]");
    $$("[data-emdr-account]").forEach((radio) => radio.addEventListener("change", () => {
      const next = defaults[radio.value];
      const other = defaults[radio.value === "production" ? "test" : "production"];
      if (!next || !other) return;
      // Only replace values that are still the other account's defaults (never an administrator's override).
      if (routing && (routing.value === "" || routing.value === other.routing)) routing.value = next.routing;
      if (esg && (esg.value === "" || esg.value === other.esg)) esg.value = next.esg;
    }));
  }
})();
