// The back office as an app (PWA) — the browser side. Pwa.cs serves the manifest, the service worker and the offline page.
// Here: register the worker, and handle a connection that drops while a page is open:
//  - a quiet bar says so (and offers to try again), it goes away when the connection is back;
//  - a form is NOT sent while offline (the browser would show its error page and the input would be gone) — it stays filled in;
//  - when the connection is back, a page with nothing typed into it reloads itself (no stale lists after a long offline time);
//    one with unsaved input keeps it and offers "Neu laden" instead.
(function () {
    'use strict';
    var de = (document.documentElement.lang || 'de').toLowerCase().indexOf('de') === 0;
    var T = de ? {
        offline: 'Keine Verbindung – Änderungen können gerade nicht gespeichert werden.',
        held: 'Ohne Verbindung wird nichts gesendet – deine Eingaben bleiben stehen.',
        back: 'Wieder verbunden.',
        retry: 'Erneut versuchen', reload: 'Neu laden', checking: 'Prüfe …'
    } : {
        offline: 'No connection – changes cannot be saved right now.',
        held: 'Nothing is sent without a connection – your input stays where it is.',
        back: 'Connected again.',
        retry: 'Try again', reload: 'Reload', checking: 'Checking …'
    };

    if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost' || location.hostname === '127.0.0.1')) {
        window.addEventListener('load', function () {
            navigator.serviceWorker.register('/pwa/sw.js', { scope: '/' }).catch(function () { /* an app without a worker still works */ });
        });
    }

    // --- the bar ---
    var bar = null, text = null, btn = null, wasOffline = false, dirty = false;
    function ensureBar() {
        if (bar) return;
        bar = document.createElement('div');
        bar.className = 'pwa-net';
        bar.setAttribute('role', 'status');
        bar.setAttribute('aria-live', 'polite');
        bar.hidden = true;
        text = document.createElement('span');
        btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'pwa-net__btn';
        bar.appendChild(text); bar.appendChild(btn);
        document.body.appendChild(bar);
        btn.addEventListener('click', function () {
            if (btn.getAttribute('data-act') === 'reload') { location.reload(); return; }
            btn.textContent = T.checking;
            probe().then(function (up) { if (up) online(); else show(T.offline, 'retry'); });
        });
    }
    function show(msg, act, tone) {
        ensureBar();
        text.textContent = msg;
        btn.textContent = act === 'reload' ? T.reload : T.retry;
        btn.setAttribute('data-act', act);
        btn.hidden = !act;
        bar.classList.toggle('is-ok', tone === 'ok');
        bar.hidden = false;
    }
    function hide() { if (bar) bar.hidden = true; }

    // navigator.onLine only knows "no network at all"; a server that is gone looks the same to the user, so the button asks the server.
    function probe() {
        return fetch('/pwa/manifest.webmanifest', { method: 'HEAD', cache: 'no-store' })
            .then(function (r) { return r.ok; }).catch(function () { return false; });
    }

    function offline() { wasOffline = true; show(T.offline, 'retry'); }
    function online() {
        if (!wasOffline) { hide(); return; }
        wasOffline = false;
        if (!dirty) { location.reload(); return; }      // nothing typed: show the current state, not the one from before
        show(T.back, 'reload', 'ok');                  // something typed: keep it, let the user decide
    }
    window.addEventListener('offline', offline);
    window.addEventListener('online', online);
    document.addEventListener('DOMContentLoaded', function () { if (navigator.onLine === false) offline(); });

    // Typing into a form marks the page as having something to lose.
    document.addEventListener('input', function (e) { if (e.target && e.target.form) dirty = true; }, true);
    document.addEventListener('submit', function (e) {
        if (navigator.onLine === false) {
            e.preventDefault();
            e.stopImmediatePropagation();   // before the confirm dialog of data-confirm and before any page script
            wasOffline = true;
            show(T.held, 'retry');
        } else {
            dirty = false;                  // what is sent is no longer "unsaved"
        }
    }, true);
})();
