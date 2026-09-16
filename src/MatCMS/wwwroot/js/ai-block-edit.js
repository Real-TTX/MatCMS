// AI "improve text" for the page/block editor: proposes a rewrite of the OPEN block's prose fields
// (relayed through the cloud), shows a Before/After per field, and — only on the operator's confirm —
// applies it through the NORMAL per-block SaveBlock (non-destructive PRG). Present only when AI is
// switched on for the site. Reads the current editor state via window.matBlockSerialize so unsaved
// edits are respected.
(function () {
    "use strict";
    var btn = document.getElementById("ai-block-btn");
    var dlg = document.getElementById("ai-dialog");
    if (!btn || !dlg) return;

    var instr = document.getElementById("ai-instruction");
    var proposeBtn = document.getElementById("ai-propose");
    var applyBtn = document.getElementById("ai-apply");
    var cancelBtn = document.getElementById("ai-cancel");
    var statusEl = document.getElementById("ai-status");
    var diffEl = document.getElementById("ai-diff");
    var applyForm = document.getElementById("ai-apply-form");
    var applyData = document.getElementById("ai-apply-data");
    var endpoint = btn.getAttribute("data-ai-url");
    var LBL = {
        before: dlg.getAttribute("data-lbl-before") || "Before",
        after: dlg.getAttribute("data-lbl-after") || "After",
        working: dlg.getAttribute("data-lbl-working") || "…"
    };
    var proposed = null;

    function open() {
        diffEl.innerHTML = ""; statusEl.textContent = ""; proposed = null; applyBtn.disabled = true;
        if (dlg.showModal) { try { dlg.showModal(); return; } catch (e) { } }
        dlg.setAttribute("open", "");
    }
    function close() {
        if (dlg.close) { try { dlg.close(); return; } catch (e) { } }
        dlg.removeAttribute("open");
    }
    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }

    btn.addEventListener("click", open);
    cancelBtn.addEventListener("click", close);
    dlg.addEventListener("cancel", function () { close(); });

    proposeBtn.addEventListener("click", function () {
        statusEl.textContent = LBL.working; diffEl.innerHTML = ""; proposed = null;
        applyBtn.disabled = true; proposeBtn.disabled = true;

        var current = "{}";
        try {
            current = window.matBlockSerialize
                ? JSON.stringify(window.matBlockSerialize())
                : (document.getElementById("DataJson") || {}).value || "{}";
        } catch (e) { }

        var body = new URLSearchParams();
        body.set("instruction", instr ? (instr.value || "") : "");
        body.set("dataJson", current);
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
                (data.changes || []).forEach(function (c) {
                    var row = document.createElement("div"); row.className = "ai-change";
                    row.innerHTML = '<div class="ai-field mono">' + esc(c.field) + '</div>'
                        + '<div class="ai-cols">'
                        + '<div class="ai-col ai-before"><div class="ai-lbl">' + esc(LBL.before) + '</div><div class="ai-text">' + esc(c.before) + '</div></div>'
                        + '<div class="ai-col ai-after"><div class="ai-lbl">' + esc(LBL.after) + '</div><div class="ai-text">' + esc(c.after) + '</div></div>'
                        + '</div>';
                    diffEl.appendChild(row);
                });
                proposed = data.proposed;
                applyBtn.disabled = !proposed;
            })
            .catch(function (e) { statusEl.textContent = "Fehler: " + e.message; })
            .then(function () { proposeBtn.disabled = false; });
    });

    applyBtn.addEventListener("click", function () {
        if (!proposed) return;
        applyData.value = proposed;
        applyForm.submit();   // → SaveBlock with the accepted DataJson → PRG reload with the new text
    });
})();
