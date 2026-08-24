// Live editor: add-block modal + live-preview linking (sidebar <-> iframe).
// Drag & drop reordering is handled by admin-sortable.js.
(function () {
    "use strict";

    // ---------- Add-block modal (categorised picker + search) ----------
    var modal = document.getElementById("add-block-modal");
    var openBtns = document.querySelectorAll(".js-add-block");   // "+ Block" in list mode AND single-block edit
    var closeBtn = document.getElementById("add-block-close");
    var search = modal ? modal.querySelector("#bpick-search") : null;
    var catBtns = modal ? modal.querySelectorAll(".bpick-cat") : [];
    var groups = modal ? modal.querySelectorAll(".bpick-group") : [];
    var emptyMsg = modal ? modal.querySelector(".bpick-empty") : null;
    var activeCat = "all";

    // Favourites + recently-used are per-browser (localStorage), keyed by block type.
    var FAV_KEY = "matBlockFav", REC_KEY = "matBlockRecent";
    function loadArr(k) { try { return JSON.parse(localStorage.getItem(k) || "[]") || []; } catch (e) { return []; } }
    function saveArr(k, a) { try { localStorage.setItem(k, JSON.stringify(a)); } catch (e) { } }
    var favs = loadArr(FAV_KEY), recent = loadArr(REC_KEY);
    function isFav(t) { return favs.indexOf(t) >= 0; }
    function toggleFav(t) { var i = favs.indexOf(t); if (i >= 0) favs.splice(i, 1); else favs.push(t); saveArr(FAV_KEY, favs); }
    function pushRecent(t) { recent = recent.filter(function (x) { return x !== t; }); recent.unshift(t); if (recent.length > 12) recent = recent.slice(0, 12); saveArr(REC_KEY, recent); }

    function updateCounts() {
        if (!modal) return;
        var tiles = modal.querySelectorAll(".bpick-grid .tile");
        var types = [].map.call(tiles, function (t) { return t.getAttribute("data-type"); });
        var fEl = modal.querySelector('.bpick-n[data-count="fav"]');
        var rEl = modal.querySelector('.bpick-n[data-count="recent"]');
        if (fEl) fEl.textContent = favs.filter(function (t) { return types.indexOf(t) >= 0; }).length;
        if (rEl) rEl.textContent = recent.filter(function (t) { return types.indexOf(t) >= 0; }).length;
        modal.querySelectorAll(".tile-fav").forEach(function (b) { b.classList.toggle("on", isFav(b.getAttribute("data-type"))); });
    }

    // Filter tiles by the active category (incl. the "fav"/"recent" pseudo-categories) + search text.
    function applyFilter() {
        var q = ((search && search.value) || "").trim().toLowerCase();
        // Only "Alle" keeps the grouped headings; any specific category (incl. fav/recent) shows a flat list.
        var flat = activeCat !== "all";
        var main = modal.querySelector(".bpick-main");
        if (main) main.classList.toggle("bpick-flat", flat);
        var anyVisible = false;
        groups.forEach(function (g) {
            var groupVisible = false;
            g.querySelectorAll(".tile").forEach(function (t) {
                var type = t.getAttribute("data-type");
                var okCat = activeCat === "all" ? true
                    : activeCat === "fav" ? isFav(type)
                    : activeCat === "recent" ? (recent.indexOf(type) >= 0)
                    : t.getAttribute("data-cat") === activeCat;
                var okText = !q
                    || (t.getAttribute("data-name") || "").indexOf(q) >= 0
                    || (t.getAttribute("data-desc") || "").indexOf(q) >= 0;
                var show = okCat && okText;
                var f = t.closest("form");
                if (f) f.style.display = show ? "" : "none";
                if (show) { groupVisible = true; anyVisible = true; }
            });
            g.style.display = groupVisible ? "" : "none";
        });
        if (emptyMsg) emptyMsg.hidden = anyVisible;
    }
    function resetFilter() {
        if (search) search.value = "";
        activeCat = "all";
        catBtns.forEach(function (x) { x.classList.toggle("is-active", x.getAttribute("data-cat") === "all"); });
        updateCounts();
        applyFilter();
    }

    // Open the block picker; `position` = insert index (null = append to the end).
    function openPicker(position) {
        if (!modal) return;
        modal.querySelectorAll(".add-position").forEach(function (i) { i.value = position == null ? "" : String(position); });
        modal.classList.add("open");
        resetFilter();
        setTimeout(function () { if (search) search.focus(); }, 30);
    }
    if (modal) {
        openBtns.forEach(function (b) { b.addEventListener("click", function () { openPicker(null); }); });
        // ＋ inside an open block saves it first and comes back here — so the picker has to reopen by
        // itself, or the save would land on a closed dialog the operator had already asked for.
        if (modal.getAttribute("data-autoopen") === "1") openPicker(null);
        if (closeBtn) closeBtn.addEventListener("click", function () { modal.classList.remove("open"); });
        modal.addEventListener("click", function (e) { if (e.target === modal) modal.classList.remove("open"); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") modal.classList.remove("open"); });
        if (search) search.addEventListener("input", applyFilter);
        catBtns.forEach(function (c) {
            c.addEventListener("click", function () {
                catBtns.forEach(function (x) { x.classList.remove("is-active"); });
                c.classList.add("is-active");
                activeCat = c.getAttribute("data-cat");
                applyFilter();
            });
        });
        // Star toggles a block as favourite (doesn't submit the add-form).
        modal.querySelectorAll(".tile-fav").forEach(function (b) {
            b.addEventListener("click", function (e) {
                e.preventDefault(); e.stopPropagation();
                var t = b.getAttribute("data-type");
                toggleFav(t);
                b.classList.toggle("on", isFav(t));
                updateCounts();
                if (activeCat === "fav") applyFilter();
            });
        });
        // Remember which block was inserted (for the "Verwendet" list) just before the form navigates.
        modal.querySelectorAll(".bpick-grid form").forEach(function (f) {
            f.addEventListener("submit", function () {
                var tile = f.querySelector(".tile");
                if (tile) pushRecent(tile.getAttribute("data-type"));
            });
        });
    }

    // ---------- Live preview linking ----------
    var root = document.querySelector(".live-editor");
    var frame = document.getElementById("preview-frame");
    if (!root || !frame) return;

    var selectedBlock = root.getAttribute("data-selected-block");

    function post(msg) { if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*"); }

    // Highlight the currently-edited block in the preview.
    function syncSelection() { if (selectedBlock) post({ type: "mat-select", id: selectedBlock }); }
    frame.addEventListener("load", syncSelection);

    // ---------- Live draft preview (nothing persists until "Speichern") ----------
    // The whole page's blocks are seeded as a client draft. Editing the selected block's fields
    // updates the draft and re-renders the preview via the server (no DB write).
    var seedEl = document.getElementById("page-blocks");
    var tokenEl = document.querySelector('.live-editor input[name="__RequestVerificationToken"]');
    var draft = [];
    try { draft = JSON.parse((seedEl && seedEl.textContent) || "[]") || []; } catch (e) { draft = []; }

    var renderT;
    function renderPreview() {
        if (!tokenEl) return;
        clearTimeout(renderT);
        renderT = setTimeout(function () {
            var body = new URLSearchParams();
            body.set("__RequestVerificationToken", tokenEl.value);
            body.set("Draft", JSON.stringify(draft));
            fetch(location.pathname + "?handler=RenderPreview", {
                method: "POST",
                headers: { "Content-Type": "application/x-www-form-urlencoded", "RequestVerificationToken": tokenEl.value },
                body: body.toString(),
                credentials: "same-origin"
            })
                .then(function (r) { return r.ok ? r.text() : null; })
                .then(function (html) {
                    if (html == null) return;
                    post({ type: "mat-render", html: html });
                    if (selectedBlock) post({ type: "mat-select", id: selectedBlock });
                })
                .catch(function () { });
        }, 80);
    }

    // Called by admin-blocks.js on every field change of the selected block.
    window.matOnBlockDataChange = function (dataJson) {
        if (!selectedBlock) return;
        var e = draft.filter(function (b) { return String(b.id) === String(selectedBlock); })[0];
        if (!e) return;
        e.dataJson = dataJson;
        renderPreview();
    };

    // Clicking a block inside the preview opens its settings inline.
    window.addEventListener("message", function (e) {
        var d = e.data || {};
        if (d.type === "mat-preview-ready") syncSelection();
        if (d.type === "mat-select-block" && d.id) {
            // Opening another block reloads the editor (?block=…), which drops any browser-side edits
            // in the open block or the settings forms. Ask first — same guard the page switcher uses.
            if (typeof window.matLostWork === "function") {
                var lost = window.matLostWork();
                if (lost.length) {
                    var tpl = root.getAttribute("data-confirm-blockswitch") || "{0}";
                    if (!window.confirm(tpl.replace("{0}", "• " + lost.join("\n• ")))) return;
                }
            }
            window.location.search = "?block=" + encodeURIComponent(d.id);
        }
        if (d.type === "mat-insert-at") openPicker(d.index);
    });
})();
