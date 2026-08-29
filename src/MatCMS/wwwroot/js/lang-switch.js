// Fallback language switcher (.mat-lang): closed by default, opens on click of the current
// language, closes on outside click / Escape / re-click. Delegated so it also works for the
// injected overlay on custom layouts.
(function () {
    "use strict";

    function close(w) {
        w.classList.remove("open");
        var m = w.querySelector(".mat-lang-menu");
        var b = w.querySelector(".mat-lang-btn");
        if (m) m.hidden = true;
        if (b) b.setAttribute("aria-expanded", "false");
    }
    function closeAll() {
        document.querySelectorAll(".mat-lang.open").forEach(close);
    }

    document.addEventListener("click", function (e) {
        var btn = e.target.closest(".mat-lang-btn");
        if (btn) {
            var w = btn.closest(".mat-lang");
            var wasOpen = w.classList.contains("open");
            closeAll();
            if (!wasOpen) {
                w.classList.add("open");
                var m = w.querySelector(".mat-lang-menu");
                if (m) m.hidden = false;
                btn.setAttribute("aria-expanded", "true");
            }
            return;
        }
        // Any click outside closes an open menu (clicks on menu links navigate anyway).
        if (!e.target.closest(".mat-lang")) closeAll();
    });

    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") closeAll();
    });

    // ---- Modal variant ({{languages:modal}}): flag button opens a dialog, full-screen on mobile. ----
    // The overlay is MOVED to <body> on open: it is rendered inside the header (wherever the token
    // sits), and a header ancestor with transform/backdrop-filter makes a fixed element contained by
    // the HEADER instead of the viewport — the dialog ended up header-height tall and off-centre.
    // Re-parenting to <body> escapes that containing block. One switcher per page is assumed.
    function markBtn(on) {
        var btn = document.querySelector("[data-mat-langm-open]");
        if (btn) btn.setAttribute("aria-expanded", on ? "true" : "false");
    }
    function openModal(ov) {
        if (ov.parentNode !== document.body) document.body.appendChild(ov);
        ov.hidden = false;
        document.documentElement.classList.add("mat-langm-lock");
        markBtn(true);
    }
    function closeModal(ov) {
        ov.hidden = true;
        document.documentElement.classList.remove("mat-langm-lock");
        markBtn(false);
    }
    function closeAllModals() {
        document.querySelectorAll("[data-mat-langm-overlay]:not([hidden])").forEach(closeModal);
    }
    document.addEventListener("click", function (e) {
        if (e.target.closest("[data-mat-langm-open]")) {
            var ov = document.querySelector("[data-mat-langm-overlay]");
            if (ov) openModal(ov);
            return;
        }
        // Close on the ✕, or on a click on the backdrop itself (not the dialog card).
        if (e.target.closest("[data-mat-langm-close]")) {
            var o1 = e.target.closest("[data-mat-langm-overlay]");
            if (o1) closeModal(o1);
            return;
        }
        if (e.target.matches("[data-mat-langm-overlay]")) closeModal(e.target);
    });
    document.addEventListener("keydown", function (e) {
        if (e.key === "Escape") closeAllModals();
    });
})();
