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

    // Internal-link picker: pick a published page (returns its URL). Used by URL fields.
    window.openLinkPicker = function (onPick) {
        var overlay = document.createElement("div");
        overlay.className = "modal-overlay open";
        overlay.innerHTML = '<div class="modal" role="dialog" aria-modal="true">' +
            '<div class="modal-head"><h2>Seite verlinken</h2><button type="button" class="modal-close" aria-label="Schließen">✕</button></div>' +
            '<div class="modal-body"><div class="link-picker-list"></div></div></div>';
        document.body.appendChild(overlay);
        var list = overlay.querySelector(".link-picker-list");
        function close() { overlay.remove(); }
        overlay.addEventListener("click", function (e) { if (e.target === overlay) close(); });
        overlay.querySelector(".modal-close").addEventListener("click", close);
        document.addEventListener("keydown", function esc(e) {
            if (e.key === "Escape") { close(); document.removeEventListener("keydown", esc); }
        });
        fetch("/admin/api/pages").then(function (r) { return r.json(); }).then(function (pages) {
            if (!pages || !pages.length) { list.innerHTML = '<p class="muted">Keine veröffentlichten Seiten.</p>'; return; }
            pages.forEach(function (p) {
                var b = document.createElement("button");
                b.type = "button"; b.className = "link-pick";
                b.innerHTML = '<span class="lp-title"></span><span class="lp-url mono"></span>';
                b.querySelector(".lp-title").textContent = p.title || p.url;
                b.querySelector(".lp-url").textContent = p.url;
                b.addEventListener("click", function () { onPick(p.url, p.title || ""); close(); });
                list.appendChild(b);
            });
        }).catch(function () { list.innerHTML = '<p class="muted">Konnte Seiten nicht laden.</p>'; });
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
                });
            });
        });
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", wireLinkButtons);
    else wireLinkButtons();
})();
