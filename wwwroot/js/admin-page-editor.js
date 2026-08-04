// Live editor: add-block modal + live-preview linking (sidebar <-> iframe).
// Drag & drop reordering is handled by admin-sortable.js.
(function () {
    "use strict";

    // ---------- Add-block modal (categorised picker + search) ----------
    var modal = document.getElementById("add-block-modal");
    var openBtn = document.getElementById("add-block-btn");
    var closeBtn = document.getElementById("add-block-close");
    var search = modal ? modal.querySelector("#bpick-search") : null;
    var catBtns = modal ? modal.querySelectorAll(".bpick-cat") : [];
    var groups = modal ? modal.querySelectorAll(".bpick-group") : [];
    var emptyMsg = modal ? modal.querySelector(".bpick-empty") : null;
    var activeCat = "all";

    // Filter tiles by the active category + the search text; hide empty groups; show "nothing found".
    function applyFilter() {
        var q = ((search && search.value) || "").trim().toLowerCase();
        var anyVisible = false;
        groups.forEach(function (g) {
            var groupVisible = false;
            g.querySelectorAll(".tile").forEach(function (t) {
                var okCat = activeCat === "all" || t.getAttribute("data-cat") === activeCat;
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
        catBtns.forEach(function (x, i) { x.classList.toggle("is-active", i === 0); });
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
        if (openBtn) openBtn.addEventListener("click", function () { openPicker(null); });
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
            window.location.search = "?block=" + encodeURIComponent(d.id);
        }
        if (d.type === "mat-insert-at") openPicker(d.index);
    });
})();
