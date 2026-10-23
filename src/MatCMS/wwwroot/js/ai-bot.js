// The AI bot's widget (plugin "bot"): a chat bubble on every page and/or a panel where the block "AI Bot" stands. It asks /ai-bot/config what to
// draw and /ai-bot/ask for answers. Everything a visitor or the model wrote is put on the page as TEXT (DOM nodes, never innerHTML); the only markup
// read from an answer is **bold**, "- " lists and [links](/local or https), and a link is only made for those two kinds of address.
// The conversation lives in sessionStorage (this tab only, gone when it is closed), so it survives following a link the bot gave.
(function () {
    "use strict";
    if (window.__matBot) return;
    window.__matBot = 1;

    var script = document.currentScript;
    var vm = script && /[?&]v=([^&]+)/.exec(script.src), version = vm ? vm[1] : "1";
    var lang = (document.documentElement.getAttribute("lang") || "de").slice(0, 2).toLowerCase();
    var KEY = "matbot";
    var cfg = null, state = load(), panels = [], busy = false;

    function load() {
        try { var s = JSON.parse(sessionStorage.getItem(KEY) || "null"); if (s && s.session && Array.isArray(s.msgs)) return s; } catch (e) { }
        return { session: newId(), msgs: [] };
    }
    function save() { try { state.msgs = state.msgs.slice(-30); sessionStorage.setItem(KEY, JSON.stringify(state)); } catch (e) { } }
    function newId() {
        var a = new Uint8Array(12); (window.crypto || window.msCrypto).getRandomValues(a);
        return Array.prototype.map.call(a, function (b) { return ("0" + b.toString(16)).slice(-2); }).join("");
    }
    function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
    function safeHref(u) { u = String(u || "").trim(); return (/^\/(?!\/)/.test(u) || /^https:\/\//i.test(u)) && !/[\s"'<>\\]/.test(u) ? u : null; }

    // ---- a small, safe reading of the answer ------------------------------------------------------------------------
    function inline(parent, text) {
        var re = /\*\*([^*]+)\*\*|\[([^\]]{1,200})\]\(([^)\s]{1,500})\)/g, last = 0, m;
        while ((m = re.exec(text))) {
            if (m.index > last) parent.appendChild(document.createTextNode(text.slice(last, m.index)));
            if (m[1]) parent.appendChild(el("strong", null, m[1]));
            else {
                var href = safeHref(m[3]);
                if (href) {
                    var a = el("a", null, m[2]); a.href = href;
                    if (/^https:/i.test(href)) { a.target = "_blank"; a.rel = "noopener noreferrer"; }
                    parent.appendChild(a);
                } else parent.appendChild(document.createTextNode(m[2]));
            }
            last = re.lastIndex;
        }
        if (last < text.length) parent.appendChild(document.createTextNode(text.slice(last)));
    }
    function rich(text) {
        var box = el("div", "mb-text"), list = null;
        String(text || "").split(/\n/).forEach(function (line) {
            var t = line.trim();
            if (!t) { list = null; return; }
            var li = /^([-*•]|\d+[.)])\s+(.*)$/.exec(t);
            if (li) {
                if (!list) { list = el(/^\d/.test(li[1]) ? "ol" : "ul"); box.appendChild(list); }
                var item = el("li"); inline(item, li[2]); list.appendChild(item);
            } else {
                list = null;
                var p = el("p"); inline(p, t.replace(/^#+\s*/, "")); box.appendChild(p);
            }
        });
        return box;
    }

    // ---- panels ------------------------------------------------------------------------------------------------------
    function initials(name) { return String(name || "?").split(/\s+/).filter(Boolean).slice(0, 2).map(function (w) { return w[0]; }).join("").toUpperCase(); }
    function avatar() {
        var a = el("span", "mb-avatar");
        if (cfg.avatar && safeHref(cfg.avatar)) { var img = el("img"); img.src = cfg.avatar; img.alt = ""; a.appendChild(img); }
        else a.textContent = initials(cfg.name);
        return a;
    }
    function icon(path) {
        var ns = "http://www.w3.org/2000/svg", s = document.createElementNS(ns, "svg");
        s.setAttribute("viewBox", "0 0 24 24"); s.setAttribute("width", "22"); s.setAttribute("height", "22"); s.setAttribute("fill", "none");
        s.setAttribute("stroke", "currentColor"); s.setAttribute("stroke-width", "2"); s.setAttribute("stroke-linecap", "round"); s.setAttribute("stroke-linejoin", "round");
        s.setAttribute("aria-hidden", "true");
        path.split("|").forEach(function (d) { var p = document.createElementNS(ns, "path"); p.setAttribute("d", d); s.appendChild(p); });
        return s;
    }
    var ICON_CHAT = "M21 12a8 8 0 0 1-11.5 7.2L4 20l1-4.4A8 8 0 1 1 21 12z", ICON_X = "M18 6 6 18|M6 6l12 12", ICON_SEND = "M5 12h14|M13 6l6 6-6 6",
        ICON_RESET = "M4 4v5h5|M20 20v-5h-5|M5.6 15A8 8 0 0 0 19 17.6|M18.4 9A8 8 0 0 0 5 6.4";

    function buildPanel(floating) {
        var root = el("div", "mb-panel" + (floating ? " mb-floating" : " mb-inline"));
        root.setAttribute("role", "dialog"); root.setAttribute("aria-label", cfg.name);
        var head = el("div", "mb-head");
        head.appendChild(avatar());
        head.appendChild(el("strong", "mb-name", cfg.name));
        var reset = el("button", "mb-icon"); reset.type = "button"; reset.title = cfg.t.reset; reset.setAttribute("aria-label", cfg.t.reset); reset.appendChild(icon(ICON_RESET));
        reset.addEventListener("click", function () { if (busy) return; state = { session: newId(), msgs: [] }; save(); renderAll(); });
        head.appendChild(reset);
        if (floating) {
            var close = el("button", "mb-icon"); close.type = "button"; close.title = cfg.t.close; close.setAttribute("aria-label", cfg.t.close); close.appendChild(icon(ICON_X));
            close.addEventListener("click", function () { toggle(false); });
            head.appendChild(close);
        }
        var log = el("div", "mb-log"); log.setAttribute("aria-live", "polite");
        var note = el("div", "mb-note", cfg.on ? cfg.t.note : cfg.t.error);
        var form = el("form", "mb-form");
        var input = el("textarea", "mb-input"); input.rows = 1; input.maxLength = cfg.maxChars || 1000; input.placeholder = cfg.t.placeholder; input.setAttribute("aria-label", cfg.t.placeholder);
        var send = el("button", "mb-send"); send.type = "submit"; send.title = cfg.t.send; send.setAttribute("aria-label", cfg.t.send); send.appendChild(icon(ICON_SEND));
        input.addEventListener("keydown", function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); form.requestSubmit ? form.requestSubmit() : form.dispatchEvent(new Event("submit")); } });
        input.addEventListener("input", function () { input.style.height = "auto"; input.style.height = Math.min(input.scrollHeight, 140) + "px"; });
        form.addEventListener("submit", function (e) { e.preventDefault(); ask(input.value); input.value = ""; input.style.height = "auto"; });
        form.appendChild(input); form.appendChild(send);
        if (!cfg.on) { input.disabled = true; send.disabled = true; }
        // Consent first, where the operator asks for it: the text and one button stand where the input will be. The server checks it as well.
        var consent = null;
        if (cfg.consent) {
            consent = el("div", "mb-consent");
            consent.appendChild(rich(cfg.consent.text));
            var agree = el("button", "mb-agree", cfg.consent.button); agree.type = "button";
            agree.addEventListener("click", function () { state.consent = cfg.consent.version; save(); renderAll(); panels.forEach(function (q) { if (q.root.offsetParent) q.input.focus(); }); });
            consent.appendChild(agree);
        }
        root.appendChild(head); root.appendChild(log);
        if (consent) root.appendChild(consent);
        root.appendChild(note); root.appendChild(form);
        var p = { root: root, log: log, input: input, send: send, consent: consent, form: form, note: note };
        panels.push(p);
        render(p);
        return p;
    }

    function bubbleFor(m) {
        var row = el("div", "mb-msg mb-" + (m.role === "user" ? "user" : "bot"));
        if (m.role === "user") row.appendChild(el("div", "mb-bubble-text", m.text));
        else {
            var b = el("div", "mb-bubble-text" + (m.error ? " mb-error" : ""));
            b.appendChild(rich(m.text));
            if (m.sources && m.sources.length) {
                var src = el("div", "mb-sources");
                src.appendChild(el("span", null, cfg.t.sources + ": "));
                m.sources.forEach(function (s, i) {
                    var href = safeHref(s.url); if (!href) return;
                    var a = el("a", null, s.title || href); a.href = href; src.appendChild(a);
                    if (i < m.sources.length - 1) src.appendChild(document.createTextNode(" · "));
                });
                b.appendChild(src);
            }
            row.appendChild(b);
        }
        return row;
    }
    function render(p) {
        p.log.textContent = "";
        p.log.appendChild(bubbleFor({ role: "bot", text: cfg.t.greeting }));
        state.msgs.forEach(function (m) { p.log.appendChild(bubbleFor(m)); });
        if (busy) { var t = el("div", "mb-msg mb-bot"); t.appendChild(el("div", "mb-bubble-text mb-typing", cfg.name + " " + cfg.t.thinking)); p.log.appendChild(t); }
        p.input.disabled = busy || !cfg.on; p.send.disabled = busy || !cfg.on;
        var waiting = needsConsent();
        if (p.consent) p.consent.hidden = !waiting;
        p.form.hidden = waiting; p.note.hidden = waiting;
        p.log.scrollTop = p.log.scrollHeight;
    }
    function needsConsent() { return !!(cfg.consent && cfg.on && state.consent !== cfg.consent.version); }
    function renderAll() { panels.forEach(render); }

    function ask(text) {
        text = String(text || "").trim();
        if (!text || busy || !cfg.on || needsConsent()) return;
        var history = state.msgs.filter(function (m) { return !m.error; }).slice(-6).map(function (m) { return { role: m.role, text: m.text }; });
        state.msgs.push({ role: "user", text: text }); busy = true; save(); renderAll();
        var body = new URLSearchParams({ q: text, history: JSON.stringify(history), session: state.session, lang: lang, consent: state.consent || "" });
        fetch("/ai-bot/ask", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/x-www-form-urlencoded" }, body: body })
            .then(function (r) { return r.json(); })
            .then(function (j) {
                if (j && j.consent) { state.consent = null; state.msgs.pop(); }      // the text changed since the visitor agreed: ask again, drop the unsent question
                else if (j && j.ok) state.msgs.push({ role: "assistant", text: String(j.answer || ""), sources: j.sources || [] });
                else state.msgs.push({ role: "assistant", text: (j && j.error) || cfg.t.error, error: true });
            })
            .catch(function () { state.msgs.push({ role: "assistant", text: cfg.t.error, error: true }); })
            .then(function () { busy = false; save(); renderAll(); panels.forEach(function (p) { if (p.root.offsetParent) p.input.focus(); }); });
    }

    // ---- the floating bubble -----------------------------------------------------------------------------------------
    var launcher = null, floatPanel = null;
    function toggle(open) {
        if (!floatPanel) return;
        floatPanel.root.hidden = !open;
        launcher.setAttribute("aria-expanded", open ? "true" : "false");
        document.documentElement.classList.toggle("mb-open", open);
        if (open) { render(floatPanel); floatPanel.input.focus(); }
    }
    function mountBubble() {
        launcher = el("button", "mb-launcher mb-" + cfg.position); launcher.type = "button";
        launcher.setAttribute("aria-label", cfg.t.open); launcher.title = cfg.t.open; launcher.setAttribute("aria-expanded", "false");
        launcher.appendChild(icon(ICON_CHAT));
        launcher.addEventListener("click", function () { toggle(floatPanel.root.hidden); });
        floatPanel = buildPanel(true);
        floatPanel.root.classList.add("mb-" + cfg.position);
        floatPanel.root.hidden = true;
        document.body.appendChild(floatPanel.root);
        document.body.appendChild(launcher);
        document.addEventListener("keydown", function (e) { if (e.key === "Escape" && floatPanel && !floatPanel.root.hidden) toggle(false); });
    }

    function start() {
        var inline = document.querySelectorAll("[data-mat-bot-inline]");
        fetch("/ai-bot/config?lang=" + encodeURIComponent(lang), { credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (c) {
                if (!c || !c.t) return;
                cfg = c;
                if (!cfg.bubble && inline.length === 0) return;
                var css = document.createElement("link"); css.rel = "stylesheet"; css.href = "/css/ai-bot.css?v=" + version; document.head.appendChild(css);
                if (cfg.color) document.documentElement.style.setProperty("--mb-c", cfg.color);
                inline.forEach(function (host) { host.appendChild(buildPanel(false).root); });
                if (cfg.bubble && cfg.on) mountBubble();
            })
            .catch(function () { });
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
