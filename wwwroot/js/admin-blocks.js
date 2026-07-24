// Schema-driven block editor. Reads a field schema + current values, renders
// controls, and serializes everything back into the hidden #DataJson field on submit.
(function () {
    "use strict";

    var schemaEl = document.getElementById("block-schema");
    var dataEl = document.getElementById("block-data");
    var editor = document.getElementById("block-editor");
    var form = document.getElementById("block-form");
    var output = document.getElementById("DataJson");
    if (!schemaEl || !editor || !form || !output) return;

    var schema = safeParse(schemaEl.textContent, []);
    var data = safeParse(dataEl ? dataEl.textContent : "{}", {});

    var collectors = [];
    schema.forEach(function (field) {
        var built = buildField(field, data[field.id]);
        editor.appendChild(built.el);
        collectors.push({ id: field.id, get: built.get });
    });

    form.addEventListener("submit", function () {
        var obj = {};
        collectors.forEach(function (c) { obj[c.id] = c.get(); });
        output.value = JSON.stringify(obj);
    });

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
        } else if (type === "image") {
            var im = buildImage(value != null ? value : "");
            wrap.appendChild(im.node);
            get = im.get;
        } else if (type === "url") {
            var iu = document.createElement("input");
            iu.type = "url";
            iu.value = value != null ? value : (field.default || "");
            if (field.placeholder) iu.placeholder = field.placeholder;
            wrap.appendChild(iu);
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

        var file = document.createElement("input");
        file.type = "file";
        file.accept = "image/*";
        file.style.display = "none";

        var btn = document.createElement("button");
        btn.type = "button";
        btn.className = "btn btn-ghost btn-sm";
        btn.textContent = "Bild hochladen";
        btn.addEventListener("click", function () { file.click(); });

        file.addEventListener("change", function () {
            if (!file.files || !file.files[0]) return;
            btn.textContent = "Lädt…"; btn.disabled = true;
            var fd = new FormData();
            fd.append("file", file.files[0]);
            fetch("/admin/api/upload", { method: "POST", body: fd })
                .then(function (res) { return res.json().then(function (j) { return { ok: res.ok, j: j }; }); })
                .then(function (r) {
                    if (r.ok && r.j.url) {
                        inp.value = r.j.url;
                        preview.style.backgroundImage = "url('" + r.j.url + "')";
                    } else {
                        alert(r.j.error || "Upload fehlgeschlagen.");
                    }
                })
                .catch(function () { alert("Upload fehlgeschlagen."); })
                .then(function () { btn.textContent = "Bild hochladen"; btn.disabled = false; file.value = ""; });
        });

        row.appendChild(btn);
        row.appendChild(file);
        controls.appendChild(inp);
        controls.appendChild(row);
        container.appendChild(preview);
        container.appendChild(controls);
        return { node: container, get: function () { return inp.value.trim(); } };
    }

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
