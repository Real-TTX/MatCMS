// Behaviour of the plugin UI library's richer fields (UiRenderer in MatCMS.Shared.Web draws the markup):
// rich text, image, image/file list, tags, and the dialog opener. Purely markup-driven — every hook is a
// data-ui-* attribute and several instances per page are fine. No per-page JavaScript needed.
// The media picker itself (window.openMediaPicker) belongs to the CMS; a field only calls it when clicked.
(function () {
    "use strict";

    // ---- Dialogs: a button with data-ui-opens="id" opens <dialog id="id" data-ui-dialog> ----
    document.addEventListener("click", function (e) {
        var opener = e.target.closest && e.target.closest("[data-ui-opens]");
        if (opener) {
            var dlg = document.getElementById(opener.getAttribute("data-ui-opens"));
            if (dlg && dlg.showModal) { e.preventDefault(); dlg.showModal(); var f = dlg.querySelector("input,textarea,select"); if (f) f.focus(); }
            return;
        }
        var closer = e.target.closest && e.target.closest("[data-ui-close]");
        if (closer) { var d = closer.closest("dialog"); if (d) d.close(); }
    });

    function each(sel, fn) { Array.prototype.forEach.call(document.querySelectorAll(sel), fn); }

    // ---- Rich text ----
    each("[data-ui-rt]", function (root) {
        var ed = root.querySelector("[data-ui-rt-editor]");
        var hidden = root.querySelector("[data-ui-rt-value]");
        var bar = root.querySelector("[data-ui-rt-toolbar]");
        if (!ed || !hidden || !bar) return;
        bar.addEventListener("click", function (e) {
            var b = e.target.closest("button"); if (!b) return;
            ed.focus();
            var cmd = b.getAttribute("data-cmd"), arg = b.getAttribute("data-arg");
            if (b.getAttribute("data-prompt")) {
                var sel = window.getSelection();
                var keep = sel.rangeCount ? sel.getRangeAt(0).cloneRange() : null;
                MatDialog.prompt(root.getAttribute("data-t-link-prompt") || "URL", "https://").then(function (url) {
                    if (!url) return;
                    ed.focus();
                    if (keep) { var s = window.getSelection(); s.removeAllRanges(); s.addRange(keep); }
                    document.execCommand(cmd, false, url);
                });
            } else { document.execCommand(cmd, false, arg || null); }
        });
        function sync() { hidden.value = ed.innerHTML.trim(); }
        ed.addEventListener("input", sync);
        var form = root.closest("form");
        if (form) form.addEventListener("submit", sync);
    });

    // ---- Image (URL + preview + media picker) ----
    each("[data-ui-image]", function (root) {
        var url = root.querySelector("[data-ui-image-url]");
        var prev = root.querySelector(".preview");
        var pick = root.querySelector("[data-ui-image-pick]");
        if (!url || !prev) return;
        function sync() { prev.style.backgroundImage = url.value ? "url('" + url.value.replace(/'/g, "%27") + "')" : ""; }
        url.addEventListener("input", sync);
        if (pick) pick.addEventListener("click", function () {
            if (window.openMediaPicker) window.openMediaPicker(function (u) { url.value = u; sync(); url.dispatchEvent(new Event("change", { bubbles: true })); });
        });
    });

    // ---- Image list (gallery) / file list (attachments): the value is a JSON array ----
    function nameFromUrl(u) { try { return decodeURIComponent((u || "").split("/").pop().split("?")[0]); } catch (e) { return u; } }
    each("[data-ui-medialist]", function (root) {
        var files = root.getAttribute("data-kind") === "files";
        var value = root.querySelector("[data-ui-medialist-value]");
        var list = root.querySelector("[data-ui-medialist-items]");
        var add = root.querySelector("[data-ui-medialist-add]");
        if (!value || !list) return;
        var items = [];
        try { items = JSON.parse(value.value || "[]") || []; } catch (e) { items = []; }
        function btn(label, title, fn) {
            var b = document.createElement("button"); b.type = "button"; b.textContent = label; b.title = title; b.addEventListener("click", fn); return b;
        }
        function render() {
            value.value = JSON.stringify(items);
            list.innerHTML = "";
            items.forEach(function (it, i) {
                if (files) {
                    var chip = document.createElement("span"); chip.className = "att-chip";
                    var a = document.createElement("a"); a.href = it.url; a.target = "_blank"; a.rel = "noopener"; a.textContent = it.name || nameFromUrl(it.url);
                    var x = btn("×", root.getAttribute("data-t-remove") || "", function () { items.splice(i, 1); render(); });
                    chip.appendChild(a); chip.appendChild(x); list.appendChild(chip);
                    return;
                }
                var box = document.createElement("div"); box.className = "gal-item";
                var img = document.createElement("img"); img.src = it.url; img.alt = it.alt || ""; img.loading = "lazy"; box.appendChild(img);
                if (i === 0) { var f = document.createElement("span"); f.className = "gal-first"; f.textContent = "1"; box.appendChild(f); }
                var tools = document.createElement("div"); tools.className = "gal-tools";
                tools.appendChild(btn("←", root.getAttribute("data-t-left") || "", function () { if (i > 0) { items.splice(i - 1, 0, items.splice(i, 1)[0]); render(); } }));
                tools.appendChild(btn("×", root.getAttribute("data-t-remove") || "", function () { items.splice(i, 1); render(); }));
                tools.appendChild(btn("→", root.getAttribute("data-t-right") || "", function () { if (i < items.length - 1) { items.splice(i + 1, 0, items.splice(i, 1)[0]); render(); } }));
                box.appendChild(tools); list.appendChild(box);
            });
        }
        if (add) add.addEventListener("click", function () {
            if (!window.openMediaPicker) return;
            window.openMediaPicker(function (urls) {
                (Array.isArray(urls) ? urls : [urls]).forEach(function (u) {
                    if (u) items.push(files ? { url: u, name: nameFromUrl(u) } : { url: u, alt: "" });
                });
                render();
            }, { multiple: true });
        });
        render();
        // Other scripts (an import from a link) can append without knowing the markup.
        root.matMediaList = { add: function (urls, alt) { urls.forEach(function (u) { items.push(files ? { url: u, name: nameFromUrl(u) } : { url: u, alt: alt || "" }); }); render(); }, count: function () { return items.length; } };
    });

    // ---- Tags (chips + suggestions); the hidden value is comma-separated ----
    each("[data-ui-tags]", function (root) {
        var box = root.querySelector("[data-ui-tags-box]");
        var entry = root.querySelector("[data-ui-tags-entry]");
        var addBtn = root.querySelector("[data-ui-tags-add]");
        var hidden = root.querySelector("[data-ui-tags-value]");
        var suggest = root.querySelector("[data-ui-tags-suggest]");
        if (!box || !entry || !addBtn || !hidden) return;
        var chips = [];
        function showEntry() { addBtn.hidden = true; entry.hidden = false; entry.focus(); }
        function hideEntry() { entry.hidden = true; addBtn.hidden = false; }
        addBtn.addEventListener("click", showEntry);
        function render() {
            Array.prototype.slice.call(box.querySelectorAll(".tag-chip-item")).forEach(function (e) { e.remove(); });
            chips.forEach(function (tag) {
                var el = document.createElement("span"); el.className = "tag-chip-item"; el.textContent = tag;
                var x = document.createElement("button"); x.type = "button"; x.className = "tag-x"; x.setAttribute("aria-label", root.getAttribute("data-t-remove") || ""); x.textContent = "×";
                x.addEventListener("click", function () { removeTag(tag); entry.focus(); });
                el.appendChild(x); box.insertBefore(el, entry);
            });
            hidden.value = chips.join(", ");
        }
        function addTag(raw) {
            var t = (raw || "").replace(/,/g, " ").trim(); if (!t) return;
            if (chips.some(function (c) { return c.toLowerCase() === t.toLowerCase(); })) return;
            chips.push(t); render();
        }
        function removeTag(tag) { chips = chips.filter(function (c) { return c !== tag; }); render(); }
        function setTags(str) {
            var seen = {};
            chips = (str || "").split(",").map(function (s) { return s.trim(); }).filter(function (c) {
                if (!c) return false; var k = c.toLowerCase(); if (seen[k]) return false; seen[k] = 1; return true;
            });
            render();
        }
        entry.addEventListener("keydown", function (e) {
            if (e.key === "Enter" || e.key === ",") { e.preventDefault(); addTag(entry.value); entry.value = ""; }
            else if (e.key === "Escape") { e.preventDefault(); entry.value = ""; hideEntry(); }
            else if (e.key === "Backspace" && !entry.value && chips.length) { removeTag(chips[chips.length - 1]); }
        });
        entry.addEventListener("blur", function () { if (entry.value.trim()) { addTag(entry.value); entry.value = ""; } hideEntry(); });
        box.addEventListener("click", function (e) { if (e.target === box) showEntry(); });
        if (suggest) suggest.addEventListener("click", function (e) {
            var c = e.target.closest(".mini-chip-add"); if (!c) return; addTag(c.getAttribute("data-tag")); entry.focus();
        });
        var form = root.closest("form");
        if (form) form.addEventListener("submit", function () { if (entry.value.trim()) { addTag(entry.value); entry.value = ""; } });
        setTags(hidden.value);
    });

    // ---- Rows: add a row, delete a row, and number them as they stand when the form is sent ----
    each("[data-ui-rows]", function (root) {
        var name = root.getAttribute("data-name");
        var body = root.querySelector("[data-ui-rows-body]");
        var tpl = root.querySelector("[data-ui-rows-template]");
        var count = root.querySelector("[data-ui-rows-count]");
        var empty = root.querySelector("[data-ui-rows-empty]");
        var add = root.querySelector("[data-ui-rows-add]");
        var max = parseInt(root.getAttribute("data-max"), 10) || 100;
        if (!body) return;
        function rows() { return body.querySelectorAll("[data-ui-row]"); }
        function renumber() {
            var list = rows();
            Array.prototype.forEach.call(list, function (tr, i) {
                Array.prototype.forEach.call(tr.querySelectorAll("[data-key]"), function (el) { el.name = name + "." + i + "." + el.getAttribute("data-key"); });
            });
            if (count) count.value = list.length;
            if (empty) empty.hidden = list.length > 0;
            if (add) add.disabled = list.length >= max;
        }
        if (add && tpl) add.addEventListener("click", function () {
            if (rows().length >= max) return;
            body.appendChild(tpl.content.cloneNode(true));
            renumber();
            var all = rows(), field = all[all.length - 1].querySelector("input[type=text]");
            if (field) field.focus();
        });
        root.addEventListener("click", function (e) {
            var b = e.target.closest("[data-ui-row-remove]"); if (!b) return;
            var tr = b.closest("[data-ui-row]"); if (tr) { tr.remove(); renumber(); }
        });
        // A picture cell: the preview follows the address, the button opens the media library (works for rows added later: the click is caught here, not on each button).
        function preview(box) {
            var url = box.querySelector("input"), prev = box.querySelector(".preview");
            if (url && prev) prev.style.backgroundImage = url.value ? "url('" + url.value.replace(/'/g, "%27") + "')" : "";
        }
        root.addEventListener("input", function (e) { var b = e.target.closest && e.target.closest(".row-img"); if (b) preview(b); });
        root.addEventListener("click", function (e) {
            var pick = e.target.closest("[data-ui-rowimage-pick]"); if (!pick) return;
            var box = pick.closest(".row-img"), url = box && box.querySelector("input");
            if (url && window.openMediaPicker) window.openMediaPicker(function (u) { url.value = u; preview(box); url.dispatchEvent(new Event("input", { bubbles: true })); });
        });
        var form = root.closest("form");
        if (form) form.addEventListener("submit", renumber);
        renumber();
    });
})();
