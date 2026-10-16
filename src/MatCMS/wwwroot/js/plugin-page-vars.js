// The variables of a plugin's page in the block editor (editor v2). A button in the top bar opens the list; a click puts {{name}} into the field the person was typing in — a heading, a text,
// a button's address, the rich text — without taking the focus away from it. The server fills the variables when the page is drawn (Content/BlockScope.cs); this only saves typing the names.
(function () {
    "use strict";
    var open = document.getElementById("ev2-vars-open"), pop = document.getElementById("ev2-vars");
    if (!open || !pop) return;
    var last = null;          // the field the person was typing in last (inside the inspector)

    function isField(el) {
        if (!el || !el.closest || !el.closest("#ev2-insp") || pop.contains(el)) return false;
        if (el.isContentEditable) return true;
        var t = (el.tagName || "").toLowerCase();
        if (t === "textarea") return true;
        return t === "input" && /^(text|url|search|email|tel|)$/i.test(el.getAttribute("type") || "");
    }
    document.addEventListener("focusin", function (e) { if (isField(e.target)) last = e.target; });

    function place() {
        var r = open.getBoundingClientRect();
        pop.style.left = Math.max(8, Math.min(r.left, innerWidth - pop.offsetWidth - 8)) + "px";
        pop.style.top = (r.bottom + 6) + "px";
    }
    open.addEventListener("click", function (e) {
        e.stopPropagation();
        pop.hidden = !pop.hidden;
        if (!pop.hidden) place();
    });
    document.addEventListener("mousedown", function (e) { if (!pop.hidden && !pop.contains(e.target) && !open.contains(e.target)) pop.hidden = true; });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") pop.hidden = true; });
    // Pressing a variable must not take the focus from the field it goes into.
    pop.addEventListener("mousedown", function (e) { if (e.target.closest("[data-var]")) e.preventDefault(); });

    pop.addEventListener("click", function (e) {
        var b = e.target.closest("[data-var]");
        if (!b) return;
        var text = "{{" + b.getAttribute("data-var") + "}}";
        var el = last;
        if (!el || !document.body.contains(el)) { b.classList.add("no-field"); setTimeout(function () { b.classList.remove("no-field"); }, 900); return; }
        el.focus();
        if (el.isContentEditable) document.execCommand("insertText", false, text);
        else {
            var s = el.selectionStart == null ? el.value.length : el.selectionStart, end = el.selectionEnd == null ? s : el.selectionEnd;
            el.setRangeText(text, s, end, "end");
            // The editor listens to input to take the value into its draft.
            el.dispatchEvent(new Event("input", { bubbles: true }));
        }
    });
})();
