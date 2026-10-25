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
            var r = btn.getBoundingClientRect();
            menu.style.left = Math.max(8, r.left) + 'px';
            menu.style.top = Math.max(8, r.top - menu.offsetHeight - 6) + 'px';
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
