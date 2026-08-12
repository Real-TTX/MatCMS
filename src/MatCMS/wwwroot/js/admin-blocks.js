// Schema-driven block editor. Reads a field schema + current values, renders
// controls, and serializes everything back into the hidden #DataJson field on submit.
(function () {
    "use strict";

    // Reusable, DOM-agnostic builder: renders `schema` into `container` with `data`, wires conditional
    // visibility, and returns { serialize() }. Used by the in-page block editor below AND by the
    // translation-compare dialog (diff-edit.js), so both share identical controls (rich text, image
    // picker, link picker, lists, conditions).
    function build(container, schema, data) {
        data = data || {};
        var collectors = [];
        (schema || []).forEach(function (field) {
            var built = buildField(field, data[field.id]);
            container.appendChild(built.el);
            collectors.push({ id: field.id, get: built.get, el: built.el, showWhen: field.showWhen });
        });
        function applyConditions() {
            collectors.forEach(function (c) {
                if (!c.showWhen) return;
                var src = collectors.filter(function (x) { return x.id === c.showWhen.field; })[0];
                var match = src && String(src.get()) === String(c.showWhen.value);
                c.el.style.display = match ? "" : "none";
            });
        }
        container.addEventListener("change", applyConditions);
        container.addEventListener("input", applyConditions);
        applyConditions();
        function serialize() {
            var obj = {};
            collectors.forEach(function (c) { obj[c.id] = c.get(); });
            return obj;
        }
        return { serialize: serialize, applyConditions: applyConditions, collectors: collectors };
    }
    window.MatBlockFields = { build: build };

    // ---- In-page block editor (only present on the page/post editor) --------------------------------
    var schemaEl = document.getElementById("block-schema");
    var dataEl = document.getElementById("block-data");
    var editor = document.getElementById("block-editor");
    var form = document.getElementById("block-form");
    var output = document.getElementById("DataJson");
    if (!schemaEl || !editor || !form || !output) return;

    var api = build(editor, safeParse(schemaEl.textContent, []), safeParse(dataEl ? dataEl.textContent : "{}", {}));
    function serialize() { return api.serialize(); }

    form.addEventListener("submit", function () {
        output.value = JSON.stringify(serialize());
    });

    // Live preview: on any field change, hand the block's current data up to the page editor, which
    // updates its draft and re-renders the preview (nothing is persisted until "Speichern").
    var liveT;
    function pushLive() {
        clearTimeout(liveT);
        liveT = setTimeout(function () {
            if (typeof window.matOnBlockDataChange === "function") {
                try { window.matOnBlockDataChange(JSON.stringify(serialize())); } catch (e) { }
            }
        }, 250);
    }
    editor.addEventListener("input", pushLive);
    editor.addEventListener("change", pushLive);

    function safeParse(txt, fallback) {
        try { return JSON.parse(txt) || fallback; } catch (e) { return fallback; }
    }

    function buildField(field, value) {
        var type = (field.type || "text").toLowerCase();
        var wrap = document.createElement("div");
        wrap.className = "field";

        if (type !== "list") {
            var label = document.createElement("label");
            label.textContent = field.label || field.id;
            wrap.appendChild(label);
        }

        var get = function () { return ""; };

        if (type === "textarea") {
            var ta = document.createElement("textarea");
            ta.value = value != null ? value : (field.default || "");
            if (field.placeholder) ta.placeholder = field.placeholder;
            wrap.appendChild(ta);
            get = function () { return ta.value; };
        } else if (type === "richtext") {
            var rt = buildRichText(value != null ? value : (field.default || ""));
            wrap.appendChild(rt.node);
            get = rt.get;
        } else if (type === "select") {
            var sel = document.createElement("select");
            (field.options || []).forEach(function (o) {
                var opt = document.createElement("option");
                opt.value = o.value; opt.textContent = o.label;
                if ((value != null ? value : field.default) === o.value) opt.selected = true;
                sel.appendChild(opt);
            });
            wrap.appendChild(sel);
            get = function () { return sel.value; };
        } else if (type === "multiselect") {
            // The SAME dropdown the public forms use (mat-richselect), not a second one built for the
            // admin: one component means one behaviour, and it already brings the bottom-sheet on
            // mobile that a list of chips could never give.
            // The value stays ONE comma-separated string — the format the server already reads, so a
            // field that used to be a single select keeps everything it ever saved.
            var msChosen = String(value != null ? value : (field.default || ""))
                .split(",").map(function (x) { return x.trim(); }).filter(Boolean);
            var opts = field.options || [];
            var rs = document.createElement("div");
            rs.className = "mat-rs";
            rs.setAttribute("data-rs", "");
            rs.setAttribute("data-rs-multi", "");

            function isOn(v) {
                // Case-insensitive: tags are typed by hand elsewhere, and "Zimmer" must not come back
                // unticked because the option is spelled "zimmer".
                return msChosen.some(function (c) { return c.toLowerCase() === String(v).toLowerCase(); });
            }
            var onLabels = opts.filter(function (o) { return isOn(o.value); }).map(function (o) { return o.label; });
            var placeholder = field.placeholder || "Nichts gewählt";
            var head = onLabels.length === 0 ? placeholder
                     : onLabels.length <= 2 ? onLabels.join(", ")
                     : onLabels.length + " gewählt";

            var trig = document.createElement("button");
            trig.type = "button";                      // never submits the settings form
            trig.className = "mat-input mat-rs-trigger";
            trig.setAttribute("data-rs-btn", "");
            trig.innerHTML = '<span class="mat-rs-current' + (onLabels.length ? '' : ' mat-rs-placeholder') +
                             '" data-rs-current><span class="mat-rs-cur-title"></span></span>' +
                             '<span class="mat-rs-chev" aria-hidden="true">▾</span>';
            trig.querySelector(".mat-rs-cur-title").textContent = head;
            rs.appendChild(trig);

            var hidden = document.createElement("input");
            hidden.type = "hidden";
            hidden.setAttribute("data-rs-input", "");
            hidden.setAttribute("data-placeholder", placeholder);
            hidden.setAttribute("data-many", "{0} gewählt");
            hidden.value = opts.filter(function (o) { return isOn(o.value); })
                               .map(function (o) { return o.value; }).join(", ");
            rs.appendChild(hidden);

            var menu = document.createElement("div");
            menu.className = "mat-rs-menu";
            menu.hidden = true;
            menu.setAttribute("data-rs-menu", "");
            var sheet = document.createElement("div");
            sheet.className = "mat-rs-sheet-head";
            sheet.innerHTML = '<span class="mat-rs-sheet-title"></span>' +
                              '<button type="button" class="mat-rs-close" data-rs-close aria-label="Schließen">✕</button>';
            sheet.querySelector(".mat-rs-sheet-title").textContent = field.label || "";
            menu.appendChild(sheet);
            var scroll = document.createElement("div");
            scroll.className = "mat-rs-scroll";
            opts.forEach(function (o) {
                var ob = document.createElement("button");
                ob.type = "button";
                ob.className = "mat-rs-opt" + (isOn(o.value) ? " on" : "");
                ob.setAttribute("data-rs-opt", "");
                ob.setAttribute("data-value", o.value);
                if (isOn(o.value)) ob.setAttribute("aria-selected", "true");
                var body = document.createElement("span");
                body.className = "mat-rs-opt-body";
                var ti = document.createElement("span");
                ti.className = "mat-rs-opt-title";
                ti.textContent = o.label;
                body.appendChild(ti);
                ob.appendChild(body);
                scroll.appendChild(ob);
            });
            if (!opts.length) {
                var none = document.createElement("div");
                none.className = "help";
                none.style.padding = "10px 14px";
                none.textContent = field.emptyHint || "Keine Auswahl vorhanden.";
                scroll.appendChild(none);
            }
            menu.appendChild(scroll);
            rs.appendChild(menu);
            wrap.appendChild(rs);
            // Read from the hidden input, which mat-richselect keeps up to date — so the editor never
            // needs its own copy of "what is ticked".
            get = function () { return hidden.value; };
        } else if (type === "image") {
            var im = buildImage(value != null ? value : "");
            wrap.appendChild(im.node);
            get = im.get;
        } else if (type === "url") {
            var lf = document.createElement("div");
            lf.className = "link-field";
            var iu = document.createElement("input");
            iu.type = "text"; // text (not url) so internal paths like "/kontakt" pass validation
            iu.value = value != null ? value : (field.default || "");
            if (field.placeholder) iu.placeholder = field.placeholder;
            lf.appendChild(iu);
            var lb = document.createElement("button");
            lb.type = "button";
            lb.className = "link-field-btn";
            lb.title = "Seite verlinken";
            lb.setAttribute("aria-label", "Seite verlinken");
            lb.innerHTML = '<i class="ti ti-link" aria-hidden="true"></i>';
            lb.addEventListener("click", function () {
                if (window.openLinkPicker) window.openLinkPicker(function (u) { iu.value = u; });
            });
            lf.appendChild(lb);
            wrap.appendChild(lf);
            get = function () { return iu.value.trim(); };
        } else if (type === "list") {
            var li = buildList(field, Array.isArray(value) ? value : []);
            wrap.appendChild(li.node);
            get = li.get;
        } else {
            var it = document.createElement("input");
            it.type = "text";
            it.value = value != null ? value : (field.default || "");
            if (field.placeholder) it.placeholder = field.placeholder;
            wrap.appendChild(it);
            get = function () { return it.value; };
        }

        if (field.help) {
            var h = document.createElement("div");
            h.className = "field-help";
            h.textContent = field.help;
            wrap.appendChild(h);
        }
        return { el: wrap, get: get };
    }

    function buildRichText(html) {
        var container = document.createElement("div");
        var toolbar = document.createElement("div");
        toolbar.className = "rt-toolbar";

        var ed = document.createElement("div");
        ed.className = "rt-editor";
        ed.contentEditable = "true";
        ed.innerHTML = html || "";

        var tools = [
            { label: "F", cmd: "bold", title: "Fett" },
            { label: "K", cmd: "italic", title: "Kursiv" },
            { label: "H3", cmd: "formatBlock", arg: "H3", title: "Überschrift" },
            { label: "Absatz", cmd: "formatBlock", arg: "P", title: "Absatz" },
            { label: "• Liste", cmd: "insertUnorderedList", title: "Liste" },
            { label: "Link", cmd: "createLink", prompt: true, title: "Link einfügen" },
            { label: "Format löschen", cmd: "removeFormat", title: "Formatierung entfernen" }
        ];

        tools.forEach(function (t) {
            var b = document.createElement("button");
            b.type = "button";
            b.textContent = t.label;
            b.title = t.title;
            b.addEventListener("mousedown", function (e) { e.preventDefault(); });
            b.addEventListener("click", function () {
                ed.focus();
                if (t.prompt) {
                    var url = window.prompt("Link-Adresse (URL):", "https://");
                    if (url) document.execCommand(t.cmd, false, url);
                } else {
                    document.execCommand(t.cmd, false, t.arg || null);
                }
            });
            toolbar.appendChild(b);
        });

        container.appendChild(toolbar);
        container.appendChild(ed);
        return { node: container, get: function () { return ed.innerHTML.trim(); } };
    }

    function buildImage(url) {
        var container = document.createElement("div");
        container.className = "image-field";

        var preview = document.createElement("div");
        preview.className = "preview";
        if (url) preview.style.backgroundImage = "url('" + url + "')";

        var controls = document.createElement("div");
        controls.className = "image-controls";

        var inp = document.createElement("input");
        inp.type = "text";
        inp.value = url || "";
        inp.placeholder = "/uploads/... oder https://...";
        inp.addEventListener("input", function () {
            preview.style.backgroundImage = inp.value ? "url('" + inp.value + "')" : "";
        });

        var row = document.createElement("div");
        row.style.marginTop = "8px";

        // Single button → the media dialog (which itself can upload). No separate upload button.
        var btn = document.createElement("button");
        btn.type = "button";
        btn.className = "btn btn-ghost btn-sm";
        btn.textContent = "Bild wählen";
        btn.addEventListener("click", function () {
            openMediaPicker(function (u) { inp.value = u; preview.style.backgroundImage = "url('" + u + "')"; });
        });

        row.appendChild(btn);
        controls.appendChild(inp);
        controls.appendChild(row);
        container.appendChild(preview);
        container.appendChild(controls);
        return { node: container, get: function () { return inp.value.trim(); } };
    }

    // Media-library picker lives in the shared wwwroot/js/media-picker.js (window.openMediaPicker),
    // loaded globally by _AdminLayout so every image field (block editor + settings) reuses it.

    function buildList(field, items) {
        var container = document.createElement("div");

        var label = document.createElement("label");
        label.textContent = field.label || field.id;
        label.style.display = "block";
        label.style.fontWeight = "600";
        label.style.marginBottom = "8px";
        container.appendChild(label);

        var listEl = document.createElement("div");
        listEl.className = "list-items";
        container.appendChild(listEl);

        var entries = [];

        function addItem(itemData) {
            var card = document.createElement("div");
            card.className = "list-item";

            var head = document.createElement("div");
            head.className = "list-item-head";
            var title = document.createElement("span");
            title.className = "li-title";
            title.textContent = field.itemLabel || "Eintrag";
            var acts = document.createElement("div");
            acts.className = "li-actions";
            var up = iconBtn("▲"), down = iconBtn("▼"), del = iconBtn("✕");
            acts.appendChild(up); acts.appendChild(down); acts.appendChild(del);
            head.appendChild(title); head.appendChild(acts);
            card.appendChild(head);

            var getters = [];
            (field.itemFields || []).forEach(function (sub) {
                var built = buildField(sub, itemData ? itemData[sub.id] : undefined);
                card.appendChild(built.el);
                getters.push({ id: sub.id, get: built.get });
            });

            var entry = {
                element: card,
                get: function () {
                    var o = {};
                    getters.forEach(function (g) { o[g.id] = g.get(); });
                    return o;
                }
            };
            up.addEventListener("click", function () { moveItem(card, -1); });
            down.addEventListener("click", function () { moveItem(card, 1); });
            del.addEventListener("click", function () { card.remove(); });

            entries.push(entry);
            listEl.appendChild(card);
        }

        function moveItem(el, dir) {
            var nodes = Array.prototype.slice.call(listEl.children);
            var i = nodes.indexOf(el);
            var j = i + dir;
            if (j < 0 || j >= nodes.length) return;
            if (dir < 0) listEl.insertBefore(el, nodes[j]);
            else listEl.insertBefore(nodes[j], el);
        }

        (items || []).forEach(function (it) { addItem(it); });

        var addBtn = document.createElement("button");
        addBtn.type = "button";
        addBtn.className = "btn btn-ghost btn-sm";
        addBtn.style.marginTop = "10px";
        addBtn.textContent = "+ " + (field.itemLabel || "Eintrag") + " hinzufügen";
        addBtn.addEventListener("click", function () { addItem({}); });
        container.appendChild(addBtn);

        // If items carry an image, allow picking several from the media library at once; each becomes
        // an ordered entry (reorder with ▲▼). Lets a gallery use images in its OWN per-block order.
        var imgField = (field.itemFields || []).filter(function (f) { return f.type === "image"; })[0];
        if (imgField && window.openMediaPicker) {
            var bulkBtn = document.createElement("button");
            bulkBtn.type = "button";
            bulkBtn.className = "btn btn-ghost btn-sm";
            bulkBtn.style.marginTop = "10px";
            bulkBtn.style.marginLeft = "8px";
            bulkBtn.textContent = "+ Aus Mediathek (mehrere)";
            bulkBtn.addEventListener("click", function () {
                window.openMediaPicker(function (urls) {
                    (urls || []).forEach(function (u) { var o = {}; o[imgField.id] = u; addItem(o); });
                }, { multiple: true });
            });
            container.appendChild(bulkBtn);
        }

        function get() {
            var result = [];
            Array.prototype.slice.call(listEl.children).forEach(function (childEl) {
                for (var i = 0; i < entries.length; i++) {
                    if (entries[i].element === childEl) { result.push(entries[i].get()); break; }
                }
            });
            return result;
        }

        return { node: container, get: get };
    }

    function iconBtn(txt) {
        var b = document.createElement("button");
        b.type = "button";
        b.className = "icon-btn";
        b.textContent = txt;
        return b;
    }
})();
