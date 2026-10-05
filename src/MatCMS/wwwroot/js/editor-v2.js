// Editor v2 (Pages/Admin/Pages/Editor.cshtml, docs/editor-v2.md).
// The whole page is a DRAFT held here: the tree on the left, the fields of the selected block on the
// right, the preview in the middle rendered from the draft by the server (Edit?handler=RenderPreview,
// nothing persisted). "Speichern" hands the whole draft to Edit?handler=SaveAll in one go. New blocks
// carry negative ids until then; the save answers with the real ids and the draft is renumbered.
(function () {
    "use strict";

    var body = document.body;
    var pageId = body.getAttribute("data-page-id");
    var renderUrl = body.getAttribute("data-render-url");
    var saveUrl = body.getAttribute("data-save-url");
    var token = (document.querySelector('input[name="__RequestVerificationToken"]') || {}).value || "";
    function json(id, fallback) { try { return JSON.parse(document.getElementById(id).textContent) || fallback; } catch (e) { return fallback; } }
    var L = json("ev2-l10n", {});
    function t(k) { return L[k] || k; }

    var types = {};
    json("ev2-types", []).forEach(function (d) { types[d.type] = d; });
    var blocks = json("ev2-blocks", []).map(function (b) {
        return { id: b.id, blockType: b.blockType, parentId: b.parentId == null ? null : b.parentId, sortOrder: b.sortOrder, dataJson: b.dataJson || "{}" };
    });

    var treeEl = document.getElementById("ev2-tree");
    var frame = document.getElementById("ev2-preview");
    var statusEl = document.getElementById("ev2-status");
    var saveBtn = document.getElementById("ev2-save");
    var undoBtn = document.getElementById("ev2-undo"), redoBtn = document.getElementById("ev2-redo");

    var selectedId = null;
    var nextNeg = -1;
    var expandKey = "ev2-open:" + pageId;
    var expanded = {};
    try { expanded = JSON.parse(sessionStorage.getItem(expandKey) || "{}") || {}; } catch (e) { expanded = {}; }

    // ---------- tree helpers ----------
    function byId(id) { for (var i = 0; i < blocks.length; i++) if (String(blocks[i].id) === String(id)) return blocks[i]; return null; }
    function kids(parentId) {
        return blocks.filter(function (b) { return String(b.parentId) === String(parentId); })
            .sort(function (a, b) { return a.sortOrder - b.sortOrder; });
    }
    function ancestors(b) { var list = []; var p = b && b.parentId != null ? byId(b.parentId) : null; while (p) { list.unshift(p); p = p.parentId != null ? byId(p.parentId) : null; } return list; }
    function descendants(id) { var out = []; kids(id).forEach(function (k) { out.push(k); out = out.concat(descendants(k.id)); }); return out; }
    function renumber(parentId) { kids(parentId).forEach(function (k, i) { k.sortOrder = i; }); }
    function def(b) { return types[b.blockType] || { type: b.blockType, name: b.blockType, svg: "", allowed: [], schema: [], childOnly: false }; }
    function isContainer(b) { return (def(b).allowed || []).length > 0; }
    function data(b) { try { return JSON.parse(b.dataJson || "{}") || {}; } catch (e) { return {}; } }
    function strip(s) { return String(s || "").replace(/<[^>]*>/g, " ").replace(/\s+/g, " ").trim(); }
    // The first meaningful text of a block, so a tree of ten "Text" nodes can be told apart.
    function summary(b) {
        var d = data(b);
        var keys = ["heading", "title", "name", "label", "buttonText", "text", "body", "quote", "subheading", "question"];
        for (var i = 0; i < keys.length; i++) { var v = strip(d[keys[i]]); if (v) return v.length > 38 ? v.slice(0, 38) + "…" : v; }
        return "";
    }
    // A column is named by its place (Spalte 1, 2 …): it has no text of its own, and three nodes all
    // called "Spalte" do not tell which one is on the left.
    function label(b) {
        var name = def(b).name;
        if (b.blockType === "el-column") name += " " + (kids(b.parentId).indexOf(b) + 1);
        return name;
    }
    function svgIcon(svg) { return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + (svg || "") + "</svg>"; }
    function esc(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }

    // ---------- history (undo / redo) ----------
    var past = [], future = [];
    var savedSnap = snap();
    var coalesceUntil = 0;
    function snap() { return JSON.stringify(blocks); }
    // Field edits arrive keystroke by keystroke; one undo step per burst of typing, not per letter.
    function record(coalesce) {
        var now = Date.now();
        if (coalesce && now < coalesceUntil) { coalesceUntil = now + 900; return; }
        past.push(lastSnap); if (past.length > 100) past.shift();
        future = [];
        coalesceUntil = coalesce ? now + 900 : 0;
    }
    var lastSnap = savedSnap;
    function committed() { lastSnap = snap(); updateChrome(); }
    function restore(s) {
        blocks = JSON.parse(s);
        lastSnap = s;
        if (selectedId != null && !byId(selectedId)) selectedId = null;
        renderTree(); renderInspector(); renderPreview(); updateChrome();
    }
    function undo() { if (!past.length) return; future.push(snap()); restore(past.pop()); }
    function redo() { if (!future.length) return; past.push(snap()); restore(future.pop()); }
    undoBtn.addEventListener("click", undo);
    redoBtn.addEventListener("click", redo);
    document.addEventListener("keydown", function (e) {
        var inField = e.target.closest && e.target.closest("input, textarea, select, [contenteditable=true]");
        if (inField || !(e.ctrlKey || e.metaKey)) return;
        var k = e.key.toLowerCase();
        if (k === "z" && !e.shiftKey) { e.preventDefault(); undo(); }
        else if (k === "y" || (k === "z" && e.shiftKey)) { e.preventDefault(); redo(); }
        else if (k === "s") { e.preventDefault(); save(); }
    });
    // Ctrl+S also inside a field — the one shortcut nobody wants the browser to answer.
    document.addEventListener("keydown", function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s" && e.target.closest && e.target.closest("input, textarea, [contenteditable=true]")) { e.preventDefault(); save(); }
    });

    function dirty() { return snap() !== savedSnap; }
    var statusText = null;
    function updateChrome() {
        undoBtn.disabled = past.length === 0;
        redoBtn.disabled = future.length === 0;
        var d = dirty();
        saveBtn.disabled = !d || saving;
        statusEl.className = "ev2-status" + (statusText && statusText.err ? " is-error" : d ? " is-dirty" : "");
        statusEl.innerHTML = statusText ? statusText.html
            : d ? '<i class="ti ti-point-filled"></i> ' + esc(t("unsaved"))
            : '<i class="ti ti-circle-check" style="color:#16a34a"></i> ' + esc(t("saved"));
    }

    // ---------- tree rendering ----------
    function renderTree() {
        var roots = kids(null);
        treeEl.innerHTML = "";
        if (!roots.length) { treeEl.innerHTML = '<div class="ev2-empty">' + esc(t("empty")) + "</div>"; return; }
        var ul = document.createElement("ul");
        ul.className = "ev2-tree";
        roots.forEach(function (b) { ul.appendChild(nodeLi(b)); });
        treeEl.appendChild(ul);
    }
    function nodeLi(b) {
        var li = document.createElement("li");
        var d = def(b), container = isContainer(b), children = kids(b.id);
        var open = container && expanded[b.id] !== false;
        if (container && !open) li.className = "is-closed";
        var node = document.createElement("div");
        node.className = "ev2-node" + (String(b.id) === String(selectedId) ? " is-sel" : "");
        node.setAttribute("role", "treeitem");
        node.setAttribute("data-id", b.id);
        node.tabIndex = 0;
        if (container) node.setAttribute("aria-expanded", open ? "true" : "false");
        node.draggable = true;
        var sum = summary(b);
        node.innerHTML =
            '<span class="tw">' + (container ? '<i class="ti ti-chevron-' + (open ? "down" : "right") + '"></i>' : "") + "</span>" +
            '<span class="ic">' + svgIcon(d.svg) + "</span>" +
            '<span class="lbl">' + esc(label(b)) + (sum ? " <em>" + esc(sum) + "</em>" : container && children.length && b.blockType !== "el-column" ? " <em>(" + children.length + ")</em>" : "") + "</span>" +
            '<span class="act">' +
            '<button type="button" data-act="up" title="' + esc(t("moveUp")) + '"><i class="ti ti-arrow-up"></i></button>' +
            '<button type="button" data-act="down" title="' + esc(t("moveDown")) + '"><i class="ti ti-arrow-down"></i></button>' +
            '<button type="button" data-act="dup" title="' + esc(t("duplicate")) + '"><i class="ti ti-copy"></i></button>' +
            '<button type="button" data-act="del" title="' + esc(t("remove")) + '"><i class="ti ti-trash"></i></button>' +
            '<i class="ti ti-grip-vertical grip" aria-hidden="true"></i></span>';
        li.appendChild(node);
        if (container) {
            var sub = document.createElement("ul");
            children.forEach(function (k) { sub.appendChild(nodeLi(k)); });
            li.appendChild(sub);
            var add = document.createElement("button");
            add.type = "button";
            add.className = "ev2-add";
            add.setAttribute("data-add-parent", b.id);
            var allowed = d.allowed || [];
            add.innerHTML = '<i class="ti ti-plus"></i> ' + esc(allowed.length === 1 && types[allowed[0]] ? types[allowed[0]].name : t("addElement"));
            li.appendChild(add);
        }
        return li;
    }

    treeEl.addEventListener("click", function (e) {
        var addBtn = e.target.closest("[data-add-parent]");
        if (addBtn) { openPicker(addBtn, Number(addBtn.getAttribute("data-add-parent")), null); return; }
        var node = e.target.closest(".ev2-node"); if (!node) return;
        var id = Number(node.getAttribute("data-id"));
        var act = e.target.closest("[data-act]");
        if (act) { e.stopPropagation(); action(act.getAttribute("data-act"), id); return; }
        if (e.target.closest(".tw") && node.hasAttribute("aria-expanded")) { toggle(id); return; }
        select(id, true);
    });
    treeEl.addEventListener("keydown", function (e) {
        var node = e.target.closest(".ev2-node"); if (!node) return;
        var id = Number(node.getAttribute("data-id"));
        var all = Array.prototype.slice.call(treeEl.querySelectorAll(".ev2-node")).filter(function (n) { return n.offsetParent !== null; });
        var i = all.indexOf(node);
        if (e.key === "ArrowDown" && all[i + 1]) { e.preventDefault(); all[i + 1].focus(); }
        else if (e.key === "ArrowUp" && all[i - 1]) { e.preventDefault(); all[i - 1].focus(); }
        else if (e.key === "Enter" || e.key === " ") { e.preventDefault(); select(id, true); }
        else if (e.key === "ArrowRight" && node.getAttribute("aria-expanded") === "false") { e.preventDefault(); toggle(id); }
        else if (e.key === "ArrowLeft" && node.getAttribute("aria-expanded") === "true") { e.preventDefault(); toggle(id); }
        else if (e.key === "Delete") { e.preventDefault(); action("del", id); }
    });
    function toggle(id) {
        expanded[id] = expanded[id] === false;
        try { sessionStorage.setItem(expandKey, JSON.stringify(expanded)); } catch (e) { }
        renderTree();
        var n = treeEl.querySelector('.ev2-node[data-id="' + id + '"]'); if (n) n.focus();
    }

    // ---------- drag & drop, across levels ----------
    // A block may go before/after any other block, or INTO a container (dropped on the middle of its
    // row) — wherever the new parent accepts that block type (AllowedChildren; the page itself takes
    // everything that is not child-only), and never into itself or its own descendants.
    var dragId = null, dropAt = null;
    function accepts(parentId, type) {
        if (parentId == null) return !(types[type] || {}).childOnly;
        var p = byId(parentId); return !!p && (def(p).allowed || []).indexOf(type) >= 0;
    }
    function inside(id, ancestorId) { var b = byId(id); while (b) { if (String(b.id) === String(ancestorId)) return true; b = b.parentId != null ? byId(b.parentId) : null; } return false; }
    treeEl.addEventListener("dragstart", function (e) {
        var n = e.target.closest(".ev2-node"); if (!n) return;
        dragId = Number(n.getAttribute("data-id"));
        n.classList.add("is-dragging");
        e.dataTransfer.effectAllowed = "move";
        try { e.dataTransfer.setData("text/plain", String(dragId)); } catch (x) { }
    });
    treeEl.addEventListener("dragend", function () { dragId = null; dropAt = null; clearDrop(); renderTree(); });
    function clearDrop() { treeEl.querySelectorAll(".is-drop-before,.is-drop-after,.is-drop-into,.is-dragging").forEach(function (n) { n.classList.remove("is-drop-before", "is-drop-after", "is-drop-into", "is-dragging"); }); }
    treeEl.addEventListener("dragover", function (e) {
        if (dragId == null) return;
        var n = e.target.closest(".ev2-node"); if (!n) return;
        var target = byId(n.getAttribute("data-id")), src = byId(dragId);
        if (!target || !src || target.id === src.id || inside(target.id, src.id)) { dropAt = null; clearDrop(); return; }
        var r = n.getBoundingClientRect(), y = (e.clientY - r.top) / r.height;
        var into = isContainer(target) && y > .3 && y < .7 && accepts(target.id, src.blockType);
        var zone = into ? "into" : y < .5 ? "before" : "after";
        if (!into && !accepts(target.parentId, src.blockType)) {
            // Not here as a sibling — maybe still into it, whatever the height.
            if (isContainer(target) && accepts(target.id, src.blockType)) zone = "into"; else { dropAt = null; clearDrop(); return; }
        }
        e.preventDefault();
        dropAt = { target: target.id, zone: zone };
        clearDrop();
        n.classList.add("is-drop-" + zone);
    });
    treeEl.addEventListener("drop", function (e) {
        if (dragId == null || !dropAt) return;
        e.preventDefault();
        var src = byId(dragId), target = byId(dropAt.target); if (!src || !target) return;
        record();
        var oldParent = src.parentId;
        var newParent = dropAt.zone === "into" ? target.id : target.parentId;
        var sibs = kids(newParent).filter(function (k) { return k.id !== src.id; });
        var at = dropAt.zone === "into" ? sibs.length : sibs.indexOf(target) + (dropAt.zone === "after" ? 1 : 0);
        src.parentId = newParent;
        sibs.splice(at, 0, src);
        sibs.forEach(function (k, i) { k.sortOrder = i; });
        if (String(oldParent) !== String(newParent)) renumber(oldParent);
        if (newParent != null) expanded[newParent] = true;
        committed();
        renderTree(); renderInspector(); renderPreview();
    });

    // ---------- actions ----------
    function action(act, id) {
        var b = byId(id); if (!b) return;
        if (act === "up" || act === "down") {
            var sibs = kids(b.parentId), i = sibs.indexOf(b), j = act === "up" ? i - 1 : i + 1;
            if (j < 0 || j >= sibs.length) return;
            record();
            sibs.splice(j, 0, sibs.splice(i, 1)[0]);
            sibs.forEach(function (k, n) { k.sortOrder = n; });
        } else if (act === "dup") {
            record();
            var copyRoot = clone(b, b.parentId);
            kids(b.parentId).forEach(function (k) { if (k.sortOrder > b.sortOrder && k.id !== copyRoot.id) k.sortOrder++; });
            copyRoot.sortOrder = b.sortOrder + 1;
            selectedId = copyRoot.id;
        } else if (act === "del") {
            var name = def(b).name + (summary(b) ? " „" + summary(b) + "“" : "");
            if (!window.confirm(t("confirmRemove").replace("{0}", name))) return;
            record();
            var gone = [b.id].concat(descendants(b.id).map(function (x) { return x.id; }));
            blocks = blocks.filter(function (x) { return gone.indexOf(x.id) < 0; });
            renumber(b.parentId);
            if (gone.indexOf(selectedId) >= 0) selectedId = b.parentId;
        }
        committed();
        renderTree(); renderInspector(); renderPreview();
    }
    // Deep copy with fresh negative ids; children follow their copied parent.
    function clone(b, parentId) {
        var c = { id: nextNeg--, blockType: b.blockType, parentId: parentId, sortOrder: b.sortOrder, dataJson: b.dataJson };
        blocks.push(c);
        kids(b.id).forEach(function (k) { if (k.id !== c.id) clone(k, c.id); });
        return c;
    }
    // ---------- new blocks ----------
    // A new block starts with its fields' defaults and — for the elements — a word or two of starter
    // text: an empty heading renders nothing, and an element nobody can see in the preview is an
    // element nobody can click.
    var STARTERS = {
        "el-heading": { text: t("starter.heading") },
        "el-text": { body: "<p>" + esc(t("starter.text")) + "</p>" },
        "el-button": { text: t("starter.button"), url: "#" }
    };
    function initialData(type) {
        var d = {};
        ((types[type] || {}).schema || []).forEach(function (f) { if (f["default"] != null && f["default"] !== "") d[f.id] = f["default"]; });
        var st = STARTERS[type]; if (st) Object.keys(st).forEach(function (k) { d[k] = st[k]; });
        return d;
    }
    // What a container should come with: buttons with one button, columns with as many columns as
    // their split has.
    function COLS(layout) { return String(layout || "50-50").split("-").length; }
    function withKids(type, data) {
        if (type === "el-buttons") return [{ type: "el-button" }];
        if (type === "el-columns") { var n = COLS(data.layout), out = []; for (var i = 0; i < n; i++) out.push({ type: "el-column" }); return out; }
        return [];
    }
    // Builds a tree of new blocks from a spec { type, data, children } (negative ids) and returns its root.
    function build(spec, parentId) {
        var data = Object.assign(initialData(spec.type), spec.data || {});
        var b = { id: nextNeg--, blockType: spec.type, parentId: parentId, sortOrder: 0, dataJson: JSON.stringify(data) };
        blocks.push(b);
        (spec.children || withKids(spec.type, data)).forEach(function (c, i) { var k = build(c, b.id); k.sortOrder = i; });
        if (isContainer(b)) expanded[b.id] = true;
        return b;
    }
    function insert(spec, parentId, index) {
        record();
        var sibs = kids(parentId);
        var b = build(spec, parentId);
        var at = index == null || index > sibs.length ? sibs.length : index;
        sibs.splice(at, 0, b);
        sibs.forEach(function (k, i) { k.sortOrder = i; });
        if (parentId != null) expanded[parentId] = true;
        committed();
        select(b.id, true);
        renderPreview();
    }
    function addBlock(type, parentId, index) { insert({ type: type }, parentId, index); }

    // Ready-made sections out of elements — the hero the mockup showed, put together in one click.
    var PRESETS = {
        hero: function () { return { type: "el-section", data: { align: "center", pad: "l", label: t("preset.hero") }, children: [
            { type: "el-image", data: { size: "xs", shape: "circle" } },
            { type: "el-heading", data: { level: "h1", size: "xl", text: t("starter.heading") } },
            { type: "el-text", data: { size: "l", measure: "read" } },
            { type: "el-buttons", children: [{ type: "el-button" }, { type: "el-button", data: { text: t("starter.button2"), style: "outline" } }] }] }; },
        imageText: function () { return { type: "el-columns", data: { layout: "50-50", valign: "center" }, children: [
            { type: "el-column", children: [{ type: "el-image", data: { shape: "rounded" } }] },
            { type: "el-column", children: [{ type: "el-heading" }, { type: "el-text" }, { type: "el-buttons" }] }] }; },
        text: function () { return { type: "el-section", data: { width: "narrow", label: t("preset.text") }, children: [{ type: "el-heading" }, { type: "el-text" }] }; },
        threeCols: function () {
            var col = function () { return { type: "el-column", children: [{ type: "el-image", data: { shape: "rounded" } }, { type: "el-heading", data: { level: "h3" } }, { type: "el-text" }] }; };
            return { type: "el-columns", data: { layout: "33-33-33" }, children: [col(), col(), col()] };
        }
    };
    var PRESET_ICON = {
        hero: '<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="12" cy="9" r="2"/><path d="M8 14h8"/><path d="M9 17h6"/>',
        imageText: '<rect x="3" y="5" width="8" height="14" rx="1"/><path d="M14 8h7"/><path d="M14 12h7"/><path d="M14 16h4"/>',
        text: '<path d="M6 6h12"/><path d="M4 11h16"/><path d="M4 15h16"/><path d="M4 19h10"/>',
        threeCols: '<rect x="2" y="5" width="5.5" height="14" rx="1"/><rect x="9.25" y="5" width="5.5" height="14" rx="1"/><rect x="16.5" y="5" width="5.5" height="14" rx="1"/>'
    };
    function presetOptions() {
        return Object.keys(PRESETS).map(function (k) { return { type: "preset:" + k, name: t("preset." + k), desc: t("preset." + k + ".desc"), cat: "presets", svg: PRESET_ICON[k] }; });
    }
    document.getElementById("ev2-dup").addEventListener("click", function () { if (selectedId != null) action("dup", selectedId); });
    document.getElementById("ev2-del").addEventListener("click", function () { if (selectedId != null) action("del", selectedId); });

    // ---------- picker ----------
    var pop = document.getElementById("ev2-pop"), popSearch = document.getElementById("ev2-pop-search");
    var popList = document.getElementById("ev2-pop-list"), popCats = document.getElementById("ev2-pop-cats");
    var pick = null;   // { parentId, index, options:[def] }
    var popCat = "all";
    function openPicker(anchor, parentId, index) {
        var options;
        if (parentId == null) {
            options = presetOptions().concat(Object.keys(types).map(function (k) { return types[k]; }).filter(function (d) { return !d.childOnly; }));
        } else {
            var p = byId(parentId);
            options = (def(p).allowed || []).map(function (k) { return types[k]; }).filter(Boolean);
            // A container that accepts exactly one kind needs no choice at all.
            if (options.length === 1) { addBlock(options[0].type, parentId, index); return; }
        }
        pick = { parentId: parentId, index: index, options: options };
        popCat = "all";
        var cats = [];
        options.forEach(function (d) { if (cats.indexOf(d.cat) < 0) cats.push(d.cat); });
        popCats.innerHTML = cats.length > 1 ? ['<button type="button" data-cat="all" class="is-on">' + esc(t("all")) + "</button>"].concat(cats.map(function (c) {
            return '<button type="button" data-cat="' + esc(c) + '">' + esc(t("cat." + c)) + "</button>";
        })).join("") : "";
        popSearch.value = "";
        fillPicker();
        pop.hidden = false;
        var r = anchor ? anchor.getBoundingClientRect() : { left: innerWidth / 2 - 170, bottom: 120 };
        pop.style.left = Math.max(8, Math.min(r.left, innerWidth - 352)) + "px";
        pop.style.top = Math.max(8, Math.min(r.bottom + 6, innerHeight - pop.offsetHeight - 8)) + "px";
        popSearch.focus();
    }
    function fillPicker() {
        var q = popSearch.value.trim().toLowerCase();
        var list = pick.options.filter(function (d) {
            return (popCat === "all" || d.cat === popCat) && (!q || (d.name + " " + (d.desc || "")).toLowerCase().indexOf(q) >= 0);
        });
        popList.innerHTML = list.length ? list.map(function (d) {
            return '<button type="button" data-type="' + esc(d.type) + '" title="' + esc(d.desc || "") + '">' + svgIcon(d.svg) + "<span>" + esc(d.name) + "</span></button>";
        }).join("") : '<div class="none">' + esc(t("notFound")) + "</div>";
    }
    popSearch.addEventListener("input", fillPicker);
    popSearch.addEventListener("keydown", function (e) {
        if (e.key === "Enter") { var first = popList.querySelector("button"); if (first) first.click(); }
        if (e.key === "Escape") closePicker();
    });
    popCats.addEventListener("click", function (e) {
        var b = e.target.closest("button"); if (!b) return;
        popCat = b.getAttribute("data-cat");
        popCats.querySelectorAll("button").forEach(function (x) { x.classList.toggle("is-on", x === b); });
        fillPicker();
    });
    popList.addEventListener("click", function (e) {
        var b = e.target.closest("button[data-type]"); if (!b || !pick) return;
        var p = pick; closePicker();
        var type = b.getAttribute("data-type");
        if (type.indexOf("preset:") === 0) insert(PRESETS[type.slice(7)](), p.parentId, p.index);
        else addBlock(type, p.parentId, p.index);
    });
    function closePicker() { pop.hidden = true; pick = null; }
    document.addEventListener("mousedown", function (e) {
        if (!pop.hidden && !pop.contains(e.target) && !e.target.closest("[data-add-parent], #ev2-add-root, [data-add-here]")) closePicker();
    });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape" && !pop.hidden) closePicker(); });
    document.getElementById("ev2-add-root").addEventListener("click", function (e) { openPicker(e.currentTarget, null, null); });

    // ---------- selection + inspector ----------
    var insp = { crumb: document.getElementById("ev2-crumb"), title: document.getElementById("ev2-insp-title"),
                 tabs: document.getElementById("ev2-tabs"), body: document.getElementById("ev2-body"), foot: document.getElementById("ev2-foot") };
    var panels = null;   // field builders of the open block
    var activeTab = "content";

    function select(id, scroll) {
        selectedId = id;
        var b = byId(id);
        if (b) ancestors(b).forEach(function (a) { expanded[a.id] = true; });
        renderTree();
        renderInspector();
        body.classList.toggle("ev2-insp-open", !!b);
        var n = treeEl.querySelector('.ev2-node[data-id="' + id + '"]');
        if (n && scroll) n.scrollIntoView({ block: "nearest" });
        syncPreviewSelection();
    }

    function renderInspector() {
        var b = selectedId != null ? byId(selectedId) : null;
        insp.tabs.innerHTML = "";
        panels = null;
        if (!b) {
            insp.crumb.innerHTML = "";
            insp.title.innerHTML = "";
            insp.body.innerHTML = '<p class="muted-box">' + esc(t("noSelection")) + "</p>";
            insp.foot.hidden = true;
            return;
        }
        var d = def(b);
        insp.foot.hidden = false;
        insp.crumb.innerHTML = ['<button type="button" data-go="">' + esc(t("page")) + "</button>"].concat(ancestors(b).map(function (a) {
            return '<button type="button" data-go="' + a.id + '">' + esc(def(a).name) + "</button>";
        })).join(' <i class="ti ti-chevron-right"></i> ');
        insp.title.innerHTML = '<span class="ic">' + svgIcon(d.svg) + "</span>" + esc(d.name);

        // Fields are split into tabs by what they are about: the block's own content, its layout
        // (width/spacing of every top-level block) and the expert corner (custom CSS).
        var schema = d.schema || [];
        var groups = {
            content: schema.filter(function (f) { return f.id.charAt(0) !== "_"; }),
            design: schema.filter(function (f) { return f.id === "_width" || f.id === "_spaceTop" || f.id === "_spaceBottom"; }),
            advanced: schema.filter(function (f) { return f.id === "_css"; })
        };
        var values = data(b);
        insp.body.innerHTML = "";
        var kidsBox = isContainer(b) ? childList(b) : null;
        panels = {};
        var names = { content: t("tabContent"), design: t("tabDesign"), advanced: t("tabAdvanced") };
        var shown = Object.keys(groups).filter(function (k) { return groups[k].length || (k === "content" && kidsBox); });
        if (shown.indexOf(activeTab) < 0) activeTab = shown[0] || "content";
        shown.forEach(function (k) {
            var panel = document.createElement("div");
            panel.className = "ev2-panel";
            panel.hidden = k !== activeTab;
            if (k === "content" && kidsBox) panel.appendChild(kidsBox);
            var holder = document.createElement("div");
            panel.appendChild(holder);
            panels[k] = window.MatBlockFields.build(holder, groups[k], values);
            insp.body.appendChild(panel);
            if (shown.length > 1) {
                var tb = document.createElement("button");
                tb.type = "button";
                tb.textContent = names[k];
                tb.className = k === activeTab ? "is-on" : "";
                tb.setAttribute("data-tab", k);
                insp.tabs.appendChild(tb);
            }
        });
        if (!schema.length && !kidsBox) insp.body.innerHTML = '<p class="muted-box">' + esc(t("noFields")) + "</p>";
    }
    // A container lists what it holds — the same children as the tree, one click away.
    function childList(b) {
        var wrap = document.createElement("div");
        var head = document.createElement("div");
        head.className = "ev2-kids-head";
        head.textContent = def(b).name;
        var box = document.createElement("div");
        box.className = "ev2-kids";
        kids(b.id).forEach(function (k) {
            var btn = document.createElement("button");
            btn.type = "button";
            btn.setAttribute("data-go", k.id);
            btn.innerHTML = svgIcon(def(k).svg) + "<span>" + esc(label(k)) + (summary(k) ? ' <span style="color:#9aa1ab">' + esc(summary(k)) + "</span>" : "") + '</span><i class="ti ti-chevron-right chev"></i>';
            box.appendChild(btn);
        });
        var add = document.createElement("button");
        add.type = "button";
        add.className = "add";
        add.setAttribute("data-add-here", b.id);
        add.innerHTML = '<i class="ti ti-plus"></i> ' + esc(t("addElement"));
        box.appendChild(add);
        wrap.appendChild(box);
        return wrap;
    }
    insp.tabs.addEventListener("click", function (e) {
        var b = e.target.closest("[data-tab]"); if (!b) return;
        activeTab = b.getAttribute("data-tab");
        insp.tabs.querySelectorAll("button").forEach(function (x) { x.classList.toggle("is-on", x === b); });
        var i = 0;
        insp.body.querySelectorAll(".ev2-panel").forEach(function (p) {
            var key = Object.keys(panels)[i++];
            p.hidden = key !== activeTab;
        });
    });
    document.querySelector(".ev2-insp").addEventListener("click", function (e) {
        var go = e.target.closest("[data-go]");
        if (go) { var v = go.getAttribute("data-go"); if (v === "") { selectedId = null; renderTree(); renderInspector(); syncPreviewSelection(); } else select(Number(v), true); return; }
        var here = e.target.closest("[data-add-here]");
        if (here) openPicker(here, Number(here.getAttribute("data-add-here")), null);
    });

    // Every keystroke writes the field values back into the draft and redraws the preview.
    var editT;
    function onFieldEdit() {
        clearTimeout(editT);
        editT = setTimeout(function () {
            var b = byId(selectedId); if (!b || !panels) return;
            var merged = data(b);
            Object.keys(panels).forEach(function (k) { var v = panels[k].serialize(); Object.keys(v).forEach(function (f) { merged[f] = v[f]; }); });
            var next = JSON.stringify(merged);
            if (next === b.dataJson) return;
            record(true);
            b.dataJson = next;
            // A wider split needs more columns: the missing ones are added, extra ones are kept —
            // they may hold content, and nothing is thrown away by a dropdown.
            if (b.blockType === "el-columns") {
                var have = kids(b.id).length, want = COLS(merged.layout);
                for (var c = have; c < want; c++) { var col = build({ type: "el-column" }, b.id); col.sortOrder = c; }
                if (want > have) { renderTree(); renderInspector(); }
            }
            committed();
            refreshNodeLabel(b);
            renderPreview();
        }, 200);
    }
    insp.body.addEventListener("input", onFieldEdit);
    insp.body.addEventListener("change", onFieldEdit);
    function refreshNodeLabel(b) {
        var n = treeEl.querySelector('.ev2-node[data-id="' + b.id + '"] .lbl');
        if (!n) return;
        var s = summary(b);
        n.innerHTML = esc(label(b)) + (s ? " <em>" + esc(s) + "</em>" : "");
    }

    // ---------- preview ----------
    function post(msg) { if (frame.contentWindow) frame.contentWindow.postMessage(msg, "*"); }
    function previewTarget() {
        var b = selectedId != null ? byId(selectedId) : null;
        while (b && b.parentId != null) b = byId(b.parentId);
        return b ? b.id : null;
    }
    // The block itself when the preview marks it (top level, or a child of a container that renders
    // its children through _ChildBlock); its top-level block otherwise.
    function syncPreviewSelection() { var top = previewTarget(); if (top != null) post({ type: "mat-select", id: String(selectedId), fallback: String(top) }); }
    var renderT, renderSeq = 0;
    function renderPreview() {
        clearTimeout(renderT);
        renderT = setTimeout(function () {
            var seq = ++renderSeq;
            var form = new URLSearchParams();
            form.set("__RequestVerificationToken", token);
            form.set("Draft", JSON.stringify(blocks));
            fetch(renderUrl, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded", "RequestVerificationToken": token }, body: form.toString(), credentials: "same-origin" })
                .then(function (r) { return r.ok ? r.text() : null; })
                .then(function (html) {
                    if (html == null || seq !== renderSeq) return;   // a newer render is on its way
                    post({ type: "mat-render", html: html });
                    syncPreviewSelection();
                })
                .catch(function () { });
        }, 120);
    }
    // The frame reloads (first load, or the site navigated it): show the draft again, not the saved page.
    frame.addEventListener("load", function () { if (dirty()) renderPreview(); else syncPreviewSelection(); });
    window.addEventListener("message", function (e) {
        if (e.source !== frame.contentWindow) return;
        var d = e.data || {};
        if (d.type === "mat-preview-ready") { if (dirty()) renderPreview(); else syncPreviewSelection(); }
        if (d.type === "mat-select-block" && d.id) select(Number(d.id), true);
        if (d.type === "mat-insert-at") openPicker(null, null, d.index);
    });
    document.getElementById("ev2-dev").addEventListener("click", function (e) {
        var b = e.target.closest("button"); if (!b) return;
        this.querySelectorAll("button").forEach(function (x) { x.classList.toggle("is-on", x === b); });
        var dev = b.getAttribute("data-dev");
        document.getElementById("ev2-frame").className = "ev2-frame" + (dev === "desktop" ? "" : " is-" + dev);
    });

    // ---------- save ----------
    var saving = false;
    function save() {
        if (saving || !dirty()) return;
        saving = true;
        statusText = { html: '<i class="ti ti-loader-2"></i> ' + esc(t("saving")) };
        updateChrome();
        var form = new URLSearchParams();
        form.set("__RequestVerificationToken", token);
        form.set("Draft", JSON.stringify(blocks));
        fetch(saveUrl, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded", "RequestVerificationToken": token }, body: form.toString(), credentials: "same-origin" })
            .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
            .then(function (res) {
                if (!res || !res.ok) throw new Error("save");
                // Renumber the draft to the ids the database gave the new blocks. History steps keep
                // the old ids, so undoing past a save would bring back blocks with stale negative
                // ids — the history starts afresh from the saved state instead.
                var map = res.ids || {};
                blocks.forEach(function (b) {
                    if (map[b.id] != null) b.id = map[b.id];
                });
                blocks.forEach(function (b) { if (b.parentId != null && map[b.parentId] != null) b.parentId = map[b.parentId]; });
                if (selectedId != null && map[selectedId] != null) selectedId = map[selectedId];
                Object.keys(expanded).forEach(function (k) { if (map[k] != null) { expanded[map[k]] = expanded[k]; } });
                savedSnap = lastSnap = snap();
                past = []; future = [];
                statusText = null;
                renderTree(); renderInspector();
                // The saved page is now what the site serves; reload so the frame shows exactly that.
                frame.contentWindow && frame.contentWindow.location.reload();
            })
            .catch(function () { statusText = { html: '<i class="ti ti-alert-triangle"></i> ' + esc(t("saveFailed")), err: true }; })
            .finally(function () { saving = false; updateChrome(); if (statusText && statusText.err) setTimeout(function () { statusText = null; updateChrome(); }, 5000); });
    }
    saveBtn.addEventListener("click", save);

    // ---------- leaving ----------
    // One question, ours: once the operator agreed to leave, the browser's own beforeunload prompt
    // must not ask the same thing a second time.
    var leaving = false;
    window.addEventListener("beforeunload", function (e) { if (dirty() && !leaving) { e.preventDefault(); e.returnValue = ""; } });
    function okToLeave() { if (!dirty() || window.confirm(t("confirmLeave"))) { leaving = true; return true; } return false; }
    document.addEventListener("click", function (e) {
        // Only the editor's own ways out (top bar, tree column, page/language lists) — not links inside
        // a field, the media picker or a dialog.
        var a = e.target.closest("a[href]"); if (!a || a.target === "_blank" || !a.closest(".ev2-top, .ev2-nav, .ev2-pages, .ev2-langs")) return;
        if (!okToLeave()) e.preventDefault();
    });
    document.querySelectorAll(".ev2-langs-form").forEach(function (f) {
        f.addEventListener("submit", function (e) {
            if (!okToLeave()) { e.preventDefault(); return; }
            var c = f.getAttribute("data-confirm");
            if (c && !window.confirm(c)) { leaving = false; e.preventDefault(); }
        });
    });

    // ---------- popovers in the title: page switcher, language versions ----------
    function popover(btnId, popId, onOpen) {
        var btn = document.getElementById(btnId), pop = document.getElementById(popId);
        if (!btn || !pop) return;
        btn.addEventListener("click", function (e) {
            e.stopPropagation();
            var open = pop.hidden;
            document.querySelectorAll(".ev2-pages, .ev2-langs").forEach(function (p) { p.hidden = true; });
            if (!open) return;
            pop.hidden = false;
            var r = btn.getBoundingClientRect();
            pop.style.left = Math.max(8, Math.min(r.left, innerWidth - pop.offsetWidth - 8)) + "px";
            pop.style.top = (r.bottom + 6) + "px";
            if (onOpen) onOpen(pop);
        });
        document.addEventListener("mousedown", function (e) { if (!pop.hidden && !pop.contains(e.target) && !btn.contains(e.target)) pop.hidden = true; });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") pop.hidden = true; });
    }
    popover("ev2-pages-open", "ev2-pages", function () {
        var q = document.getElementById("ev2-pages-search"); q.value = ""; filterPages(); q.focus();
        var cur = document.querySelector("#ev2-pages-list .is-cur"); if (cur) cur.scrollIntoView({ block: "nearest" });
    });
    popover("ev2-langs-open", "ev2-langs");
    function filterPages() {
        var q = document.getElementById("ev2-pages-search").value.trim().toLowerCase();
        document.querySelectorAll("#ev2-pages-list .ev2-page").forEach(function (a) { a.hidden = q && a.getAttribute("data-search").indexOf(q) < 0; });
    }
    var ps = document.getElementById("ev2-pages-search");
    if (ps) {
        ps.addEventListener("input", filterPages);
        ps.addEventListener("keydown", function (e) { if (e.key === "Enter") { var first = document.querySelector("#ev2-pages-list .ev2-page:not([hidden])"); if (first) first.click(); } });
    }

    // ---------- KI ----------
    // Both work on the DRAFT: a proposal is applied into it like any other edit — undoable, and only
    // saved with "Speichern". The server never writes anything here.
    function aiPost(url, fields) {
        var form = new URLSearchParams();
        form.set("__RequestVerificationToken", token);
        Object.keys(fields).forEach(function (k) { form.set(k, fields[k]); });
        return fetch(url, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded", "RequestVerificationToken": token }, body: form.toString(), credentials: "same-origin" })
            .then(function (r) { return r.json(); });
    }
    function dialog(id) {
        var d = document.getElementById(id); if (!d) return null;
        d.querySelectorAll("[data-close]").forEach(function (b) { b.addEventListener("click", function () { d.close(); }); });
        return d;
    }
    var aiBlock = dialog("ev2-aiblock"), aiBlockProposal = null;
    if (aiBlock) {
        var aiBlockStatus = document.getElementById("ev2-aiblock-status"), aiBlockDiff = document.getElementById("ev2-aiblock-diff"), aiBlockApply = document.getElementById("ev2-aiblock-apply");
        document.getElementById("ev2-aiblock-open").addEventListener("click", function () {
            if (selectedId == null) return;
            aiBlockProposal = null; aiBlockDiff.innerHTML = ""; aiBlockStatus.textContent = ""; aiBlockApply.disabled = true;
            aiBlock.showModal();
        });
        document.getElementById("ev2-aiblock-go").addEventListener("click", function () {
            var b = byId(selectedId); if (!b) return;
            aiBlockStatus.textContent = t("aiWorking"); aiBlockApply.disabled = true; aiBlockDiff.innerHTML = "";
            var forId = b.id;
            aiPost(aiBlock.getAttribute("data-url"), { instruction: document.getElementById("ev2-aiblock-instr").value, dataJson: b.dataJson })
                .then(function (res) {
                    if (!res.ok) { aiBlockStatus.textContent = res.error || "—"; return; }
                    aiBlockStatus.textContent = "";
                    aiBlockProposal = { id: forId, data: res.proposed };
                    aiBlockDiff.innerHTML = (res.changes || []).map(function (c) {
                        return '<div class="ev2-ai-change"><div class="k">' + esc(c.field) + '</div><div class="b"><span>' + esc(t("aiBefore")) + '</span>' + esc(strip(c.before)) +
                            '</div><div class="a"><span>' + esc(t("aiAfter")) + '</span>' + esc(strip(c.after)) + "</div></div>";
                    }).join("");
                    aiBlockApply.disabled = false;
                })
                .catch(function () { aiBlockStatus.textContent = t("saveFailed"); });
        });
        aiBlockApply.addEventListener("click", function () {
            var b = aiBlockProposal && byId(aiBlockProposal.id); if (!b) return;
            record();
            b.dataJson = aiBlockProposal.data;
            committed();
            aiBlock.close();
            renderTree(); renderInspector(); renderPreview();
        });
    }
    var aiPage = dialog("ev2-aipage"), aiPageProposal = null;
    if (aiPage) {
        var aiPageStatus = document.getElementById("ev2-aipage-status"), aiPageList = document.getElementById("ev2-aipage-list"), aiPageApply = document.getElementById("ev2-aipage-apply");
        document.getElementById("ev2-aipage-open").addEventListener("click", function () {
            aiPageProposal = null; aiPageList.innerHTML = ""; aiPageStatus.textContent = ""; aiPageApply.disabled = true;
            aiPage.showModal();
        });
        document.getElementById("ev2-aipage-go").addEventListener("click", function () {
            aiPageStatus.textContent = t("aiWorking"); aiPageApply.disabled = true; aiPageList.innerHTML = "";
            aiPost(aiPage.getAttribute("data-url"), { instruction: document.getElementById("ev2-aipage-instr").value })
                .then(function (res) {
                    if (!res.ok) { aiPageStatus.textContent = res.error || "—"; return; }
                    aiPageStatus.textContent = "";
                    try { aiPageProposal = JSON.parse(res.proposed); } catch (x) { aiPageProposal = null; }
                    aiPageList.innerHTML = (res.blocks || []).map(function (b) { return "<li><b>" + esc(b.name) + "</b> " + esc(b.snippet || "") + "</li>"; }).join("");
                    aiPageApply.disabled = !aiPageProposal || !aiPageProposal.length;
                })
                .catch(function () { aiPageStatus.textContent = t("saveFailed"); });
        });
        aiPageApply.addEventListener("click", function () {
            if (!aiPageProposal) return;
            record();
            var first = null;
            aiPageProposal.forEach(function (p) {
                if (!types[p.type]) return;
                var b = { id: nextNeg--, blockType: p.type, parentId: null, sortOrder: kids(null).length, dataJson: JSON.stringify(p.data || {}) };
                blocks.push(b);
                if (first == null) first = b.id;
            });
            committed();
            aiPage.close();
            if (first != null) select(first, true); else renderTree();
            renderPreview();
        });
    }

    // ---------- page settings dialog ----------
    var dlg = document.getElementById("ev2-settings");
    document.getElementById("ev2-settings-open").addEventListener("click", function () {
        // Saving the settings reloads the editor — unsaved blocks would be lost, so say so up front.
        document.getElementById("ev2-settings-dirty").hidden = !dirty();
        dlg.showModal();
    });
    document.getElementById("ev2-settings-close").addEventListener("click", function () { dlg.close(); });
    document.getElementById("ev2-settings-form").addEventListener("submit", function (e) {
        if (!okToLeave()) e.preventDefault();   // saving the settings reloads the editor
    });

    // ---------- small screens: tree and inspector as sheets ----------
    document.getElementById("ev2-tree-toggle").addEventListener("click", function () { body.classList.toggle("ev2-nav-open"); });
    document.getElementById("ev2-insp-close").addEventListener("click", function () { body.classList.remove("ev2-insp-open"); });
    // Choosing a block in the tree on a phone hands the screen to its fields.
    treeEl.addEventListener("click", function (e) { if (e.target.closest(".ev2-node") && !e.target.closest("[data-act], .tw")) body.classList.remove("ev2-nav-open"); });

    // ---------- start ----------
    renderTree();
    renderInspector();
    updateChrome();
})();
