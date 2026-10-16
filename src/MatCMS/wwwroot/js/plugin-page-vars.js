// The variables of a plugin's page in the block editor (editor v2). The list sits in the inspector, above the fields of the selected block; a click puts {{name}} into the field the person was
// typing in last — a heading, a text, a button's address, the rich text — without taking the focus away from it. The server fills the variables when the page is drawn (Content/BlockScope.cs);
// this only saves typing the names.
(function () {
    "use strict";
    var box = document.getElementById("ev2-vars");
    if (!box) return;
    var last = null;          // the field the person was typing in last (inside the inspector)

    function isField(el) {
        if (!el || !el.closest || !el.closest("#ev2-insp") || box.contains(el)) return false;
        if (el.isContentEditable) return true;
        var t = (el.tagName || "").toLowerCase();
        if (t === "textarea") return true;
        return t === "input" && /^(text|url|search|email|tel|)$/i.test(el.getAttribute("type") || "");
    }
    document.addEventListener("focusin", function (e) { if (isField(e.target)) last = e.target; });
    // Pressing a variable must not take the focus from the field it goes into.
    box.addEventListener("mousedown", function (e) { if (e.target.closest("[data-var]")) e.preventDefault(); });

    box.addEventListener("click", function (e) {
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
