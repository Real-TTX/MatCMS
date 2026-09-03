// Shared behaviour for admin list/overview pages: client-side live search, optional paging, and an
// optional table<->tiles view toggle (persisted in localStorage). Purely markup-driven — a page opts
// in with [data-list] on a wrapper and data-* hooks on its parts. No per-page JavaScript needed.
(function () {
    document.querySelectorAll('[data-list]').forEach(function (root) {
        var search = root.querySelector('[data-list-search]');
        var pageSize = parseInt(root.getAttribute('data-list-page-size'), 10) || 0;
        var pager = root.querySelector('[data-list-pager]');
        var emptyEl = root.querySelector('[data-list-empty]');
        var toggle = root.querySelector('[data-list-viewtoggle]');
        // Optional tag filter: a <select data-list-filter> whose value must appear in a row's
        // data-tags. Lets a list offer "only offline" / "only outdated" without a server round trip.
        var filter = root.querySelector('[data-list-filter]');
        var key = root.getAttribute('data-list-key');   // localStorage suffix for the chosen view
        var page = 0;

        // Contain a wide table's horizontal scroll to the table itself. Without this the table widens
        // the whole page, the sticky top bar then stops at the viewport edge while the body scrolls
        // on — the reported "header not full width" bug. Idempotent, and it leaves the table's own
        // width:100% intact (so it still stretches when it fits). Tiles/pager/empty stay siblings.
        var listTable = root.querySelector('[data-list-table]');
        if (listTable && listTable.parentElement && !listTable.parentElement.classList.contains('table-scroll')) {
            var scrollWrap = document.createElement('div');
            scrollWrap.className = 'table-scroll';
            listTable.parentNode.insertBefore(scrollWrap, listTable);
            scrollWrap.appendChild(listTable);
        }

        function items() {
            return Array.prototype.slice.call(root.querySelectorAll(
                '[data-list-table] tbody > tr, [data-list-tiles] > [data-search]'));
        }
        function hay(el) { return (el.getAttribute('data-search') || el.textContent || '').toLowerCase(); }

        function apply() {
            var all = items();
            var q = ((search && search.value) || '').trim().toLowerCase();
            var tag = ((filter && filter.value) || '').trim().toLowerCase();
            var matched = all.filter(function (el) {
                if (q && hay(el).indexOf(q) === -1) return false;
                if (!tag) return true;
                var tags = (el.getAttribute('data-tags') || '').toLowerCase().split(/\s+/);
                return tags.indexOf(tag) !== -1;
            });
            var pages = pageSize ? Math.max(1, Math.ceil(matched.length / pageSize)) : 1;
            if (page >= pages) page = pages - 1;
            if (page < 0) page = 0;
            all.forEach(function (el) { el.hidden = true; });
            matched.forEach(function (el, i) {
                el.hidden = pageSize ? (i < page * pageSize || i >= (page + 1) * pageSize) : false;
            });
            if (emptyEl) emptyEl.hidden = matched.length !== 0;
            renderPager(pages);
        }
        function renderPager(pages) {
            if (!pager) return;
            pager.innerHTML = '';
            if (pages <= 1) return;
            for (var i = 0; i < pages; i++) (function (i) {
                var b = document.createElement('button');
                b.type = 'button'; b.className = 'pager-btn' + (i === page ? ' active' : '');
                b.textContent = i + 1;
                b.addEventListener('click', function () { page = i; apply(); window.scrollTo({ top: 0, behavior: 'smooth' }); });
                pager.appendChild(b);
            })(i);
        }
        function setView(v) {
            root.classList.toggle('list-view-tiles', v === 'tiles');
            root.classList.toggle('list-view-table', v === 'table');
            if (toggle) toggle.querySelectorAll('.vt-btn').forEach(function (b) {
                b.classList.toggle('active', b.getAttribute('data-view') === v);
            });
            if (key) { try { localStorage.setItem('matcms.list.' + key, v); } catch (e) { } }
        }

        if (search) search.addEventListener('input', function () { page = 0; apply(); });
        if (filter) filter.addEventListener('change', function () { page = 0; apply(); });
        if (toggle) {
            var saved = key && (function () { try { return localStorage.getItem('matcms.list.' + key); } catch (e) { return null; } })();
            setView(saved || (root.classList.contains('list-view-table') ? 'table' : 'tiles'));
            toggle.addEventListener('click', function (e) {
                var b = e.target.closest('.vt-btn'); if (b) setView(b.getAttribute('data-view'));
            });
        }
        apply();
    });
})();

// ---- "Hinzufügen" dialogs --------------------------------------------------------------------
// Markup-only, like the list driver above: a page renders _AddMenu and needs no script of its own.
// An option may open ANOTHER dialog (data-add-menu on the option itself) — that is how a question
// gets a second step, and it needs no extra code because both halves already exist here.
(function () {
    function close(dialog) { dialog.classList.remove('open'); }

    document.addEventListener('click', function (e) {
        var opener = e.target.closest('[data-add-menu]');
        var closer = e.target.closest('[data-add-menu-close]');
        // Order matters: an option that opens step two is BOTH, and closing first would otherwise
        // leave the caller's dialog on top of the one it just opened.
        if (closer) {
            var owner = closer.closest('[data-add-menu-dialog]');
            if (owner) close(owner);
        }
        if (opener) {
            var next = document.querySelector('[data-add-menu-dialog="' + opener.getAttribute('data-add-menu') + '"]');
            if (next) next.classList.add('open');
        }
        // Clicking the backdrop closes; clicking inside the dialog must not.
        if (e.target.matches('[data-add-menu-dialog]')) close(e.target);

        // The import forms sit collapsed under their list until asked for. Scrolled to and focused,
        // because unhiding a form somewhere below the fold looks like nothing happened at all.
        var imp = e.target.closest('[data-add-import]');
        if (imp) {
            var target = document.getElementById(imp.getAttribute('data-add-import'));
            if (target) {
                target.hidden = false;
                target.scrollIntoView({ behavior: 'smooth', block: 'center' });
                var field = target.querySelector('textarea, input:not([type=hidden])');
                if (field) field.focus({ preventScroll: true });
            }
        }
    });

    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape') return;
        document.querySelectorAll('[data-add-menu-dialog].open').forEach(close);
    });
})();
