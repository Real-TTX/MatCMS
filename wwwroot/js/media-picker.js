// Shared media-library picker dialog (upload happens on the field; this picks an existing file).
// Exposed globally so every image field (block editor, settings logo/favicon, …) can reuse it.
(function () {
    "use strict";
    if (window.openMediaPicker) return;

    window.openMediaPicker = function (onPick) {
        var overlay = document.createElement("div");
        overlay.className = "modal-overlay open";
        overlay.innerHTML = '<div class="modal" role="dialog" aria-modal="true">' +
            '<div class="modal-head"><h2>Medium wählen</h2><button type="button" class="modal-close" aria-label="Schließen">✕</button></div>' +
            '<div class="modal-body"><div class="media-picker-grid"></div></div></div>';
        document.body.appendChild(overlay);
        var grid = overlay.querySelector(".media-picker-grid");
        function close() { overlay.remove(); }
        overlay.addEventListener("click", function (e) { if (e.target === overlay) close(); });
        overlay.querySelector(".modal-close").addEventListener("click", close);
        document.addEventListener("keydown", function esc(e) {
            if (e.key === "Escape") { close(); document.removeEventListener("keydown", esc); }
        });
        fetch("/admin/api/media").then(function (r) { return r.json(); }).then(function (list) {
            if (!list || !list.length) { grid.innerHTML = '<p class="muted">Noch keine Medien vorhanden.</p>'; return; }
            list.forEach(function (m) {
                var b = document.createElement("button");
                b.type = "button"; b.className = "media-pick"; b.title = m.name || m.url;
                var img = document.createElement("img"); img.src = m.url; img.alt = m.name || "";
                b.appendChild(img);
                b.addEventListener("click", function () { onPick(m.url); close(); });
                grid.appendChild(b);
            });
        }).catch(function () { grid.innerHTML = '<p class="muted">Konnte Mediathek nicht laden.</p>'; });
    };
})();
