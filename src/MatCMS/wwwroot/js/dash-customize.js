// The dashboard's "Anpassen" panel: opens and closes it, and moves a row up or down. What is shown and in which order is posted as the rows stand
// (the hidden "id" inputs, in order; the ticked "show" boxes) — the server stores it, there is nothing else to keep in step here.
(function () {
    var panel = document.getElementById("dash-cfg");
    if (!panel) return;
    document.querySelectorAll("[data-dash-cfg-open]").forEach(function (b) {
        b.addEventListener("click", function () {
            panel.hidden = !panel.hidden;
            if (!panel.hidden) panel.scrollIntoView({ block: "nearest", behavior: "smooth" });
        });
    });
    panel.querySelectorAll("[data-dash-cfg-close]").forEach(function (b) { b.addEventListener("click", function () { panel.hidden = true; }); });
    panel.addEventListener("click", function (e) {
        var btn = e.target.closest("[data-dash-up],[data-dash-down]");
        if (!btn) return;
        var row = btn.closest("li");
        if (!row) return;
        if (btn.hasAttribute("data-dash-up") && row.previousElementSibling) row.parentNode.insertBefore(row, row.previousElementSibling);
        else if (btn.hasAttribute("data-dash-down") && row.nextElementSibling) row.parentNode.insertBefore(row.nextElementSibling, row);
        btn.focus();
    });
})();
