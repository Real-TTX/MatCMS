// Behaviour of the plugin UI library's richer fields (UiRenderer in MatCMS.Shared.Web draws the markup):
// rich text, image, image/file list, tags, and the dialog opener. Purely markup-driven — every hook is a
// data-ui-* attribute and several instances per page are fine. No per-page JavaScript needed.
// The media picker itself (window.openMediaPicker) belongs to the CMS; a field only calls it when clicked.
(function () {
    "use strict";

    // ---- Dialogs: a button with data-ui-opens="id" opens <dialog id="id" data-ui-dialog> ----
    document.addEventListener("click", function (e) {
        var opener = e.target.closest && e.target.closest("[data-ui-opens]");
        if (opener) {
            var dlg = document.getElementById(opener.getAttribute("data-ui-opens"));
            if (dlg && dlg.showModal) { e.preventDefault(); dlg.showModal(); var f = dlg.querySelector("input,textarea,select"); if (f) f.focus(); }
            return;
        }
        var closer = e.target.closest && e.target.closest("[data-ui-close]");
        if (closer) { var d = closer.closest("dialog"); if (d) d.close(); }
    });

    function each(sel, fn) { Array.prototype.forEach.call(document.querySelectorAll(sel), fn); }

    // ---- Rich text ----
    each("[data-ui-rt]", function (root) {
        var ed = root.querySelector("[data-ui-rt-editor]");
        var hidden = root.querySelector("[data-ui-rt-value]");
        var bar = root.querySelector("[data-ui-rt-toolbar]");
        if (!ed || !hidden || !bar) return;
        bar.addEventListener("click", function (e) {
            var b = e.target.closest("button"); if (!b) return;
            ed.focus();
            var cmd = b.getAttribute("data-cmd"), arg = b.getAttribute("data-arg");
            if (b.getAttribute("data-prompt")) {
                var sel = window.getSelection();
                var keep = sel.rangeCount ? sel.getRangeAt(0).cloneRange() : null;
                MatDialog.prompt(root.getAttribute("data-t-link-prompt") || "URL", "https://").then(function (url) {
                    if (!url) return;
                    ed.focus();
                    if (keep) { var s = window.getSelection(); s.removeAllRanges(); s.addRange(keep); }
                    document.execCommand(cmd, false, url);
                });
            } else { document.execCommand(cmd, false, arg || null); }
        });
        function sync() { hidden.value = ed.innerHTML.trim(); }
        ed.addEventListener("input", sync);
        var form = root.closest("form");
        if (form) form.addEventListener("submit", sync);
    });

    // ---- Image (URL + preview + media picker) ----
    each("[data-ui-image]", function (root) {
        var url = root.querySelector("[data-ui-image-url]");
        var prev = root.querySelector(".preview");
        var pick = root.querySelector("[data-ui-image-pick]");
        if (!url || !prev) return;
        function sync() { prev.style.backgroundImage = url.value ? "url('" + url.value.replace(/'/g, "%27") + "')" : ""; }
        url.addEventListener("input", sync);
        if (pick) pick.addEventListener("click", function () {
            if (window.openMediaPicker) window.openMediaPicker(function (u) { url.value = u; sync(); url.dispatchEvent(new Event("change", { bubbles: true })); });
        });
    });

    // ---- Focus of an image (UiImage.FocusName): a click into the whole picture sets the point a cropping tile keeps in view ----
    each("[data-ui-focus]", function (fc) {
        var field = fc.previousElementSibling, url = field && field.querySelector("[data-ui-image-url]");
        var pic = fc.querySelector("[data-ui-focus-pic]"), img = fc.querySelector("[data-ui-focus-img]"), dot = fc.querySelector("[data-ui-focus-dot]");
        var value = fc.querySelector("[data-ui-focus-value]"), reset = fc.querySelector("[data-ui-focus-reset]");
        var tiles = fc.querySelectorAll("[data-ui-focus-tile]"), def = fc.getAttribute("data-default") || "50% 50%";
        if (!pic || !img || !value) return;
        function show() {
            var pos = value.value || def, src = url ? url.value : img.getAttribute("src") || "";
            if (src && img.getAttribute("src") !== src) img.setAttribute("src", src);
            fc.classList.toggle("empty", !src);
            fc.classList.toggle("is-default", !value.value);
            var p = pos.split(/\s+/);
            dot.style.left = p[0]; dot.style.top = p[1];
            Array.prototype.forEach.call(tiles, function (t) {
                t.style.backgroundImage = src ? "url('" + src.replace(/'/g, "%27") + "')" : "";
                t.style.backgroundPosition = pos;
            });
        }
        pic.addEventListener("click", function (e) {
            var r = img.getBoundingClientRect();
            if (!r.width || !r.height) return;
            var x = Math.round(Math.min(100, Math.max(0, (e.clientX - r.left) / r.width * 100)));
            var y = Math.round(Math.min(100, Math.max(0, (e.clientY - r.top) / r.height * 100)));
            value.value = x + "% " + y + "%";
            show();
        });
        if (reset) reset.addEventListener("click", function () { value.value = ""; show(); });
        if (url) { url.addEventListener("input", show); url.addEventListener("change", show); }
        show();
    });

    // ---- Image list (gallery) / file list (attachments): the value is a JSON array ----
    function nameFromUrl(u) { try { return decodeURIComponent((u || "").split("/").pop().split("?")[0]); } catch (e) { return u; } }
    each("[data-ui-medialist]", function (root) {
        var files = root.getAttribute("data-kind") === "files";
        var value = root.querySelector("[data-ui-medialist-value]");
        var list = root.querySelector("[data-ui-medialist-items]");
        var add = root.querySelector("[data-ui-medialist-add]");
        if (!value || !list) return;
        var items = [];
        try { items = JSON.parse(value.value || "[]") || []; } catch (e) { items = []; }
        function btn(label, title, fn) {
            var b = document.createElement("button"); b.type = "button"; b.textContent = label; b.title = title; b.addEventListener("click", fn); return b;
        }
        function render() {
            value.value = JSON.stringify(items);
            list.innerHTML = "";
            items.forEach(function (it, i) {
                if (files) {
                    var chip = document.createElement("span"); chip.className = "att-chip";
                    var a = document.createElement("a"); a.href = it.url; a.target = "_blank"; a.rel = "noopener"; a.textContent = it.name || nameFromUrl(it.url);
                    var x = btn("×", root.getAttribute("data-t-remove") || "", function () { items.splice(i, 1); render(); });
                    chip.appendChild(a); chip.appendChild(x); list.appendChild(chip);
                    return;
                }
                var box = document.createElement("div"); box.className = "gal-item";
                var img = document.createElement("img"); img.src = it.url; img.alt = it.alt || ""; img.loading = "lazy"; box.appendChild(img);
                if (i === 0) { var f = document.createElement("span"); f.className = "gal-first"; f.textContent = "1"; box.appendChild(f); }
                var tools = document.createElement("div"); tools.className = "gal-tools";
                tools.appendChild(btn("←", root.getAttribute("data-t-left") || "", function () { if (i > 0) { items.splice(i - 1, 0, items.splice(i, 1)[0]); render(); } }));
                tools.appendChild(btn("×", root.getAttribute("data-t-remove") || "", function () { items.splice(i, 1); render(); }));
                tools.appendChild(btn("→", root.getAttribute("data-t-right") || "", function () { if (i < items.length - 1) { items.splice(i + 1, 0, items.splice(i, 1)[0]); render(); } }));
                box.appendChild(tools); list.appendChild(box);
            });
        }
        if (add) add.addEventListener("click", function () {
            if (!window.openMediaPicker) return;
            window.openMediaPicker(function (urls) {
                (Array.isArray(urls) ? urls : [urls]).forEach(function (u) {
                    if (u) items.push(files ? { url: u, name: nameFromUrl(u) } : { url: u, alt: "" });
                });
                render();
            }, { multiple: true });
        });
        render();
        // Other scripts (an import from a link) can append without knowing the markup.
        root.matMediaList = { add: function (urls, alt) { urls.forEach(function (u) { items.push(files ? { url: u, name: nameFromUrl(u) } : { url: u, alt: alt || "" }); }); render(); }, count: function () { return items.length; } };
    });

    // ---- Tags (chips + suggestions); the hidden value is comma-separated ----
    each("[data-ui-tags]", function (root) {
        var box = root.querySelector("[data-ui-tags-box]");
        var entry = root.querySelector("[data-ui-tags-entry]");
        var addBtn = root.querySelector("[data-ui-tags-add]");
        var hidden = root.querySelector("[data-ui-tags-value]");
        var suggest = root.querySelector("[data-ui-tags-suggest]");
        if (!box || !entry || !addBtn || !hidden) return;
        var chips = [];
        function showEntry() { addBtn.hidden = true; entry.hidden = false; entry.focus(); }
        function hideEntry() { entry.hidden = true; addBtn.hidden = false; }
        addBtn.addEventListener("click", showEntry);
        function render() {
            Array.prototype.slice.call(box.querySelectorAll(".tag-chip-item")).forEach(function (e) { e.remove(); });
            chips.forEach(function (tag) {
                var el = document.createElement("span"); el.className = "tag-chip-item"; el.textContent = tag;
                var x = document.createElement("button"); x.type = "button"; x.className = "tag-x"; x.setAttribute("aria-label", root.getAttribute("data-t-remove") || ""); x.textContent = "×";
                x.addEventListener("click", function () { removeTag(tag); entry.focus(); });
                el.appendChild(x); box.insertBefore(el, entry);
            });
            hidden.value = chips.join(", ");
        }
        function addTag(raw) {
            var t = (raw || "").replace(/,/g, " ").trim(); if (!t) return;
            if (chips.some(function (c) { return c.toLowerCase() === t.toLowerCase(); })) return;
            chips.push(t); render();
        }
        function removeTag(tag) { chips = chips.filter(function (c) { return c !== tag; }); render(); }
        function setTags(str) {
            var seen = {};
            chips = (str || "").split(",").map(function (s) { return s.trim(); }).filter(function (c) {
                if (!c) return false; var k = c.toLowerCase(); if (seen[k]) return false; seen[k] = 1; return true;
            });
            render();
        }
        entry.addEventListener("keydown", function (e) {
            if (e.key === "Enter" || e.key === ",") { e.preventDefault(); addTag(entry.value); entry.value = ""; }
            else if (e.key === "Escape") { e.preventDefault(); entry.value = ""; hideEntry(); }
            else if (e.key === "Backspace" && !entry.value && chips.length) { removeTag(chips[chips.length - 1]); }
        });
        entry.addEventListener("blur", function () { if (entry.value.trim()) { addTag(entry.value); entry.value = ""; } hideEntry(); });
        box.addEventListener("click", function (e) { if (e.target === box) showEntry(); });
        if (suggest) suggest.addEventListener("click", function (e) {
            var c = e.target.closest(".mini-chip-add"); if (!c) return; addTag(c.getAttribute("data-tag")); entry.focus();
        });
        var form = root.closest("form");
        if (form) form.addEventListener("submit", function () { if (entry.value.trim()) { addTag(entry.value); entry.value = ""; } });
        setTags(hidden.value);
    });

    // ---- Rows: add a row, delete a row, and number them as they stand when the form is sent ----
    each("[data-ui-rows]", function (root) {
        var name = root.getAttribute("data-name");
        var body = root.querySelector("[data-ui-rows-body]");
        var tpl = root.querySelector("[data-ui-rows-template]");
        var count = root.querySelector("[data-ui-rows-count]");
        var empty = root.querySelector("[data-ui-rows-empty]");
        var add = root.querySelector("[data-ui-rows-add]");
        var max = parseInt(root.getAttribute("data-max"), 10) || 100;
        if (!body) return;
        function rows() { return body.querySelectorAll("[data-ui-row]"); }
        function renumber() {
            var list = rows();
            Array.prototype.forEach.call(list, function (tr, i) {
                Array.prototype.forEach.call(tr.querySelectorAll("[data-key]"), function (el) { el.name = name + "." + i + "." + el.getAttribute("data-key"); });
            });
            if (count) count.value = list.length;
            if (empty) empty.hidden = list.length > 0;
            if (add) add.disabled = list.length >= max;
        }
        if (add && tpl) add.addEventListener("click", function () {
            if (rows().length >= max) return;
            body.appendChild(tpl.content.cloneNode(true));
            renumber();
            var all = rows(), field = all[all.length - 1].querySelector("input[type=text]");
            if (field) field.focus();
        });
        root.addEventListener("click", function (e) {
            var b = e.target.closest("[data-ui-row-remove]"); if (!b) return;
            var tr = b.closest("[data-ui-row]"); if (tr) { tr.remove(); renumber(); }
        });
        // A picture cell: the preview follows the address, the button opens the media library (works for rows added later: the click is caught here, not on each button).
        function preview(box) {
            var url = box.querySelector("input"), prev = box.querySelector(".preview");
            if (url && prev) prev.style.backgroundImage = url.value ? "url('" + url.value.replace(/'/g, "%27") + "')" : "";
        }
        root.addEventListener("input", function (e) { var b = e.target.closest && e.target.closest(".row-img"); if (b) preview(b); });
        root.addEventListener("click", function (e) {
            var pick = e.target.closest("[data-ui-rowimage-pick]"); if (!pick) return;
            var box = pick.closest(".row-img"), url = box && box.querySelector("input");
            if (url && window.openMediaPicker) window.openMediaPicker(function (u) { url.value = u; preview(box); url.dispatchEvent(new Event("input", { bubbles: true })); });
        });
        var form = root.closest("form");
        if (form) form.addEventListener("submit", renumber);
        renumber();
    });

    // ---- Lookup: one choice out of a long list, searched on the server while one types ----
    // The field (UiRenderer) is a button with the current choice and a hidden input. A click opens the link picker's drop-down (.lp-pop, same look)
    // under it — on a phone a dialog over the page — with a search box; the results come from data-source as JSON ({items:[{value,label,hint,group}],
    // more}), the next page when the list is scrolled to its end. Arrow keys move, Enter takes, Escape closes. Caught on the document, so a field
    // that arrives later (a fragment) works too.
    document.addEventListener("click", function (e) {
        var btn = e.target.closest && e.target.closest(".ui-lookup-btn");
        if (!btn || btn.disabled) return;
        var root = btn.closest("[data-ui-lookup]");
        if (root) { e.preventDefault(); openLookup(root, btn); }
    });
    document.addEventListener("keydown", function (e) {
        if (e.key !== "ArrowDown" || !e.target.classList || !e.target.classList.contains("ui-lookup-btn")) return;
        var root = e.target.closest("[data-ui-lookup]");
        if (root) { e.preventDefault(); openLookup(root, e.target); }
    });

    function openLookup(root, btn) {
        var open = document.querySelector(".lp-pop");
        if (open && open._close) open._close();
        var hidden = root.querySelector("[data-ui-lookup-value]"), text = root.querySelector("[data-ui-lookup-text]");
        var src = root.getAttribute("data-source") || "";
        if (!hidden || !text || !src || src === "#") return;
        var T = function (k, d) { return root.getAttribute("data-t-" + k) || d; };
        var pinned = []; try { pinned = JSON.parse(root.getAttribute("data-pinned") || "[]"); } catch (x) { pinned = []; }
        var phone = window.matchMedia("(max-width: 600px)").matches;

        var back = null;
        if (phone) { back = document.createElement("div"); back.className = "ui-lookup-back"; document.body.appendChild(back); document.documentElement.classList.add("ui-lookup-open"); }
        var pop = document.createElement("div");
        pop.className = "lp-pop ui-lookup-pop" + (phone ? " is-sheet" : "");
        pop.setAttribute("role", "dialog");
        pop.setAttribute("aria-label", root.getAttribute("data-label") || T("search", ""));
        pop.innerHTML = '<div class="lp-pop-head"><i class="ti ti-search" aria-hidden="true"></i><input type="search" class="lp-pop-search" autocomplete="off" enterkeyhint="search"></div><div class="lp-pop-list" role="listbox"></div>';
        var head = pop.querySelector(".lp-pop-head"), search = pop.querySelector(".lp-pop-search"), list = pop.querySelector(".lp-pop-list");
        search.placeholder = T("search", "");
        search.setAttribute("aria-label", T("search", ""));
        if (phone) {
            var title = document.createElement("div"); title.className = "ui-lookup-title"; title.textContent = root.getAttribute("data-label") || "";
            var x = document.createElement("button"); x.type = "button"; x.className = "icon-btn ui-lookup-x"; x.setAttribute("aria-label", T("close", ""));
            x.innerHTML = '<i class="ti ti-x" aria-hidden="true"></i>';
            x.addEventListener("click", function () { close(true); });
            title.appendChild(x);
            pop.insertBefore(title, head);
        }
        document.body.appendChild(pop);

        function place() {
            if (phone) return;   // the dialog's place is CSS (fixed, over the page)
            var vw = document.documentElement.clientWidth, vh = window.innerHeight, r = btn.getBoundingClientRect();
            var width = Math.min(Math.max(r.width, 340), vw - 16);
            var left = Math.min(Math.max(r.left, 8), vw - width - 8);
            var below = vh - r.bottom - 12, above = r.top - 12;
            var up = below < 260 && above > below;
            var room = Math.max(180, Math.min(400, up ? above : below));
            pop.style.width = width + "px";
            pop.style.left = (left + window.scrollX) + "px";
            pop.style.maxHeight = room + "px";
            if (up) { pop.style.top = ""; pop.style.bottom = (document.documentElement.clientHeight - r.top - window.scrollY + 4) + "px"; }
            else { pop.style.bottom = ""; pop.style.top = (r.bottom + window.scrollY + 4) + "px"; }
        }
        place();
        window.addEventListener("resize", place);

        var items = [], active = -1, skip = 0, more = false, loading = false, seq = 0, ctrl = null, timer = 0, lastGroup = null;
        function close(refocus) {
            window.removeEventListener("resize", place);
            document.removeEventListener("mousedown", outside, true);
            document.removeEventListener("keydown", keys, true);
            if (ctrl) ctrl.abort();
            clearTimeout(timer);
            pop.remove();
            if (back) back.remove();
            document.documentElement.classList.remove("ui-lookup-open");
            if (refocus) btn.focus();
        }
        pop._close = close;
        function outside(e) { if (!pop.contains(e.target) && !btn.contains(e.target)) close(false); }
        function setActive(i) {
            if (!items.length) { active = -1; return; }
            active = Math.max(0, Math.min(items.length - 1, i));
            items.forEach(function (it) { it.classList.remove("on"); });
            items[active].classList.add("on");
            items[active].scrollIntoView({ block: "nearest" });
        }
        function keys(e) {
            if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(true); return; }
            if (!pop.contains(document.activeElement)) return;
            if (e.key === "ArrowDown") { e.preventDefault(); setActive(active + 1); if (active >= items.length - 3) loadMore(); }
            else if (e.key === "ArrowUp") { e.preventDefault(); setActive(active - 1); }
            else if (e.key === "Enter") { e.preventDefault(); if (items[active]) items[active].click(); }
        }
        document.addEventListener("mousedown", outside, true);
        document.addEventListener("keydown", keys, true);

        function pick(value, label) {
            hidden.value = value;
            text.textContent = label;
            text.classList.remove("is-empty");
            close(true);
            hidden.dispatchEvent(new Event("change", { bubbles: true }));
            if (root.hasAttribute("data-submit")) {
                var form = root.closest("form");
                if (form) { if (form.requestSubmit) form.requestSubmit(); else form.submit(); }
            }
        }
        function note(cls, words) {
            var old = list.querySelector(".lp-pop-empty.is-note"); if (old) old.remove();
            if (!words) return;
            var n = document.createElement("div"); n.className = "lp-pop-empty is-note" + (cls ? " " + cls : ""); n.textContent = words; list.appendChild(n);
        }
        function add(it) {
            if (it.group && it.group !== lastGroup) {
                var h = document.createElement("div"); h.className = "lp-pop-group"; h.textContent = it.group; list.appendChild(h);
            }
            lastGroup = it.group || lastGroup;
            var b = document.createElement("button");
            b.type = "button"; b.className = "lp-pop-item"; b.setAttribute("role", "option");
            b.innerHTML = '<span class="t"></span><span class="u"></span>';
            b.querySelector(".t").textContent = it.label || it.value;
            b.querySelector(".u").textContent = it.hint || "";
            var current = String(it.value) === hidden.value;
            if (current) { b.classList.add("is-current"); b.setAttribute("aria-selected", "true"); }
            b.addEventListener("mouseenter", function () { setActive(items.indexOf(b)); });
            b.addEventListener("click", function () { pick(String(it.value), it.label || String(it.value)); });
            list.appendChild(b);
            items.push(b);
            return current;
        }
        function load(reset) {
            if (reset) {
                skip = 0; more = false; items = []; active = -1; lastGroup = null; list.innerHTML = "";
                if (!search.value.trim()) pinned.forEach(function (p) { add({ value: p.value, label: p.label }); });
                lastGroup = null;
            }
            var my = ++seq;
            if (ctrl) ctrl.abort();
            ctrl = window.AbortController ? new AbortController() : null;
            loading = true;
            note("is-loading", T("loading", "…"));
            var url = src + (src.indexOf("?") < 0 ? "?" : "&") + "q=" + encodeURIComponent(search.value.trim()) + "&skip=" + skip;
            fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" }, signal: ctrl ? ctrl.signal : undefined })
                .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
                .then(function (data) {
                    if (my !== seq) return;
                    loading = false; note("", "");
                    var got = (data && data.items) || [], hit = -1;
                    got.forEach(function (it) { if (add(it)) hit = items.length - 1; });
                    skip += got.length;
                    more = !!(data && data.more) && got.length > 0;
                    if (!items.length) note("", T("none", ""));
                    if (reset) setActive(hit >= 0 ? hit : 0);
                    place();
                    // a short first page that leaves no scrollbar would never ask for the rest
                    if (more && list.scrollHeight <= list.clientHeight + 4) loadMore();
                })
                .catch(function (err) {
                    if (err && err.name === "AbortError") return;
                    if (my !== seq) return;
                    loading = false; note("", T("failed", ""));
                });
        }
        function loadMore() { if (more && !loading) load(false); }
        list.addEventListener("scroll", function () { if (list.scrollTop + list.clientHeight > list.scrollHeight - 48) loadMore(); });
        search.addEventListener("input", function () { clearTimeout(timer); timer = setTimeout(function () { load(true); }, 200); });
        load(true);
        search.focus();
    }
})();
