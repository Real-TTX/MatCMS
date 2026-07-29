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
        var key = root.getAttribute('data-list-key');   // localStorage suffix for the chosen view
        var page = 0;

        function items() {
            return Array.prototype.slice.call(root.querySelectorAll(
                '[data-list-table] tbody > tr, [data-list-tiles] > [data-search]'));
        }
        function hay(el) { return (el.getAttribute('data-search') || el.textContent || '').toLowerCase(); }

        function apply() {
            var all = items();
            var q = ((search && search.value) || '').trim().toLowerCase();
            var matched = all.filter(function (el) { return !q || hay(el).indexOf(q) !== -1; });
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
