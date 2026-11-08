/* The back office's fields are MatUi's (FORM-15 pickers, FORM-20 stepper) — never the browser's own drop-down, calendar or colour box, which
   look foreign, ignore the colour scheme and differ from browser to browser.

   Loaded after matui.js in both admin layouts. It changes no page: every native field it finds keeps its place in the form, its name, its value and
   its events, and becomes the INPUT of a MatUi picker — the picker writes the value back into it and fires input/change on it, so a page script
   that listens to a select or reads its .value keeps working:
     <select>                     → data-picker="select" (its options as the picker's options; optgroups as groups; a search field from 11 options;
                                    the option with value "" is the placeholder)
     input[type=date|time|color]  → data-picker="date|time|colour" (min/max and step carried over; clearable unless required)
     input[type=datetime-local]   → a date and a time picker side by side, joined into the original field ("2026-11-08T14:30")
     input[type=number]           → MatUi's stepper (− and + beside the field), except inside a table, where the buttons would crowd the column
   A field marked data-native, a <select multiple> and everything inside MatUi's own widgets or a code editor stay as they are.
   Fields that arrive later (a dialog, a row added to a row editor, a fragment) are taken by a MutationObserver. A script that sets .value or
   .selectedIndex, fills a select anew, or switches it off or hides it is followed: the picker shows what the field holds. */
