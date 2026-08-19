// Client-side tag filtering for media galleries.
(function () {
    "use strict";
    Array.prototype.slice.call(document.querySelectorAll("[data-gallery-filter]")).forEach(function (bar) {
        var gallery = bar.parentNode.querySelector("[data-gallery]");
        if (!gallery) return;
        var items = Array.prototype.slice.call(gallery.querySelectorAll(".gallery__item"));
        bar.addEventListener("click", function (e) {
            var btn = e.target.closest(".gfilter");
            if (!btn) return;
            var tag = (btn.getAttribute("data-tag") || "").toLowerCase();
            bar.querySelectorAll(".gfilter").forEach(function (b) { b.classList.remove("active"); });
            btn.classList.add("active");
            items.forEach(function (it) {
                var tags = (it.getAttribute("data-tags") || "").toLowerCase().split(" ");
                it.style.display = (!tag || tags.indexOf(tag) !== -1) ? "" : "none";
            });
        });
    });
})();

// Self-contained lightbox for gallery blocks. No dependencies.
//
// GROUPS. Every [data-lightbox] belongs to the nearest [data-lightbox-group] ancestor; links without
// one share a single implicit page-wide group. That implicit group is what this file used to do for
// EVERYTHING: one flat list of every [data-lightbox] on the page. On a Referenzen page with nine
// projects that meant "weiter" ran out of project 1 and into project 2's screenshots, and two gallery
// blocks under each other were silently one chain. Prev/next now wrap inside the group they were
// opened from. Markup that predates the attribute (a plugin, a hand-written HTML block) still lands
// in the implicit group and behaves exactly as before.
//
// STRIP. data-lightbox-group="strip" (the gallery block's "Bildstreifen" option) additionally puts a
// row of thumbnails along the bottom edge of the open view, current image centred. The flag lives in
// the group attribute's VALUE rather than in a second attribute because Razor keeps an attribute with
// a null value as attr="" — a separate data-strip would show up empty on every gallery that never
// turned the option on. Off unless asked for, and it renders the generated 320-px copies via
// data-thumb: a strip that pulls the full-size originals makes the page slower, which is the opposite
// of the point.
(function () {
    "use strict";
    var all = Array.prototype.slice.call(document.querySelectorAll("[data-lightbox]"));
    if (!all.length) return;

    // prefers-reduced-motion: no gliding strip, it jumps. Read live so a change in the OS setting
    // applies without a reload.
    var motionQuery = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
    function scrollBehavior() { return (motionQuery && motionQuery.matches) ? "auto" : "smooth"; }

    // Build the groups. One entry per [data-lightbox-group] element, plus the implicit one (key null).
    var groups = new Map();
    all.forEach(function (a) {
        var host = a.closest("[data-lightbox-group]");
        if (!groups.has(host)) groups.set(host, []);
        groups.get(host).push(a);
    });

    var links = [];                  // the links of the group currently open
    var index = 0;
    var showStrip = false;
    var overlay, imgEl, capEl, stripEl, prevBtn, nextBtn, lastFocus;

    function build() {
        overlay = document.createElement("div");
        overlay.className = "lightbox";
        overlay.setAttribute("role", "dialog");
        overlay.setAttribute("aria-modal", "true");
        overlay.innerHTML =
            '<button class="lb-close" aria-label="Schließen">&times;</button>' +
            '<button class="lb-prev" aria-label="Zurück">&#8249;</button>' +
            '<figure class="lb-figure"><img class="lb-img" alt="" /><figcaption class="lb-cap"></figcaption></figure>' +
            '<button class="lb-next" aria-label="Weiter">&#8250;</button>' +
            '<div class="lb-strip" role="tablist" aria-label="Bilder dieser Galerie"></div>';
        document.body.appendChild(overlay);
        imgEl = overlay.querySelector(".lb-img");
        capEl = overlay.querySelector(".lb-cap");
        stripEl = overlay.querySelector(".lb-strip");
        prevBtn = overlay.querySelector(".lb-prev");
        nextBtn = overlay.querySelector(".lb-next");
        overlay.querySelector(".lb-close").addEventListener("click", close);
        prevBtn.addEventListener("click", function (e) { e.stopPropagation(); step(-1); });
        nextBtn.addEventListener("click", function (e) { e.stopPropagation(); step(1); });
        overlay.addEventListener("click", function (e) { if (e.target === overlay) close(); });

        // One delegated handler: the strip's contents are rebuilt per group.
        stripEl.addEventListener("click", function (e) {
            var btn = e.target.closest(".lb-thumb");
            if (!btn) return;
            e.stopPropagation();
            index = +btn.getAttribute("data-i");
            show();
        });

        document.addEventListener("keydown", function (e) {
            if (!overlay.classList.contains("open")) return;
            if (e.key === "Escape") { e.preventDefault(); close(); }
            else if (e.key === "ArrowLeft") { e.preventDefault(); step(-1); }
            else if (e.key === "ArrowRight") { e.preventDefault(); step(1); }
            else if (e.key === "Home") { e.preventDefault(); index = 0; show(); }
            else if (e.key === "End") { e.preventDefault(); index = links.length - 1; show(); }
        });

        // Finger: a horizontal swipe over the picture pages through the group. The strip itself is a
        // normal overflow-x element, so it is already draggable/flickable without any JS.
        var x0 = null, y0 = null;
        overlay.addEventListener("touchstart", function (e) {
            if (e.touches.length !== 1) { x0 = null; return; }
            x0 = e.touches[0].clientX; y0 = e.touches[0].clientY;
        }, { passive: true });
        overlay.addEventListener("touchend", function (e) {
            if (x0 === null || !e.changedTouches.length) return;
            var dx = e.changedTouches[0].clientX - x0, dy = e.changedTouches[0].clientY - y0;
            // Only a clearly horizontal drag counts, or flicking the strip sideways past its end
            // would page the gallery as well.
            if (Math.abs(dx) > 45 && Math.abs(dx) > Math.abs(dy) * 1.5) step(dx < 0 ? 1 : -1);
            x0 = null;
        }, { passive: true });
    }

    function buildStrip() {
        stripEl.innerHTML = "";
        if (!showStrip || links.length < 2) { stripEl.style.display = "none"; return; }
        stripEl.style.display = "";
        links.forEach(function (a, i) {
            var b = document.createElement("button");
            b.type = "button";
            b.className = "lb-thumb";
            b.setAttribute("data-i", i);
            b.setAttribute("role", "tab");
            var img = document.createElement("img");
            // data-thumb is the generated 320-px copy. Fall back to the tile's own <img> (already
            // downloaded, so it costs nothing) and only then to the original.
            var inner = a.querySelector("img");
            img.src = a.getAttribute("data-thumb") || (inner && inner.getAttribute("src")) || a.getAttribute("href");
            img.alt = (inner && inner.getAttribute("alt")) || "";
            img.loading = "lazy";
            img.decoding = "async";
            b.appendChild(img);
            stripEl.appendChild(b);
        });
    }

    function syncStrip() {
        if (!showStrip || !stripEl.children.length) return;
        var thumbs = stripEl.children;
        for (var i = 0; i < thumbs.length; i++) {
            var on = i === index;
            thumbs[i].classList.toggle("is-active", on);
            thumbs[i].setAttribute("aria-selected", on ? "true" : "false");
            // Only the active thumb stays tabbable, so Tab does not walk through 88 buttons before it
            // reaches the close button.
            thumbs[i].tabIndex = on ? 0 : -1;
        }
        var el = thumbs[index];
        if (!el) return;
        // The active one sits in the MIDDLE, the rest run off to both sides. scrollLeft rather than
        // scrollIntoView: the latter also scrolls the page behind the overlay in some browsers.
        stripEl.scrollTo({
            left: el.offsetLeft - (stripEl.clientWidth - el.offsetWidth) / 2,
            behavior: scrollBehavior()
        });
    }

    function show() {
        var a = links[index];
        imgEl.src = a.getAttribute("href");
        var cap = a.getAttribute("data-caption") || "";
        capEl.textContent = cap;
        capEl.style.display = cap ? "" : "none";
        // A one-image group has nothing to page through.
        var many = links.length > 1;
        prevBtn.style.display = many ? "" : "none";
        nextBtn.style.display = many ? "" : "none";
        syncStrip();
    }

    function open(group, i) {
        links = groups.get(group);
        index = i;
        showStrip = !!group && group.getAttribute("data-lightbox-group") === "strip";
        if (!overlay) build();
        overlay.classList.toggle("lightbox--strip", showStrip);
        buildStrip();
        show();
        overlay.classList.add("open");
        document.body.style.overflow = "hidden";
        lastFocus = document.activeElement;
        overlay.querySelector(".lb-close").focus();
    }

    function close() {
        overlay.classList.remove("open");
        document.body.style.overflow = "";
        // Back to the thumbnail the visitor came from, not to the top of the document.
        if (lastFocus && lastFocus.focus) lastFocus.focus();
    }

    function step(d) { index = (index + d + links.length) % links.length; show(); }

    groups.forEach(function (list, group) {
        list.forEach(function (a, i) {
            a.addEventListener("click", function (e) { e.preventDefault(); open(group, i); });
        });
    });
})();
