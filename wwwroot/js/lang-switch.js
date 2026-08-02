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
})();