(function () {
    "use strict";
    var M = window.MatUi;
    if (!M || !M.refresh) return;
    var doc = document;
    var SKIP = "[data-native], .mat-picker, .mat-datetime, .mat-stepper, .mat-otp, .mat-calendar, .mat-slider, .CodeMirror, .lp-pop, [data-ui-lookup]";

    function skipped(el) {
        if (el.hasAttribute("data-native") || el.__matDone) return true;
        var p = el.parentElement;
        return !p || !!p.closest(SKIP);
    }

    function wrapIn(el, className, attrs) {
        var w = doc.createElement("div");
        w.className = className;
        Object.keys(attrs || {}).forEach(function (k) { if (attrs[k] != null) w.setAttribute(k, attrs[k]); });
        el.parentNode.insertBefore(w, el);
        w.appendChild(el);
        return w;
    }

    // A small field where the page made a small one (toolbars, rows of a table, .btn-sm neighbours): MatUi's small size.
    function size(el) {
        return el.closest("td, .list-toolbar, .page-head, .toolbar, .ui-inline") ? " mat-picker--sm" : "";
    }

    function resync(w) { if (w && w.parentNode) M.refresh(w.parentNode); }

    // .value / .selectedIndex set by a page script: the native setter, then the picker shows it.
    function follow(el, w, props) {
        props.forEach(function (prop) {
            var proto = Object.getPrototypeOf(el), d = null;
            while (proto && !(d = Object.getOwnPropertyDescriptor(proto, prop))) proto = Object.getPrototypeOf(proto);
            if (!d || !d.set) return;
            Object.defineProperty(el, prop, {
                configurable: true, enumerable: true,
                get: function () { return d.get.call(this); },
                set: function (v) { d.set.call(this, v); resync(w); }
            });
        });
    }

    // Switched off, hidden or (for a select) given new options by a script: the picker follows.
    function watch(el, w, onOptions) {
        if (el.__matObs) el.__matObs.disconnect();
        var obs = new MutationObserver(function (list) {
            var options = list.some(function (m) { return m.type !== "attributes"; });
            if (options && onOptions) { onOptions(); return; }
            w.hidden = el.hidden;
            w.style.display = el.style.display === "none" ? "none" : "";
            resync(w);
        });
        obs.observe(el, { childList: !!onOptions, subtree: !!onOptions, characterData: !!onOptions, attributes: true, attributeFilter: ["disabled", "hidden", "style", "required"] });
        el.__matObs = obs;
    }

    // ---- select ----
    function optionButton(o) {
        var b = doc.createElement("button");
        b.type = "button";
        b.className = "mat-picker__option";
        b.setAttribute("data-value", o.value);
        var n = doc.createElement("span");
        n.className = "mat-picker__name";
        n.textContent = o.textContent.trim() || o.value || " ";
        b.appendChild(n);
        if (o.disabled) b.disabled = true;
        if (o.hidden) b.hidden = true;
        return b;
    }
    function optionsOf(sel) {
        var box = doc.createElement("div");
        box.className = "mat-picker__options";
        Array.prototype.forEach.call(sel.children, function (c) {
            if (c.tagName === "OPTGROUP") {
                var g = doc.createElement("div");
                g.className = "mat-picker__group";
                g.setAttribute("role", "group");
                var h = doc.createElement("div");
                h.className = "mat-picker__group-head";
                h.textContent = c.label;
                g.appendChild(h);
                Array.prototype.forEach.call(c.children, function (o) { g.appendChild(optionButton(o)); });
                box.appendChild(g);
            } else if (c.tagName === "OPTION") {
                box.appendChild(optionButton(c));
            }
        });
        return box;
    }
    function select(sel) {
        if (sel.multiple || sel.size > 1 || skipped(sel)) return;
        var empty = Array.prototype.filter.call(sel.options, function (o) { return o.value === ""; })[0];
        var w = wrapIn(sel, "mat-picker" + size(sel), {
            "data-picker": "select",
            "data-placeholder": empty && empty.textContent.trim() ? empty.textContent.trim() : null,
            "data-search": sel.options.length > 10 ? "" : null
        });
        sel.classList.add("mat-picker__input");
        sel.__matDone = true;
        w.appendChild(optionsOf(sel));
        follow(sel, w, ["value", "selectedIndex"]);
        watch(sel, w, function () { rebuild(sel, w); });
    }
    // The options were written anew (a script filled the select): the picker is built again around the same select.
    function rebuild(sel, w) {
        if (!w.parentNode) return;
        var host = w.parentNode;
        host.insertBefore(sel, w);
        w.remove();
        sel.classList.remove("mat-picker__input");
        sel.removeAttribute("aria-hidden");
        sel.__matDone = false;
        select(sel);
        M.refresh(host);
    }

    // ---- date, time, colour ----
    function picker(inp, kind) {
        if (skipped(inp)) return;
        var attrs = { "data-picker": kind };
        if (kind === "date") { attrs["data-min"] = inp.min || null; attrs["data-max"] = inp.max || null; }
        if (kind === "time" && +inp.step >= 60) attrs["data-step"] = String(Math.round(+inp.step / 60));
        if (kind !== "colour" && !inp.required) attrs["data-clearable"] = "";
        if (inp.placeholder) attrs["data-placeholder"] = inp.placeholder;
        var w = wrapIn(inp, "mat-picker" + size(inp), attrs);
        inp.classList.add("mat-picker__input");
        inp.__matDone = true;
        follow(inp, w, ["value"]);
        watch(inp, w, null);
    }

    // ---- date and time: two pickers, one value ----
    function dateTime(inp) {
        if (skipped(inp)) return;
        var box = wrapIn(inp, "mat-datetime", {});
        inp.__matDone = true;
        inp.tabIndex = -1;
        inp.setAttribute("aria-hidden", "true");
        var nativeValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value");
        // each half is half as wide as a field: MatUi's "Choose a date" did not fit, so the halves are named short in the admin's language
        var de = (doc.documentElement.lang || "de").slice(0, 2).toLowerCase() === "de";
        function part(kind, id) {
            var w = doc.createElement("div");
            w.className = "mat-picker" + size(inp);
            w.setAttribute("data-picker", kind);
            w.setAttribute("data-placeholder", kind === "date" ? (de ? "Datum" : "Date") : (de ? "Uhrzeit" : "Time"));
            if (kind === "date") {
                if (inp.min) w.setAttribute("data-min", inp.min.slice(0, 10));
                if (inp.max) w.setAttribute("data-max", inp.max.slice(0, 10));
                if (!inp.required) w.setAttribute("data-clearable", "");
            } else if (+inp.step >= 60) {
                w.setAttribute("data-step", String(Math.round(+inp.step / 60)));
            }
            var i = doc.createElement("input");
            i.type = "text";
            i.className = "mat-picker__input";
            i.id = id;
            w.appendChild(i);
            box.appendChild(w);
            return i;
        }
        var base = inp.id || ("mat-dt-" + Math.random().toString(36).slice(2, 8));
        var d = part("date", base + "-date"), t = part("time", base + "-time");
        // the field's label names the date; a browser message about a required field points at the original, which lies over both
        var lab = inp.id ? doc.querySelector('label[for="' + inp.id + '"]') : null;
        if (lab) lab.htmlFor = d.id;
        function split() {
            var v = nativeValue.get.call(inp) || "";
            d.value = v.slice(0, 10);
            t.value = v.length >= 16 ? v.slice(11, 16) : "";
        }
        function join() {
            var v = d.value ? d.value + "T" + (t.value || "00:00") : "";
            if (nativeValue.get.call(inp) === v) return;
            nativeValue.set.call(inp, v);
            inp.dispatchEvent(new Event("input", { bubbles: true }));
            inp.dispatchEvent(new Event("change", { bubbles: true }));
        }
        d.addEventListener("change", function (e) { e.stopPropagation(); join(); });
        t.addEventListener("change", function (e) { e.stopPropagation(); if (!d.value && t.value) { return; } join(); });
        Object.defineProperty(inp, "value", {
            configurable: true, enumerable: true,
            get: function () { return nativeValue.get.call(this); },
            set: function (v) { nativeValue.set.call(this, v); split(); resync(box); }
        });
        split();
        new MutationObserver(function () {
            box.hidden = inp.hidden;
            d.disabled = t.disabled = inp.disabled;
            resync(box);
        }).observe(inp, { attributes: true, attributeFilter: ["disabled", "hidden"] });
    }

    // ---- number: MatUi's stepper ----
    function number(inp) {
        if (skipped(inp) || inp.closest("table, .row-editor")) return;
        inp.__matDone = true;
        inp.classList.add("mat-input");
        wrapIn(inp, "mat-stepper", { "data-stepper": "" });
    }

    function each(scope, selector, fn) {
        if (scope.matches && scope.matches(selector)) fn(scope);
        if (scope.querySelectorAll) Array.prototype.slice.call(scope.querySelectorAll(selector)).forEach(fn);
    }
    function enhance(scope) {
        each(scope, "select", select);
        each(scope, "input[type=date]", function (i) { picker(i, "date"); });
        each(scope, "input[type=time]", function (i) { picker(i, "time"); });
        each(scope, "input[type=color]", function (i) { picker(i, "colour"); });
        each(scope, "input[type=datetime-local]", dateTime);
        each(scope, "input[type=number]", number);
    }

    enhance(doc.body);
    // matui.js builds the pickers when the page is loaded; arriving later, they are built now
    if (doc.readyState !== "loading") M.refresh(doc.body);

    var queued = [];
    new MutationObserver(function (list) {
        list.forEach(function (m) {
            Array.prototype.forEach.call(m.addedNodes, function (n) {
                if (n.nodeType === 1 && !(n.parentElement && n.parentElement.closest(SKIP)) && !n.matches(".mat-picker, .mat-datetime, .mat-stepper")) queued.push(n);
            });
        });
        if (!queued.length) return;
        var nodes = queued;
        queued = [];
        nodes.forEach(function (n) { if (n.isConnected) enhance(n); });
        nodes.forEach(function (n) { if (n.isConnected) M.refresh(n.parentNode || doc.body); });
    }).observe(doc.body, { childList: true, subtree: true });
})();
