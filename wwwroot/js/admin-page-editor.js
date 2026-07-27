// Live editor: add-block modal + live-preview linking (sidebar <-> iframe).
// Drag & drop reordering is handled by admin-sortable.js.
(function () {
    "use strict";

    // ---------- Add-block modal ----------
    var modal = document.getElementById("add-block-modal");
    var openBtn = document.getElementById("add-block-btn");
    var closeBtn = document.getElementById("add-block-close");
    if (modal && openBtn) {
        var positionInputs = modal.querySelectorAll(".add-position");
        function setInsertPosition(val) { positionInputs.forEach(function (i) { i.value = val; }); }

        // Bottom "add block" button appends (no position).
        openBtn.addEventListener("click", function () { setInsertPosition(""); modal.classList.add("open"); });
        if (closeBtn) closeBtn.addEventListener("click", function () { modal.classList.remove("open"); });
        modal.addEventListener("click", function (e) { if (e.target === modal) modal.classList.remove("open"); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") modal.classList.remove("open"); });

        // "Insert here" zones between blocks open the same picker but target a specific index.
        document.querySelectorAll(".block-insert").forEach(function (zone) {
            zone.addEventListener("click", function () {
                setInsertPosition(zone.getAttribute("data-insert-at") || "");
                modal.classList.add("open");
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

    // Clicking a block inside the preview opens its settings inline.
    window.addEventListener("message", function (e) {
        var d = e.data || {};
        if (d.type === "mat-preview-ready") syncSelection();
        if (d.type === "mat-select-block" && d.id) {
            window.location.search = "?block=" + encodeURIComponent(d.id);
        }
    });
})();
