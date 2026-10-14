// The blog editor's one piece of behaviour the field library does not have: "Aus Link übernehmen". It reads a post behind a link once
// (the plugin's import-link endpoint) and fills the editor's fields; nothing is saved until the normal "Speichern".
// The fields themselves (rich text, image, gallery, tags) are the library's own (MatCMS.Shared.Web/js/ui-fields.js).
(function () {
    "use strict";
    var box = document.getElementById("pi-dialog");
    var cfg = document.getElementById("pi-config");
    if (!box || !cfg) return;

    var input = document.getElementById("pi-url");
    var go = box.querySelector("[data-pi-go]");
    var status = document.getElementById("pi-status");
    var form = document.getElementById("post-form");
    var token = form && form.querySelector('input[name="__RequestVerificationToken"]');

    function show(text, isError, notes) {
        status.hidden = false;
        status.className = "post-import__status" + (isError ? " is-error" : "");
        status.textContent = text;
        if (notes && notes.length) {
            var ul = document.createElement("ul");
            notes.forEach(function (n) { var li = document.createElement("li"); li.textContent = n; ul.appendChild(li); });
            status.appendChild(ul);
        }
    }
    function el(id) { return document.getElementById(id); }
    function val(id) { var e = el(id); return e ? e.value.trim() : ""; }
    function set(id, v) { var e = el(id); if (e && v != null) e.value = v; }

    function run() {
        var url = input.value.trim();
        if (!url) { input.focus(); return; }
        go.disabled = true;
        show(cfg.dataset.busy, false);
        var fd = new FormData();
        fd.append("url", url);
        if (token) fd.append("__RequestVerificationToken", token.value);
        fetch(cfg.dataset.url, { method: "POST", body: fd, credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(async function (d) {
                if (!d.ok) { show(d.error || cfg.dataset.failed, true, d.notes); return; }
                var editor = el("post-content-editor");
                var filled = val("post-title") || val("post-excerpt") || (editor && editor.textContent.trim());
                var overwrite = !filled || await MatDialog.confirm(cfg.dataset.overwrite);
                if (overwrite) {
                    if (d.title) set("post-title", d.title);
                    if (d.excerpt) set("post-excerpt", d.excerpt);
                    if (d.html && editor) { editor.innerHTML = d.html; set("post-content", d.html); }
                    if (d.date) set("post-date", d.date);
                }
                set("post-sourceurl", d.sourceUrl);
                set("post-sourcename", d.sourceName);
                if (d.images && d.images.length) {
                    var ti = el("post-titleimage");
                    if (ti && !ti.value) { ti.value = d.images[0]; ti.dispatchEvent(new Event("input")); }
                    var list = el("post-gallery");
                    var root = list && list.closest("[data-ui-medialist]");
                    if (root && root.matMediaList) root.matMediaList.add(d.images, d.title);
                }
                show(cfg.dataset.done.replace("{0}", d.via).replace("{1}", (d.images || []).length), false, d.notes);
                // Nothing to read beyond the success line: the filled form behind is the result.
                if (!d.notes || !d.notes.length) box.close();
            })
            .catch(function () { show(cfg.dataset.failed, true); })
            .finally(function () { go.disabled = false; });
    }
    go.addEventListener("click", run);
    input.addEventListener("keydown", function (e) { if (e.key === "Enter") { e.preventDefault(); run(); } });
})();
