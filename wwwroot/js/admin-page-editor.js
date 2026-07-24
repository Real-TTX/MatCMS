// Page editor: add-block modal + drag & drop reordering of blocks.
(function () {
    "use strict";

    // ---------- Add-block modal ----------
    var modal = document.getElementById("add-block-modal");
    var openBtn = document.getElementById("add-block-btn");
    var closeBtn = document.getElementById("add-block-close");

    if (modal && openBtn) {
        var open = function () { modal.classList.add("open"); document.body.style.overflow = "hidden"; };
        var close = function () { modal.classList.remove("open"); document.body.style.overflow = ""; };
        openBtn.addEventListener("click", open);
        if (closeBtn) closeBtn.addEventListener("click", close);
        modal.addEventListener("click", function (e) { if (e.target === modal) close(); });
        document.addEventListener("keydown", function (e) {
            if (e.key === "Escape" && modal.classList.contains("open")) close();
        });
    }

    // ---------- Drag & drop reorder ----------
    var list = document.getElementById("block-list");
    var reorderForm = document.getElementById("reorder-form");
    var reorderInputs = document.getElementById("reorder-inputs");
    if (!list || !reorderForm || !reorderInputs) return;

    var dragged = null;

    function currentOrder() {
        return Array.prototype.slice.call(list.querySelectorAll(".block-item"))
            .map(function (el) { return el.getAttribute("data-block-id"); });
    }

    var initialOrder = currentOrder().join(",");

    function getAfterElement(y) {
        var els = Array.prototype.slice.call(list.querySelectorAll(".block-item:not(.dragging)"));
        var closest = { offset: Number.NEGATIVE_INFINITY, element: null };
        els.forEach(function (child) {
            var box = child.getBoundingClientRect();
            var offset = y - box.top - box.height / 2;
            if (offset < 0 && offset > closest.offset) closest = { offset: offset, element: child };
        });
        return closest.element;
    }

    function persistIfChanged() {
        var order = currentOrder();
        if (order.join(",") === initialOrder) return;
        reorderInputs.innerHTML = "";
        order.forEach(function (id) {
            var input = document.createElement("input");
            input.type = "hidden";
            input.name = "order";
            input.value = id;
            reorderInputs.appendChild(input);
        });
        reorderForm.submit();
    }

    Array.prototype.slice.call(list.querySelectorAll(".block-item")).forEach(function (item) {
        var handle = item.querySelector(".drag-handle");
        if (handle) {
            handle.addEventListener("mousedown", function () { item.draggable = true; });
        }
        item.addEventListener("dragstart", function (e) {
            dragged = item;
            item.classList.add("dragging");
            if (e.dataTransfer) { e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", ""); }
        });
        item.addEventListener("dragend", function () {
            item.classList.remove("dragging");
            item.draggable = false;
            dragged = null;
            persistIfChanged();
        });
    });

    list.addEventListener("dragover", function (e) {
        if (!dragged) return;
        e.preventDefault();
        var after = getAfterElement(e.clientY);
        if (after == null) list.appendChild(dragged);
        else list.insertBefore(dragged, after);
    });

    // Reset stray draggable state when a drag never started (plain click on handle).
    document.addEventListener("mouseup", function () {
        Array.prototype.slice.call(list.querySelectorAll('.block-item[draggable="true"]')).forEach(function (i) {
            if (i !== dragged) i.draggable = false;
        });
    });
})();
