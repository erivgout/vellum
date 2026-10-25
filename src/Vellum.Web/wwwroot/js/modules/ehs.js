// EHS module behaviour: live OSHA reporting-clock countdown (EHS-114). No inline script (strict CSP).
(() => {
    "use strict";

    const pad = n => String(n).padStart(2, "0");

    function format(ms) {
        const total = Math.floor(Math.abs(ms) / 1000);
        const d = Math.floor(total / 86400);
        const h = Math.floor((total % 86400) / 3600);
        const m = Math.floor((total % 3600) / 60);
        const s = total % 60;
        if (d >= 2) return `${d}d ${h}h ${pad(m)}m`;
        const hours = d * 24 + h;
        return hours > 0 ? `${hours}h ${pad(m)}m ${pad(s)}s` : `${m}m ${pad(s)}s`;
    }

    function tick(el) {
        const deadline = Date.parse(el.dataset.deadline || "");
        if (Number.isNaN(deadline)) return;
        const left = deadline - Date.now();
        el.textContent = format(left);
        const panel = el.closest(".vl-osha-clock");
        const label = panel && panel.querySelector("[data-osha-count-label]");
        if (label) label.textContent = left > 0 ? "Time left" : "Overdue by";
        if (panel) panel.classList.toggle("is-missed", left <= 0);
    }

    function start() {
        const clocks = Array.from(document.querySelectorAll("[data-osha-countdown]"));
        if (clocks.length === 0) return;
        const run = () => clocks.forEach(tick);
        run();
        window.setInterval(run, 1000);
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
    else start();
})();
