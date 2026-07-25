// Component designer: repeatable field rows -> hidden FieldsJson, plus a live placeholder hint.
(function () {
    "use strict";
    var rows = document.getElementById("field-rows");
    var addBtn = document.getElementById("add-field");
    var hidden = document.getElementById("FieldsJson");
    var form = document.getElementById("component-form");
    var hint = document.getElementById("placeholder-hint");
    if (!rows || !form) return;

    var TYPES = window.MATCMS_FIELD_TYPES || [["text", "Text"]];

    function slug(s) {
        return (s || "").trim().toLowerCase()
            .replace(/ä/g, "ae").replace(/ö/g, "oe").replace(/ü/g, "ue").replace(/ß/g, "ss")
            .replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
    }

    function addRow(field) {
        field = field || { label: "", type: "text" };
        var row = document.createElement("div");
        row.className = "field-row";

        var label = document.createElement("input");
        label.type = "text";
        label.placeholder = "Label";
        label.value = field.label || "";
        label.className = "fr-label";

        var sel = document.createElement("select");
        sel.className = "fr-type";
        TYPES.forEach(function (t) {
            var o = document.createElement("option");
            o.value = t[0]; o.textContent = t[1];
            if (t[0] === field.type) o.selected = true;
            sel.appendChild(o);
        });

        var rm = document.createElement("button");
        rm.type = "button"; rm.className = "btn btn-sm btn-danger"; rm.textContent = "✕";
        rm.title = window.MATCMS_REMOVE || "Löschen";
        rm.addEventListener("click", function () { row.remove(); updateHint(); });

        label.addEventListener("input", updateHint);

        row.appendChild(label);
        row.appendChild(sel);
        row.appendChild(rm);
        rows.appendChild(row);
    }

    function collect() {
        return Array.prototype.slice.call(rows.querySelectorAll(".field-row")).map(function (r) {
            var label = r.querySelector(".fr-label").value.trim();
            var type = r.querySelector(".fr-type").value;
            var existing = r.getAttribute("data-id");
            var id = existing || slug(label);
            return { id: id, label: label || id, type: type };
        }).filter(function (f) { return f.id; });
    }

    function updateHint() {
        if (!hint) return;
        var ids = collect().map(function (f) { return "{{" + f.id + "}}"; });
        hint.textContent = ids.join("  ");
    }

    // Seed rows from existing data.
    try {
        var initial = JSON.parse(rows.getAttribute("data-fields") || "[]");
        if (Array.isArray(initial)) initial.forEach(function (f) { addRow(f); });
    } catch (e) { /* ignore */ }
    if (!rows.querySelector(".field-row")) addRow();
    updateHint();

    addBtn.addEventListener("click", function () { addRow(); updateHint(); });

    form.addEventListener("submit", function () {
        hidden.value = JSON.stringify(collect());
    });
})();
