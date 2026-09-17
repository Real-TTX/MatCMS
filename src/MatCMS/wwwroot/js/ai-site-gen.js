// AI "generate whole website": asks the cloud relay to propose several pages (each with blocks + a nav
// flag), shows one card per page as a switcher and renders the selected page VISUALLY in an iframe (the
// real public layout, nothing saved). On confirm, creates all pages via a normal antiforgery-protected
// form post (add-only; PRG reloads the page list). Present only when AI is switched on for the site.
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
    var tabsEl = document.getElementById("ai-sitegen-tabs");
    var frame = document.getElementById("ai-sitegen-frame");
    var form = document.getElementById("ai-sitegen-form");
    var dataEl = document.getElementById("ai-sitegen-data");
    var previewForm = document.getElementById("ai-sitegen-preview-form");
    var previewData = document.getElementById("ai-sitegen-preview-data");
    var previewTitle = document.getElementById("ai-sitegen-preview-title");
    var endpoint = btn.getAttribute("data-ai-url");
    var working = dlg.getAttribute("data-lbl-working") || "…";
    var lblNav = dlg.getAttribute("data-lbl-nav") || "menu";
    var proposed = null;
    var pages = [];

    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function open() {
        statusEl.textContent = ""; proposed = null; pages = []; applyBtn.disabled = true;
        tabsEl.innerHTML = "";
        try { frame.src = "about:blank"; } catch (e) { }
        if (dlg.showModal) { try { dlg.showModal(); return; } catch (e) { } }
        dlg.setAttribute("open", "");
    }
    function close() {
        if (dlg.close) { try { dlg.close(); return; } catch (e) { } }
        dlg.removeAttribute("open");
    }
    function showPage(i) {
        if (!pages[i]) return;
        Array.prototype.forEach.call(tabsEl.children, function (c, idx) {
            c.classList.toggle("is-active", idx === i);
        });
        try { previewData.value = JSON.stringify(pages[i].blocks || []); } catch (e) { previewData.value = "[]"; }
        if (previewTitle) previewTitle.value = pages[i].title || "Vorschau";
        previewForm.submit();
    }

    btn.addEventListener("click", open);
    cancelBtn.addEventListener("click", close);
    dlg.addEventListener("cancel", function () { close(); });

    proposeBtn.addEventListener("click", function () {
        statusEl.textContent = working; proposed = null; pages = []; tabsEl.innerHTML = "";
        applyBtn.disabled = true; proposeBtn.disabled = true;
        try { frame.src = "about:blank"; } catch (e) { }

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
                proposed = data.proposed;
                try { pages = JSON.parse(proposed) || []; } catch (e) { pages = []; }
                (data.pages || []).forEach(function (p, i) {
                    var tab = document.createElement("button");
                    tab.type = "button"; tab.className = "ai-preview-tab";
                    tab.innerHTML = esc(p.title) + (p.nav ? ' <span class="ai-preview-tab-nav">· ' + esc(lblNav) + '</span>' : '');
                    tab.addEventListener("click", function () { showPage(i); });
                    tabsEl.appendChild(tab);
                });
                applyBtn.disabled = !(proposed && pages.length);
                if (pages.length) showPage(0);
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
