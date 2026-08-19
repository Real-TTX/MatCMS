// The page switcher in the live editor's toolbar.
//
// Look and markup are the FORM field "richselect" (.mat-rs*, mat-richselect.css): two-line entries
// with a title, the address, the language and the state, an inline dropdown on the desktop and a
// full-screen sheet on a phone. What it adds over that field is a search box and full keyboard
// operation — 36 pages in four languages are not a list anybody scrolls through.
//
// It deliberately does NOT reuse mat-richselect.js: that script writes the picked value into a
// hidden input, while every entry here is a LINK that leaves the page. Leaving is the whole trap
// this file exists for — the block editor and the page settings keep their edits in the browser
// until they are submitted, so a switch would silently drop them. It asks first, and the question
// names what would be lost.
//
// mat-richselect.js IS loaded on this page (the block editor's multi-selects need it) and its
// document handlers close anything with a .mat-rs-menu. That is why the handlers below sit in the
// CAPTURE phase: they have to run before that script hides the menu, or Escape would close the
// menu and leave the focus on a hidden element instead of on the trigger.
(function () {
    "use strict";

    var root = document.querySelector("[data-page-switch]");
    if (!root) return;

    var btn = root.querySelector("[data-ps-btn]");
    var menu = root.querySelector("[data-ps-menu]");
    var search = root.querySelector("[data-ps-search]");
    var list = root.querySelector(".mat-rs-scroll");
    var empty = root.querySelector("[data-ps-empty]");
    var closeBtn = root.querySelector("[data-ps-close]");
    if (!btn || !menu || !search || !list) return;

    var opts = [].slice.call(root.querySelectorAll("[data-ps-opt]"));
    // Two nesting levels that follow the search: the section ("this page" / "all pages") and, inside
    // it, one block per logical page holding its language versions.
    var groups = [].slice.call(root.querySelectorAll("[data-ps-group], [data-ps-section]"));
    var active = -1;   // index into the currently VISIBLE options

    // Same breakpoint as mat-richselect.css, which owns the full-screen presentation.
    function isMobile() { return window.matchMedia("(max-width: 600px)").matches; }
    function visible() { return opts.filter(function (o) { return !o.hidden; }); }
    function isOpen() { return !menu.hidden; }

    // ---------- unsaved-change guard ----------------------------------------------------------
    // Two independent pots of unsaved work in this editor, both browser-side until submitted:
    // the open block's fields (admin-blocks.js) and the page settings form.
    var metaForm = document.getElementById("page-meta-form");
    function snapshot(form) {
        if (!form) return "";
        try { return new URLSearchParams(new FormData(form)).toString(); } catch (e) { return ""; }
    }
    var metaPristine = snapshot(metaForm);

    function lostWork() {
        var parts = [];
        if (typeof window.matBlockDirty === "function" && window.matBlockDirty())
            parts.push(root.getAttribute("data-confirm-block") || "");
        if (metaForm && snapshot(metaForm) !== metaPristine)
            parts.push(root.getAttribute("data-confirm-settings") || "");
        return parts.filter(function (p) { return !!p; });
    }

    // Returns false when the operator cancels — the switch is then simply not carried out and the
    // page stays where it is, menu and all. With nothing unsaved there is no question.
    function mayLeave(title) {
        var parts = lostWork();
        if (!parts.length) return true;
        var tpl = root.getAttribute("data-confirm") || "{0}\n\n{1}";
        return window.confirm(tpl.replace("{0}", "• " + parts.join("\n• ")).replace("{1}", title || ""));
    }

    // ---------- open / close -------------------------------------------------------------------
    function setActive(i) {
        var vis = visible();
        opts.forEach(function (o) { o.classList.remove("is-active"); });
        if (i < 0 || i >= vis.length) { active = -1; search.removeAttribute("aria-activedescendant"); return; }
        active = i;
        var el = vis[i];
        el.classList.add("is-active");
        search.setAttribute("aria-activedescendant", el.id || "");
        // Keep the cursor in view without scrolling the editor behind the menu.
        if (el.scrollIntoView) el.scrollIntoView({ block: "nearest" });
    }

    function open(on) {
        menu.hidden = !on;
        btn.setAttribute("aria-expanded", on ? "true" : "false");
        root.classList.toggle("is-open", on);
        // Phone: the same full-screen presentation the richselect field uses, because a floating
        // menu under a trigger in the middle of a toolbar is exactly what a 390px screen with an
        // open on-screen keyboard cuts in half.
        root.classList.toggle("mat-rs-modal", on && isMobile());
        if (on) {
            search.value = "";
            filter();
            // Start on the page you are on, so ↓ steps to its neighbour instead of to the top.
            var cur = visible().indexOf(root.querySelector('[data-ps-opt][aria-selected="true"]'));
            setActive(cur >= 0 ? cur : (visible().length ? 0 : -1));
            search.focus();
        } else {
            setActive(-1);
        }
    }

    // Escape closes WITHOUT switching and hands the focus back to the trigger — otherwise the focus
    // is left on a hidden element and the next Tab starts over at the top of the document.
    function close(refocus) {
        var wasOpen = isOpen() || root.classList.contains("is-open");
        open(false);
        if (wasOpen && refocus) btn.focus();
    }

    // ---------- search -------------------------------------------------------------------------
    function filter() {
        var q = (search.value || "").trim().toLowerCase();
        var any = false;
        opts.forEach(function (o) {
            // Title AND address (which carries the slug) AND the language, so "apartman",
            // "apartment-1" and "hr" all find the Croatian version of the same page.
            var hit = !q || (o.getAttribute("data-search") || "").indexOf(q) >= 0;
            o.hidden = !hit;
            if (hit) any = true;
        });
        groups.forEach(function (g) {
            var shown = [].slice.call(g.querySelectorAll("[data-ps-opt]")).some(function (o) { return !o.hidden; });
            g.hidden = !shown;
        });
        if (empty) empty.hidden = any;
        setActive(any ? 0 : -1);
    }

    search.addEventListener("input", filter);

    // ---------- events -------------------------------------------------------------------------
    btn.addEventListener("click", function (e) {
        e.stopPropagation();
        open(!isOpen());
    });
    btn.addEventListener("keydown", function (e) {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); open(true); }
    });
    if (closeBtn) closeBtn.addEventListener("click", function (e) { e.preventDefault(); close(true); });

    function go(el) {
        if (!el) return;
        if (!mayLeave(el.getAttribute("data-title") || "")) return;   // cancel → stay put
        window.location.href = el.getAttribute("href");
    }

    // Delegated, so it catches the mouse and a finger anywhere on the whole two-line row.
    list.addEventListener("click", function (e) {
        var opt = e.target.closest ? e.target.closest("[data-ps-opt]") : null;
        if (!opt) return;
        e.preventDefault();
        go(opt);
    });

    menu.addEventListener("keydown", function (e) {
        var vis = visible();
        if (e.key === "ArrowDown") { e.preventDefault(); setActive(vis.length ? (active + 1) % vis.length : -1); }
        else if (e.key === "ArrowUp") { e.preventDefault(); setActive(vis.length ? (active <= 0 ? vis.length - 1 : active - 1) : -1); }
        else if (e.key === "Home") { e.preventDefault(); setActive(vis.length ? 0 : -1); }
        else if (e.key === "End") { e.preventDefault(); setActive(vis.length - 1); }
        // Only from the search box. The entries carry tabindex="-1" (combobox pattern: the focus
        // stays in the input, aria-activedescendant marks the cursor), so an Enter anywhere else
        // would be the browser following a link that is already handled by the click above.
        else if (e.key === "Enter" && e.target === search) { e.preventDefault(); go(vis[active]); }
    });

    // Capture phase — see the note at the top of the file.
    document.addEventListener("click", function (e) {
        if (isOpen() && !root.contains(e.target)) close(false);
    }, true);
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape" || !isOpen()) return;
        e.stopPropagation();   // the add-block modal listens for Escape too
        close(true);
    }, true);

    // A rotation can turn the desktop dropdown into the phone sheet mid-flight, so the presentation
    // is re-decided rather than frozen at opening time.
    window.addEventListener("resize", function () {
        if (isOpen()) root.classList.toggle("mat-rs-modal", isMobile());
    });
})();
