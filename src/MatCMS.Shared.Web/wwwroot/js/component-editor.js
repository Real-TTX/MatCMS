// Der Komponenten-Editor — EIN Skript für CMS und Cloud, zur geteilten Ansicht
// (_ComponentEditor.cshtml). Es stand zweimal da: MatCMS/wwwroot/js/admin-component-editor.js und
// MatCMS.Cloud/wwwroot/js/component-editor.js. Beide taten dasselbe, und wer eines anfasste, ließ
// das andere zurück — dieselbe Falle, aus der die Ansicht schon herausgeholt wurde.
//
// Was die beiden WIRKLICH unterschied, steht jetzt als Parameter am Element #field-rows und nicht
// als Zweig "wenn Cloud, dann …":
//   data-fields         die gespeicherte Feldliste (wie bisher)
//   data-field-types    die Feldarten als [[wert, beschriftung], …] — übersetzt von der Anwendung
//   data-labels         die Wörter des Skripts (Entfernen-Titel, die Zeilen des Debug-Bereichs)
//   data-preview-theme  die Farben/Schriften, in denen die Vorschau zeichnet: im CMS die des Admin,
//                       in der Cloud die des Templates, das das Profil aktiviert
// Warum am Element und nicht mehr über window.MATCMS_* / window.CLOUD_*: die Ansicht liegt einmal,
// also darf ihre einzige Schnittstelle nicht davon abhängen, welche Anwendung sie rendert.
//
// CodeMirror ist ausdrücklich KEIN Schalter: ob am Vorlagenfeld ein Editor hängt, sieht das Skript
// dem DOM an. Wo keiner hängt (CMS), wird die Textfläche gelesen — dieselbe Zeile, kein Zweig.
//
// Aufgabe: wiederholbare Feldzeilen -> verstecktes FieldsJson, ein lebender Platzhalter-Hinweis,
// Beispieldaten je Feld, eine gezeichnete Vorschau im iframe und ein Debug-Bereich, der Platzhalter
// nennt, die die Vorlage benutzt, aber kein Feld definiert (der Fehler, der sonst erst als leere
// Stelle auf der Seite eines Kunden auffällt).
(function () {
    "use strict";
    var rows = document.getElementById("field-rows");
    var addBtn = document.getElementById("add-field");
    var hidden = document.getElementById("FieldsJson");
    var form = document.getElementById("component-form");
    var hint = document.getElementById("placeholder-hint");
    if (!rows || !form) return;

    // Die Parameter der Seite. Fehlt einer, arbeitet der Editor weiter — nur mit weniger: ohne
    // Feldarten bliebe jedes Feld "text", was die Vorschau falsch zeichnen würde, deshalb ist das
    // der einzige Wert mit einer wirklichen Rückfallposition.
    function data(name, fallback) {
        try {
            var raw = rows.getAttribute(name);
            if (!raw) return fallback;
            var parsed = JSON.parse(raw);
            return parsed == null ? fallback : parsed;
        } catch (e) { return fallback; }
    }
    var TYPES = data("data-field-types", [["text", "Text"]]);
    var CP = data("data-labels", {});
    var THEME = data("data-preview-theme", {});

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

    // Am Vorlagenfeld kann CodeMirror hängen (dort, wo die Seite das Bündel lädt). Dann steht der
    // aktuelle Text im Editor und nicht in der Textfläche, die erst beim Absenden nachgezogen wird —
    // also durch den Editor lesen, sobald es einen gibt. Wo keiner ist, ist das die Textfläche.
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
            // Ein vorhandenes Feld behält seine id, auch wenn die Beschriftung umbenannt wird — die
            // id ist das, worauf der {{platzhalter}} der Vorlage zeigt, und ein neues Schneiden
            // würde Blöcke stillschweigend zerlegen, die auf laufenden Seiten schon stehen.
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
            // Rich-Text wird als HTML eingesetzt, alles andere maskiert — genau wie der Renderer auf
            // der Instanz. Sonst schmeichelte die Vorschau einer kaputten Vorlage.
            var rep = (f.type === "richtext") ? v : esc(v);
            out = out.split("{{" + f.id + "}}").join(rep);
        });
        return out;
    }

    // Die Stilvariablen der Vorschau. Was die Seite nicht mitgibt, bleibt WEG statt auf einen Wert
    // gesetzt zu werden, den sie nicht gewählt hat: --accent-2 setzt nur das CMS, und ein von hier
    // erfundener Wert überschriebe stumm den der Stilvorlage.
    function themeCss() {
        var accent = THEME.accent || "#2563eb";
        var text = THEME.text || "#333";
        var background = THEME.background || "#fff";
        var bodyFont = THEME.bodyFont || "Inter";
        var vars = [
            "--accent:" + accent,
            "--accent-dark:" + (THEME.accentDark || accent),
            THEME.accent2 ? "--accent-2:" + THEME.accent2 : null,
            "--black:" + (THEME.heading || "#111"),
            "--ink:" + text,
            "--bg:" + background,
            "--bg-alt:" + (THEME.altBackground || "#f6f7f9"),
            "--max:" + (THEME.containerWidth || "1180") + "px",
            "--btn-radius:" + (THEME.buttonRadius || "0") + "px",
            "--font-head:" + (THEME.headingFont || "Inter") + ",system-ui,sans-serif",
            "--font-body:" + bodyFont + ",system-ui,sans-serif"
        ].filter(Boolean).join(";");
        return ":root{" + vars + "}" +
            "body{margin:0;padding:18px;background:" + background + ";color:" + text + ";" +
            "font-family:" + bodyFont + ",system-ui,sans-serif}";
    }

    function renderPreview() {
        if (!frame) return;
        var fields = collect();
        var tpl = templateValue();
        var out = substitute(tpl, fields);
        frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8">' +
            // Die Stilvorlage der öffentlichen Seite liegt in der geteilten Razor-Klassenbibliothek;
            // /css/site.css antwortete seither mit 404 und die Vorschau stand ungestylt da — sie
            // zeigte also gerade NICHT, wie der Block auf der Website aussieht.
            '<link rel="stylesheet" href="/_content/MatCMS.Shared.Web/css/site.css">' +
            '<style>' + themeCss() + '</style>' +
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
        // Feldliste — dieselbe Falle, die im Template- und im Plugin-Editor schon aufgeräumt wurde.
        // Der submit-Zuhörer bleibt trotzdem stehen: er kostet nichts und fängt einen Fall ab, in dem
        // refresh() aus irgendeinem Grund nicht mehr gelaufen ist.
        if (hidden) hidden.value = JSON.stringify(collect());
        if (hint) hint.textContent = collect().map(function (f) { return "{{" + f.id + "}}"; }).join("  ");
        buildSamples(collect());
        clearTimeout(deb);
        deb = setTimeout(renderPreview, 120);
    }

    try {
        var initial = JSON.parse(rows.getAttribute("data-fields") || "[]");
        if (Array.isArray(initial)) initial.forEach(function (f) { addRow(f); });
    } catch (e) { /* fehlerhaft gespeichertes JSON: lieber mit einer leeren Zeile anfangen als den Editor blockieren */ }
    if (!rows.querySelector(".field-row")) addRow();

    if (addBtn) addBtn.addEventListener("click", function () { addRow(); refresh(); });
    if (templateEl) {
        templateEl.addEventListener("input", function () { clearTimeout(deb); deb = setTimeout(renderPreview, 150); });
        // CodeMirror löst kein input auf der Textfläche aus, also am Editor selbst einhaken, sobald
        // es ihn gibt. WARTEN, BIS ES IHN GIBT: code-editor.js baut den Editor erst bei
        // DOMContentLoaded, das frühere setTimeout(0) lief davor und fand nichts — der Haken wurde
        // nie gesetzt, und die Vorschau zog beim Tippen IN DER VORLAGE nicht nach (erst wieder,
        // sobald ein Beispielfeld sie anstieß). Am laufenden System nachgewiesen, bevor es hier
        // stand. Wo gar kein CodeMirror geladen ist, findet der Haken nichts und kostet nichts.
        function hookCodeMirror() {
            var cm = templateEl.nextElementSibling && templateEl.nextElementSibling.CodeMirror;
            if (!cm || cm._cpHooked) return !!cm;
            cm._cpHooked = true;
            cm.on("change", function () { clearTimeout(deb); deb = setTimeout(renderPreview, 150); });
            return true;
        }
        if (!hookCodeMirror()) {
            document.addEventListener("DOMContentLoaded", hookCodeMirror);
            window.addEventListener("load", hookCodeMirror);
        }
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
