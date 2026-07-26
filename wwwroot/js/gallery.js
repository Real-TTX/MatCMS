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
(function () {
    "use strict";
    var links = Array.prototype.slice.call(document.querySelectorAll("[data-lightbox]"));
    if (!links.length) return;

    var index = 0;
    var overlay, imgEl, capEl;

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
        overlay.querySelector(".lb-close").addEventListener("click", close);
        overlay.querySelector(".lb-prev").addEventListener("click", function (e) { e.stopPropagation(); step(-1); });
        overlay.querySelector(".lb-next").addEventListener("click", function (e) { e.stopPropagation(); step(1); });
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
    }
    function open(i) { index = i; if (!overlay) build(); show(); overlay.classList.add("open"); document.body.style.overflow = "hidden"; }
    function close() { overlay.classList.remove("open"); document.body.style.overflow = ""; }
    function step(d) { index = (index + d + links.length) % links.length; show(); }

    links.forEach(function (a, i) {
        a.addEventListener("click", function (e) { e.preventDefault(); open(i); });
    });
})();
