// The back office's MatUi shell: the drawer on a phone, the user's context menu at the foot of the menu and the
// 3-state theme button. matui.js does all of this too, but it also handles data-confirm and form validation,
// which mat-dialogs.js and the pages already do here — loading both would ask every question twice. Until the
// pages move onto MatUi's dialogs, this file is the only script of the shell; it uses MatUi's classes and states
// (is-nav-open, aria-expanded, [hidden], data-theme-state) so swapping it for matui.js later changes no markup.
(function () {
    'use strict';
    var app = document.querySelector('[data-admin-app]');
    if (!app) return;

    // --- drawer (max-width: 900px in matui.shell.css) ---
    var toggle = document.querySelector('[data-admin-toggle]');
    function setNav(open) {
        app.classList.toggle('is-nav-open', open);
        if (toggle) toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    }
    if (toggle) toggle.addEventListener('click', function () { setNav(!app.classList.contains('is-nav-open')); });
    document.querySelectorAll('[data-admin-close]').forEach(function (el) { el.addEventListener('click', function () { setNav(false); }); });
    document.querySelectorAll('.mat-nav a').forEach(function (a) { a.addEventListener('click', function () { setNav(false); }); });

    // --- the user's context menu: opens upwards from the foot, positioned like MatUi's (position: fixed) ---
    var openMenu = null, openBtn = null;
    function closeMenu() {
        if (!openMenu) return;
        openMenu.hidden = true;
        openBtn.setAttribute('aria-expanded', 'false');
        openMenu = openBtn = null;
    }
    document.querySelectorAll('[data-admin-menu]').forEach(function (btn) {
        var menu = document.getElementById(btn.getAttribute('data-admin-menu'));
        if (!menu) return;
        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            if (openMenu === menu) { closeMenu(); return; }
            closeMenu();
            menu.hidden = false;
            // The user's menu at the foot opens upwards; a switcher under the logo (data-menu-place="bottom") opens downwards.
            var r = btn.getBoundingClientRect();
            menu.style.left = Math.max(8, r.left) + 'px';
            menu.style.top = (btn.getAttribute('data-menu-place') === 'bottom'
                ? r.bottom + 6
                : Math.max(8, r.top - menu.offsetHeight - 6)) + 'px';
            btn.setAttribute('aria-expanded', 'true');
            openMenu = menu; openBtn = btn;
            var first = menu.querySelector('.mat-menu__item');
            if (first) first.focus();
        });
    });
    document.addEventListener('click', function (e) { if (openMenu && !openMenu.contains(e.target)) closeMenu(); });
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        if (openMenu) { var b = openBtn; closeMenu(); b.focus(); } else setNav(false);
    });
    window.addEventListener('resize', closeMenu);

    // --- list tables on a phone (MatUi TABLE-02): every row becomes a card and every cell carries the name of its
    // column (data-label, read from the table's head), which the CSS writes in front of the value. Done here once for
    // all lists instead of in each of their views; a cell that already has a data-label keeps it ---
    // Every record table with a head row. Left as they are: tables with a phone layout of their own (attention, cleanup,
    // notification matrix) and tables one types into (row editors, inputs, selects) — a form in cards would lose its columns.
    document.querySelectorAll('table.data:not(.attention-list):not(.dash-attention):not(.cleanup-list):not(.notify-matrix)').forEach(function (t) {
        if (!t.tHead || t.closest('.row-editor') || t.querySelector('tbody input:not([type=checkbox]):not([type=radio]):not([type=hidden]), tbody select, tbody textarea')) return;
        var heads = [];
        t.querySelectorAll('thead tr:first-child > th, thead tr:first-child > td').forEach(function (th) {
            var span = parseInt(th.getAttribute('colspan') || '1', 10);
            for (var i = 0; i < span; i++) heads.push(i === 0 ? th.textContent.trim() : '');
        });
        if (!heads.length) return;
        t.classList.add('has-labels');
        t.querySelectorAll('tbody > tr').forEach(function (tr) {
            var col = 0;
            Array.prototype.forEach.call(tr.children, function (cell) {
                if (!cell.hasAttribute('data-label')) cell.setAttribute('data-label', heads[col] || '');
                col += parseInt(cell.getAttribute('colspan') || '1', 10);
            });
        });
    });

    // --- the switcher's small picture of the current site (cloud and connected site alike): one frame, filled after the page
    // has loaded and only on a desktop — a phone shows the initial underneath ---
    var own = document.querySelector('.inst-switch-shot iframe[data-src]');
    if (own && window.matchMedia('(min-width: 861px)').matches) {
        var fill = function () {
            var src = own.getAttribute('data-src');
            if (!src) return;
            own.removeAttribute('data-src');
            own.addEventListener('load', function () { own.classList.add('is-loaded'); });
            own.src = src;
        };
        if (document.readyState === 'complete') fill(); else window.addEventListener('load', fill);
    }

    // --- live site pictures in tile views: the frame renders at desktop width (its CSS width) and is scaled to the
    // tile's real width, so it fills the tile at every screen size instead of a fixed factor leaving a gap ---
    var shots = document.querySelectorAll('.instance-tile-shot, .payload-tile-shot');
    if (shots.length && 'ResizeObserver' in window) {
        var fit = new ResizeObserver(function (entries) {
            entries.forEach(function (e) {
                var frame = e.target.querySelector('iframe');
                if (!frame || !frame.offsetWidth) return;
                e.target.style.setProperty('--shot-scale', (e.target.clientWidth / frame.offsetWidth).toFixed(4));
            });
        });
        shots.forEach(function (s) { fit.observe(s); });
    }

    // --- theme: system -> light -> dark, written on <html> at once and kept on the user ---
    var order = ['system', 'light', 'dark'];
    var themeBtn = document.querySelector('[data-admin-theme]');
    if (themeBtn) {
        var names = (themeBtn.getAttribute('data-names') || 'System|Light|Dark').split('|');
        var label = themeBtn.getAttribute('aria-label');
        function show(state) {
            themeBtn.setAttribute('data-theme-state', state);
            var t = label + ': ' + names[order.indexOf(state)];
            themeBtn.title = t; themeBtn.setAttribute('aria-label', t);
        }
        show(themeBtn.getAttribute('data-theme-state') || 'light');
        themeBtn.addEventListener('click', function () {
            var next = order[(order.indexOf(themeBtn.getAttribute('data-theme-state')) + 1) % order.length];
            document.documentElement.setAttribute('data-theme', next);
            show(next);
            var token = document.querySelector('input[name="__RequestVerificationToken"]');
            var body = new URLSearchParams({ theme: next });
            if (token) body.append('__RequestVerificationToken', token.value);
            fetch(themeBtn.getAttribute('data-save'), { method: 'POST', body: body, credentials: 'same-origin' });
        });
    }
})();
