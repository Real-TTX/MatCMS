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

    // --- a site's stored picture of its home page (/_preview.webp): faded in when it has arrived; a site that has none yet (or is
    // older than the picture) answers 404, and the box falls back to the live frame it always had ---
    function liveFrame(img) {
        var src = img.getAttribute('data-fallback');
        var box = img.parentNode;
        img.remove();
        if (!src || !box) return;
        // the small pictures of the bar and the site picker never load a live site on a phone (the initial stays)
        if (box.matches('.inst-switch-shot, .inst-picker-shot') && !window.matchMedia('(min-width: 861px)').matches) return;
        var fr = document.createElement('iframe');
        fr.setAttribute('tabindex', '-1'); fr.setAttribute('scrolling', 'no'); fr.setAttribute('aria-hidden', 'true');
        fr.setAttribute('referrerpolicy', 'no-referrer');
        if (/^https?:/i.test(src)) fr.setAttribute('sandbox', 'allow-same-origin allow-scripts');
        fr.addEventListener('load', function () { fr.classList.add('is-loaded'); });
        fr.src = src;
        box.appendChild(fr);
        // tiles scale their frame to the tile's width (see below); a frame added later is measured here
        if (box.matches('.instance-tile-shot, .payload-tile-shot') && fr.offsetWidth)
            box.style.setProperty('--shot-scale', (box.clientWidth / fr.offsetWidth).toFixed(4));
    }
    document.querySelectorAll('img.site-shot').forEach(function (img) {
        var shown = function () { img.classList.add('is-loaded'); };
        if (img.complete && img.naturalWidth) shown();
        else if (img.complete && img.getAttribute('src')) liveFrame(img);
        else {
            img.addEventListener('load', shown);
            img.addEventListener('error', function () { liveFrame(img); });
        }
    });

    // --- taking that picture (CMS admin of a site connected to a cloud): when the layout says one is due, the home page is loaded into
    // a hidden same-origin frame at desktop size, drawn with the vendored modern-screenshot INSIDE that frame (its own document), and
    // posted; the server re-encodes it. Desktop only, after the page has settled, at most one try per ten minutes per tab. ---
    var due = document.querySelector('[data-site-shot-due]');
    if (due && window.matchMedia('(min-width: 861px)').matches && window.fetch && window.Promise) {
        var tried = 0;
        try { tried = +(sessionStorage.getItem('matSiteShot') || 0); } catch (e) { /* private mode */ }
        if (Date.now() - tried > 10 * 60 * 1000) {
            var take = function () {
                try { sessionStorage.setItem('matSiteShot', String(Date.now())); } catch (e) { /* private mode */ }
                var fr = document.createElement('iframe');
                fr.setAttribute('aria-hidden', 'true'); fr.setAttribute('tabindex', '-1');
                // Off screen, NOT transparent: Chromium throttles drawing in a frame with opacity 0, and the picture then takes half a minute.
                fr.style.cssText = 'position:fixed;left:-20000px;top:0;width:1280px;height:800px;border:0;pointer-events:none';
                var done = function () { if (fr.parentNode) fr.parentNode.removeChild(fr); };
                var timer = setTimeout(done, 60000);
                fr.addEventListener('load', function () {
                    var w = fr.contentWindow, d = fr.contentDocument;
                    if (!w || !d || !d.head) { done(); return; }
                    var s = d.createElement('script');
                    s.src = due.getAttribute('data-lib');
                    s.onerror = done;
                    s.onload = function () {
                        (d.fonts && d.fonts.ready ? d.fonts.ready : Promise.resolve())
                            .then(function () { return new Promise(function (r) { setTimeout(r, 1500); }); })
                            .then(function () {
                                var bg = w.getComputedStyle(d.body).backgroundColor;
                                return w.modernScreenshot.domToBlob(d.documentElement, {
                                    // timeout: the library waits for EVERY picture of the page, and a lazy one below the fold never loads in a
                                    // hidden frame — it would wait its default 30 s for pictures the cut-out does not even show
                                    width: 1280, height: 800, scale: 0.75, type: 'image/webp', quality: 0.85, timeout: 3000,
                                    backgroundColor: bg && bg !== 'rgba(0, 0, 0, 0)' ? bg : '#ffffff'
                                });
                            })
                            .then(function (blob) {
                                return fetch('/admin/api/site-preview', {
                                    method: 'POST', credentials: 'same-origin', body: blob,
                                    headers: { 'Content-Type': blob.type || 'image/webp', 'RequestVerificationToken': due.getAttribute('data-token') }
                                });
                            })
                            .then(function (r) { return r.ok ? r.json() : null; })
                            .then(function (j) {
                                var box = document.querySelector('.inst-switch-shot');
                                if (!j || !j.url || !box) return;
                                box.querySelectorAll('iframe, img.site-shot').forEach(function (x) { x.remove(); });
                                var img = new Image();
                                img.className = 'site-shot'; img.alt = ''; img.setAttribute('aria-hidden', 'true');
                                img.onload = function () { img.classList.add('is-loaded'); };
                                img.src = j.url;
                                box.appendChild(img);
                            })
                            .catch(function () { /* no picture this time; the live frame stays */ })
                            .then(function () { clearTimeout(timer); done(); });
                    };
                    d.head.appendChild(s);
                });
                fr.src = '/';
                document.body.appendChild(fr);
            };
            var later = function () { setTimeout(function () { (window.requestIdleCallback || function (f) { f(); })(take); }, 2500); };
            if (document.readyState === 'complete') later(); else window.addEventListener('load', later);
        }
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
