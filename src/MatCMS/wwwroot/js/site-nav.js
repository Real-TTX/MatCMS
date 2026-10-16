// The site's main menu on a phone and on a touch screen.
//  - On a phone (the burger layout) every entry that has a submenu gets a small arrow button next to it: the arrow opens and closes the submenu, the entry itself still leads to its page.
//  - On a touch screen that is wider than a phone (a tablet) there is no hover: the first tap on such an entry opens its dropdown, the second tap follows the link.
// The markup is the one LayoutRenderer writes (.mat-hassub > a + .mat-sub), so it works for the built-in header and for a template's own menu loop alike.
(function () {
    "use strict";
    var PHONE = "(max-width: 760px)";
    var en = (document.documentElement.lang || "de").slice(0, 2) === "en";
    var label = en ? "Open submenu" : "Untermenü öffnen";

    function sub(h) { for (var i = 0; i < h.children.length; i++) if (h.children[i].classList.contains("mat-sub")) return h.children[i]; return null; }
    function link(h) { for (var i = 0; i < h.children.length; i++) if (h.children[i].tagName === "A") return h.children[i]; return null; }

    function setOpen(h, on) {
        h.classList.toggle("open", on);
        var b = h.querySelector(":scope > .mat-sub-toggle");
        if (b) b.setAttribute("aria-expanded", on ? "true" : "false");
        if (!on) h.querySelectorAll(".mat-hassub.open").forEach(function (c) { setOpen(c, false); });
    }

    function init() {
        document.querySelectorAll(".main-nav .mat-hassub").forEach(function (h) {
            var a = link(h);
            if (!a || !sub(h) || h.querySelector(":scope > .mat-sub-toggle")) return;
            var b = document.createElement("button");
            b.type = "button";
            b.className = "mat-sub-toggle";
            b.setAttribute("aria-label", label);
            b.setAttribute("aria-expanded", "false");
            b.innerHTML = '<span aria-hidden="true"></span>';
            a.insertAdjacentElement("afterend", b);
            b.addEventListener("click", function (e) { e.preventDefault(); setOpen(h, !h.classList.contains("open")); });
        });
    }

    // A touch screen without a phone layout: tap once to open the dropdown, again to go.
    document.addEventListener("click", function (e) {
        var a = e.target.closest && e.target.closest(".main-nav .mat-hassub > a");
        var touch = window.matchMedia("(hover: none)").matches && !window.matchMedia(PHONE).matches;
        if (a && touch) {
            var h = a.parentElement;
            if (!h.classList.contains("open")) {
                e.preventDefault();
                // Close what is open beside it (but not what it sits in).
                document.querySelectorAll(".main-nav .mat-hassub.open").forEach(function (o) { if (!o.contains(h)) setOpen(o, false); });
                setOpen(h, true);
            }
            return;
        }
        // A tap outside closes the dropdowns of a touch screen.
        if (touch && !(e.target.closest && e.target.closest(".main-nav .mat-hassub")))
            document.querySelectorAll(".main-nav .mat-hassub.open").forEach(function (o) { setOpen(o, false); });
    });

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})();
