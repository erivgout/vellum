// Supplier Quality, Risk Register and FMEA: small progressive enhancements (CSP-safe, no inline handlers).
(() => {
  "use strict";

  // Sections whose fields are all conditional (e.g. chemical data on a material) are hidden while none apply.
  const conditionalSections = ["sec-chemical"];
  function syncSections() {
    conditionalSections.forEach((id) => {
      const sec = document.getElementById(id);
      if (!sec) return;
      const fields = Array.from(sec.querySelectorAll(".vl-field"));
      if (fields.length === 0) return;
      sec.hidden = fields.every((f) => f.hidden);
    });
  }

  document.querySelectorAll("form[data-record-form]").forEach((form) => {
    // site.js re-evaluates VisibleWhen on change/input; run after it.
    form.addEventListener("change", () => setTimeout(syncSections, 0));
    form.addEventListener("input", () => setTimeout(syncSections, 160));
  });
  syncSections();
})();
