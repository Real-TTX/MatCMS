// The context switcher in the top bar: open/close plus lazily filled thumbnails.
//
// It lives in the LAYOUT, so this has to as well. It used to sit in the one page that embeds an
// instance, and when the switcher moved into the bar on every page the script stayed behind — the
// menu was then present everywhere and opened nowhere.
(function () {
    "use strict";

    var picker = document.querySelector('[data-inst-picker]');
    if (!picker) return;
    var toggle = picker.querySelector('[data-inst-toggle]');
    var menu = picker.querySelector('[data-inst-menu]');
    if (!toggle || !menu) return;

    var search = menu.querySelector('[data-inst-search]');
    var close = menu.querySelector('[data-inst-close]');
    var loaded = false;
    function open(on) {
        menu.hidden = !on;
        toggle.setAttribute('aria-expanded', on ? 'true' : 'false');
        picker.classList.toggle('is-open', on);
        // Full-screen on a phone: the page behind must not scroll along under the finger.
        document.body.classList.toggle('inst-picker-open', on);
        if (on && search) { search.value = ''; filter(); if (window.matchMedia('(min-width: 861px)').matches) search.focus(); }
        // First open fills the thumbnails. Doing it on page load would fetch every customer site just
        // to draw a menu nobody may open.
        if (on && !loaded) {
            loaded = true;
            menu.querySelectorAll('iframe[data-src]').forEach(function (fr) {
                fr.src = fr.getAttribute('data-src');
            });
        }
    }

    function filter() {
        var q = (search && search.value || '').trim().toLowerCase();
        menu.querySelectorAll('.inst-picker-item').forEach(function (a) {
            a.hidden = q.length > 0 && a.textContent.toLowerCase().indexOf(q) < 0;
        });
    }
    if (search) search.addEventListener('input', filter);
    if (close) close.addEventListener('click', function (e) { e.stopPropagation(); open(false); });

    toggle.addEventListener('click', function (e) {
        e.stopPropagation();          // the document handler below would close it again immediately
        open(menu.hidden);
    });
    document.addEventListener('click', function (e) {
        if (!picker.contains(e.target)) open(false);
    });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') open(false); });
})();
