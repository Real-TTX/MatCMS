// Live editor: add-block modal + live-preview linking (sidebar <-> iframe).
// Drag & drop reordering is handled by admin-sortable.js.
(function () {
    "use strict";

    // ---------- Add-block modal ----------
    var modal = document.getElementById("add-block-modal");
    var openBtn = document.getElementById("add-block-btn");
    var closeBtn = document.getElementById("add-block-close");
    if (modal && openBtn) {
        openBtn.addEventListener("click", function () { modal.classList.add("open"); });
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

    // Hovering a block row scrolls the preview to that block.
    Array.prototype.slice.call(document.querySelectorAll(".block-item[data-block-id]")).forEach(function (row) {
        var id = row.getAttribute("data-block-id");
        row.addEventListener("mouseenter", function () { post({ type: "mat-scroll", id: id }); });
    });

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
    });
})();
