// Document Control: client-side search and pagination for read-only tables such as the revision history (DOC-016).
// Progressive enhancement only: without JavaScript every row is shown.
(function () {
  "use strict";

  document.querySelectorAll("[data-paginate]").forEach((root) => {
    const size = parseInt(root.dataset.paginate, 10) || 10;
    const rows = Array.from(root.querySelectorAll("[data-paginate-row]"));
    const search = root.querySelector("[data-paginate-search]");
    const footer = root.querySelector("[data-paginate-footer]");
    const status = root.querySelector("[data-paginate-status]");
    const prev = root.querySelector("[data-paginate-prev]");
    const next = root.querySelector("[data-paginate-next]");
    const empty = root.querySelector("[data-paginate-empty]");
    let page = 0;

    function render() {
      const term = (search?.value || "").trim().toLowerCase();
      const matches = rows.filter((r) => !term || (r.dataset.text || r.textContent.toLowerCase()).includes(term));
      const pages = Math.max(1, Math.ceil(matches.length / size));
      page = Math.min(page, pages - 1);
      rows.forEach((r) => { r.hidden = true; });
      matches.slice(page * size, page * size + size).forEach((r) => { r.hidden = false; });
      if (empty) empty.classList.toggle("d-none", matches.length > 0);
      if (footer) footer.classList.toggle("d-none", matches.length <= size);
      if (status) status.textContent = matches.length === 0 ? "" : `${page * size + 1}–${Math.min(matches.length, page * size + size)} of ${matches.length}`;
      if (prev) prev.disabled = page === 0;
      if (next) next.disabled = page >= pages - 1;
    }

    if (search) {
      // The table may sit inside the record form: keep the search box from marking the form dirty or submitting it.
      ["input", "change"].forEach((type) => search.addEventListener(type, (e) => { e.stopPropagation(); page = 0; render(); }));
      search.addEventListener("keydown", (e) => { if (e.key === "Enter") e.preventDefault(); });
    }
    prev?.addEventListener("click", () => { page = Math.max(0, page - 1); render(); });
    next?.addEventListener("click", () => { page += 1; render(); });
    render();
  });
})();
