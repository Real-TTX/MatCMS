// Live theme preview for the profile's template editor.
//
// MatCMS has no such preview — its designer shows values, not the result. Here it matters more:
// a template is authored blind, without the site it will style, so the operator needs to SEE the
// theme before it lands on a customer's homepage.
//
// The preview renders a realistic sample page (header, hero, buttons, cards, text) into an iframe,
// driven purely by the form's current values. When the template supplies its own layout HTML with
// {{content}}, that layout is used and the sample page is dropped into the placeholder — so a broken
// layout looks broken here rather than in production.
(function () {
    "use strict";
    var form = document.getElementById("template-form");
    var frame = document.getElementById("tpl-frame");
    if (!form || !frame) return;

    var L = window.CLOUD_TPL_PREVIEW || {};

    function val(id, fallback) {
        var el = document.getElementById(id);
        if (!el) return fallback;
        // Code fields are CodeMirror-backed; read the editor, not the hidden textarea.
        var cm = el.nextElementSibling && el.nextElementSibling.CodeMirror;
        var v = (cm ? cm.getValue() : el.value);
        v = (v == null ? "" : String(v)).trim();
        return v === "" ? fallback : v;
    }

    function esc(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    // ---- Sample menus -------------------------------------------------------
    // A theme is judged by how its navigation looks, so every {{menu:slot}} is filled with real
    // entries instead of being marked as unresolved. Deterministic per slot rather than random: the
    // preview must not reshuffle on every keystroke, but two different slots should look different.
    var MENU_POOL = ["Start", "Über uns", "Leistungen", "Referenzen", "Blog", "Kontakt", "Team", "Preise", "Impressum", "Datenschutz"];

    function slotSeed(slot) {
        var n = 0;
        for (var i = 0; i < slot.length; i++) { n = (n * 31 + slot.charCodeAt(i)) % 9973; }
        return n;
    }

    function sampleItems(slot) {
        var seed = slotSeed(slot);
        var count = 3 + (seed % 3);              // 3 to 5 entries
        var items = [];
        for (var i = 0; i < count; i++) { items.push(MENU_POOL[(seed + i * 3) % MENU_POOL.length]); }
        return items;
    }

    function sampleMenu(slot) {
        return '<nav class="tp-nav">' + sampleItems(slot).map(function (label) {
            return '<a href="#">' + esc(label) + '</a>';
        }).join("") + '</nav>';
    }

    // Both menu forms the CMS renders: the plain {{menu:slot}} and the per-item loop
    // {{#menu:slot}} … {{label}}/{{url}}/{{icon}}/{{target}} … {{/menu:slot}}. A layout authored here
    // therefore behaves the same way once it is on an instance.
    function expandMenus(html) {
        var loop = new RegExp("\\{\\{#menu:([a-zA-Z0-9_-]+)\\}\\}([\\s\\S]*?)\\{\\{/menu:\\1\\}\\}", "g");
        html = html.replace(loop, function (m, slot, inner) {
            return sampleItems(slot).map(function (label) {
                return inner
                    .replace(/\{\{label\}\}/g, esc(label))
                    .replace(/\{\{url\}\}/g, "#")
                    .replace(/\{\{icon\}\}/g, "")
                    .replace(/\{\{target\}\}/g, "");
            }).join("");
        });
        return html.replace(/\{\{menu:([a-zA-Z0-9_-]+)\}\}/g, function (m, slot) { return sampleMenu(slot); });
    }

    function samplePage(t) {
        return '' +
            '<header class="tp-header"><div class="tp-wrap tp-header-in">' +
            '<strong class="tp-logo">' + esc(L.brand || "Beispielseite") + '</strong>' +
            sampleMenu("primary") +
            '</div></header>' +
            '<section class="tp-hero"><div class="tp-wrap">' +
            '<h1>' + esc(L.heroTitle || "Überschrift im Template-Stil") + '</h1>' +
            '<p class="tp-lead">' + esc(L.heroText || "Ein Absatz Fließtext, um Schriftart, Zeilenhöhe und Textfarbe zu beurteilen.") + '</p>' +
            '<p><a class="tp-btn" href="#">' + esc(L.cta || "Aktion") + '</a> <a class="tp-btn tp-btn-2" href="#">' + esc(L.cta2 || "Zweitaktion") + '</a></p>' +
            '</div></section>' +
            '<section class="tp-alt"><div class="tp-wrap tp-cards">' +
            '<div class="tp-card"><h3>' + esc(L.card || "Karte") + ' 1</h3><p>' + esc(L.cardText || "Kurzer Beschreibungstext.") + '</p></div>' +
            '<div class="tp-card"><h3>' + esc(L.card || "Karte") + ' 2</h3><p>' + esc(L.cardText || "Kurzer Beschreibungstext.") + '</p></div>' +
            '<div class="tp-card"><h3>' + esc(L.card || "Karte") + ' 3</h3><p>' + esc(L.cardText || "Kurzer Beschreibungstext.") + '</p></div>' +
            '</div></section>';
    }

    function render() {
        var accent = val("accentColor", "#de7e11");
        var secondary = val("secondaryColor", accent);
        var heading = val("headingColor", "#010101");
        var text = val("textColor", "#1a1a1a");
        var bg = val("backgroundColor", "#ffffff");
        var alt = val("altBackground", "#f6f7f9");
        var headerBg = val("headerBackground", "rgba(255,255,255,.9)");
        var headerText = val("headerTextColor", text);
        var headingFont = val("headingFont", "Geologica");
        var bodyFont = val("bodyFont", "Inter");
        var width = val("containerWidth", "1180");
        var radius = val("buttonRadius", "0");
        var headerPad = val("headerPadding", "16");
        var outline = (document.getElementById("buttonStyle") || {}).value === "outline";
        var customCss = val("customCss", "");
        var layout = val("layoutHtml", "");

        var body = layout.indexOf("{{content}}") !== -1
            ? expandMenus(layout.split("{{content}}").join(samplePage()))
                   .replace(/\{\{logo\}\}/g, esc(L.brand || "Beispielseite"))
                   .replace(/\{\{nav\}\}/g, sampleMenu("primary"))
                   .replace(/\{\{site_name\}\}/g, esc(L.brand || "Beispielseite"))
                   .replace(/\{\{footer_text\}\}/g, esc(L.brand || "Beispielseite"))
                   .replace(/\{\{year\}\}/g, String(new Date().getFullYear()))
                   .replace(/\{\{footer\}\}/g, '<footer class="tp-foot">© ' + esc(L.brand || "Beispielseite") + '</footer>')
                   // Any remaining {{token}} is shown as a visible marker instead of raw braces, so
                   // an unresolved placeholder is obvious at a glance.
                   .replace(/\{\{[^}]+\}\}/g, function (m) { return '<span class="tp-token">' + esc(m) + '</span>'; })
            : samplePage();

        var fonts = encodeURIComponent(headingFont) + ':wght@500;600;700&family=' + encodeURIComponent(bodyFont) + ':wght@400;500;600';

        frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8">' +
            '<link href="https://fonts.googleapis.com/css2?family=' + fonts + '&display=swap" rel="stylesheet">' +
            '<style>' +
            '*{box-sizing:border-box}' +
            'body{margin:0;background:' + bg + ';color:' + text + ';font-family:"' + bodyFont + '",system-ui,sans-serif;line-height:1.65;font-size:16px}' +
            'h1,h2,h3{font-family:"' + headingFont + '",system-ui,sans-serif;color:' + heading + ';margin:0 0 .4em}' +
            'h1{font-size:34px}h3{font-size:18px}' +
            '.tp-wrap{max-width:' + width + 'px;margin:0 auto;padding:0 22px}' +
            '.tp-header{background:' + headerBg + ';color:' + headerText + ';border-bottom:1px solid rgba(0,0,0,.08)}' +
            '.tp-header-in{display:flex;align-items:center;justify-content:space-between;padding:' + headerPad + 'px 22px}' +
            '.tp-logo{font-family:"' + headingFont + '",sans-serif;font-size:18px;color:' + headerText + '}' +
            '.tp-nav a{color:' + headerText + ';text-decoration:none;margin-left:18px;font-size:14px}' +
            '.tp-hero{padding:52px 0 44px}' +
            '.tp-lead{max-width:60ch;color:' + text + '}' +
            '.tp-btn{display:inline-block;margin-right:10px;padding:11px 22px;border-radius:' + radius + 'px;font-size:14px;font-weight:600;text-decoration:none;' +
            (outline
                ? 'background:transparent;color:' + accent + ';border:2px solid ' + accent + ';'
                : 'background:' + accent + ';color:#fff;border:2px solid ' + accent + ';') + '}' +
            '.tp-btn-2{' + (outline ? 'color:' + secondary + ';border-color:' + secondary + ';' : 'background:' + secondary + ';border-color:' + secondary + ';') + '}' +
            '.tp-alt{background:' + alt + ';padding:38px 0}' +
            '.tp-cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:18px}' +
            '.tp-card{background:' + bg + ';border:1px solid rgba(0,0,0,.08);border-radius:' + radius + 'px;padding:18px}' +
            '.tp-foot{padding:22px;text-align:center;font-size:13px;opacity:.7}' +
            '.tp-token{background:#fde8e8;color:#b42318;font-family:monospace;font-size:12px;padding:1px 5px;border-radius:4px}' +
            customCss +
            '</style></head><body>' + body + '</body></html>';
    }

    var deb;
    function schedule() { clearTimeout(deb); deb = setTimeout(render, 160); }

    form.addEventListener("input", schedule);
    form.addEventListener("change", schedule);

    // Code fields need their own hook — CodeMirror never fires input on the textarea.
    // WARTEN, BIS ES DEN EDITOR GIBT: code-editor.js baut ihn erst bei DOMContentLoaded, ein
    // setTimeout(0) lief davor und fand nichts — der Haken wurde also gar nicht gesetzt und die
    // Vorschau blieb beim Tippen IM CODE stehen, bis irgendein anderes Feld sie anstieß. Deshalb
    // wird der Versuch wiederholt, statt ihn an einen Zeitpunkt zu hängen.
    ["layoutHtml", "customCss"].forEach(function (id) {
        function hook() {
            var el = document.getElementById(id);
            var cm = el && el.nextElementSibling && el.nextElementSibling.CodeMirror;
            if (!cm || cm._tpHooked) return !!cm;
            cm._tpHooked = true;
            cm.on("change", schedule);
            return true;
        }
        if (hook()) return;
        document.addEventListener("DOMContentLoaded", hook);
        window.addEventListener("load", hook);
    });

    render();
})();
