// Generic "AI SEO field" widget: a [data-ai-seo] button proposes a short SEO text (meta description or
// blog teaser) for ONE target field, relayed through the cloud, shown as Before/After, and — only on the
// operator's confirm — written into that field. Present only when AI is switched on for the site.
//
// Button data- attributes:
//   data-ai-url       endpoint (a page handler that returns { ok, proposed, error })
//   data-target       id of the field to fill (input/textarea)
//   data-title        dialog heading
//   data-content-from (optional) id of an element whose value/innerHTML is sent as `content`
//   data-title-from   (optional) id of an element whose value is sent as `title`
(function () {
    "use strict";
    var dlg = document.getElementById("ai-seo-dialog");
    var buttons = document.querySelectorAll("[data-ai-seo]");
    if (!dlg || !buttons.length) return;

    var titleEl = document.getElementById("ai-seo-title");
    var instr = document.getElementById("ai-seo-instruction");
    var proposeBtn = document.getElementById("ai-seo-propose");
    var applyBtn = document.getElementById("ai-seo-apply");
    var cancelBtn = document.getElementById("ai-seo-cancel");
    var statusEl = document.getElementById("ai-seo-status");
    var diffEl = document.getElementById("ai-seo-diff");
    var LBL = {
        before: dlg.getAttribute("data-lbl-before") || "Vorher",
        after: dlg.getAttribute("data-lbl-after") || "Nachher",
        working: dlg.getAttribute("data-lbl-working") || "…"
    };
    var current = null;    // { url, target, contentFrom, titleFrom }
    var proposed = null;

    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function readEl(id) {
        if (!id) return "";
        var el = document.getElementById(id);
        if (!el) return "";
        return (el.value !== undefined && el.value !== null) ? el.value : (el.innerHTML || "");
    }
    function open(cfg) {
        current = cfg; proposed = null;
        diffEl.innerHTML = ""; statusEl.textContent = ""; applyBtn.disabled = true;
        if (instr) instr.value = "";
        titleEl.textContent = cfg.title || titleEl.textContent;
        if (dlg.showModal) { try { dlg.showModal(); return; } catch (e) { } }
        dlg.setAttribute("open", "");
    }
    function close() {
        if (dlg.close) { try { dlg.close(); return; } catch (e) { } }
        dlg.removeAttribute("open");
    }

    Array.prototype.forEach.call(buttons, function (b) {
        b.addEventListener("click", function () {
            open({
                url: b.getAttribute("data-ai-url"),
                target: b.getAttribute("data-target"),
                contentFrom: b.getAttribute("data-content-from"),
                titleFrom: b.getAttribute("data-title-from"),
                title: b.getAttribute("data-title")
            });
        });
    });
    cancelBtn.addEventListener("click", close);
    dlg.addEventListener("cancel", function () { close(); });

    proposeBtn.addEventListener("click", function () {
        if (!current) return;
        statusEl.textContent = LBL.working; diffEl.innerHTML = ""; proposed = null;
        applyBtn.disabled = true; proposeBtn.disabled = true;

        var body = new URLSearchParams();
        body.set("instruction", instr ? (instr.value || "") : "");
        if (current.contentFrom) body.set("content", readEl(current.contentFrom));
        if (current.titleFrom) body.set("title", readEl(current.titleFrom));
        var tok = document.querySelector('input[name="__RequestVerificationToken"]');
        if (tok) body.set("__RequestVerificationToken", tok.value);

        fetch(current.url, {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
            body: body.toString()
        })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (!data || !data.ok) { statusEl.textContent = (data && data.error) || "Kein Vorschlag."; return; }
                statusEl.textContent = "";
                proposed = data.proposed;
                var before = readEl(current.target);
                diffEl.innerHTML = '<div class="ai-change"><div class="ai-cols">'
                    + '<div class="ai-col ai-before"><div class="ai-lbl">' + esc(LBL.before) + '</div><div class="ai-text">' + esc(before) + '</div></div>'
                    + '<div class="ai-col ai-after"><div class="ai-lbl">' + esc(LBL.after) + '</div><div class="ai-text">' + esc(proposed) + '</div></div>'
                    + '</div></div>';
                applyBtn.disabled = !proposed;
            })
            .catch(function (e) { statusEl.textContent = "Fehler: " + e.message; })
            .then(function () { proposeBtn.disabled = false; });
    });

    applyBtn.addEventListener("click", function () {
        if (!proposed || !current) return;
        var el = document.getElementById(current.target);
        if (el) {
            if (el.value !== undefined && el.value !== null) el.value = proposed; else el.textContent = proposed;
            el.dispatchEvent(new Event("input", { bubbles: true }));
        }
        close();
    });
})();
