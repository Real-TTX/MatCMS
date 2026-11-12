// The docs editor's visual mode and live preview (Docs / Wiki plugin, back office only).
// Markdown stays the one stored format: the visual mode shows the blocks the SERVER drew from the Markdown (DocsMarkdown.ToEditor) and hands
// them back as they are — a block nobody touched goes back as the Markdown it was (byte for byte), a touched one as its HTML, which the server
// writes as Markdown (DocsVisual). So there is no second Markdown renderer or writer in this file, and switching back and forth changes nothing.
// The preview is drawn by the server too, with the public renderer and stylesheet.
(function () {
    "use strict";
    var cfg = document.getElementById("docs-ed-config");
    var form = document.getElementById("docs-page-form");
    if (!cfg || !form) return;
    var ta = form.querySelector('textarea[name="body"]');
    var modeIn = form.querySelector('input[name="md_mode"]');
    var stateIn = form.querySelector('input[name="md_visual"]');
    if (!ta || !modeIn || !stateIn) return;
    var token = form.querySelector('input[name="__RequestVerificationToken"]');
    var W = {};
    try { W = JSON.parse(cfg.getAttribute("data-words") || "{}"); } catch (e) { W = {}; }
    function w(k) { return W[k] || k; }
    var KINDS = ["note", "tip", "important", "warning", "caution"];
    var BLOCK = /^(P|DIV|H[1-6]|UL|OL|PRE|BLOCKQUOTE|TABLE|HR|SECTION|ARTICLE|LI|FIGURE|DL)$/;
    var STORE = "docs-ed-mode", PREVIEW = "docs-ed-preview";

    // ---- talking to the server --------------------------------------------------------------------------------------
    function post(action, data) {
        var fd = new FormData();
        fd.append("action", action);
        fd.append("lang", cfg.getAttribute("data-lang") || "");
        fd.append("v", cfg.getAttribute("data-v") || "");
        if (token) fd.append("__RequestVerificationToken", token.value);
        Object.keys(data || {}).forEach(function (k) { fd.append(k, data[k]); });
        return fetch(cfg.getAttribute("data-url"), { method: "POST", body: fd, credentials: "same-origin" })
            .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); });
    }

    // ---- the frame around the text area ----------------------------------------------------------------------------
    var field = ta.closest(".field, .form-field") || ta.parentElement;
    var split = document.createElement("div");
    split.className = "docs-ed-split";
    field.parentNode.insertBefore(split, field);
    var bar = document.createElement("div");
    bar.className = "docs-ed-bar";
    split.parentNode.insertBefore(bar, split);
    var left = document.createElement("div");
    split.appendChild(left);
    left.appendChild(field);
    var visual = document.createElement("div");
    visual.className = "docs docs-ed-visual docs-ed-hidden";
    visual.style.setProperty("--docs-ed-code", JSON.stringify(w("code")));
    var surface = document.createElement("div");
    surface.className = "docs-content";
    surface.contentEditable = "true";
    surface.spellcheck = true;
    visual.appendChild(surface);
    left.appendChild(visual);
    var previewBox = document.createElement("div");
    previewBox.className = "docs-ed-hidden";
    split.appendChild(previewBox);

    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
    function button(icon, title, run, cls) {
        var b = el("button", "btn btn-ghost btn-sm" + (cls ? " " + cls : ""));
        b.type = "button"; b.title = title; b.setAttribute("aria-label", title);
        b.innerHTML = '<i class="ti ' + icon + '" aria-hidden="true"></i>';
        b.addEventListener("mousedown", function (e) { e.preventDefault(); });       // keep the caret where it is
        b.addEventListener("click", function (e) { e.preventDefault(); run(b); });
        return b;
    }
    function sep() { return el("span", "docs-ed-sep"); }

    var modes = el("div", "docs-ed-modes");
    var bVisual = el("button", "", w("visual")); bVisual.type = "button";
    var bMd = el("button", "", w("markdown")); bMd.type = "button";
    modes.appendChild(bVisual); modes.appendChild(bMd);
    bar.appendChild(modes);

    var tools = el("span", "docs-ed-tools");
    tools.style.display = "contents";
    var tableTools = [];
    [
        sep(),
        button("ti-pilcrow", w("paragraph"), function () { block("p"); }),
        button("ti-h-2", w("h2"), function () { block("h2"); }),
        button("ti-h-3", w("h3"), function () { block("h3"); }),
        button("ti-h-4", w("h4"), function () { block("h4"); }),
        sep(),
        button("ti-bold", w("bold"), function () { exec("bold"); }),
        button("ti-italic", w("italic"), function () { exec("italic"); }),
        button("ti-strikethrough", w("strike"), function () { exec("strikeThrough"); }),
        button("ti-code", w("code"), inlineCode),
        sep(),
        button("ti-link", w("link"), link),
        button("ti-file-symlink", w("docLink"), docLink),
        button("ti-photo", w("image"), image),
        sep(),
        button("ti-list", w("ul"), function () { exec("insertUnorderedList"); }),
        button("ti-list-numbers", w("ol"), function () { exec("insertOrderedList"); }),
        button("ti-list-check", w("task"), task),
        button("ti-quote", w("quote"), function () { block("blockquote"); }),
        button("ti-info-square-rounded", w("callout"), function (b) { calloutMenu(b, null); }),
        button("ti-source-code", w("codeBlock"), codeBlock),
        button("ti-table", w("table"), table),
        button("ti-separator-horizontal", w("hr"), rule),
        sep(),
        button("ti-arrow-back-up", w("undo"), function () { exec("undo"); }),
        button("ti-arrow-forward-up", w("redo"), function () { exec("redo"); })
    ].forEach(function (b) { tools.appendChild(b); });
    [
        button("ti-row-insert-bottom", w("rowAdd"), function () { tableEdit("rowAdd"); }),
        button("ti-column-insert-right", w("colAdd"), function () { tableEdit("colAdd"); }),
        button("ti-row-remove", w("rowDel"), function () { tableEdit("rowDel"); }),
        button("ti-column-remove", w("colDel"), function () { tableEdit("colDel"); })
    ].forEach(function (b) { b.disabled = true; tableTools.push(b); tools.appendChild(b); });
    bar.appendChild(tools);
    bar.appendChild(el("span", "docs-ed-grow"));
    var status = el("span", "docs-ed-status");
    status.setAttribute("role", "status");
    bar.appendChild(status);
    var bPreview = button("ti-eye", w("preview"), function () { setPreview(previewBox.classList.contains("docs-ed-hidden")); });
    bPreview.innerHTML += " " + w("preview");
    bar.appendChild(bPreview);

    // ---- modes ------------------------------------------------------------------------------------------------------
    var mode = "md";
    var snaps = new WeakMap();      // block element -> { raw, html } as the server gave it
    var lead = "";
    var busy = false;

    function show() {
        var v = mode === "visual";
        visual.classList.toggle("docs-ed-hidden", !v);
        field.classList.toggle("docs-ed-hidden", v);
        tools.style.display = v ? "contents" : "none";
        bVisual.classList.toggle("is-on", v);
        bMd.classList.toggle("is-on", !v);
        modeIn.value = v ? "visual" : "md";
    }

    function toVisual() {
        if (busy || mode === "visual") return Promise.resolve();
        busy = true; status.textContent = w("busy");
        return post("visual", { body: ta.value }).then(function (d) {
            lead = d.lead || "";
            surface.innerHTML = "";
            (d.blocks || []).forEach(function (b) {
                var t = document.createElement("template");
                t.innerHTML = b.html;
                var node = t.content.firstElementChild;
                if (!node) return;
                surface.appendChild(node);
                snaps.set(node, { raw: b.raw, i: b.i, html: node.outerHTML });
            });
            if (!surface.firstChild) surface.appendChild(emptyParagraph());
            mode = "visual"; show(); remember();
            status.textContent = "";
        }).catch(function () { status.textContent = w("failed"); }).finally(function () { busy = false; });
    }

    function toMarkdown() {
        if (busy || mode === "md") return Promise.resolve();
        busy = true; status.textContent = w("busy");
        return post("markdown", { visual: JSON.stringify(state()) }).then(function (d) {
            if (typeof d.markdown === "string") ta.value = d.markdown;
            mode = "md"; show(); remember();
            status.textContent = "";
        }).catch(function () { status.textContent = w("failed"); }).finally(function () { busy = false; });
    }

    function remember() { try { localStorage.setItem(STORE, mode); } catch (e) { /* private mode */ } }

    /** The editor's state for the server: untouched blocks as their Markdown, touched or new ones as HTML; loose text becomes a paragraph. */
    function state() {
        var blocks = [], run = [];
        function flush() {
            if (!run.length) return;
            var html = run.map(function (n) { return n.nodeType === 3 ? escapeHtml(n.textContent) : n.outerHTML; }).join("");
            if (html.replace(/<br\s*\/?>|&nbsp;|\s/g, "").length) blocks.push({ html: "<p>" + html + "</p>" });
            run = [];
        }
        Array.prototype.forEach.call(surface.childNodes, function (n) {
            if (n.nodeType === 1 && BLOCK.test(n.tagName)) {
                flush();
                var s = snaps.get(n);
                blocks.push(s && s.html === n.outerHTML ? { raw: s.raw, i: s.i } : { html: n.outerHTML });
            } else if (n.nodeType === 1 || n.nodeType === 3) run.push(n);
        });
        flush();
        return { lead: lead, blocks: blocks };
    }
    window.DocsEditor = { state: state, toVisual: toVisual, toMarkdown: toMarkdown, mode: function () { return mode; } };

    bVisual.addEventListener("click", function () { toVisual().then(schedulePreview); });
    bMd.addEventListener("click", function () { toMarkdown().then(schedulePreview); });

    form.addEventListener("submit", function () {
        if (mode === "visual") { stateIn.value = JSON.stringify(state()); modeIn.value = "visual"; }
        else { stateIn.value = ""; modeIn.value = "md"; }
    }, true);

    // ---- editing helpers --------------------------------------------------------------------------------------------
    function escapeHtml(s) { return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;"); }
    function emptyParagraph() { var p = document.createElement("p"); p.appendChild(document.createElement("br")); return p; }
    function exec(cmd, value) { surface.focus(); document.execCommand(cmd, false, value == null ? null : value); changed(); }
    function block(tag) { exec("formatBlock", "<" + tag + ">"); }

    var saved = null;
    function saveRange() {
        var sel = window.getSelection();
        if (sel && sel.rangeCount && surface.contains(sel.getRangeAt(0).commonAncestorContainer)) saved = sel.getRangeAt(0).cloneRange();
        return saved;
    }
    function restoreRange() {
        surface.focus();
        if (!saved) return;
        var sel = window.getSelection();
        sel.removeAllRanges(); sel.addRange(saved);
    }
    function current() {
        var sel = window.getSelection();
        if (!sel || !sel.rangeCount) return null;
        var n = sel.getRangeAt(0).startContainer;
        return surface.contains(n) ? n : null;
    }
    function closest(sel) { var n = current(); if (!n) return null; if (n.nodeType !== 1) n = n.parentNode; var c = n.closest(sel); return c && surface.contains(c) ? c : null; }
    function rootBlock() {
        var n = current();
        while (n && n.parentNode !== surface) n = n.parentNode;
        return n && n.parentNode === surface ? n : null;
    }
    function insertAfterCurrent(node) {
        var at = rootBlock();
        if (at && at.nextSibling) surface.insertBefore(node, at.nextSibling);
        else if (at) surface.appendChild(node);
        else surface.appendChild(node);
        return node;
    }
    function caretInto(node, atEnd) {
        var r = document.createRange();
        r.selectNodeContents(node);
        r.collapse(!atEnd);
        var sel = window.getSelection();
        sel.removeAllRanges(); sel.addRange(r);
        surface.focus();
    }

    /** Puts an inline node where the caret is (or over the selection) and the caret after it. Built as nodes, not with insertHTML,
     *  which copies the computed look of the spot into style attributes. */
    function insertInline(node) {
        surface.focus();
        var sel = window.getSelection();
        var r = sel && sel.rangeCount && surface.contains(sel.getRangeAt(0).commonAncestorContainer) ? sel.getRangeAt(0) : null;
        if (!r) { r = document.createRange(); r.selectNodeContents(surface.lastElementChild || surface); r.collapse(false); }
        r.deleteContents();
        r.insertNode(node);
        r.setStartAfter(node); r.collapse(true);
        sel.removeAllRanges(); sel.addRange(r);
        changed();
        return node;
    }
    function make(tag, attrs, text) {
        var n = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) { n.setAttribute(k, attrs[k]); });
        if (text != null) n.textContent = text;
        return n;
    }

    function inlineCode() {
        var c = closest("code");
        if (c && !c.closest("pre")) {           // inside inline code: take it off again
            var text = document.createTextNode(c.textContent);
            c.parentNode.replaceChild(text, c);
            changed(); return;
        }
        var sel = window.getSelection();
        var t = sel ? sel.toString() : "";
        insertInline(make("code", null, t || w("code")));
    }

    function ask(question, value) {
        if (window.MatDialog && window.MatDialog.prompt) return window.MatDialog.prompt(question, value || "");
        return Promise.resolve(null);
    }

    function link() {
        var a = closest("a");
        saveRange();
        ask(w("linkUrl"), a ? a.getAttribute("href") : "https://").then(function (url) {
            if (url == null) return;
            url = url.trim();
            restoreRange();
            if (a) { if (url) a.setAttribute("href", url); else a.replaceWith(document.createTextNode(a.textContent)); changed(); return; }
            if (!url) return;
            var t = window.getSelection().toString();
            if (t) exec("createLink", url);
            else insertInline(make("a", { href: url }, url));
        });
    }

    var docDlg = document.getElementById("docs-ed-doc");
    function docLink() {
        if (!docDlg) return;
        var a = closest("a.docs-doclink");
        saveRange();
        var page = document.getElementById("docs-ed-doc-page");
        var anchor = document.getElementById("docs-ed-doc-anchor");
        var text = document.getElementById("docs-ed-doc-text");
        var href = a ? (a.getAttribute("href") || "").replace(/^doc:/i, "") : "";
        var hash = href.indexOf("#");
        if (page) { page.value = hash >= 0 ? href.slice(0, hash) : href; page.dispatchEvent(new Event("change", { bubbles: true })); }
        if (anchor) anchor.value = hash >= 0 ? href.slice(hash + 1) : "";
        if (text) text.value = a ? a.textContent : (window.getSelection().toString() || "");
        docDlg.dataset.editing = a ? "1" : "";
        docDlg._link = a;
        docDlg.showModal();
    }
    if (docDlg) {
        var go = docDlg.querySelector("[data-docs-ed-doc-go]");
        if (go) go.addEventListener("click", function (e) {
            e.preventDefault();
            var page = document.getElementById("docs-ed-doc-page");
            var anchor = document.getElementById("docs-ed-doc-anchor");
            var textIn = document.getElementById("docs-ed-doc-text");
            var path = page ? page.value.trim() : "";
            var hash = anchor ? anchor.value.trim().replace(/^#/, "") : "";
            if (!path && !hash) return;
            var href = "doc:" + path + (hash ? "#" + hash : "");
            var label = (textIn && textIn.value.trim()) || (page && page.selectedOptions[0] ? page.selectedOptions[0].text.split(/\s+·\s+/)[0].replace(/^(—\s*)+/, "").trim() : path);
            docDlg.close();
            var a = docDlg._link;
            if (a) { a.setAttribute("href", href); a.textContent = label; a.classList.remove("docs-broken"); changed(); return; }
            restoreRange();
            insertInline(make("a", { href: href, class: "docs-doclink" }, label));
        });
    }

    function image() {
        saveRange();
        function put(url) {
            if (Array.isArray(url)) url = url[0];
            if (!url) return;
            ask(w("imageAlt"), "").then(function (alt) {
                restoreRange();
                insertInline(make("img", { src: url, alt: alt || "" }));
            });
        }
        if (window.openMediaPicker) window.openMediaPicker(put);
        else ask(w("imageUrl"), "/uploads/").then(put);
    }

    function task() {
        var li = closest("li");
        if (!li) { exec("insertUnorderedList"); li = closest("li"); if (!li) return; }
        var box = li.querySelector(":scope > input[type=checkbox]");
        if (box) { box.remove(); li.classList.remove("docs-task"); if (!li.className) li.removeAttribute("class"); }
        else {
            var cb = document.createElement("input"); cb.type = "checkbox";
            li.insertBefore(document.createTextNode(" "), li.firstChild);
            li.insertBefore(cb, li.firstChild);
            li.classList.add("docs-task");
        }
        changed();
    }

    // ---- alert boxes --------------------------------------------------------------------------------------------------
    var pop = null;
    function closePop() { if (pop) { pop.remove(); pop = null; document.removeEventListener("mousedown", outside, true); } }
    function outside(e) { if (pop && !pop.contains(e.target)) closePop(); }
    function calloutMenu(anchorEl, box) {
        closePop();
        box = box || closest(".docs-callout");
        pop = el("div", "docs-ed-pop");
        KINDS.forEach(function (k) {
            var b = el("button", "btn btn-ghost btn-sm", w("alert_" + k));
            b.type = "button";
            b.addEventListener("mousedown", function (e) { e.preventDefault(); });
            b.addEventListener("click", function () { closePop(); setCallout(box, k); });
            pop.appendChild(b);
        });
        if (box) {
            var off = el("button", "btn btn-ghost btn-sm", w("clear"));
            off.type = "button";
            off.addEventListener("click", function () { closePop(); unwrap(box); });
            pop.appendChild(off);
        }
        var r = anchorEl.getBoundingClientRect();
        pop.style.left = (r.left + window.scrollX) + "px";
        pop.style.top = (r.bottom + window.scrollY + 4) + "px";
        document.body.appendChild(pop);
        document.addEventListener("mousedown", outside, true);
    }
    function setCallout(box, kind) {
        if (!box) {
            var at = rootBlock();
            box = document.createElement("div");
            var title = document.createElement("p");
            title.className = "docs-callout-title";
            title.contentEditable = "false";
            box.appendChild(title);
            if (at && /^(P|UL|OL|DIV|BLOCKQUOTE|H[2-6])$/.test(at.tagName) && !at.classList.contains("docs-callout")) {
                surface.insertBefore(box, at);
                box.appendChild(at);
            } else {
                insertAfterCurrent(box);
                box.appendChild(emptyParagraph());
            }
            caretInto(box.lastChild, true);
        }
        box.className = "docs-callout docs-callout-" + kind;
        box.setAttribute("data-kind", kind);
        var t = box.querySelector(":scope > .docs-callout-title");
        if (t) t.textContent = w("alert_" + kind);
        changed();
    }
    function unwrap(box) {
        var t = box.querySelector(":scope > .docs-callout-title");
        if (t) t.remove();
        while (box.firstChild) box.parentNode.insertBefore(box.firstChild, box);
        box.remove();
        changed();
    }

    // ---- code blocks, tables, rules -----------------------------------------------------------------------------------
    function codeBlock() {
        var at = rootBlock();
        var text = "";
        if (at && at.tagName === "P") text = at.innerText.replace(/\n$/, "");
        var div = document.createElement("div");
        div.className = "docs-code docs-code-has-head";
        div.setAttribute("data-lang", "");
        div.innerHTML = '<div class="docs-code-head" contenteditable="false"><span class="docs-code-lang" data-docs-lang></span></div><pre><code></code></pre>';
        div.querySelector("code").textContent = text || "\n";
        if (at && at.tagName === "P") surface.replaceChild(div, at); else insertAfterCurrent(div);
        caretInto(div.querySelector("code"), false);
        changed();
    }
    function language(span) {
        var box = span.closest(".docs-code");
        ask(w("language"), box.getAttribute("data-lang") || "").then(function (v) {
            if (v == null) return;
            v = v.trim().toLowerCase().replace(/[^a-z0-9#+.\-]/g, "");
            box.setAttribute("data-lang", v);
            span.textContent = v;
            var code = box.querySelector("pre > code");
            if (code) { if (v) code.className = "language-" + v; else code.removeAttribute("class"); }
            changed();
        });
    }
    function table() {
        var wrap = document.createElement("div");
        wrap.className = "docs-table";
        wrap.innerHTML = "<table><thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td><br></td><td><br></td></tr></tbody></table>";
        insertAfterCurrent(wrap);
        if (!wrap.nextSibling) surface.appendChild(emptyParagraph());
        caretInto(wrap.querySelector("th"), true);
        changed();
    }
    function tableEdit(what) {
        var cell = closest("td, th");
        if (!cell) return;
        var row = cell.parentNode, tbl = row.closest("table");
        var index = Array.prototype.indexOf.call(row.children, cell);
        var rows = tbl.querySelectorAll("tr");
        if (what === "rowAdd") {
            var tr = document.createElement("tr");
            for (var i = 0; i < row.children.length; i++) { var td = document.createElement("td"); td.appendChild(document.createElement("br")); tr.appendChild(td); }
            if (row.parentNode.tagName === "THEAD") {
                var body = tbl.tBodies[0] || tbl.appendChild(document.createElement("tbody"));
                body.insertBefore(tr, body.firstChild);
            } else row.parentNode.insertBefore(tr, row.nextSibling);
            caretInto(tr.firstChild, false);
        } else if (what === "rowDel") {
            if (row.parentNode.tagName !== "THEAD") row.remove();
        } else if (what === "colAdd") {
            Array.prototype.forEach.call(rows, function (r) {
                var c = document.createElement(r.parentNode.tagName === "THEAD" ? "th" : "td");
                c.appendChild(document.createElement("br"));
                r.insertBefore(c, r.children[index + 1] || null);
            });
        } else if (what === "colDel") {
            if (row.children.length > 1) Array.prototype.forEach.call(rows, function (r) { if (r.children[index]) r.children[index].remove(); });
        }
        changed();
    }
    function rule() {
        var hr = document.createElement("hr");
        insertAfterCurrent(hr);
        var p = emptyParagraph();
        surface.insertBefore(p, hr.nextSibling);
        caretInto(p, false);
        changed();
    }

    // ---- events in the visual area ------------------------------------------------------------------------------------
    surface.addEventListener("click", function (e) {
        var t = e.target;
        if (t.matches && t.matches("input[type=checkbox]")) {
            // a click only changes the box's property; the attribute is what the HTML (and so the Markdown) carries
            if (t.checked) t.setAttribute("checked", ""); else t.removeAttribute("checked");
            changed(); return;
        }
        var lang = t.closest && t.closest(".docs-code-lang");
        if (lang) { e.preventDefault(); language(lang); return; }
        var title = t.closest && t.closest(".docs-callout-title");
        if (title) { e.preventDefault(); calloutMenu(title, title.parentNode); return; }
        if (t.closest && t.closest("a")) e.preventDefault();                // links do not navigate while editing
    });
    surface.addEventListener("keydown", function (e) {
        if (e.key === "Enter" && !e.shiftKey && closest("pre")) { e.preventDefault(); exec("insertText", "\n"); return; }
        if (e.key === "Tab" && closest("li")) { e.preventDefault(); exec(e.shiftKey ? "outdent" : "indent"); return; }
        if ((e.ctrlKey || e.metaKey) && (e.key === "k" || e.key === "K")) { e.preventDefault(); link(); }
    });
    surface.addEventListener("paste", function (e) {
        // only text: what a web page or a word processor puts on the clipboard is not Markdown's business
        var text = e.clipboardData && e.clipboardData.getData("text/plain");
        if (text == null) return;
        e.preventDefault();
        exec("insertText", text);
    });
    surface.addEventListener("input", changed);
    document.addEventListener("selectionchange", function () {
        if (mode !== "visual") return;
        var inTable = !!closest("td, th");
        tableTools.forEach(function (b) { b.disabled = !inTable; });
    });
    try { document.execCommand("defaultParagraphSeparator", false, "p"); document.execCommand("styleWithCSS", false, false); } catch (e) { /* older browsers */ }

    // ---- preview ------------------------------------------------------------------------------------------------------
    var timer = null, frame = null, seq = 0;
    function setPreview(on) {
        previewBox.classList.toggle("docs-ed-hidden", !on);
        split.classList.toggle("has-preview", on);
        bPreview.classList.toggle("is-on", on);
        try { localStorage.setItem(PREVIEW, on ? "1" : "0"); } catch (e) { /* private mode */ }
        if (on) refreshPreview();
    }
    function changed() { schedulePreview(); }
    function schedulePreview() {
        if (previewBox.classList.contains("docs-ed-hidden")) return;
        clearTimeout(timer);
        timer = setTimeout(refreshPreview, 500);
    }
    function refreshPreview() {
        var my = ++seq;
        var title = form.querySelector('[name="title"]'), summary = form.querySelector('[name="summary"]');
        var data = { mode: mode, title: title ? title.value : "", summary: summary ? summary.value : "" };
        if (mode === "visual") data.visual = JSON.stringify(state()); else data.body = ta.value;
        post("preview", data).then(function (d) {
            if (my !== seq) return;
            // A new frame each time: changing srcdoc on a frame in the page is a history entry ("back" would step through old previews).
            var y = 0;
            try { y = frame && frame.contentWindow ? frame.contentWindow.scrollY : 0; } catch (e) { y = 0; }
            var f = document.createElement("iframe");
            f.className = "docs-ed-preview";
            f.title = w("preview");
            f.setAttribute("sandbox", "allow-same-origin allow-popups");
            f.srcdoc = d.html || "<p>" + escapeHtml(w("previewEmpty")) + "</p>";
            f.addEventListener("load", function () { try { f.contentWindow.scrollTo(0, y); } catch (e) { /* ignore */ } });
            if (frame) previewBox.replaceChild(f, frame); else previewBox.appendChild(f);
            frame = f;
        }).catch(function () { status.textContent = w("failed"); });
    }
    ta.addEventListener("input", schedulePreview);
    ["title", "summary"].forEach(function (n) { var f = form.querySelector('[name="' + n + '"]'); if (f) f.addEventListener("input", schedulePreview); });

    // ---- start --------------------------------------------------------------------------------------------------------
    var wanted = "visual", withPreview = false;
    try { wanted = localStorage.getItem(STORE) || "visual"; withPreview = localStorage.getItem(PREVIEW) === "1"; } catch (e) { /* private mode */ }
    show();
    if (withPreview) setPreview(true);
    if (wanted === "visual") toVisual().then(schedulePreview);
})();
