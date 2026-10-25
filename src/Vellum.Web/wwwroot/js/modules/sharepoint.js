// SharePoint publishing administration (Areas/Admin/Pages/Integrations/SharePoint). Progressive enhancement only:
// the server validates and sanitises everything (INT-302, INT-304). Hooks are data-sp-* attributes.
(function () {
  "use strict";
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  // ------------------------------------------------------------ token chips insert into the last focused template field
  let lastTemplate = $("[data-sp-template]");
  const targetLabel = $("[data-sp-token-target-label]");
  $$("[data-sp-template]").forEach((el) => el.addEventListener("focus", () => {
    lastTemplate = el;
    if (targetLabel) targetLabel.textContent = el.id === "r-file" ? "file name" : "folder";
  }));
  document.addEventListener("click", (e) => {
    const chip = e.target.closest("[data-sp-token]");
    if (!chip) return;
    const input = chip.dataset.spTarget ? $(chip.dataset.spTarget) : lastTemplate;
    if (!input) return;
    const token = chip.dataset.spToken;
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? input.value.length;
    input.value = input.value.slice(0, start) + token + input.value.slice(end);
    input.focus();
    input.setSelectionRange(start + token.length, start + token.length);
    input.dispatchEvent(new Event("input", { bubbles: true }));
  });

  // ------------------------------------------------------------ live folder / file name preview (mirrors SharePointNaming)
  const preview = $("[data-sp-preview-path]");
  if (preview) {
    const sample = JSON.parse($("#sp-sample")?.textContent || "{}");
    const folder = $("#r-folder"), file = $("#r-file"), lib = $("[data-sp-lib]");
    const invalid = /["*:<>?/\\|#%\u0000-\u001f]/g;
    const clean = (s) => {
      let v = String(s).replace(invalid, "_").replace(/\s+/g, " ").trim();
      if (v.startsWith("~$")) v = "_" + v.slice(1);
      if (v.endsWith(".")) v = v.replace(/\.+$/, "") + "_";
      return v || "_";
    };
    const apply = (tpl, slashSafe) => tpl.replace(/\{([^{}]+)\}/g, (_, k) => {
      const key = k.trim();
      const v = key in sample ? sample[key] : (key === "file" ? "procedure" : `{${key}}`);
      return slashSafe ? String(v).replace(/[\\/]/g, "_") : v;
    }).replace(/\s+[–\-|]\s*$|^\s*[–\-|]\s+/g, "").trim();
    const render = () => {
      const parts = [lib?.selectedOptions[0]?.dataset.name || "Library"];
      const f = apply(folder?.value || "", true);
      f.replace(/\\/g, "/").split("/").map((s) => s.trim()).filter(Boolean).forEach((s) => parts.push(clean(s)));
      const stem = file?.value ? apply(file.value, false).replace(/[\\/]/g, "_") : "procedure";
      parts.push(clean((stem || "procedure") + ".pdf"));
      preview.textContent = parts.join(" / ");
    };
    [folder, file].forEach((el) => el?.addEventListener("input", render));
    lib?.addEventListener("change", render);
    render();
  }

  // ------------------------------------------------------------ mapping rows: add, remove, renumber
  const body = $("[data-sp-map-body]");
  const tpl = $("template[data-sp-map-template]");
  if (body && tpl) {
    const renumber = () => $$("tr[data-sp-map-row]", body).forEach((tr, i) => {
      const n = i + 1;
      const num = $("[data-sp-num]", tr);
      if (num) num.textContent = n;
      $("select", tr)?.setAttribute("aria-label", `Vellum value, row ${n}`);
      $("input[name=template]", tr)?.setAttribute("aria-label", `Template, row ${n}`);
      $("input[name=column]", tr)?.setAttribute("aria-label", `Column internal name, row ${n}`);
    });
    $("[data-sp-map-add]")?.addEventListener("click", () => {
      const row = tpl.content.firstElementChild.cloneNode(true);
      body.appendChild(row);
      renumber();
      $("select", row)?.focus();
    });
    body.addEventListener("click", (e) => {
      const rm = e.target.closest("[data-sp-map-remove]");
      if (!rm) return;
      const rows = $$("tr[data-sp-map-row]", body);
      const tr = rm.closest("tr");
      if (rows.length === 1) {
        $$("input,select", tr).forEach((el) => { el.value = ""; });
        $("select", tr)?.focus();
      } else {
        const next = tr.nextElementSibling || tr.previousElementSibling;
        tr.remove();
        $("select", next)?.focus();
      }
      renumber();
    });
    // Choosing "Template…" moves focus to the template field.
    body.addEventListener("change", (e) => {
      if (e.target.matches("select[name=source]") && e.target.value === "__template")
        e.target.closest("tr")?.querySelector("input[name=template]")?.focus();
    });
  }
})();
