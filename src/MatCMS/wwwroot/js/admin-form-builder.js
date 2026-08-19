// Visual form builder: add-element modal, preview<->sidebar linking, and the
// schema-like settings form for a single element (serialized into #ElementJson on submit).
(function () {
    "use strict";

    var INPUT_TYPES = ["text", "textarea", "date", "daterange", "number", "phone", "email", "select", "richselect", "multiselect"];
    var CHILD_TYPES = ["title", "description", "text", "textarea", "date", "daterange", "number", "phone", "email", "select", "richselect", "multiselect"];
    var TYPE_LABELS = {
        title: "Überschrift", description: "Beschreibung", text: "Textfeld", textarea: "Textfeld (mehrzeilig)",
        date: "Datum", daterange: "Zeitraum", number: "Zahl", phone: "Telefon", email: "E-Mail",
        select: "Auswahl", richselect: "Auswahl mit Bild", multiselect: "Mehrfachauswahl", group: "Gruppe"
    };
    // multiselect counts as select-like: it is the options editor that makes the type usable at
    // all, and a field you cannot give options to is an empty dropdown.
    function isSelectLike(t) { return t === "select" || t === "richselect" || t === "multiselect"; }
    var OP_LABELS = { eq: "ist gleich", neq: "ist ungleich", contains: "enthält", filled: "ist ausgefüllt" };

    function isInput(t) { return INPUT_TYPES.indexOf(t) >= 0; }
    function safeParse(txt, fb) { try { return JSON.parse(txt); } catch (e) { return fb; } }

    // ---------- Add-element modal (categorised picker + search, mirrors the page block picker) ----------
    var modal = document.getElementById("add-element-modal");
    var openBtn = document.getElementById("add-element-btn");
    var closeBtn = document.getElementById("add-element-close");
    var fSearch = modal ? modal.querySelector("#fpick-search") : null;
    var fCatBtns = modal ? modal.querySelectorAll(".bpick-cat") : [];
    var fGroups = modal ? modal.querySelectorAll(".bpick-group") : [];
    var fEmpty = modal ? modal.querySelector(".bpick-empty") : null;
    var fActiveCat = "all";

    function fApplyFilter() {
        if (!modal) return;
        var q = ((fSearch && fSearch.value) || "").trim().toLowerCase();
        var flat = fActiveCat !== "all";
        var main = modal.querySelector(".bpick-main");
        if (main) main.classList.toggle("bpick-flat", flat);
        var anyVisible = false;
        fGroups.forEach(function (g) {
            var groupVisible = false;
            g.querySelectorAll(".tile").forEach(function (t) {
                var okCat = fActiveCat === "all" ? true : t.getAttribute("data-cat") === fActiveCat;
                var okText = !q
                    || (t.getAttribute("data-name") || "").indexOf(q) >= 0
                    || (t.getAttribute("data-desc") || "").indexOf(q) >= 0;
                var show = okCat && okText;
                var f = t.closest("form");
                if (f) f.style.display = show ? "" : "none";
                if (show) { groupVisible = true; anyVisible = true; }
            });
            g.style.display = groupVisible ? "" : "none";
        });
        if (fEmpty) fEmpty.hidden = anyVisible;
    }
    function fResetFilter() {
        if (fSearch) fSearch.value = "";
        fActiveCat = "all";
        fCatBtns.forEach(function (x) { x.classList.toggle("is-active", x.getAttribute("data-cat") === "all"); });
        fApplyFilter();
    }
    // `position` = where the new element goes among the top-level elements; null appends. The
    // picker carries it in a hidden field on every tile form, so whichever tile is pressed posts
    // the place the operator pointed at.
    function fOpenPicker(position) {
        if (!modal) return;
        modal.querySelectorAll(".add-position").forEach(function (i) { i.value = position == null ? "" : String(position); });
        modal.classList.add("open");
        fResetFilter();
        setTimeout(function () { if (fSearch) fSearch.focus(); }, 30);
    }
    if (modal && openBtn) {
        openBtn.addEventListener("click", function () { fOpenPicker(null); });
        if (closeBtn) closeBtn.addEventListener("click", function () { modal.classList.remove("open"); });
        modal.addEventListener("click", function (e) { if (e.target === modal) modal.classList.remove("open"); });
        document.addEventListener("keydown", function (e) { if (e.key === "Escape") modal.classList.remove("open"); });
        if (fSearch) fSearch.addEventListener("input", fApplyFilter);
        fCatBtns.forEach(function (c) {
            c.addEventListener("click", function () {
                fCatBtns.forEach(function (x) { x.classList.remove("is-active"); });
                c.classList.add("is-active");
                fActiveCat = c.getAttribute("data-cat");
                fApplyFilter();
            });
        });
    }

    // ---------- Preview linking ----------
    var frame = document.getElementById("preview-frame");
    function postFrame(msg) { if (frame && frame.contentWindow) frame.contentWindow.postMessage(msg, "*"); }
    Array.prototype.slice.call(document.querySelectorAll(".block-item[data-element-id]")).forEach(function (row) {
        var id = row.getAttribute("data-element-id");
        row.addEventListener("mouseenter", function () { postFrame({ type: "mat-scroll-element", id: id }); });
    });
    // The element open in the sidebar is highlighted in the preview, so the two halves of the
    // editor always agree on what is being edited.
    var selectedElId = (document.querySelector(".form-builder") || {}).getAttribute
        ? document.querySelector(".form-builder").getAttribute("data-selected-element") : null;
    function syncSelection() { if (selectedElId) postFrame({ type: "mat-select-el", id: selectedElId }); }
    if (frame) frame.addEventListener("load", syncSelection);

    window.addEventListener("message", function (e) {
        var d = e.data || {};
        if (d.type === "mat-preview-ready") syncSelection();
        if (d.type === "mat-select-element" && d.id) {
            window.location.search = "?element=" + encodeURIComponent(d.id);
        }
        // Clicked a gap between two elements in the preview: open the picker for that place.
        if (d.type === "mat-insert-at") fOpenPicker(d.index);
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

    // What the element looked like the moment its panel was built. The switcher in the toolbar asks
    // this before it lets go of the page — nothing here is persisted until the form is submitted.
    // A comparison, not an "input" listener: the controls fire events while they are being set up,
    // so a listener would call an untouched element dirty and the question would come every time.
    var elPristine = JSON.stringify(read());
    window.matElementDirty = function () {
        try { return JSON.stringify(read()) !== elPristine; } catch (e) { return false; }
    };

    // ---------- Live preview: re-render the whole (unsaved) form on every edit ----------
    (function () {
        var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
        var seed = document.getElementById("form-elements");
        if (!frame || !tokenEl || !seed) return;
        var draft = safeParse(seed.textContent, []) || [];
        var selId = el && el.id;
        var t;
        function push() {
            var cur = read();
            for (var i = 0; i < draft.length; i++) {
                if (draft[i] && draft[i].id === selId) { draft[i] = cur; break; }
            }
            clearTimeout(t);
            t = setTimeout(function () {
                var body = new URLSearchParams();
                body.set("__RequestVerificationToken", tokenEl.value);
                body.set("Draft", JSON.stringify(draft));
                fetch(location.pathname + "?handler=RenderPreview", {
                    method: "POST",
                    headers: { "Content-Type": "application/x-www-form-urlencoded", "RequestVerificationToken": tokenEl.value },
                    body: body.toString(),
                    credentials: "same-origin"
                })
                    .then(function (r) { return r.ok ? r.text() : null; })
                    .then(function (html) { if (html != null) postFrame({ type: "mat-render", html: html }); })
                    .catch(function () { });
            }, 200);
        }
        editor.addEventListener("input", push);
        editor.addEventListener("change", push);
    })();

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
        var phLabel = phWrap.querySelector("label");
        var phInput = document.createElement("input");
        phInput.type = "text"; phInput.value = data.placeholder || "";
        phWrap.appendChild(phInput);
        container.appendChild(phWrap);

        // Separator (multi-select only). What joins the chosen values in the stored string — the
        // side that reads the submission decides this, not the field, so it has to be settable.
        var sepWrap = fieldWrap("Trennzeichen");
        var sepInput = document.createElement("input");
        sepInput.type = "text";
        sepInput.value = data.separator != null ? data.separator : "";
        sepInput.placeholder = ", ";
        sepWrap.appendChild(sepInput);
        container.appendChild(sepWrap);

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

        // Ungenaue Zeitangaben (± Tage) — nur für Datum/Zeitraum. Schaltet die Flex-Chips im Picker frei.
        var flexWrap = document.createElement("div"); flexWrap.className = "field";
        var flexLabel = document.createElement("label"); flexLabel.className = "check";
        var flexInput = document.createElement("input"); flexInput.type = "checkbox"; flexInput.checked = !!data.flex;
        flexLabel.appendChild(flexInput);
        flexLabel.appendChild(document.createTextNode(" Ungenaue Zeitangaben zulassen (± Tage)"));
        flexWrap.appendChild(flexLabel);
        container.appendChild(flexWrap);

        // Button-Texte des Zeitraum-/Datums-Controls — pro Feld überschreibbar. Leer = lokalisierter
        // Standard. Werden pro Element gespeichert und sind damit je Sprachversion übersetzbar.
        var TEXT_KEYS = [
            { key: "ok", label: "Übernehmen-Button", def: "Übernehmen" },
            { key: "clear", label: "Löschen-Button", def: "Löschen" },
            { key: "today", label: "Heute-Button", def: "Heute" },
            { key: "cancel", label: "Abbrechen-Button", def: "Abbrechen" },
            { key: "flexTitle", label: "Titel „Flexible Datumsoptionen“", def: "Flexible Datumsoptionen" },
            { key: "exact", label: "Chip „Genaue Zeitangabe“", def: "Genaue Zeitangabe" }
        ];
        var textsData = (data.texts && typeof data.texts === "object") ? data.texts : {};
        var textsWrap = document.createElement("div"); textsWrap.className = "field dp-texts";
        var textsHead = document.createElement("div"); textsHead.className = "field-help";
        textsHead.textContent = "Button-Texte (leer = Standard der jeweiligen Sprache):";
        textsWrap.appendChild(textsHead);
        var textInputs = {};
        TEXT_KEYS.forEach(function (tk) {
            var row = document.createElement("label"); row.className = "dp-text-row";
            var cap = document.createElement("span"); cap.className = "dp-text-cap"; cap.textContent = tk.label;
            var inp = document.createElement("input"); inp.type = "text";
            inp.value = textsData[tk.key] || ""; inp.placeholder = tk.def;
            row.appendChild(cap); row.appendChild(inp);
            textsWrap.appendChild(row);
            textInputs[tk.key] = inp;
        });
        container.appendChild(textsWrap);

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
            show(phWrap, isInput(t));
            // Only the multi-select joins anything, so only it has something to separate.
            show(sepWrap, t === "multiselect");
            // For selects the placeholder is the empty-option / prompt text ("– Bitte auswählen –").
            if (phLabel) phLabel.textContent = isSelectLike(t) ? "Auswahl-Platzhalter" : "Platzhalter";
            show(helpWrap, t !== "title");
            show(reqWrap, isInput(t));
            show(reWrap, t === "text" || t === "phone" || t === "email" || t === "number");
            show(flexWrap, t === "date" || t === "daterange");
            show(textsWrap, t === "date" || t === "daterange");
            show(optsCtl.node, isSelectLike(t));
            optsCtl.setRich(t === "richselect");
        }

        function read() {
            var t = typeGetter();
            var out = { type: t, label: labelInput.value };
            if (data.id) out.id = data.id;
            if (isInput(t)) out.placeholder = phInput.value;
            // Empty means "use the default", so it is left out rather than stored as "".
            if (t === "multiselect" && sepInput.value !== "") out.separator = sepInput.value;
            if (t !== "title") out.help = helpInput.value;
            if (isInput(t)) out.required = reqInput.checked;
            if (t === "text" || t === "phone" || t === "email" || t === "number") out.regex = reInput.value;
            if (t === "date" || t === "daterange") {
                out.flex = flexInput.checked;
                var texts = {};
                TEXT_KEYS.forEach(function (tk) { var v = (textInputs[tk.key].value || "").trim(); if (v) texts[tk.key] = v; });
                out.texts = texts;
            }
            if (isSelectLike(t)) out.options = optsCtl.get();
            if (groupCtl) out.fields = groupCtl.get();
            if (condCtl) { var c = condCtl.get(); if (c) out.condition = c; }
            return out;
        }

        return read;
    }

    // ---- Tag chips input (for rich-select option tags) ----
    function buildTagChips(initial) {
        var node = document.createElement("div"); node.className = "opt-tags";
        var chips = [];
        var inp = document.createElement("input"); inp.type = "text"; inp.className = "tag-chip-input"; inp.placeholder = "Tag + Enter";
        function addChip(text) {
            text = (text || "").trim();
            if (!text || chips.indexOf(text) >= 0) return;
            chips.push(text);
            var c = document.createElement("span"); c.className = "tag-chip"; c.appendChild(document.createTextNode(text));
            var x = document.createElement("button"); x.type = "button"; x.className = "tag-chip-x"; x.textContent = "✕";
            x.addEventListener("click", function () { var i = chips.indexOf(text); if (i >= 0) chips.splice(i, 1); c.remove(); });
            c.appendChild(x); node.insertBefore(c, inp);
        }
        inp.addEventListener("keydown", function (e) {
            if (e.key === "Enter" || e.key === ",") { e.preventDefault(); addChip(inp.value); inp.value = ""; }
            else if (e.key === "Backspace" && !inp.value && chips.length) {
                var last = node.querySelector(".tag-chip:last-of-type");
                if (last) { var t = last.childNodes[0].nodeValue; var i = chips.indexOf(t); if (i >= 0) chips.splice(i, 1); last.remove(); }
            }
        });
        inp.addEventListener("blur", function () { if (inp.value.trim()) { addChip(inp.value); inp.value = ""; } });
        node.appendChild(inp);
        (initial || []).forEach(addChip);
        return { node: node, get: function () { return chips.slice(); } };
    }

    // ---- Options editor (for select / richselect) ----
    function buildOptions(options) {
        var wrap = document.createElement("div"); wrap.className = "field";
        var label = document.createElement("label"); label.textContent = "Optionen"; wrap.appendChild(label);
        var listEl = document.createElement("div"); listEl.className = "list-items"; wrap.appendChild(listEl);
        var rows = [];

        function addRow(o) {
            o = o || {};
            var row = document.createElement("div"); row.className = "opt-row2";
            var top = document.createElement("div"); top.className = "opt-top";
            var val = document.createElement("input"); val.type = "text"; val.placeholder = "Schlüssel (Wert)"; val.value = o.value || "";
            var lab = document.createElement("input"); lab.type = "text"; lab.placeholder = "Titel"; lab.value = o.label || "";
            var del = iconBtn("✕");
            top.appendChild(val); top.appendChild(lab); top.appendChild(del);
            row.appendChild(top);

            // Rich extras (image, description, tags) — shown only for the "Auswahl mit Bild" type.
            var rich = document.createElement("div"); rich.className = "opt-rich";
            var imgRow = document.createElement("div"); imgRow.className = "opt-img-row";
            var imgPrev = document.createElement("span"); imgPrev.className = "opt-img-prev";
            if (o.image) imgPrev.style.backgroundImage = "url('" + o.image + "')";
            var imgVal = document.createElement("input"); imgVal.type = "text"; imgVal.placeholder = "/uploads/… oder URL"; imgVal.value = o.image || "";
            imgVal.addEventListener("input", function () { imgPrev.style.backgroundImage = imgVal.value ? "url('" + imgVal.value + "')" : ""; });
            var imgBtn = document.createElement("button"); imgBtn.type = "button"; imgBtn.className = "btn btn-ghost btn-sm"; imgBtn.textContent = "Bild";
            imgBtn.addEventListener("click", function () { if (window.openMediaPicker) window.openMediaPicker(function (u) { imgVal.value = u; imgPrev.style.backgroundImage = "url('" + u + "')"; }); });
            imgRow.appendChild(imgPrev); imgRow.appendChild(imgVal); imgRow.appendChild(imgBtn);
            rich.appendChild(imgRow);
            var desc = document.createElement("input"); desc.type = "text"; desc.className = "opt-desc"; desc.placeholder = "Beschreibung"; desc.value = o.description || "";
            rich.appendChild(desc);
            var tagsCtl = buildTagChips(o.tags || []);
            rich.appendChild(tagsCtl.node);
            row.appendChild(rich);

            var entry = {
                element: row,
                get: function () {
                    var out = { value: val.value.trim(), label: lab.value.trim() };
                    if (imgVal.value.trim()) out.image = imgVal.value.trim();
                    if (desc.value.trim()) out.description = desc.value.trim();
                    var tl = tagsCtl.get();
                    if (tl.length) out.tags = tl;
                    return out;
                }
            };
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
            setRich: function (on) { wrap.classList.toggle("opts-rich", !!on); },
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

        // For a SELECT source field, compare against its real option VALUES via a dropdown — a
        // free-text label would never equal the value the form actually submits.
        var valSelect = document.createElement("select");
        function sourceField() {
            for (var i = 0; i < available.length; i++) if (available[i].id === fieldSel.value) return available[i];
            return null;
        }
        function isSelectSource() {
            var f = sourceField(); return !!(f && isSelectLike(f.type) && f.options && f.options.length);
        }
        function populateSelect(prefer) {
            var f = sourceField(); if (!(f && f.options)) return;
            var cur = prefer != null ? prefer : valSelect.value;
            valSelect.innerHTML = "";
            f.options.forEach(function (o) {
                var op = document.createElement("option"); op.value = o.value; op.textContent = o.label || o.value;
                valSelect.appendChild(op);
            });
            if (cur != null) valSelect.value = cur;
        }

        box.appendChild(fieldSel); box.appendChild(opSel); box.appendChild(valInput); box.appendChild(valSelect);
        wrap.appendChild(box);

        if (available.length === 0) {
            var hint = document.createElement("div"); hint.className = "field-help";
            hint.textContent = "Es sind noch keine anderen Felder vorhanden, auf die sich eine Bedingung beziehen kann.";
            wrap.appendChild(hint);
        }

        function refresh() {
            box.style.display = enable.checked ? "" : "none";
            var sel = isSelectSource(), filled = opSel.value === "filled";
            valInput.style.display = (!filled && !sel) ? "" : "none";
            valSelect.style.display = (!filled && sel) ? "" : "none";
        }
        enable.addEventListener("change", refresh);
        opSel.addEventListener("change", refresh);
        fieldSel.addEventListener("change", function () { populateSelect(); refresh(); });
        populateSelect((cond && cond.value) || null);   // seed the dropdown when the source is a select
        refresh();

        return {
            node: wrap,
            get: function () {
                if (!enable.checked || !fieldSel.value) return null;
                if (opSel.value === "filled") return { field: fieldSel.value, op: opSel.value, value: "" };
                var v = isSelectSource() ? valSelect.value : valInput.value;
                return { field: fieldSel.value, op: opSel.value, value: v };
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
