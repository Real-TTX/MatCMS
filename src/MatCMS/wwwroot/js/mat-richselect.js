// Rich select: a custom dropdown for form fields of type "richselect". Each option carries a title,
// optional image, description and small tags; the chosen option's KEY is stored in the hidden input
// (server-side stays a plain value). Delegated on document so the builder's live-preview innerHTML
// swaps don't lose the handlers. Guarded against multiple loads.
// On mobile the menu is presented as a bottom-sheet dialog (with a ✕ header + dimmed backdrop),
// mirroring the datepicker; on desktop it stays an inline dropdown.
(function () {
    if (window.__matRichSelectReady) return;
    window.__matRichSelectReady = true;

    function isMobile() { return window.matchMedia('(max-width: 600px)').matches; }

    var backdrop = null;
    function showBackdrop() {
        if (backdrop) return;
        backdrop = document.createElement('div');
        backdrop.className = 'mat-rs-backdrop';
        backdrop.addEventListener('click', function () { closeAll(null); });
        document.body.appendChild(backdrop);
    }
    function hideBackdrop() {
        if (backdrop) { backdrop.remove(); backdrop = null; }
    }

    function closeAll(except) {
        var anyOpen = false;
        document.querySelectorAll('.mat-rs-menu').forEach(function (m) {
            if (m === except) { anyOpen = true; return; }
            if (!m.hidden) m.hidden = true;
            var wrap = m.closest('.mat-rs');
            if (wrap) {
                wrap.classList.remove('mat-rs-modal');
                var b = wrap.querySelector('[data-rs-btn]');
                if (b) b.setAttribute('aria-expanded', 'false');
            }
        });
        if (!anyOpen) hideBackdrop();
    }

    document.addEventListener('click', function (e) {
        var closeBtn = e.target.closest('[data-rs-close]');
        if (closeBtn) { e.preventDefault(); closeAll(null); return; }

        var btn = e.target.closest('[data-rs-btn]');
        if (btn) {
            e.preventDefault();
            if (btn.disabled) return;
            var wrap = btn.closest('.mat-rs');
            var menu = wrap.querySelector('[data-rs-menu]');
            var willOpen = menu.hidden;
            closeAll(willOpen ? menu : null);
            menu.hidden = !willOpen;
            btn.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
            if (willOpen && isMobile()) { wrap.classList.add('mat-rs-modal'); showBackdrop(); }
            else { wrap.classList.remove('mat-rs-modal'); hideBackdrop(); }
            return;
        }

        var opt = e.target.closest('[data-rs-opt]');
        if (opt) {
            e.preventDefault();
            var wrap = opt.closest('.mat-rs');
            var input = wrap.querySelector('[data-rs-input]');
            var cur = wrap.querySelector('[data-rs-current]');
            var menu = wrap.querySelector('[data-rs-menu]');

            // --- Multi-select ---------------------------------------------------------------
            // One comma-separated value, because that is what the single-select variant and the
            // server already store — a field can be switched from one to the other without
            // touching what it saved.
            if (wrap.hasAttribute('data-rs-multi')) {
                opt.classList.toggle('on');
                var chosen = [].slice.call(menu.querySelectorAll('[data-rs-opt].on'));
                input.value = chosen.map(function (o) { return o.getAttribute('data-value') || ''; }).join(', ');
                chosen.forEach(function (o) { o.setAttribute('aria-selected', 'true'); });
                menu.querySelectorAll('[data-rs-opt]:not(.on)').forEach(function (o) { o.removeAttribute('aria-selected'); });

                cur.innerHTML = '';
                var ph = input.getAttribute('data-placeholder') || '';
                var label = chosen.length === 0 ? ph
                    : chosen.length <= 2
                        ? chosen.map(function (o) {
                            var t = o.querySelector('.mat-rs-opt-title');
                            return t ? t.textContent : o.getAttribute('data-value');
                          }).join(', ')
                        // Beyond two the names stop fitting the closed field, and a truncated list
                        // reads as if the rest were not selected.
                        : (input.getAttribute('data-many') || '{0} gewählt').replace('{0}', chosen.length);
                var mt = document.createElement('span');
                mt.className = 'mat-rs-cur-title';
                mt.textContent = label;
                cur.appendChild(mt);
                cur.classList.toggle('mat-rs-placeholder', chosen.length === 0);

                // The menu STAYS open — closing after every tick would make picking three options
                // three trips, which is the whole reason this field exists.
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                return;
            }

            input.value = opt.getAttribute('data-value') || '';
            // Rebuild the compact "current" view from the option's image + title.
            var img = opt.querySelector('.mat-rs-opt-img');
            var title = opt.querySelector('.mat-rs-opt-title');
            cur.innerHTML = '';
            if (img) {
                var ci = document.createElement('span');
                ci.className = 'mat-rs-cur-img';
                ci.style.backgroundImage = img.style.backgroundImage;
                cur.appendChild(ci);
            }
            var ct = document.createElement('span');
            ct.className = 'mat-rs-cur-title';
            ct.textContent = title ? title.textContent : input.value;
            cur.appendChild(ct);
            cur.classList.remove('mat-rs-placeholder');
            menu.querySelectorAll('[data-rs-opt]').forEach(function (o) { o.removeAttribute('aria-selected'); });
            opt.setAttribute('aria-selected', 'true');
            menu.hidden = true;
            wrap.classList.remove('mat-rs-modal');
            hideBackdrop();
            wrap.querySelector('[data-rs-btn]').setAttribute('aria-expanded', 'false');
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
            return;
        }

        if (!e.target.closest('.mat-rs')) closeAll(null);
    });

    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeAll(null); });

    // Close an open desktop dropdown when the page scrolls, so the absolutely-positioned menu never
    // slides over a sticky header. Non-capturing window listener → inner scrolling of the options
    // list (.mat-rs-scroll) does not bubble here, so it won't self-close. The mobile bottom-sheet
    // (.mat-rs-modal, position: fixed) is left open.
    window.addEventListener('scroll', function () {
        var open = document.querySelector('.mat-rs-menu:not([hidden])');
        if (open) {
            var wrap = open.closest('.mat-rs');
            if (wrap && !wrap.classList.contains('mat-rs-modal')) closeAll(null);
        }
    }, { passive: true });
})();
