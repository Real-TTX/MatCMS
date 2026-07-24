// Generic drag & drop reordering for any [data-sortable] list.
// Each list persists its new order by submitting the form referenced via
// data-reorder-form; the ordered item ids are written into [data-order-inputs].
(function () {
    "use strict";

    Array.prototype.slice.call(document.querySelectorAll("[data-sortable]")).forEach(function (list) {
        var form = document.getElementById(list.getAttribute("data-reorder-form"));
        if (!form) return;
        var inputs = form.querySelector("[data-order-inputs]");
        if (!inputs) return;

        var dragged = null;

        function order() {
            return Array.prototype.slice.call(list.querySelectorAll(".sortable-item"))
                .map(function (el) { return el.getAttribute("data-item-id"); });
        }
        var initial = order().join(",");

        function afterElement(y) {
            var els = Array.prototype.slice.call(list.querySelectorAll(".sortable-item:not(.dragging)"));
            var closest = { offset: Number.NEGATIVE_INFINITY, element: null };
            els.forEach(function (child) {
                var box = child.getBoundingClientRect();
                var offset = y - box.top - box.height / 2;
                if (offset < 0 && offset > closest.offset) closest = { offset: offset, element: child };
            });
            return closest.element;
        }

        function persist() {
            var o = order();
            if (o.join(",") === initial) return;
            inputs.innerHTML = "";
            o.forEach(function (id) {
                var input = document.createElement("input");
                input.type = "hidden";
                input.name = "order";
                input.value = id;
                inputs.appendChild(input);
            });
            form.submit();
        }

        Array.prototype.slice.call(list.querySelectorAll(".sortable-item")).forEach(function (item) {
            var handle = item.querySelector(".drag-handle");
            if (handle) handle.addEventListener("mousedown", function () { item.draggable = true; });
            item.addEventListener("dragstart", function (e) {
                dragged = item;
                item.classList.add("dragging");
                if (e.dataTransfer) { e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", ""); }
            });
            item.addEventListener("dragend", function () {
                item.classList.remove("dragging");
                item.draggable = false;
                dragged = null;
                persist();
            });
        });

        list.addEventListener("dragover", function (e) {
            if (!dragged) return;
            e.preventDefault();
            var after = afterElement(e.clientY);
            if (after == null) list.appendChild(dragged);
            else list.insertBefore(dragged, after);
        });

        document.addEventListener("mouseup", function () {
            Array.prototype.slice.call(list.querySelectorAll('.sortable-item[draggable="true"]')).forEach(function (i) {
                if (i !== dragged) i.draggable = false;
            });
        });
    });
})();
