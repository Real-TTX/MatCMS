// AI "adjust design" for the template Customize page: asks the cloud relay to propose new values for
// the template's published design parameters, shows a Before/After per parameter (with a colour swatch
// where the parameter is a colour) and — only on the operator's confirm — writes the accepted values
// into the REAL form controls and submits the normal save (non-destructive PRG). Present only when AI
// is switched on for the site. Nothing is applied until "Übernehmen".
(function () {
    "use strict";
    var btn = document.getElementById("ai-theme-btn");
    var dlg = document.getElementById("ai-theme-dialog");
    if (!btn || !dlg) return;

    var instr = document.getElementById("ai-theme-instruction");
    var proposeBtn = document.getElementById("ai-theme-propose");
    var applyBtn = document.getElementById("ai-theme-apply");
    var cancelBtn = document.getElementById("ai-theme-cancel");
    var statusEl = document.getElementById("ai-theme-status");
    var diffEl = document.getElementById("ai-theme-diff");
    var form = document.getElementById("customize-form");
    var endpoint = btn.getAttribute("data-ai-url");
    var LBL = {
        before: dlg.getAttribute("data-lbl-before") || "Vorher",
        after: dlg.getAttribute("data-lbl-after") || "Nachher",
        working: dlg.getAttribute("data-lbl-working") || "…"
    };
    var accepted = null;   // the list of {id,type,after} to write into the form on confirm

    function open() {
        diffEl.innerHTML = ""; statusEl.textContent = ""; accepted = null; applyBtn.disabled = true;
        if (dlg.showModal) { try { dlg.showModal(); return; } catch (e) { } }
        dlg.setAttribute("open", "");
    }
    function close() {
        if (dlg.close) { try { dlg.close(); return; } catch (e) { } }
        dlg.removeAttribute("open");
    }
    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }

    // One side of a change: a colour parameter gets a swatch next to its hex, everything else is text.
    function cell(kind, type, value) {
        var body = type === "color"
            ? '<span class="ai-swatch" style="background:' + esc(value) + '"></span><span class="mono">' + esc(value) + '</span>'
            : esc(value);
        return '<div class="ai-col ai-' + kind + '"><div class="ai-lbl">'
            + esc(kind === "before" ? LBL.before : LBL.after) + '</div>'
            + '<div class="ai-text">' + body + '</div></div>';
    }

    btn.addEventListener("click", open);
    cancelBtn.addEventListener("click", close);
    dlg.addEventListener("cancel", function () { close(); });

    proposeBtn.addEventListener("click", function () {
        statusEl.textContent = LBL.working; diffEl.innerHTML = ""; accepted = null;
        applyBtn.disabled = true; proposeBtn.disabled = true;

        var body = new URLSearchParams();
        body.set("instruction", instr ? (instr.value || "") : "");
        var tok = document.querySelector('input[name="__RequestVerificationToken"]');
        if (tok) body.set("__RequestVerificationToken", tok.value);

        fetch(endpoint, {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
            body: body.toString()
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (!data || !data.ok) { statusEl.textContent = (data && data.error) || "Kein Vorschlag."; return; }
                statusEl.textContent = "";
                accepted = [];
                (data.changes || []).forEach(function (c) {
                    accepted.push({ id: c.id, type: c.type, after: c.after });
                    var row = document.createElement("div"); row.className = "ai-change";
                    row.innerHTML = '<div class="ai-field">' + esc(c.label || c.id) + '</div>'
                        + '<div class="ai-cols">' + cell("before", c.type, c.before) + cell("after", c.type, c.after) + '</div>';
                    diffEl.appendChild(row);
                });
                applyBtn.disabled = !(accepted && accepted.length);
            })
            .catch(function (e) { statusEl.textContent = "Fehler: " + e.message; })
            .then(function () { proposeBtn.disabled = false; });
    });

    applyBtn.addEventListener("click", function () {
        if (!accepted || !accepted.length || !form) return;
        accepted.forEach(function (c) {
            // Brackets in the name break a CSS selector — resolve by name instead.
            var el = document.getElementsByName("Val[" + c.id + "]")[0];
            if (!el) return;
            if (el.type === "checkbox") el.checked = (String(c.after) === "true");
            else el.value = c.after;
        });
        close();
        form.submit();   // → OnPostAsync saves the new values → PRG back to the template list
    });
})();
