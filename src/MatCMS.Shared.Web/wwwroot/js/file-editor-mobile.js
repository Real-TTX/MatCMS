// The file editors on a phone (template editor in CMS and cloud, plugin editor): two steps instead of two columns.
//
// On a wide screen a [data-fe] tree is "list left, open file right" and this script does nothing. Below 800 px it
// turns the same markup into a file app: step 1 is the list (big rows, actions in a sheet from below, long-press
// for the menu — iOS has no contextmenu event), step 2 is the open file full screen: the editor's own menu bar
// becomes the top bar with "back" and "save", lines wrap, and a bar of the characters a phone keyboard hides
// (Tab < > / { } = " ;) sits above the keyboard.
//
// It is a LAYER: the editors keep their own logic and still write every keystroke into the field the form posts —
// this script only moves what is shown, so nothing typed can be lost by going back or switching files. It knows the
// editors only by their shared classes (.pf-side, .pf-main, .pf-file, .pf-menu, .pf-ctx) and reads which file is
// open from the row marked .is-open, the same thing the tree shows.
(function () {
    if (window.MatFE) return;
    var narrow = window.matchMedia('(max-width: 800px)');
    var de = (document.documentElement.lang || 'de').toLowerCase().indexOf('de') === 0;
    var TXT = de ? { back: 'Dateien', notes: 'Hinweise' } : { back: 'Files', notes: 'Notes' };
    var KEYS = ['⇥', '<', '>', '/', '{', '}', '(', ')', '=', '"', "'", ';', ':', '$', '#'];

    // Asked by the editors before they focus the code on opening a file: on a phone that would pop the keyboard
    // up over a file the user only wanted to look at. The keyboard comes when they tap into the code.
    window.MatFE = { noAutoFocus: function () { return narrow.matches && !!document.querySelector('[data-fe]'); } };

    function init() { document.querySelectorAll('[data-fe]').forEach(setup); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();

    function setup(tree) {
        var side = tree.querySelector('.pf-side'), main = tree.querySelector('.pf-main'), list = tree.querySelector('.pf-list');
        var menu = main && main.querySelector('.pf-menu');
        if (!side || !main || !list || !menu) return;
        var form = tree.closest('form');
        var key = 'matfe.reopen:' + location.pathname;

        // ---- the top bar: the editor's own menu bar, plus "back" in front and "save" at the end ----------------
        var back = document.createElement('button');
        back.type = 'button';
        back.className = 'pf-menu-item fe-back';
        back.innerHTML = '<i class="ti ti-chevron-left" aria-hidden="true"></i> ' + TXT.back;
        back.addEventListener('click', function () { if (history.state && history.state.matfe) history.back(); else showList(); });
        menu.insertBefore(back, menu.firstChild);

        var submitter = findSubmitter(form);
        var save = null;
        if (submitter) {
            save = document.createElement('button');
            save.type = 'button';
            save.className = 'btn btn-sm fe-save';
            save.textContent = (submitter.textContent || '').trim();
            save.addEventListener('click', function () {
                // Back to the same file after the round trip — the page reloads into its list otherwise.
                var cur = openLabel();
                try { if (cur) sessionStorage.setItem(key, JSON.stringify({ name: cur, at: Date.now() })); } catch (e) { }
                submitter.click();
            });
            menu.appendChild(save);
            // A dot on "save" while there are changes: every editor writes into its field with an input event.
            main.addEventListener('input', function () { save.classList.add('is-dirty'); });
        }

        // ---- the bar above the keyboard ------------------------------------------------------------------------
        var keys = document.createElement('div');
        keys.className = 'fe-keys';
        keys.setAttribute('aria-hidden', 'true');   // a typing aid; every character is also on the keyboard
        KEYS.forEach(function (k) {
            var b = document.createElement('button');
            b.type = 'button';
            b.className = 'fe-key mono';
            b.textContent = k;
            // pointerdown, not click, and prevented: the editor must keep the focus or the keyboard closes.
            b.addEventListener('pointerdown', function (ev) { ev.preventDefault(); type(k === '⇥' ? '  ' : k); });
            b.addEventListener('click', function (ev) { ev.preventDefault(); });
            keys.appendChild(b);
        });
        main.appendChild(keys);

        function codeMirror() {
            var el = main.querySelector('.CodeMirror');
            return el && el.CodeMirror && el.offsetParent !== null ? el.CodeMirror : null;
        }
        function textarea() {
            return Array.prototype.find.call(main.querySelectorAll('textarea'), function (t) { return t.offsetParent !== null && !t.readOnly; }) || null;
        }
        function type(text) {
            var cm = codeMirror();
            if (cm) { cm.replaceSelection(text); return; }
            var t = textarea();
            if (!t) return;
            t.setRangeText(text, t.selectionStart, t.selectionEnd, 'end');
            t.dispatchEvent(new Event('input', { bubbles: true }));
        }
        function updateKeys() { keys.hidden = !(codeMirror() || textarea()); }

        // ---- notes behind one "ⓘ": the help texts around the tree are a wall above (or below) it on a phone -----
        var notes = [];
        function collect(el, dir) {
            for (var n = el[dir]; n; n = n[dir]) {
                if (n.matches && n.matches('style, script')) continue;
                if (n.classList && n.classList.contains('help')) { notes.push(n); continue; }
                break;
            }
        }
        collect(tree, 'previousElementSibling');
        collect(tree, 'nextElementSibling');
        if (notes.length) {
            var det = document.createElement('details');
            det.className = 'fe-notes';
            var sum = document.createElement('summary');
            sum.innerHTML = '<i class="ti ti-info-circle" aria-hidden="true"></i> ' + TXT.notes;
            det.appendChild(sum);
            tree.parentNode.insertBefore(det, tree);
            notes.forEach(function (n) { det.appendChild(n); });
        }

        // ---- the two steps -------------------------------------------------------------------------------------
        function openLabel() {
            var n = list.querySelector('.pf-file.is-open .pf-file-name');
            return n ? n.textContent.trim() : null;
        }
        function showFile(push) {
            if (!narrow.matches) return;
            tree.classList.add('fe-file');
            document.body.classList.add('fe-file-open');
            if (push && !(history.state && history.state.matfe)) history.pushState({ matfe: true }, '');
            var cm = codeMirror();
            if (cm) { cm.setOption('lineWrapping', true); setTimeout(function () { cm.refresh(); }, 0); }
            updateKeys();
            fit();
        }
        function showList() {
            tree.classList.remove('fe-file');
            document.body.classList.remove('fe-file-open');
            var a = document.activeElement;
            if (a && main.contains(a)) a.blur();
        }
        window.addEventListener('popstate', function () { if (tree.classList.contains('fe-file')) showList(); });

        // A row that was tapped opens its file — read back from the tree once the editor has done its work, so
        // only a row that really became the open one counts (the root and a folder toggle do not).
        list.addEventListener('click', function (ev) {
            var name = ev.target.closest('.pf-file-name');
            if (!name || !narrow.matches) return;
            if (suppressClick) { suppressClick = false; ev.preventDefault(); ev.stopPropagation(); return; }
            var label = name.textContent.trim();
            setTimeout(function () { if (openLabel() === label) showFile(true); }, 0);
        });
        // "Edit", a blueprint, … in the row's menu: when it changed the open file, show it.
        document.addEventListener('click', function (ev) {
            if (!narrow.matches || !ev.target.closest('.pf-ctx-item')) return;
            var before = openLabel();
            setTimeout(function () { var now = openLabel(); if (now && now !== before) showFile(true); }, 0);
        }, true);

        // ---- long-press opens the row's menu (iOS fires no contextmenu) ---------------------------------------
        var timer = null, startX = 0, startY = 0, suppressClick = false, gotNative = false, pressed = false;
        list.addEventListener('touchstart', function (ev) {
            var row = ev.target.closest('.pf-file');
            if (!row || ev.touches.length !== 1) return;
            var t = ev.touches[0]; startX = t.clientX; startY = t.clientY; gotNative = false; pressed = false;
            timer = setTimeout(function () {
                timer = null;
                if (gotNative) return;
                suppressClick = true; pressed = true;
                row.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: startX, clientY: startY }));
            }, 550);
        }, { passive: true });
        list.addEventListener('touchmove', function (ev) {
            if (!timer) return;
            var t = ev.touches[0];
            if (Math.abs(t.clientX - startX) > 10 || Math.abs(t.clientY - startY) > 10) { clearTimeout(timer); timer = null; }
        }, { passive: true });
        // Lifting the finger after a long press must not become a click: the editors close their menu on any click
        // outside it, and it would shut the sheet the press just opened.
        list.addEventListener('touchend', function (ev) {
            if (timer) { clearTimeout(timer); timer = null; }
            if (pressed) { pressed = false; ev.preventDefault(); setTimeout(function () { suppressClick = false; }, 400); }
        }, { passive: false });
        // Android does fire contextmenu on a long press — then that one is used and ours stands down.
        list.addEventListener('contextmenu', function (ev) { if (ev.isTrusted) { gotNative = true; if (timer) { clearTimeout(timer); timer = null; } } }, true);

        // ---- the open file fills the VISIBLE viewport (smaller while the keyboard is up) -----------------------
        function fit() {
            var vv = window.visualViewport;
            var h = vv ? vv.height : window.innerHeight, top = vv ? vv.offsetTop : 0;
            tree.style.setProperty('--fe-vh', h + 'px');
            tree.style.setProperty('--fe-top', top + 'px');
            var cm = codeMirror(); if (cm) cm.refresh();
        }
        if (window.visualViewport) {
            window.visualViewport.addEventListener('resize', function () { if (tree.classList.contains('fe-file')) fit(); });
            window.visualViewport.addEventListener('scroll', function () { if (tree.classList.contains('fe-file')) fit(); });
        }
        main.addEventListener('focusin', updateKeys);

        // ---- phone or desktop ---------------------------------------------------------------------------------
        function apply() {
            tree.classList.toggle('fe-mobile', narrow.matches);
            document.body.classList.toggle('fe-mobile-page', narrow.matches);
            if (!narrow.matches) showList();
        }
        if (narrow.addEventListener) narrow.addEventListener('change', apply); else narrow.addListener(apply);
        apply();

        // Back into the file that was open before "save" reloaded the page.
        var reopen = null;
        // Only right after the save: a page that redirects elsewhere (the template editor goes to its list) must not
        // have a file pop open on some later visit.
        try {
            var saved = JSON.parse(sessionStorage.getItem(key) || "null");
            sessionStorage.removeItem(key);
            if (saved && Date.now() - saved.at < 30000) reopen = saved.name;
        } catch (e) { }
        if (reopen && narrow.matches) {
            setTimeout(function () {
                var hit = Array.prototype.find.call(list.querySelectorAll('.pf-file-name'), function (n) { return n.textContent.trim() === reopen; });
                if (hit) hit.click();
            }, 50);
        }
    }

    // The page's own save button for the form the tree is in: inside the form, or outside it via form="…".
    function findSubmitter(form) {
        if (!form) return null;
        var outside = form.id ? document.querySelectorAll('button[type=submit][form="' + form.id + '"]') : [];
        var all = Array.prototype.slice.call(outside).concat(Array.prototype.slice.call(form.querySelectorAll('button[type=submit]')));
        return all.find(function (b) { return !b.classList.contains('btn-danger') && !b.classList.contains('btn-ghost') && !b.hasAttribute('formaction') && !b.closest('[data-fe]'); }) || null;
    }

    // ---- the row menu as a sheet from below ---------------------------------------------------------------------
    // The editors place .pf-ctx at the finger; on a phone CSS pins it to the bottom edge. A backdrop catches the tap
    // outside it, so that tap closes the sheet instead of also opening whatever row lies under it.
    var backdrop = null;
    new MutationObserver(function () {
        var sheet = narrow.matches && document.querySelector('body > .pf-ctx');
        if (sheet && !backdrop) {
            backdrop = document.createElement('div');
            backdrop.className = 'fe-backdrop';
            backdrop.addEventListener('click', function (ev) {
                ev.stopPropagation();
                // Both editors close their menu on Escape.
                document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
            });
            document.body.appendChild(backdrop);
        } else if (!sheet && backdrop) {
            backdrop.remove(); backdrop = null;
        }
    }).observe(document.body, { childList: true });
})();
