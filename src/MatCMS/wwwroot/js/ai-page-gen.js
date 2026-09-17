// AI "generate page": asks the cloud relay to propose a whole page as a list of blocks (the server
// tells the model which block types exist + the site's global instruction), shows the proposed blocks
// as a list, and — only on the operator's confirm — creates them via a normal antiforgery-protected
// form post (PRG: the editor reloads with the new blocks, ready to edit or delete). Nothing is written
// until "Anlegen". Present only when AI is switched on for the site.
(function () {
    "use strict";
    var btn = document.getElementById("ai-pagegen-btn");
    var dlg = document.getElementById("ai-pagegen-dialog");
    if (!btn || !dlg) return;

    var instr = document.getElementById("ai-pagegen-instruction");
    var proposeBtn = document.getElementById("ai-pagegen-propose");
    var applyBtn = document.getElementById("ai-pagegen-apply");
    var cancelBtn = document.getElementById("ai-pagegen-cancel");
    var statusEl = document.getElementById("ai-pagegen-status");
    var listEl = document.getElementById("ai-pagegen-list");
    var form = document.getElementById("ai-pagegen-form");
    var dataEl = document.getElementById("ai-pagegen-data");
    var endpoint = btn.getAttribute("data-ai-url");
    var working = dlg.getAttribute("data-lbl-working") || "…";
    var proposed = null;

    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function open() {
        listEl.innerHTML = ""; statusEl.textContent = ""; proposed = null; applyBtn.disabled = true;
        if (dlg.showModal) { try { dlg.showModal(); return; } catch (e) { } }
        dlg.setAttribute("open", "");
    }
    function close() {
        if (dlg.close) { try { dlg.close(); return; } catch (e) { } }
        dlg.removeAttribute("open");
    }

    btn.addEventListener("click", open);
    cancelBtn.addEventListener("click", close);
    dlg.addEventListener("cancel", function () { close(); });

    proposeBtn.addEventListener("click", function () {
        statusEl.textContent = working; listEl.innerHTML = ""; proposed = null;
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
                (data.blocks || []).forEach(function (b, i) {
                    var row = document.createElement("div"); row.className = "ai-pagegen-item";
                    row.innerHTML = '<span class="ai-pagegen-num">' + (i + 1) + '</span>'
                        + '<span class="ai-pagegen-name">' + esc(b.name || b.type) + '</span>'
                        + '<span class="ai-pagegen-snip">' + esc(b.snippet || "") + '</span>';
                    listEl.appendChild(row);
                });
                proposed = data.proposed;
                applyBtn.disabled = !proposed;
            })
            .catch(function (e) { statusEl.textContent = "Fehler: " + e.message; })
            .then(function () { proposeBtn.disabled = false; });
    });

    applyBtn.addEventListener("click", function () {
        if (!proposed || !form) return;
        dataEl.value = proposed;
        form.submit();   // → AiCreateBlocks: re-validates + appends the blocks → PRG reload
    });
})();
