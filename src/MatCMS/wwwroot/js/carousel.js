// Card carousels (cards block, layout = carousel | coverflow).
// Progressive enhancement: the track already scrolls/snaps via CSS. This adds prev/next arrows,
// optional autoplay (data-autoplay, pausing on hover/touch), and — for coverflow — marks the
// centred card so CSS can enlarge it while the neighbours stay small and dimmed.
(function () {
    "use strict";
    var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    Array.prototype.slice.call(document.querySelectorAll("[data-carousel]")).forEach(function (wrap) {
        var track = wrap.querySelector(".cards-grid, [data-carousel-track]");
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
            // Distance between the first two slides = one item + gap (works for any track/slide type).
            var items = track.children;
            if (items.length >= 2) return items[1].getBoundingClientRect().left - items[0].getBoundingClientRect().left;
            return items.length ? items[0].getBoundingClientRect().width : Math.round(track.clientWidth * 0.8);
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
        // Autoplay speed: the block can set data-interval (ms); default 3500.
        var interval = parseInt(wrap.getAttribute("data-interval"), 10);
        if (!(interval > 0)) interval = 3500;
        function play() { if (auto && !timer) timer = setInterval(advance, interval); }
        function pause() { if (timer) { clearInterval(timer); timer = null; } }
        if (auto) {
            ["mouseenter", "focusin", "pointerdown", "touchstart"].forEach(function (ev) { wrap.addEventListener(ev, pause, { passive: true }); });
            ["mouseleave", "focusout"].forEach(function (ev) { wrap.addEventListener(ev, play); });
            // Touch has no "leave"; resume a few seconds after the last touch.
            wrap.addEventListener("touchend", function () { pause(); clearTimeout(resumeAt); resumeAt = setTimeout(play, 4000); }, { passive: true });
            play();
        }

        // Dots + counter (data-dots, the gallery's "single" view): one dot per slide, the current one marked, a
        // "3/11" badge on the image; a dot jumps to its slide. The index follows the scroll position, so swiping,
        // the arrows and autoplay all keep it right.
        if (wrap.hasAttribute("data-dots") && track.children.length > 1) {
            var total = track.children.length;
            var label = wrap.getAttribute("data-slide-label") || "Bild {0} von {1}";
            var count = document.createElement("span");
            count.className = "carousel-count";
            count.setAttribute("aria-hidden", "true");
            var dots = document.createElement("div");
            dots.className = "carousel-dots";
            var dotEls = [];
            for (var i = 0; i < total; i++) {
                (function (n) {
                    var d = document.createElement("button");
                    d.type = "button";
                    d.className = "carousel-dot";
                    d.setAttribute("aria-label", label.replace("{0}", n + 1).replace("{1}", total));
                    d.addEventListener("click", function () { track.scrollTo({ left: n * step(), behavior: "smooth" }); });
                    dots.appendChild(d);
                    dotEls.push(d);
                })(i);
            }
            wrap.appendChild(count);
            wrap.appendChild(dots);
            var current = -1;
            var syncDots = function () {
                var s = step() || 1;
                var idx = Math.max(0, Math.min(total - 1, Math.round(track.scrollLeft / s)));
                if (idx === current) return;
                current = idx;
                count.textContent = (idx + 1) + "/" + total;
                dotEls.forEach(function (d, k) {
                    d.classList.toggle("is-active", k === idx);
                    if (k === idx) d.setAttribute("aria-current", "true"); else d.removeAttribute("aria-current");
                });
            };
            track.addEventListener("scroll", function () { requestAnimationFrame(syncDots); }, { passive: true });
            window.addEventListener("resize", function () { current = -1; syncDots(); });
            syncDots();
        }

        update();
        updateCenter();
    });
})();
