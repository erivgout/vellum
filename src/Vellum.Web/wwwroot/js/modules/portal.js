// Supplier portal (SUP-004). Loaded by the portal layout, and auto-included internally for the supplier profile's
// "Supplier portal" tab. Progressive enhancement only: every form works without it, and the server re-validates everything.
(function () {
  "use strict";
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  // Internal: buttons in the portal-contacts table open the matching record action with the row's contact pre-selected.
  document.addEventListener("click", (e) => {
    const b = e.target.closest("[data-portal-action]");
    if (!b) return;
    const modal = document.getElementById("action-" + b.dataset.portalAction);
    if (!modal || !window.bootstrap) return;
    e.preventDefault();
    const field = modal.querySelector(`[name="a.${CSS.escape(b.dataset.portalField || "")}"]`);
    if (field) field.value = b.dataset.portalValue || "";
    bootstrap.Modal.getOrCreateInstance(modal).show();
  });

  // Show / hide password fields (portal account pages; the sign-in pages use login.js).
  if (!document.querySelector("script[src*='login.js']")) {
    $$("[data-toggle-password]").forEach((b) => b.addEventListener("click", () => {
      const input = document.querySelector(b.dataset.togglePassword);
      if (!input) return;
      const show = input.type === "password";
      input.type = show ? "text" : "password";
      const icon = b.querySelector(".bi");
      if (icon) icon.className = "bi " + (show ? "bi-eye-slash" : "bi-eye");
      b.setAttribute("aria-label", show ? "Hide password" : "Show password");
    }));
  }

  // File pickers: list the chosen files, highlight the drop zone while dragging.
  $$("[data-portal-file]").forEach((input) => {
    const zone = input.closest("[data-portal-drop]");
    const list = zone?.querySelector("[data-portal-picked]");
    const render = () => {
      if (!list) return;
      list.innerHTML = "";
      Array.from(input.files || []).forEach((f) => {
        const li = document.createElement("li");
        li.textContent = `${f.name} (${f.size >= 1048576 ? (f.size / 1048576).toFixed(1) + " MB" : Math.max(1, Math.round(f.size / 1024)) + " KB"}): uploads when you save or submit`;
        list.appendChild(li);
      });
    };
    input.addEventListener("change", render);
    if (zone) {
      ["dragenter", "dragover"].forEach((ev) => zone.addEventListener(ev, () => zone.classList.add("dragover")));
      ["dragleave", "drop"].forEach((ev) => zone.addEventListener(ev, () => zone.classList.remove("dragover")));
    }
  });

  // Move focus to the error summary after a failed save or submit.
  const errors = document.querySelector("[data-focus-on-load]");
  if (errors) errors.focus();

  // Warn before leaving a response form with unsaved changes.
  $$("[data-portal-form]").forEach((form) => {
    let dirty = false;
    form.addEventListener("input", () => { dirty = true; });
    form.addEventListener("change", () => { dirty = true; });
    form.addEventListener("submit", () => { dirty = false; });
    window.addEventListener("beforeunload", (e) => { if (dirty) { e.preventDefault(); e.returnValue = ""; } });
  });
})();
