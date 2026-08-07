// Adds prev/next controls to card carousels (cards block, layout = carousel).
// Progressive enhancement: the track already scrolls/snaps via CSS; this just adds arrows.
(function () {
    "use strict";
    Array.prototype.slice.call(document.querySelectorAll("[data-carousel]")).forEach(function (wrap) {
        var track = wrap.querySelector(".cards-grid");
        if (!track) return;
        if (track.scrollWidth <= track.clientWidth + 4) return; // nothing to scroll

        // Localized aria-labels come from the block (data-prev/data-next); fall back to German.
        var prevLabel = wrap.getAttribute("data-prev") || "Zurück";
        var nextLabel = wrap.getAttribute("data-next") || "Weiter";
        var prev = document.createElement("button");
        prev.type = "button"; prev.className = "carousel-btn prev"; prev.setAttribute("aria-label", prevLabel); prev.innerHTML = "&#8249;";
        var next = document.createElement("button");
        next.type = "button"; next.className = "carousel-btn next"; next.setAttribute("aria-label", nextLabel); next.innerHTML = "&#8250;";
        wrap.appendChild(prev); wrap.appendChild(next);

        function step() {
            var card = track.querySelector(".feat-card");
            return card ? card.getBoundingClientRect().width + 26 : Math.round(track.clientWidth * 0.8);
        }
        function update() {
            prev.disabled = track.scrollLeft <= 2;
            next.disabled = track.scrollLeft + track.clientWidth >= track.scrollWidth - 2;
        }
        prev.addEventListener("click", function () { track.scrollBy({ left: -step(), behavior: "smooth" }); });
        next.addEventListener("click", function () { track.scrollBy({ left: step(), behavior: "smooth" }); });
        track.addEventListener("scroll", update, { passive: true });
        window.addEventListener("resize", update);
        update();
    });
})();
