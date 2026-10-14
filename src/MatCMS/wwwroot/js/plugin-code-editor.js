// The script plugin editor's code field: syntax highlighting for C#, completion of the plugin API, and "Prüfen" (compile without running).
//
// The editor page (Pages/Admin/Plugins/Edit.cshtml) talks to a plain <textarea data-pf-content>: it reads and writes .value and listens for
// "input". This file puts a CodeMirror in front of that textarea and makes the textarea's .value / .focus() go through it, so the page's own
// script needs no change except two calls (setMode when another file opens, jump when a finding is clicked). Without CodeMirror, or on a
// phone (where the page has its own handling of the field), nothing here happens and the textarea works as it always did.
(function () {
    "use strict";
    var ta = document.querySelector("[data-pf-content]");
    if (!ta || typeof CodeMirror === "undefined") return;
    if (window.matchMedia && window.matchMedia("(max-width: 900px)").matches) return;

    var cm = CodeMirror(function (el) { ta.parentNode.insertBefore(el, ta); }, {
        value: ta.value,
        mode: "text/x-csharp",
        lineNumbers: true,
        matchBrackets: true,
        autoCloseBrackets: true,
        styleActiveLine: true,
        indentUnit: 4,
        tabSize: 4,
        extraKeys: {
            "Ctrl-Space": function (c) { complete(c, true); },
            Tab: function (c) { if (c.somethingSelected()) c.indentSelection("add"); else c.replaceSelection("    ", "end"); },
            Esc: function (c) { c.getInputField().blur(); }
        }
    });
    ta.style.display = "none";

    // The page sets and reads the textarea; route both through the editor. A programmatic set must not look like typing.
    var syncing = false;
    Object.defineProperty(ta, "value", {
        configurable: true,
        get: function () { return cm.getValue(); },
        set: function (v) { syncing = true; cm.setValue(v == null ? "" : String(v)); cm.clearHistory(); syncing = false; setTimeout(function () { cm.refresh(); }, 0); }
    });
    ta.focus = function () { cm.focus(); };
    cm.on("change", function () {
        clearMarks();
        if (!syncing) ta.dispatchEvent(new Event("input", { bubbles: true }));
    });

    // ---- language by file name ----
    function setMode(name) {
        name = (name || "").toLowerCase();
        cm.setOption("mode", /\.json$/.test(name) ? { name: "javascript", json: true } : /\.(js|mjs)$/.test(name) ? "javascript" : /\.css$/.test(name) ? "css" : "text/x-csharp");
    }

    // ---- findings ("Prüfen") are marked in the text and cleared by the next keystroke ----
    var marks = [];
    function clearMarks() { marks.forEach(function (m) { m.clear(); }); marks = []; }
    function mark(line, col, severity) {
        if (line < 1 || line > cm.lineCount()) return;
        var from = { line: line - 1, ch: Math.max(0, col - 1) };
        var text = cm.getLine(line - 1) || "";
        var to = { line: line - 1, ch: Math.max(from.ch + 1, Math.min(text.length, from.ch + (text.slice(from.ch).match(/^[\w.]+/) || [" "])[0].length)) };
        marks.push(cm.markText(from, to, { className: "pf-diag pf-diag-" + severity }));
    }
    function jump(line, col) {
        line = Math.max(1, Math.min(line, cm.lineCount()));
        cm.setCursor({ line: line - 1, ch: Math.max(0, (col || 1) - 1) });
        cm.scrollIntoView(null, 120);
        cm.focus();
    }

    // ---- completion from the API catalogue ----
    var catalog = null, loading = null;
    function loadCatalog() {
        if (catalog || loading) return loading;
        var url = ta.closest("[data-pf-catalog]") ? ta.closest("[data-pf-catalog]").getAttribute("data-pf-catalog") : null;
        if (!url) return null;
        loading = fetch(url, { credentials: "same-origin" }).then(function (r) { return r.json(); }).then(function (c) { catalog = c; }).catch(function () { });
        return loading;
    }
    loadCatalog();

    function stripType(t) { return (t || "").replace(/\[\]$/, "").replace(/<.*$/, ""); }

    // The names before the cursor's dot: "Plugin.Admin.Section(\"a\", \"b\")\n    .Page" -> ["Plugin","Admin","Section"].
    function chainBefore(text) {
        var i = text.length - 1;
        // the word being typed
        while (i >= 0 && /[\w]/.test(text[i])) i--;
        var names = [];
        for (;;) {
            while (i >= 0 && /\s/.test(text[i])) i--;
            if (i < 0 || text[i] !== ".") return names.length ? names : null;
            i--;
            while (i >= 0 && /\s/.test(text[i])) i--;
            if (text[i] === ")") {                       // skip a balanced argument list, backwards
                var depth = 0;
                for (; i >= 0; i--) { if (text[i] === ")") depth++; else if (text[i] === "(") { depth--; if (depth === 0) { i--; break; } } }
            }
            var end = i;
            while (i >= 0 && /[\w]/.test(text[i])) i--;
            if (end === i) return null;
            names.unshift(text.slice(i + 1, end + 1));
            if (names.length > 12) return null;
        }
    }

    function membersFor(names) {
        if (!catalog || !names || !names.length) return null;
        var type = catalog.roots[names[0]];
        if (!type) return null;
        for (var k = 1; k < names.length; k++) {
            var list = catalog.types[stripType(type)] || [];
            var m = list.filter(function (x) { return x.name === names[k]; })[0];
            if (!m) return null;
            type = stripType(m.returns);
        }
        return catalog.types[stripType(type)] || null;
    }

    function render(el, self, data) {
        var name = document.createElement("span"); name.className = "pf-hint-name"; name.textContent = data.text;
        el.appendChild(name);
        if (data.sig && data.sig !== data.text) { var s = document.createElement("span"); s.className = "pf-hint-sig"; s.textContent = data.sig.slice(data.text.length); el.appendChild(s); }
        if (data.returns && data.returns !== "void") { var r = document.createElement("span"); r.className = "pf-hint-ret"; r.textContent = " → " + data.returns; el.appendChild(r); }
        if (data.doc) { var d = document.createElement("div"); d.className = "pf-hint-doc"; d.textContent = data.doc; el.appendChild(d); }
    }

    function complete(c, explicit) {
        if (!catalog || !CodeMirror.showHint) { loadCatalog(); return; }
        CodeMirror.showHint(c, function (cmi) {
            var cur = cmi.getCursor();
            var before = cmi.getRange({ line: 0, ch: 0 }, cur);
            var word = (before.match(/[\w]*$/) || [""])[0];
            var from = { line: cur.line, ch: cur.ch - word.length };
            var names = chainBefore(before);
            var items;
            if (names) {
                var members = membersFor(names);
                if (!members) return null;
                var seen = {};
                items = members.filter(function (m) {
                    if (seen[m.name]) return false; seen[m.name] = true;
                    return m.name.toLowerCase().indexOf(word.toLowerCase()) === 0;
                });
            } else if (explicit || word.length >= 2) {
                // At the start of an expression: the names a script finds in scope.
                var all = Object.keys(catalog.roots).map(function (n) { return { name: n, sig: n, returns: catalog.roots[n], doc: "" }; });
                items = all.filter(function (m) { return m.name.toLowerCase().indexOf(word.toLowerCase()) === 0 && m.name !== word; });
            } else return null;
            if (!items.length) return null;
            return {
                from: from, to: cur,
                list: items.map(function (m) { return { text: m.name, sig: m.sig, returns: m.returns, doc: m.doc, render: render }; })
            };
        }, { completeSingle: false, closeCharacters: /[()\[\]{};:>,\s"]/ });
    }

    cm.on("inputRead", function (c, change) {
        if (!change.text || change.origin === "setValue") return;
        var t = change.text[0];
        if (t.slice(-1) === ".") complete(c, true);
        else if (/^\w$/.test(t) && !c.state.completionActive) {
            var line = c.getLine(c.getCursor().line).slice(0, c.getCursor().ch);
            if (/(^|[^\w.])(Pl|Ui|ct|Pu|Jo|Da|Ho|Ad|Fi|Se|Co)\w*$/.test(line) || /\.\w$/.test(line)) complete(c, false);
        }
    });

    window.PluginCodeEditor = { setMode: setMode, jump: jump, mark: mark, clear: clearMarks, editor: cm };

    // ---- "Prüfen" ----
    var btn = document.querySelector("[data-pf-check]");
    var panel = document.querySelector("[data-pf-diag]");
    if (btn && panel) btn.addEventListener("click", function () {
        var form = document.getElementById("plugin-form");
        if (!form) return;
        btn.disabled = true;
        panel.hidden = false;
        panel.className = "pf-diag-panel";
        panel.textContent = btn.getAttribute("data-t-working") || "…";
        var fd = new FormData(form);
        fetch(btn.getAttribute("data-pf-check"), { method: "POST", body: fd, credentials: "same-origin" })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                panel.textContent = "";
                clearMarks();
                if (!d.ok) { panel.className = "pf-diag-panel is-error"; panel.textContent = d.error || "?"; return; }
                var errors = d.diagnostics.filter(function (x) { return x.severity === "error"; }).length;
                var head = document.createElement("div");
                head.className = "pf-diag-head";
                head.textContent = d.diagnostics.length === 0 ? btn.getAttribute("data-t-ok")
                    : (errors + " " + btn.getAttribute("data-t-errors") + ", " + (d.diagnostics.length - errors) + " " + btn.getAttribute("data-t-warnings"));
                panel.className = "pf-diag-panel " + (errors ? "is-error" : d.diagnostics.length ? "is-warn" : "is-ok");
                panel.appendChild(head);
                var current = document.querySelector("[data-pf-current]");
                var open = current ? current.textContent : "plugin.csx";
                d.diagnostics.forEach(function (x) {
                    var row = document.createElement("button");
                    row.type = "button"; row.className = "pf-diag-row is-" + x.severity;
                    row.textContent = x.file + ":" + x.line + ":" + x.column + "  " + x.id + "  " + x.message;
                    row.addEventListener("click", function () { document.dispatchEvent(new CustomEvent("pf:open", { detail: { file: x.file, line: x.line, column: x.column } })); });
                    panel.appendChild(row);
                    if (x.file === open) mark(x.line, x.column, x.severity);
                });
            })
            .catch(function () { panel.className = "pf-diag-panel is-error"; panel.textContent = "?"; })
            .finally(function () { btn.disabled = false; });
    });
})();
