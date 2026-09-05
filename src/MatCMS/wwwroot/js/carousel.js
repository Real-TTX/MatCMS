// Card carousels (cards block, layout = carousel | coverflow).
// Progressive enhancement: the track already scrolls/snaps via CSS. This adds prev/next arrows,
// optional autoplay (data-autoplay, pausing on hover/touch), and — for coverflow — marks the
// centred card so CSS can enlarge it while the neighbours stay small and dimmed.
(function () {
    "use strict";
    var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    Array.prototype.slice.call(document.querySelectorAll("[data-carousel]")).forEach(function (wrap) {
        var track = wrap.querySelector(".cards-grid");
        if (!track) return;
        var coverflow = wrap.classList.contains("is-coverflow");

        function cards() { return Array.prototype.slice.call(track.querySelectorAll(".feat-card")); }
        function scrollable() { return track.scrollWidth > track.clientWidth + 4; }

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
            return card ? card.getBoundingClientRect().width + 30 : Math.round(track.clientWidth * 0.8);
        }
        function update() {
            var can = scrollable();
            prev.disabled = !can || track.scrollLeft <= 2;
            next.disabled = !can || track.scrollLeft + track.clientWidth >= track.scrollWidth - 2;
            prev.hidden = next.hidden = !can;
        }
        // Coverflow: whichever card sits nearest the track's centre is the "hero" (CSS enlarges it).
        function updateCenter() {
            if (!coverflow) return;
            var tr = track.getBoundingClientRect();
            var mid = tr.left + tr.width / 2;
            var best = null, bd = Infinity;
            cards().forEach(function (c) {
                var r = c.getBoundingClientRect();
                var d = Math.abs(r.left + r.width / 2 - mid);
                if (d < bd) { bd = d; best = c; }
            });
            cards().forEach(function (c) { c.classList.toggle("cf-center", c === best); });
        }

        prev.addEventListener("click", function () { track.scrollBy({ left: -step(), behavior: "smooth" }); });
        next.addEventListener("click", function () { track.scrollBy({ left: step(), behavior: "smooth" }); });

        var ticking = false;
        track.addEventListener("scroll", function () {
            update();
            if (coverflow && !ticking) { ticking = true; requestAnimationFrame(function () { updateCenter(); ticking = false; }); }
        }, { passive: true });
        window.addEventListener("resize", function () { update(); updateCenter(); });

        // Coverflow: clicking a dimmed side cover brings it to the centre instead of following a link
        // (its body — and button — are hidden until it is the centre).
        if (coverflow) {
            track.addEventListener("click", function (e) {
                var card = e.target.closest(".feat-card");
                if (card && !card.classList.contains("cf-center")) {
                    e.preventDefault();
                    card.scrollIntoView({ behavior: "smooth", inline: "center", block: "nearest" });
                }
            });
        }

        // Autoplay: slide on by itself, loop at the end, and pause while the visitor is interacting.
        var auto = wrap.hasAttribute("data-autoplay") && !reduce;
        var timer = null, resumeAt = 0;
        function advance() {
            if (!scrollable()) return;
            if (track.scrollLeft + track.clientWidth >= track.scrollWidth - 4) track.scrollTo({ left: 0, behavior: "smooth" });
            else track.scrollBy({ left: step(), behavior: "smooth" });
        }
        function play() { if (auto && !timer) timer = setInterval(advance, 3500); }
        function pause() { if (timer) { clearInterval(timer); timer = null; } }
        if (auto) {
            ["mouseenter", "focusin", "pointerdown", "touchstart"].forEach(function (ev) { wrap.addEventListener(ev, pause, { passive: true }); });
            ["mouseleave", "focusout"].forEach(function (ev) { wrap.addEventListener(ev, play); });
            // Touch has no "leave"; resume a few seconds after the last touch.
            wrap.addEventListener("touchend", function () { pause(); clearTimeout(resumeAt); resumeAt = setTimeout(play, 4000); }, { passive: true });
            play();
        }

        update();
        updateCenter();
    });
})();
