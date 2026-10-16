// Shared media-library picker dialog (upload happens on the field; this picks an existing file).
// Exposed globally so every image field (block editor, settings logo/favicon, …) can reuse it.
(function () {
    "use strict";
    if (window.openMediaPicker) return;

    // openMediaPicker(onPick)                      → single pick; onPick(url) then closes.
    // openMediaPicker(onPick, { multiple: true })  → multi pick; onPick([url, …]) in click order.
    window.openMediaPicker = function (onPick, opts) {
        var multiple = !!(opts && opts.multiple);
        var overlay = document.createElement("div");
        overlay.className = "modal-overlay open";
        overlay.innerHTML = '<div class="modal" role="dialog" aria-modal="true">' +
            '<div class="modal-head"><h2>' + (multiple ? 'Medien wählen' : 'Medium wählen') + '</h2>' +
            '<div class="mp-head-actions">' +
            '<button type="button" class="btn btn-sm" data-mp-upload>Hochladen</button>' +
            '<button type="button" class="modal-close" aria-label="Schließen"><i class="ti ti-x" aria-hidden="true"></i></button>' +
            '</div></div>' +
            '<div class="modal-body"><div class="media-picker-grid"></div></div>' +
            (multiple ? '<div class="modal-foot media-picker-foot"><span class="mp-count muted">0 gewählt</span><button type="button" class="btn btn-sm" data-mp-apply disabled>Übernehmen</button></div>' : '') +
            '</div>';
        document.body.appendChild(overlay);
        var grid = overlay.querySelector(".media-picker-grid");
        var fileInp = document.createElement("input");
        fileInp.type = "file"; fileInp.accept = "image/*"; fileInp.style.display = "none";
        overlay.appendChild(fileInp);
        function close() { overlay.remove(); }
        overlay.addEventListener("click", function (e) { if (e.target === overlay) close(); });
        overlay.querySelector(".modal-close").addEventListener("click", close);
        document.addEventListener("keydown", function esc(e) {
            if (e.key === "Escape") { close(); document.removeEventListener("keydown", esc); }
        });

        var selected = [];   // ordered urls (multi mode)
        var applyBtn = overlay.querySelector("[data-mp-apply]");
        var countEl = overlay.querySelector(".mp-count");
        function refresh() {
            if (countEl) countEl.textContent = selected.length + " gewählt";
            if (applyBtn) applyBtn.disabled = selected.length === 0;
        }
        if (applyBtn) applyBtn.addEventListener("click", function () { if (selected.length) { onPick(selected.slice()); close(); } });

        function makeTile(m) {
            var b = document.createElement("button");
            b.type = "button"; b.className = "media-pick"; b.title = m.name || m.url;
            b.dataset.tags = m.tags || "";
            var img = document.createElement("img"); img.src = m.url; img.alt = m.name || "";
            b.appendChild(img);
            b.addEventListener("click", function () {
                if (!multiple) { onPick(m.url); close(); return; }
                var i = selected.indexOf(m.url);
                if (i >= 0) { selected.splice(i, 1); b.classList.remove("selected"); }
                else { selected.push(m.url); b.classList.add("selected"); }
                refresh();
            });
            return b;
        }

        // Upload straight from the dialog: the new file appears in the grid and is picked/selected.
        var uploadBtn = overlay.querySelector("[data-mp-upload]");
        uploadBtn.addEventListener("click", function () { fileInp.click(); });
        fileInp.addEventListener("change", function () {
            if (!fileInp.files || !fileInp.files[0]) return;
            uploadBtn.disabled = true; var lbl = uploadBtn.textContent; uploadBtn.textContent = "Lädt…";
            var fd = new FormData(); fd.append("file", fileInp.files[0]);
            fetch("/admin/api/upload", { method: "POST", body: fd })
                .then(function (r) { return r.json().then(function (j) { return { ok: r.ok, j: j }; }); })
                .then(function (r) {
                    if (r.ok && r.j.url) {
                        if (!multiple) { onPick(r.j.url); close(); return; }
                        var tile = makeTile({ url: r.j.url, name: "" });
                        var empty = grid.querySelector("p.muted"); if (empty) grid.innerHTML = "";
                        grid.insertBefore(tile, grid.firstChild);
                        selected.push(r.j.url); tile.classList.add("selected"); refresh();
                    } else MatDialog.alert((r.j && r.j.error) || "Upload fehlgeschlagen.");
                })
                .catch(function () { MatDialog.alert("Upload fehlgeschlagen."); })
                .then(function () { uploadBtn.disabled = false; uploadBtn.textContent = lbl; fileInp.value = ""; });
        });

        // A tag-filter bar above the grid: "Alle" + one chip per distinct tag (case preserved,
        // deduped case-insensitively). Clicking a chip shows only tiles carrying that tag.
        function buildFilter(list) {
            var tags = [], seen = {};
            list.forEach(function (m) {
                (m.tags || "").split(",").forEach(function (t) {
                    t = t.trim(); if (!t) return;
                    var k = t.toLowerCase(); if (!seen[k]) { seen[k] = 1; tags.push(t); }
                });
            });
            if (tags.length < 2) return; // nothing useful to filter by
            tags.sort(function (a, b) { return a.toLowerCase().localeCompare(b.toLowerCase()); });
            var bar = document.createElement("div");
            bar.className = "tag-filter media-picker-filter";
            function chip(label, tag, active) {
                var c = document.createElement("button");
                c.type = "button"; c.className = "tag-chip" + (active ? " active" : "");
                c.textContent = label; c.dataset.tag = tag; return c;
            }
            bar.appendChild(chip("Alle", "", true));
            tags.forEach(function (t) { bar.appendChild(chip(t, t.toLowerCase(), false)); });
            bar.addEventListener("click", function (e) {
                var b = e.target.closest(".tag-chip"); if (!b) return;
                var tag = b.dataset.tag;
                bar.querySelectorAll(".tag-chip").forEach(function (x) { x.classList.remove("active"); });
                b.classList.add("active");
                Array.prototype.slice.call(grid.querySelectorAll(".media-pick")).forEach(function (tile) {
                    var tl = (tile.dataset.tags || "").toLowerCase().split(",").map(function (s) { return s.trim(); });
                    tile.style.display = (!tag || tl.indexOf(tag) !== -1) ? "" : "none";
                });
            });
            grid.parentNode.insertBefore(bar, grid);
        }

        fetch("/admin/api/media").then(function (r) { return r.json(); }).then(function (list) {
            if (!list || !list.length) { grid.innerHTML = '<p class="muted">Noch keine Medien vorhanden.</p>'; return; }
            buildFilter(list);
            list.forEach(function (m) { grid.appendChild(makeTile(m)); });
        }).catch(function () { grid.innerHTML = '<p class="muted">Konnte Mediathek nicht laden.</p>'; });
    };

    // Link picker: a drop-down under the address field — a search box on top, the places to link to below it, grouped by where they come from (pages, blog posts, shop categories,
    // products …), one compact line each (title, address). opts.anchor is the element it opens under (the link field); opts.elements also offers the plugin elements a menu item can stand for
    // (the cart, the search box). Arrow keys move, Enter takes, Escape closes. onPick(url, title).
    window.openLinkPicker = function (onPick, opts) {
        opts = opts || {};
        var open = document.querySelector(".lp-pop");
        if (open && open._close) open._close();
        // The picker's own words: the admin's language is on <html lang>.
        var en = (document.documentElement.lang || "de").slice(0, 2) === "en";
        var L = en
            ? { search: "Search pages, posts, categories, products …", searchLabel: "Search", none: "Nothing found.", empty: "No targets available.", failed: "Could not load the targets." }
            : { search: "Seiten, Beiträge, Kategorien, Produkte durchsuchen …", searchLabel: "Suchen", none: "Nichts gefunden.", empty: "Keine Ziele vorhanden.", failed: "Konnte die Ziele nicht laden." };
        var anchor = opts.anchor || null;
        var pop = document.createElement("div");
        pop.className = "lp-pop";
        pop.setAttribute("role", "dialog");
        pop.innerHTML = '<div class="lp-pop-head"><i class="ti ti-search" aria-hidden="true"></i><input type="search" class="lp-pop-search" autocomplete="off"></div><div class="lp-pop-list" role="listbox"></div>';
        var search = pop.querySelector(".lp-pop-search");
        var list = pop.querySelector(".lp-pop-list");
        search.placeholder = L.search;
        search.setAttribute("aria-label", L.searchLabel);
        document.body.appendChild(pop);

        function place() {
            var vw = document.documentElement.clientWidth, vh = window.innerHeight;
            if (!anchor) { pop.style.cssText = "position:fixed;top:12vh;left:50%;width:min(560px,calc(100vw - 24px));transform:translateX(-50%)"; return; }
            var r = anchor.getBoundingClientRect();
            var width = Math.min(Math.max(r.width, 440), vw - 16);
            var left = Math.min(Math.max(r.left, 8), vw - width - 8);
            var below = vh - r.bottom - 12, above = r.top - 12;
            var up = below < 240 && above > below;
            var room = Math.max(160, Math.min(380, up ? above : below));
            pop.style.width = width + "px";
            pop.style.left = (left + window.scrollX) + "px";
            pop.style.maxHeight = room + "px";
            if (up) { pop.style.top = ""; pop.style.bottom = (document.documentElement.clientHeight - r.top - window.scrollY + 4) + "px"; }
            else { pop.style.bottom = ""; pop.style.top = (r.bottom + window.scrollY + 4) + "px"; }
        }
        place();
        window.addEventListener("resize", place);

        var items = [], active = -1;
        function close(refocus) {
            window.removeEventListener("resize", place);
            document.removeEventListener("mousedown", outside, true);
            document.removeEventListener("keydown", keys, true);
            pop.remove();
            if (refocus && anchor) { var f = anchor.querySelector("input"); if (f) f.focus(); }
        }
        pop._close = close;
        function outside(e) { if (!pop.contains(e.target) && !(anchor && anchor.contains(e.target))) close(false); }
        function visible() { return items.filter(function (i) { return !i.el.hidden; }); }
        function setActive(i) {
            var v = visible();
            if (!v.length) { active = -1; return; }
            active = Math.max(0, Math.min(v.length - 1, i));
            items.forEach(function (x) { x.el.classList.remove("on"); });
            v[active].el.classList.add("on");
            v[active].el.scrollIntoView({ block: "nearest" });
        }
        function keys(e) {
            if (e.key === "Escape") { e.preventDefault(); close(true); return; }
            if (!pop.contains(document.activeElement)) return;
            if (e.key === "ArrowDown") { e.preventDefault(); setActive(active + 1); }
            else if (e.key === "ArrowUp") { e.preventDefault(); setActive(active - 1); }
            else if (e.key === "Enter") { e.preventDefault(); var v = visible(); if (v[active]) v[active].el.click(); }
        }
        document.addEventListener("mousedown", outside, true);
        document.addEventListener("keydown", keys, true);

        function filter() {
            var q = search.value.trim().toLowerCase(), any = false;
            items.forEach(function (i) { i.el.hidden = !!q && i.find.indexOf(q) === -1; });
            list.querySelectorAll(".lp-pop-group").forEach(function (g) {
                var shown = false, n = g.nextElementSibling;
                while (n && n.classList.contains("lp-pop-item")) { if (!n.hidden) shown = true; n = n.nextElementSibling; }
                g.hidden = !shown; if (shown) any = true;
            });
            var empty = list.querySelector(".lp-pop-empty"); if (empty) empty.hidden = any;
            setActive(0);
        }
        search.addEventListener("input", filter);
        search.focus();

        fetch("/admin/api/link-targets" + (opts.elements ? "?elements=true" : "")).then(function (r) { return r.json(); }).then(function (data) {
            if (!data || !data.length) { list.innerHTML = '<div class="lp-pop-empty"></div>'; list.firstChild.textContent = L.empty; return; }
            data.forEach(function (g) {
                var h = document.createElement("div"); h.className = "lp-pop-group"; h.textContent = g.group; list.appendChild(h);
                g.items.forEach(function (p) {
                    var b = document.createElement("button");
                    b.type = "button"; b.className = "lp-pop-item"; b.setAttribute("role", "option");
                    b.innerHTML = '<span class="t"></span><span class="u mono"></span>';
                    b.querySelector(".t").textContent = p.title || p.url;
                    b.querySelector(".u").textContent = p.url;
                    b.title = p.url;
                    b.addEventListener("mouseenter", function () { var v = visible(); setActive(v.findIndex(function (x) { return x.el === b; })); });
                    b.addEventListener("click", function () { var t = p.title || ""; close(false); onPick(p.url, t); });
                    list.appendChild(b);
                    items.push({ el: b, find: ((p.title || "") + " " + p.url + " " + g.group).toLowerCase() });
                });
            });
            var none = document.createElement("div"); none.className = "lp-pop-empty"; none.hidden = true; none.textContent = L.none; list.appendChild(none);
            setActive(0);
            place();
        }).catch(function () { list.innerHTML = '<div class="lp-pop-empty"></div>'; list.firstChild.textContent = L.failed; });
    };

    // Reusable pattern: any [data-link-btn] opens the link picker and fills a target input.
    // Optional data-target (CSS selector for the URL input) and data-label (selector for a label
    // input that is auto-filled with the page title when still empty).
    function wireLinkButtons() {
        document.querySelectorAll("[data-link-btn]").forEach(function (btn) {
            if (btn._linkWired) return;
            btn._linkWired = true;
            btn.addEventListener("click", function () {
                var input = btn.dataset.target ? document.querySelector(btn.dataset.target) : null;
                var label = btn.dataset.label ? document.querySelector(btn.dataset.label) : null;
                window.openLinkPicker(function (url, title) {
                    if (input) { input.value = url; input.dispatchEvent(new Event("input", { bubbles: true })); }
                    if (label && !label.value && title) label.value = title;
                }, { elements: btn.hasAttribute("data-elements"), anchor: btn.closest(".link-field") || btn });
            });
        });
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", wireLinkButtons);
    else wireLinkButtons();
})();
