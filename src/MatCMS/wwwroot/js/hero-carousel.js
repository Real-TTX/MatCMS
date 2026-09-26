// Hero slideshow: progressive enhancement over [data-hero-carousel] emitted by Blocks/_Hero.cshtml.
// Drives crossfade / ken-burns / slide, builds the navigation dots, auto-advances (pausing on hover
// and when the tab is hidden), and respects prefers-reduced-motion (no auto-advance, no transitions).
(function () {
    function initOne(el) {
        var mode = el.getAttribute('data-mode') || 'crossfade';
        var interval = parseInt(el.getAttribute('data-interval'), 10) || 5000;
        var track = el.querySelector('.hero-carousel__track');
        var slides = Array.prototype.slice.call(el.querySelectorAll('.hero-carousel__slide'));
        if (!track || slides.length < 2) return;

        var idx = 0, timer = null;
        var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        var dots = document.createElement('div');
        dots.className = 'hero-carousel__dots';
        var dotEls = slides.map(function (_, i) {
            var b = document.createElement('button');
            b.type = 'button';
            b.className = 'hero-carousel__dot' + (i === 0 ? ' is-active' : '');
            b.setAttribute('aria-label', 'Bild ' + (i + 1));
            b.addEventListener('click', function () { go(i); restart(); });
            dots.appendChild(b);
            return b;
        });
        el.appendChild(dots);

        function apply() {
            if (mode === 'slide') track.style.transform = 'translateX(' + (-idx * 100) + '%)';
            slides.forEach(function (s, i) { s.classList.toggle('is-active', i === idx); });
            dotEls.forEach(function (d, i) { d.classList.toggle('is-active', i === idx); });
        }
        function go(i) { idx = (i + slides.length) % slides.length; apply(); }
        function next() { go(idx + 1); }
        function stop() { if (timer) { clearInterval(timer); timer = null; } }
        function start() { if (reduce) return; stop(); timer = setInterval(next, interval); }
        function restart() { stop(); start(); }

        el.addEventListener('mouseenter', stop);
        el.addEventListener('mouseleave', start);
        document.addEventListener('visibilitychange', function () {
            if (document.hidden) { stop(); } else { start(); }
        });

        apply();
        start();
    }

    function init() {
        var list = document.querySelectorAll('[data-hero-carousel]');
        Array.prototype.forEach.call(list, initOne);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
