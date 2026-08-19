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
(function () {
    "use strict";
    var all = Array.prototype.slice.call(document.querySelectorAll("[data-lightbox]"));
    if (!all.length) return;

    // Build the groups. One entry per [data-lightbox-group] element, plus the implicit one (key null).
    var groups = new Map();
    all.forEach(function (a) {
        var host = a.closest("[data-lightbox-group]");
        if (!groups.has(host)) groups.set(host, []);
        groups.get(host).push(a);
    });

    var links = [];                  // the links of the group currently open
    var index = 0;
    var overlay, imgEl, capEl, prevBtn, nextBtn;

    function build() {
        overlay = document.createElement("div");
        overlay.className = "lightbox";
        overlay.innerHTML =
            '<button class="lb-close" aria-label="Schließen">&times;</button>' +
            '<button class="lb-prev" aria-label="Zurück">&#8249;</button>' +
            '<figure class="lb-figure"><img class="lb-img" alt="" /><figcaption class="lb-cap"></figcaption></figure>' +
            '<button class="lb-next" aria-label="Weiter">&#8250;</button>';
        document.body.appendChild(overlay);
        imgEl = overlay.querySelector(".lb-img");
        capEl = overlay.querySelector(".lb-cap");
        prevBtn = overlay.querySelector(".lb-prev");
        nextBtn = overlay.querySelector(".lb-next");
        overlay.querySelector(".lb-close").addEventListener("click", close);
        prevBtn.addEventListener("click", function (e) { e.stopPropagation(); step(-1); });
        nextBtn.addEventListener("click", function (e) { e.stopPropagation(); step(1); });
        overlay.addEventListener("click", function (e) { if (e.target === overlay) close(); });
        document.addEventListener("keydown", function (e) {
            if (!overlay.classList.contains("open")) return;
            if (e.key === "Escape") close();
            else if (e.key === "ArrowLeft") step(-1);
            else if (e.key === "ArrowRight") step(1);
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
    }

    function open(group, i) {
        links = groups.get(group);
        index = i;
        if (!overlay) build();
        show();
        overlay.classList.add("open");
        document.body.style.overflow = "hidden";
    }

    function close() { overlay.classList.remove("open"); document.body.style.overflow = ""; }
    function step(d) { index = (index + d + links.length) % links.length; show(); }

    groups.forEach(function (list, group) {
        list.forEach(function (a, i) {
            a.addEventListener("click", function (e) { e.preventDefault(); open(group, i); });
        });
    });
})();
