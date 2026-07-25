// Visual form builder: add-element modal, preview<->sidebar linking, and the
// schema-like settings form for a single element (serialized into #ElementJson on submit).
(function () {
    "use strict";

    var INPUT_TYPES = ["text", "date", "number", "phone", "email", "select"];
    var CHILD_TYPES = ["title", "description", "text", "date", "number", "phone", "email", "select"];
    var TYPE_LABELS = {
        title: "Überschrift", description: "Beschreibung", text: "Textfeld",
        date: "Datum", number: "Zahl", phone: "Telefon", email: "E-Mail",
        select: "Auswahl", group: "Gruppe"
    };
    var OP_LABELS = { eq: "ist gleich", neq: "ist ungleich", contains: "enthält", filled: "ist ausgefüllt" };

    function isInput(t) { return INPUT_TYPES.indexOf(t) >= 0; }
    function safeParse(txt, fb) { try { return JSON.parse(txt); } catch (e) { return fb; } }

    // ---------- Add-element modal ----------
    var modal = document.getElementById("add-element-modal");
    var openBtn = document.getElementById("add-element-btn");
    var closeBtn = document.getElementById("add-element-close");
    if (modal && openBtn) {
        openBtn.addEventListener("click", function () { modal.classList.add("open"); });
        if (closeBtn) closeBtn.addEventListener("click", function () { modal.classList.remove("open"); });
        modal.addEventListener("click", function (e) { if (e.target === modal) modal.classList.remove("open"); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") modal.classList.remove("open"); });
    }

    // ---------- Preview linking ----------
    var frame = document.getElementById("preview-frame");
    function postFrame(msg) { if (frame && frame.contentWindow) frame.contentWindow.postMessage(msg, "*"); }
    Array.prototype.slice.call(document.querySelectorAll(".block-item[data-element-id]")).forEach(function (row) {
        var id = row.getAttribute("data-element-id");
        row.addEventListener("mouseenter", function () { postFrame({ type: "mat-scroll-element", id: id }); });
    });
    window.addEventListener("message", function (e) {
        var d = e.data || {};
        if (d.type === "mat-select-element" && d.id) {
            window.location.search = "?element=" + encodeURIComponent(d.id);
        }
    });

    // ---------- Element settings form ----------
    var editor = document.getElementById("element-editor");
    var form = document.getElementById("element-form");
    var output = document.getElementById("ElementJson");
    if (!editor || !form || !output) return;

    var el = safeParse(document.getElementById("element-data").textContent, {}) || {};
    var available = safeParse((document.getElementById("available-fields") || {}).textContent, []) || [];

    var read = buildElementForm(editor, el, { isChild: false, available: available });

    form.addEventListener("submit", function () {
        output.value = JSON.stringify(read());
    });

    // -----------------------------------------------------------------
    // Builds the settings UI for one element and returns a read() closure.
    // -----------------------------------------------------------------
    function buildElementForm(container, data, opts) {
        data = data || {};
        var isChild = !!opts.isChild;
        var fixedType = data.type || "text";

        var typeGetter = function () { return fixedType; };

        // Child elements can change their type; top-level elements cannot.
        if (isChild) {
            var typeWrap = fieldWrap("Typ");
            var typeSel = document.createElement("select");
            CHILD_TYPES.forEach(function (t) {
                var o = document.createElement("option");
                o.value = t; o.textContent = TYPE_LABELS[t];
                if (t === fixedType) o.selected = true;
                typeSel.appendChild(o);
            });
            typeWrap.appendChild(typeSel);
            container.appendChild(typeWrap);
            typeGetter = function () { return typeSel.value; };
            typeSel.addEventListener("change", refresh);
        }

        // Label
        var labelWrap = fieldWrap(labelCaption(fixedType));
        var labelInput = document.createElement("input");
        labelInput.type = "text";
        labelInput.value = data.label || "";
        labelWrap.appendChild(labelInput);
        container.appendChild(labelWrap);
        if (isChild) typeSel && typeSel.addEventListener("change", function () { labelWrap.querySelector("label").textContent = labelCaption(typeGetter()); });

        // Placeholder
        var phWrap = fieldWrap("Platzhalter");
        var phInput = document.createElement("input");
        phInput.type = "text"; phInput.value = data.placeholder || "";
        phWrap.appendChild(phInput);
        container.appendChild(phWrap);

        // Help
        var helpWrap = fieldWrap("Hilfetext");
        var helpInput = document.createElement("input");
        helpInput.type = "text"; helpInput.value = data.help || "";
        helpWrap.appendChild(helpInput);
        container.appendChild(helpWrap);

        // Required
        var reqWrap = document.createElement("div"); reqWrap.className = "field";
        var reqLabel = document.createElement("label"); reqLabel.className = "check";
        var reqInput = document.createElement("input"); reqInput.type = "checkbox"; reqInput.checked = !!data.required;
        reqLabel.appendChild(reqInput);
        reqLabel.appendChild(document.createTextNode(" Pflichtfeld"));
        reqWrap.appendChild(reqLabel);
        container.appendChild(reqWrap);

        // Regex
        var reWrap = fieldWrap("Muster (Regex, optional)");
        var reInput = document.createElement("input");
        reInput.type = "text"; reInput.value = data.regex || "";
        reInput.placeholder = "z. B. ^[0-9]{5}$";
        reWrap.appendChild(reInput);
        var reHelp = document.createElement("div"); reHelp.className = "field-help";
        reHelp.textContent = "Regulärer Ausdruck zur Validierung (Client + Server).";
        reWrap.appendChild(reHelp);
        container.appendChild(reWrap);

        // Options (select only)
        var optsCtl = buildOptions(data.options || []);
        container.appendChild(optsCtl.node);

        // Child fields (group only) — only for top-level group elements.
        var groupCtl = null;
        if (!isChild && fixedType === "group") {
            groupCtl = buildChildren(data.fields || []);
            container.appendChild(groupCtl.node);
        }

        // Condition (top-level only)
        var condCtl = null;
        if (!isChild) {
            condCtl = buildCondition(data.condition, opts.available);
            container.appendChild(condCtl.node);
        }

        refresh();

        function refresh() {
            var t = typeGetter();
            show(phWrap, isInput(t) && t !== "select");
            show(helpWrap, t !== "title");
            show(reqWrap, isInput(t));
            show(reWrap, t === "text" || t === "phone" || t === "email" || t === "number");
            show(optsCtl.node, t === "select");
        }

        function read() {
            var t = typeGetter();
            var out = { type: t, label: labelInput.value };
            if (data.id) out.id = data.id;
            if (isInput(t) && t !== "select") out.placeholder = phInput.value;
            if (t !== "title") out.help = helpInput.value;
            if (isInput(t)) out.required = reqInput.checked;
            if (t === "text" || t === "phone" || t === "email" || t === "number") out.regex = reInput.value;
            if (t === "select") out.options = optsCtl.get();
            if (groupCtl) out.fields = groupCtl.get();
            if (condCtl) { var c = condCtl.get(); if (c) out.condition = c; }
            return out;
        }

        return read;
    }

    // ---- Options editor (for select) ----
    function buildOptions(options) {
        var wrap = document.createElement("div"); wrap.className = "field";
        var label = document.createElement("label"); label.textContent = "Optionen"; wrap.appendChild(label);
        var listEl = document.createElement("div"); listEl.className = "list-items"; wrap.appendChild(listEl);
        var rows = [];

        function addRow(o) {
            o = o || {};
            var row = document.createElement("div"); row.className = "opt-row";
            var val = document.createElement("input"); val.type = "text"; val.placeholder = "Wert"; val.value = o.value || "";
            var lab = document.createElement("input"); lab.type = "text"; lab.placeholder = "Anzeigetext"; lab.value = o.label || "";
            var del = iconBtn("✕");
            row.appendChild(val); row.appendChild(lab); row.appendChild(del);
            var entry = { element: row, get: function () { return { value: val.value.trim(), label: lab.value.trim() }; } };
            del.addEventListener("click", function () { row.remove(); });
            rows.push(entry); listEl.appendChild(row);
        }
        (options || []).forEach(addRow);

        var add = document.createElement("button");
        add.type = "button"; add.className = "btn btn-ghost btn-sm"; add.style.marginTop = "8px";
        add.textContent = "+ Option hinzufügen";
        add.addEventListener("click", function () { addRow({}); });
        wrap.appendChild(add);

        return {
            node: wrap,
            get: function () {
                var result = [];
                Array.prototype.slice.call(listEl.children).forEach(function (child) {
                    for (var i = 0; i < rows.length; i++) {
                        if (rows[i].element === child) {
                            var v = rows[i].get();
                            if (v.value) result.push(v);
                            break;
                        }
                    }
                });
                return result;
            }
        };
    }

    // ---- Group children editor ----
    function buildChildren(children) {
        var wrap = document.createElement("div"); wrap.className = "field";
        var label = document.createElement("label"); label.textContent = "Felder in der Gruppe"; wrap.appendChild(label);
        var listEl = document.createElement("div"); listEl.className = "list-items"; wrap.appendChild(listEl);
        var entries = [];

        function addChild(childData) {
            childData = childData || { type: "text", label: "Feld" };
            var card = document.createElement("div"); card.className = "list-item";
            var head = document.createElement("div"); head.className = "list-item-head";
            var title = document.createElement("span"); title.className = "li-title"; title.textContent = "Feld";
            var acts = document.createElement("div"); acts.className = "li-actions";
            var up = iconBtn("▲"), down = iconBtn("▼"), del = iconBtn("✕");
            acts.appendChild(up); acts.appendChild(down); acts.appendChild(del);
            head.appendChild(title); head.appendChild(acts);
            card.appendChild(head);

            var body = document.createElement("div");
            card.appendChild(body);
            var read = buildElementForm(body, childData, { isChild: true, available: [] });

            var entry = { element: card, get: read };
            up.addEventListener("click", function () { move(card, -1); });
            down.addEventListener("click", function () { move(card, 1); });
            del.addEventListener("click", function () { card.remove(); });
            entries.push(entry); listEl.appendChild(card);
        }

        function move(node, dir) {
            var nodes = Array.prototype.slice.call(listEl.children);
            var i = nodes.indexOf(node), j = i + dir;
            if (j < 0 || j >= nodes.length) return;
            if (dir < 0) listEl.insertBefore(node, nodes[j]);
            else listEl.insertBefore(nodes[j], node);
        }

        (children || []).forEach(addChild);

        var add = document.createElement("button");
        add.type = "button"; add.className = "btn btn-ghost btn-sm"; add.style.marginTop = "8px";
        add.textContent = "+ Feld hinzufügen";
        add.addEventListener("click", function () { addChild({ type: "text", label: "Feld" }); });
        wrap.appendChild(add);

        return {
            node: wrap,
            get: function () {
                var result = [];
                Array.prototype.slice.call(listEl.children).forEach(function (child) {
                    for (var i = 0; i < entries.length; i++) {
                        if (entries[i].element === child) { result.push(entries[i].get()); break; }
                    }
                });
                return result;
            }
        };
    }

    // ---- Condition editor ----
    function buildCondition(cond, available) {
        available = available || [];
        var wrap = document.createElement("div"); wrap.className = "field cond-field";
        var label = document.createElement("label"); label.className = "check";
        var enable = document.createElement("input"); enable.type = "checkbox";
        enable.checked = !!(cond && cond.field);
        label.appendChild(enable);
        label.appendChild(document.createTextNode(" Bedingung: nur anzeigen, wenn …"));
        wrap.appendChild(label);

        var box = document.createElement("div"); box.className = "cond-box"; box.style.marginTop = "8px";

        var fieldSel = document.createElement("select");
        var none = document.createElement("option"); none.value = ""; none.textContent = "– Feld wählen –";
        fieldSel.appendChild(none);
        available.forEach(function (f) {
            var o = document.createElement("option"); o.value = f.id; o.textContent = f.label;
            if (cond && cond.field === f.id) o.selected = true;
            fieldSel.appendChild(o);
        });

        var opSel = document.createElement("select");
        Object.keys(OP_LABELS).forEach(function (op) {
            var o = document.createElement("option"); o.value = op; o.textContent = OP_LABELS[op];
            if (cond && cond.op === op) o.selected = true;
            opSel.appendChild(o);
        });

        var valInput = document.createElement("input");
        valInput.type = "text"; valInput.placeholder = "Wert"; valInput.value = (cond && cond.value) || "";

        box.appendChild(fieldSel); box.appendChild(opSel); box.appendChild(valInput);
        wrap.appendChild(box);

        if (available.length === 0) {
            var hint = document.createElement("div"); hint.className = "field-help";
            hint.textContent = "Es sind noch keine anderen Felder vorhanden, auf die sich eine Bedingung beziehen kann.";
            wrap.appendChild(hint);
        }

        function refresh() {
            box.style.display = enable.checked ? "" : "none";
            valInput.style.display = opSel.value === "filled" ? "none" : "";
        }
        enable.addEventListener("change", refresh);
        opSel.addEventListener("change", refresh);
        refresh();

        return {
            node: wrap,
            get: function () {
                if (!enable.checked || !fieldSel.value) return null;
                return { field: fieldSel.value, op: opSel.value, value: opSel.value === "filled" ? "" : valInput.value };
            }
        };
    }

    // ---- small DOM helpers ----
    function fieldWrap(caption) {
        var w = document.createElement("div"); w.className = "field";
        var l = document.createElement("label"); l.textContent = caption; w.appendChild(l);
        return w;
    }
    function labelCaption(type) {
        if (type === "title" || type === "description") return "Text";
        return "Beschriftung";
    }
    function show(node, visible) { node.style.display = visible ? "" : "none"; }
    function iconBtn(txt) {
        var b = document.createElement("button"); b.type = "button"; b.className = "icon-btn"; b.textContent = txt;
        return b;
    }
})();
