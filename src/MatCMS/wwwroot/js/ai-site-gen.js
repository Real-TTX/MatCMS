// AI "generate whole website": asks the cloud relay to propose several pages (each with blocks, and a
// nav flag), shows them as a list, and — only on confirm — creates them via a normal antiforgery-
// protected form post (add-only: existing pages are never overwritten; PRG reloads the page list).
// Nothing is written until "Website anlegen". Present only when AI is switched on for the site.
(function () {
    "use strict";
    var btn = document.getElementById("ai-sitegen-btn");
    var dlg = document.getElementById("ai-sitegen-dialog");
    if (!btn || !dlg) return;

    var instr = document.getElementById("ai-sitegen-instruction");
    var proposeBtn = document.getElementById("ai-sitegen-propose");
    var applyBtn = document.getElementById("ai-sitegen-apply");
    var cancelBtn = document.getElementById("ai-sitegen-cancel");
    var statusEl = document.getElementById("ai-sitegen-status");
    var listEl = document.getElementById("ai-sitegen-list");
    var form = document.getElementById("ai-sitegen-form");
    var dataEl = document.getElementById("ai-sitegen-data");
    var endpoint = btn.getAttribute("data-ai-url");
    var working = dlg.getAttribute("data-lbl-working") || "…";
    var lblBlocks = dlg.getAttribute("data-lbl-blocks") || "blocks";
    var lblNav = dlg.getAttribute("data-lbl-nav") || "menu";
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
        body.set("briefing", instr ? (instr.value || "") : "");
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
                (data.pages || []).forEach(function (p, i) {
                    var meta = "/" + esc(p.slug) + " · " + p.blocks + " " + esc(lblBlocks) + (p.nav ? " · " + esc(lblNav) : "");
                    var row = document.createElement("div"); row.className = "ai-pagegen-item";
                    row.innerHTML = '<span class="ai-pagegen-num">' + (i + 1) + '</span>'
                        + '<span class="ai-pagegen-name">' + esc(p.title) + '</span>'
                        + '<span class="ai-pagegen-snip">' + meta + '</span>';
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
        form.submit();   // → AiCreateSite: re-validates + creates the pages → PRG reload
    });
})();
