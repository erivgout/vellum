// Applies the colour theme before first paint (loaded synchronously in <head>; CSP forbids inline script).
(function () {
  var root = document.documentElement;
  var pref = root.getAttribute("data-theme-pref") || "auto";
  try { var local = localStorage.getItem("vl-theme"); if (local) pref = local; } catch (e) { }
  var dark = pref === "dark" || (pref === "auto" && window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
  root.setAttribute("data-bs-theme", dark ? "dark" : "light");
  root.setAttribute("data-theme-pref", pref);
})();
