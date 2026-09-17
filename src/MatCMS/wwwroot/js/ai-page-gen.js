// AI "generate page": asks the cloud relay to propose a whole page as a list of blocks (the server
// tells the model which block types exist + the site's global instruction), renders the proposal as a
// VISUAL preview in an iframe (the real public layout, nothing saved), and — only on the operator's
// confirm — creates the blocks via a normal antiforgery-protected form post (PRG: the editor reloads
// with the new blocks). Present only when AI is switched on for the site.
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
    var frame = document.getElementById("ai-pagegen-frame");
    var form = document.getElementById("ai-pagegen-form");
    var dataEl = document.getElementById("ai-pagegen-data");
    var previewForm = document.getElementById("ai-pagegen-preview-form");
    var previewData = document.getElementById("ai-pagegen-preview-data");
    var previewTitle = document.getElementById("ai-pagegen-preview-title");
    var endpoint = btn.getAttribute("data-ai-url");
    var working = dlg.getAttribute("data-lbl-working") || "…";
    var proposed = null;

    function open() {
        statusEl.textContent = ""; proposed = null; applyBtn.disabled = true;
        try { frame.src = "about:blank"; } catch (e) { }
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
        statusEl.textContent = working; proposed = null; applyBtn.disabled = true; proposeBtn.disabled = true;

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
                proposed = data.proposed;
                // Render the proposal visually: post the blocks to the preview page, into the iframe.
                previewData.value = proposed;
                if (previewTitle) previewTitle.value = (instr && instr.value || "").trim() || "Vorschau";
                previewForm.submit();
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
