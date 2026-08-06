// Rich select: a custom dropdown for form fields of type "richselect". Each option carries a title,
// optional image, description and small tags; the chosen option's KEY is stored in the hidden input
// (server-side stays a plain value). Delegated on document so the builder's live-preview innerHTML
// swaps don't lose the handlers. Guarded against multiple loads.
(function () {
    if (window.__matRichSelectReady) return;
    window.__matRichSelectReady = true;

    function closeAll(except) {
        document.querySelectorAll('.mat-rs-menu').forEach(function (m) {
            if (m === except) return;
            m.hidden = true;
            var b = m.closest('.mat-rs') && m.closest('.mat-rs').querySelector('[data-rs-btn]');
            if (b) b.setAttribute('aria-expanded', 'false');
        });
    }

    document.addEventListener('click', function (e) {
        var btn = e.target.closest('[data-rs-btn]');
        if (btn) {
            e.preventDefault();
            if (btn.disabled) return;
            var menu = btn.closest('.mat-rs').querySelector('[data-rs-menu]');
            var willOpen = menu.hidden;
            closeAll(willOpen ? menu : null);
            menu.hidden = !willOpen;
            btn.setAttribute('aria-expanded', willOpen ? 'true' : 'false');
            return;
        }
        var opt = e.target.closest('[data-rs-opt]');
        if (opt) {
            e.preventDefault();
            var wrap = opt.closest('.mat-rs');
            var input = wrap.querySelector('[data-rs-input]');
            var cur = wrap.querySelector('[data-rs-current]');
            var menu = wrap.querySelector('[data-rs-menu]');
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
            wrap.querySelector('[data-rs-btn]').setAttribute('aria-expanded', 'false');
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
            return;
        }
        if (!e.target.closest('.mat-rs')) closeAll(null);
    });

    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeAll(null); });
})();
