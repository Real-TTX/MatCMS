// Component designer — the same editor MatCMS ships (wwwroot/js/admin-component-editor.js),
// adapted for the profile page. Keep the two in sync: a component authored here has to look and
// behave exactly like one authored on an instance, or the rollout produces surprises.
//
// Repeatable field rows -> hidden FieldsJson, a live placeholder hint, sample data per field, a
// rendered preview in an iframe, and a debug panel that names placeholders the template uses but no
// field defines (the mistake that otherwise only shows up as an empty spot on a customer's site).
(function () {
    "use strict";
    var rows = document.getElementById("field-rows");
    var addBtn = document.getElementById("add-field");
    var hidden = document.getElementById("FieldsJson");
    var form = document.getElementById("component-form");
    var hint = document.getElementById("placeholder-hint");
    if (!rows || !form) return;

    var TYPES = window.CLOUD_FIELD_TYPES || [["text", "Text"]];
    var CP = window.CLOUD_CP || {};

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

    // The template textarea is upgraded to CodeMirror; read through the editor when it exists so the
    // preview follows keystrokes instead of the stale textarea value.
    function templateValue() {
        if (!templateEl) return "";
        var cm = templateEl.nextElementSibling && templateEl.nextElementSibling.CodeMirror;
        return cm ? cm.getValue() : templateEl.value;
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
        rm.type = "button";
        rm.className = "btn btn-sm btn-danger btn-icon";
        rm.innerHTML = '<i class="ti ti-trash" aria-hidden="true"></i>';
        rm.title = CP.remove || "Entfernen";
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
            // An existing field keeps its id even when the label is renamed — the id is what the
            // template's {{placeholder}} refers to, and re-slugging it would silently break blocks
            // already using this component on live sites.
            var existing = r.getAttribute("data-id");
            var id = existing || slug(label);
            return { id: id, label: label || id, type: type };
        }).filter(function (f) { return f.id; });
    }

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
            // Rich text is inserted as HTML; everything else is escaped, exactly as the renderer on
            // the instance does it — otherwise the preview would flatter a broken template.
            var rep = (f.type === "richtext") ? v : esc(v);
            out = out.split("{{" + f.id + "}}").join(rep);
        });
        return out;
    }
    function renderPreview() {
        if (!frame) return;
        var fields = collect();
        var tpl = templateValue();
        var out = substitute(tpl, fields);
        // The preview borrows the theme variables of the profile's activated template when there is
        // one, so a component is judged against the design it will actually live in.
        var theme = window.CLOUD_PREVIEW_THEME || {};
        frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8">' +
            // The public-site stylesheet moved into the shared Razor Class Library; the old /css/site.css
            // 404s here, which left every component preview unstyled apart from the variables below.
            '<link rel="stylesheet" href="/_content/MatCMS.Shared.Web/css/site.css">' +
            '<style>:root{' +
            '--accent:' + (theme.accent || "#2563eb") + ';' +
            '--accent-dark:' + (theme.accent || "#2563eb") + ';' +
            '--black:' + (theme.heading || "#111") + ';' +
            '--ink:' + (theme.text || "#333") + ';' +
            '--bg:' + (theme.background || "#fff") + ';' +
            '--bg-alt:' + (theme.altBackground || "#f6f7f9") + ';' +
            '--max:' + (theme.containerWidth || "1180") + 'px;' +
            '--btn-radius:' + (theme.buttonRadius || "0") + 'px;' +
            '--font-head:' + (theme.headingFont || "Inter") + ',system-ui,sans-serif;' +
            '--font-body:' + (theme.bodyFont || "Inter") + ',system-ui,sans-serif}' +
            'body{margin:0;padding:18px;background:' + (theme.background || "#fff") + ';color:' + (theme.text || "#333") + ';' +
            'font-family:' + (theme.bodyFont || "Inter") + ',system-ui,sans-serif}</style>' +
            '</head><body>' + out + '</body></html>';
        updateDebug(tpl, fields, out);
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
            '<div class="cp-dbg-row"><span>' + (CP.dbgPlaceholders || "Platzhalter") + "</span><div>" + chips(uniq) + "</div></div>" +
            (unknown.length ? '<div class="cp-dbg-row"><span class="cp-warn">' + (CP.dbgUnknown || "Unbekannt") + "</span><div>" + chips(unknown, true) + "</div></div>" : "") +
            '<div class="cp-dbg-row"><span>' + (CP.dbgUnused || "Ungenutzt") + "</span><div>" + chips(unused) + "</div></div>" +
            (unknown.length === 0 ? '<div class="cp-dbg-ok">✓ ' + (CP.dbgOk || "") + "</div>" : "") +
            '<div class="cp-dbg-out"><div class="cp-dbg-out-label">' + (CP.dbgOutput || "Ausgabe") + "</div><pre>" + esc(out) + "</pre></div>";
    }

    var deb;
    function refresh() {
        // Jede Änderung geht SOFORT in das Feld, das abgeschickt wird — bei jedem Tastendruck in
        // einer Feldzeile, bei jedem Typwechsel, beim Hinzufügen und beim Entfernen.
        // WIE ES VORHER WAR: FieldsJson entstand ausschließlich im submit-Zuhörer weiter unten. Alles,
        // was das Formular auf einem anderen Weg abschickte oder die Seite verließ, verlor die
        // Feldliste. Der submit-Zuhörer bleibt trotzdem stehen: er kostet nichts und fängt einen Fall
        // ab, in dem refresh() aus irgendeinem Grund nicht mehr gelaufen ist.
        if (hidden) hidden.value = JSON.stringify(collect());
        if (hint) hint.textContent = collect().map(function (f) { return "{{" + f.id + "}}"; }).join("  ");
        buildSamples(collect());
        clearTimeout(deb);
        deb = setTimeout(renderPreview, 120);
    }

    try {
        var initial = JSON.parse(rows.getAttribute("data-fields") || "[]");
        if (Array.isArray(initial)) initial.forEach(function (f) { addRow(f); });
    } catch (e) { /* malformed stored JSON: start with an empty row rather than blocking the editor */ }
    if (!rows.querySelector(".field-row")) addRow();

    if (addBtn) addBtn.addEventListener("click", function () { addRow(); refresh(); });
    if (templateEl) {
        templateEl.addEventListener("input", function () { clearTimeout(deb); deb = setTimeout(renderPreview, 150); });
        // CodeMirror does not fire input on the textarea, so hook the editor once it exists.
        setTimeout(function () {
            var cm = templateEl.nextElementSibling && templateEl.nextElementSibling.CodeMirror;
            if (cm) cm.on("change", function () { clearTimeout(deb); deb = setTimeout(renderPreview, 150); });
        }, 0);
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
