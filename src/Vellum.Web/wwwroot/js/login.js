// Sign-in page helpers (no inline script under CSP).
(function () {
  document.querySelectorAll("[data-toggle-password]").forEach((b) => b.addEventListener("click", () => {
    const input = document.querySelector(b.dataset.togglePassword);
    const show = input.type === "password";
    input.type = show ? "text" : "password";
    b.querySelector(".bi").className = "bi " + (show ? "bi-eye-slash" : "bi-eye");
    b.setAttribute("aria-label", show ? "Hide password" : "Show password");
  }));
  document.querySelectorAll("[data-demo-user]").forEach((b) => b.addEventListener("click", () => {
    const params = new URLSearchParams(location.search);
    params.set("u", b.dataset.demoUser);
    location.href = "/login?" + params.toString();
  }));
})();
