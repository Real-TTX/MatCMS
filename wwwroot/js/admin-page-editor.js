// Live editor: add-block modal + live-preview linking (sidebar <-> iframe).
// Drag & drop reordering is handled by admin-sortable.js.
(function () {
    "use strict";

    // ---------- Add-block modal ----------
    var modal = document.getElementById("add-block-modal");
    var openBtn = document.getElementById("add-block-btn");
    var closeBtn = document.getElementById("add-block-close");
    // Open the block picker; `position` = insert index (null = append to the end).
    function openPicker(position) {
        if (!modal) return;
        modal.querySelectorAll(".add-position").forEach(function (i) { i.value = position == null ? "" : String(position); });
        modal.classList.add("open");
    }
    if (modal) {
        if (openBtn) openBtn.addEventListener("click", function () { openPicker(null); });
        if (closeBtn) closeBtn.addEventListener("click", function () { modal.classList.remove("open"); });
        modal.addEventListener("click", function (e) { if (e.target === modal) modal.classList.remove("open"); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") modal.classList.remove("open"); });
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
