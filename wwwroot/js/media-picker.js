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
            '<button type="button" class="modal-close" aria-label="Schließen">✕</button>' +
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
                    } else alert((r.j && r.j.error) || "Upload fehlgeschlagen.");
                })
                .catch(function () { alert("Upload fehlgeschlagen."); })
                .then(function () { uploadBtn.disabled = false; uploadBtn.textContent = lbl; fileInp.value = ""; });
        });

        fetch("/admin/api/media").then(function (r) { return r.json(); }).then(function (list) {
            if (!list || !list.length) { grid.innerHTML = '<p class="muted">Noch keine Medien vorhanden.</p>'; return; }
            list.forEach(function (m) { grid.appendChild(makeTile(m)); });
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
