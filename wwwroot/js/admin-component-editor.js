// Component designer: repeatable field rows -> hidden FieldsJson, a live placeholder hint,
// plus a live preview (sample data -> rendered iframe) and a debug panel.
(function () {
    "use strict";
    var rows = document.getElementById("field-rows");
    var addBtn = document.getElementById("add-field");
    var hidden = document.getElementById("FieldsJson");
    var form = document.getElementById("component-form");
    var hint = document.getElementById("placeholder-hint");
    if (!rows || !form) return;

    var TYPES = window.MATCMS_FIELD_TYPES || [["text", "Text"]];
    var CP = window.MATCMS_CP || {};

    // ---- Preview / debug elements (optional) ----
    var templateEl = document.getElementById("TemplateHtml");
    var sampleWrap = document.getElementById("cp-sample");
    var frame = document.getElementById("cp-frame");
    var debugEl = document.getElementById("cp-debug");
    var debugToggle = document.getElementById("cp-debug-toggle");
    var samples = {};

    function slug(s) {
        return (s || "").trim().toLowerCase()
            .replace(/ä/g, "ae").replace(/ö/g, "oe").replace(/ü/g, "ue").replace(/ß/g, "ss")
            .replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
    }
    function esc(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function addRow(field) {
        field = field || { label: "", type: "text" };
        var row = document.createElement("div");
        row.className = "field-row";
        if (field.id) row.setAttribute("data-id", field.id);

        var label = document.createElement("input");
        label.type = "text";
        label.placeholder = "Label";
        label.value = field.label || "";
        label.className = "fr-label";

        var sel = document.createElement("select");
        sel.className = "fr-type";
        TYPES.forEach(function (t) {
            var o = document.createElement("option");
            o.value = t[0]; o.textContent = t[1];
            if (t[0] === field.type) o.selected = true;
            sel.appendChild(o);
        });

        var rm = document.createElement("button");
        rm.type = "button"; rm.className = "btn btn-sm btn-danger btn-icon"; rm.innerHTML = '<i class="ti ti-trash" aria-hidden="true"></i>';
        rm.title = window.MATCMS_REMOVE || "Löschen";
        rm.addEventListener("click", function () { row.remove(); refresh(); });

        label.addEventListener("input", refresh);
        sel.addEventListener("change", refresh);

        row.appendChild(label);
        row.appendChild(sel);
        row.appendChild(rm);
        rows.appendChild(row);
    }

    function collect() {
        return Array.prototype.slice.call(rows.querySelectorAll(".field-row")).map(function (r) {
            var label = r.querySelector(".fr-label").value.trim();
            var type = r.querySelector(".fr-type").value;
            var existing = r.getAttribute("data-id");
            var id = existing || slug(label);
            return { id: id, label: label || id, type: type };
        }).filter(function (f) { return f.id; });
    }

    // ---- Live preview ----
    function defaultSample(f) {
        switch (f.type) {
            case "textarea": return (f.label || f.id) + " – Beispieltext für die Vorschau.";
            case "richtext": return "<p>" + (f.label || f.id) + " – <strong>Beispiel</strong>-Inhalt.</p>";
            case "image": return "data:image/svg+xml;utf8," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="600" height="280"><rect width="600" height="280" fill="#e2e5ea"/><text x="50%" y="50%" fill="#8a8f98" font-family="sans-serif" font-size="22" text-anchor="middle" dominant-baseline="middle">Bild</text></svg>');
            case "url": return "#";
            default: return f.label || f.id;
        }
    }
    function buildSamples(fields) {
        if (!sampleWrap) return;
        var prev = samples; samples = {};
        sampleWrap.innerHTML = "";
        fields.forEach(function (f) {
            samples[f.id] = (prev[f.id] != null) ? prev[f.id] : defaultSample(f);
            var row = document.createElement("label"); row.className = "cp-sample-row";
            var lab = document.createElement("span"); lab.className = "cp-sample-lab";
            lab.textContent = (f.label || f.id) + " · {{" + f.id + "}}";
            var multi = (f.type === "textarea" || f.type === "richtext");
            var inp = document.createElement(multi ? "textarea" : "input");
            inp.className = "cp-sample-inp"; inp.value = samples[f.id];
            if (multi) inp.rows = 2;
            inp.addEventListener("input", function () { samples[f.id] = inp.value; renderPreview(); });
            row.appendChild(lab); row.appendChild(inp);
            sampleWrap.appendChild(row);
        });
    }
    function substitute(tpl, fields) {
        var out = tpl;
        fields.forEach(function (f) {
            var v = samples[f.id] != null ? samples[f.id] : "";
            var rep = (f.type === "richtext") ? v : esc(v);
            out = out.split("{{" + f.id + "}}").join(rep);
        });
        return out;
    }
    function renderPreview() {
        if (!frame) return;
        var fields = collect();
        var out = substitute(templateEl ? templateEl.value : "", fields);
        frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8">' +
            '<link rel="stylesheet" href="/css/site.css">' +
            '<style>:root{--accent:#de7e11;--accent-dark:#bc6b0e;--accent-2:#22d3ee;--black:#111;--ink:#333;--bg:#fff;--bg-alt:#f6f7f9;--max:1180px;--btn-radius:0;--font-head:Inter,system-ui,sans-serif;--font-body:Inter,system-ui,sans-serif}' +
            'body{margin:0;padding:18px;background:#fff;color:#333;font-family:Inter,system-ui,sans-serif}</style>' +
            '</head><body>' + out + '</body></html>';
        updateDebug(templateEl ? templateEl.value : "", fields, out);
    }
    function updateDebug(tpl, fields, out) {
        if (!debugEl) return;
        var found = (tpl.match(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g) || []).map(function (m) { return m.replace(/[{}\s]/g, ""); });
        var uniq = found.filter(function (v, i) { return found.indexOf(v) === i; });
        var ids = fields.map(function (f) { return f.id; });
        var unknown = uniq.filter(function (p) { return ids.indexOf(p) === -1; });
        var unused = ids.filter(function (id) { return uniq.indexOf(id) === -1; });
        function chips(arr, bad) {
            return arr.length
                ? arr.map(function (x) { return '<code class="' + (bad ? "cp-bad" : "") + '">' + esc(x) + "</code>"; }).join(" ")
                : '<span class="muted">' + (CP.dbgEmpty || "—") + "</span>";
        }
        debugEl.innerHTML =
            '<div class="cp-dbg-row"><span>' + (CP.dbgPlaceholders || "Placeholders") + "</span><div>" + chips(uniq) + "</div></div>" +
            (unknown.length ? '<div class="cp-dbg-row"><span class="cp-warn">' + (CP.dbgUnknown || "Unknown") + "</span><div>" + chips(unknown, true) + "</div></div>" : "") +
            '<div class="cp-dbg-row"><span>' + (CP.dbgUnused || "Unused") + "</span><div>" + chips(unused) + "</div></div>" +
            (unknown.length === 0 ? '<div class="cp-dbg-ok">✓ ' + (CP.dbgOk || "") + "</div>" : "") +
            '<div class="cp-dbg-out"><div class="cp-dbg-out-label">' + (CP.dbgOutput || "Output") + "</div><pre>" + esc(out) + "</pre></div>";
    }

    var deb;
    function refresh() {
        if (hint) hint.textContent = collect().map(function (f) { return "{{" + f.id + "}}"; }).join("  ");
        buildSamples(collect());
        clearTimeout(deb);
        deb = setTimeout(renderPreview, 120);
    }

    // Seed rows from existing data.
    try {
        var initial = JSON.parse(rows.getAttribute("data-fields") || "[]");
        if (Array.isArray(initial)) initial.forEach(function (f) { addRow(f); });
    } catch (e) { /* ignore */ }
    if (!rows.querySelector(".field-row")) addRow();

    addBtn.addEventListener("click", function () { addRow(); refresh(); });
    if (templateEl) {
        templateEl.addEventListener("input", function () { clearTimeout(deb); deb = setTimeout(renderPreview, 150); });
    }
    if (debugToggle && debugEl) {
        debugToggle.addEventListener("click", function () {
            debugEl.hidden = !debugEl.hidden;
            debugToggle.classList.toggle("active", !debugEl.hidden);
        });
    }

    form.addEventListener("submit", function () {
        hidden.value = JSON.stringify(collect());
    });

    refresh();
})();
