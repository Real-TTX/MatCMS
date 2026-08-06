// Custom date picker used by form fields of type "date" (single) and "daterange" (Anreise–Abreise).
// The hidden <input> holds the canonical value: ISO "YYYY-MM-DD" for single, "YYYY-MM-DD..YYYY-MM-DD"
// for range. Only the visible trigger button carries the locale-formatted display, so the server side
// stays untouched. Guard against multiple loads via the self-init flag.
(function () {
    if (window.__matDatepickerReady) return;
    window.__matDatepickerReady = true;

    var LANG = (document.documentElement.lang || 'de').slice(0, 2).toLowerCase();
    var LOCALE_MAP = { de: 'de-DE', en: 'en-GB', hr: 'hr-HR', sk: 'sk-SK', fr: 'fr-FR', it: 'it-IT', es: 'es-ES', nl: 'nl-NL', pl: 'pl-PL' };
    var LOCALE = LOCALE_MAP[LANG] || 'de-DE';

    var I18N_DEFAULTS = {
        de: { today: 'Heute', clear: 'Löschen', cancel: 'Abbrechen', ok: 'Übernehmen', prev: 'Vorheriger Monat', next: 'Nächster Monat', to: 'bis', placeholder: 'Datum wählen', placeholderRange: 'Zeitraum wählen', startLabel: 'Anreise', endLabel: 'Abreise', exact: 'Genaue Zeitangabe', day: 'Tag', days: 'Tage', flexTitle: 'Flexible Datumsoptionen' },
        en: { today: 'Today', clear: 'Clear', cancel: 'Cancel', ok: 'Apply', prev: 'Previous month', next: 'Next month', to: 'to', placeholder: 'Select date', placeholderRange: 'Select range', startLabel: 'Check-in', endLabel: 'Check-out', exact: 'Exact date', day: 'day', days: 'days', flexTitle: 'Flexible date options' },
        hr: { today: 'Danas', clear: 'Obriši', cancel: 'Odustani', ok: 'Primijeni', prev: 'Prethodni mjesec', next: 'Sljedeći mjesec', to: 'do', placeholder: 'Odaberi datum', placeholderRange: 'Odaberi razdoblje', startLabel: 'Dolazak', endLabel: 'Odlazak', exact: 'Točan datum', day: 'dan', days: 'dana', flexTitle: 'Fleksibilni datumi' },
        sk: { today: 'Dnes', clear: 'Vymazať', cancel: 'Zrušiť', ok: 'Použiť', prev: 'Predchádzajúci mesiac', next: 'Nasledujúci mesiac', to: 'do', placeholder: 'Vyberte dátum', placeholderRange: 'Vyberte obdobie', startLabel: 'Príchod', endLabel: 'Odchod', exact: 'Presný dátum', day: 'deň', days: 'dní', flexTitle: 'Flexibilné dátumy' }
    };
    var FLEX_PRESETS = [1, 2, 3, 7];
    function flexLabel(n) { return '± ' + n + ' ' + (n === 1 ? T.day : T.days); }
    function flexSuffix(n) { return n > 0 ? ' (' + flexLabel(n) + ')' : ''; }
    // Server-provided labels (matDpI18n) override the built-ins, but defaults fill any missing keys
    // (e.g. the flexibility labels added later) so nothing renders as "undefined".
    var T = Object.assign({}, I18N_DEFAULTS[LANG] || I18N_DEFAULTS.de, (window.matDpI18n && typeof window.matDpI18n === 'object') ? window.matDpI18n : {});

    var FMT_DISPLAY = new Intl.DateTimeFormat(LOCALE, { year: 'numeric', month: '2-digit', day: '2-digit' });
    var FMT_MONTH = new Intl.DateTimeFormat(LOCALE, { month: 'long', year: 'numeric' });

    function pad2(n) { return String(n).padStart(2, '0'); }
    function iso(d) { return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()); }
    function parseIso(s) {
        if (!s || !/^\d{4}-\d{2}-\d{2}$/.test(s)) return null;
        var d = new Date(s + 'T00:00:00');
        return isNaN(d.getTime()) ? null : d;
    }
    function fmt(d) { return FMT_DISPLAY.format(d); }
    function stripTime(d) { var x = new Date(d); x.setHours(0, 0, 0, 0); return x; }
    function sameDay(a, b) { return a && b && a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate(); }

    // Split "YYYY-MM-DD..YYYY-MM-DD" / single "YYYY-MM-DD", with an optional "~N" flexibility suffix
    // (± N days), into { start, end?, flex }.
    function parseValue(raw, isRange) {
        raw = (raw || '').trim();
        var fm = raw.match(/~(\d+)$/);
        var flex = fm ? parseInt(fm[1], 10) : 0;
        raw = raw.replace(/~\d+$/, '');
        if (!raw) return { start: null, end: null, flex: flex };
        if (isRange) {
            var parts = raw.split('..');
            return { start: parseIso(parts[0] || ''), end: parseIso(parts[1] || ''), flex: flex };
        }
        return { start: parseIso(raw), end: null, flex: flex };
    }

    function displayFor(sel, isRange, flex) {
        if (!sel.start) return '';
        var base;
        if (!isRange) base = fmt(sel.start);
        else if (!sel.end) base = fmt(sel.start) + ' – …';
        else base = fmt(sel.start) + ' – ' + fmt(sel.end);
        return base + flexSuffix(flex || 0);
    }

    // --- Dialog (built once, reused for every trigger) ------------------------------------------

    var dialog, monthsEl, flexEl, currentTrigger, viewDate, selection, isRange, hoverDate, flex;
    var WD_FMT = new Intl.DateTimeFormat(LOCALE, { weekday: 'short' });   // Monday-first header labels

    function ensureDialog() {
        if (dialog) return;
        dialog = document.createElement('dialog');
        dialog.className = 'mat-dp-dialog';
        dialog.innerHTML =
            '<div class="mat-dp-body">'
                + '<button type="button" class="mat-dp-nav mat-dp-nav-prev" data-dp-prev aria-label="' + T.prev + '">‹</button>'
                + '<div class="mat-dp-months" data-dp-months></div>'
                + '<button type="button" class="mat-dp-nav mat-dp-nav-next" data-dp-next aria-label="' + T.next + '">›</button>'
            + '</div>'
            + '<div class="mat-dp-flex" data-dp-flex>'
                + '<div class="mat-dp-flex-title">' + T.flexTitle + '</div>'
                + '<div class="mat-dp-flex-chips">'
                    + '<button type="button" class="mat-dp-chip" data-flex="0">' + T.exact + '</button>'
                    + FLEX_PRESETS.map(function (n) { return '<button type="button" class="mat-dp-chip" data-flex="' + n + '">' + flexLabel(n) + '</button>'; }).join('')
                + '</div>'
            + '</div>'
            + '<div class="mat-dp-foot">'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-ghost" data-dp-clear>' + T.clear + '</button>'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-ghost" data-dp-today>' + T.today + '</button>'
                + '<span class="mat-dp-spacer"></span>'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-ghost" data-dp-cancel>' + T.cancel + '</button>'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-primary" data-dp-ok>' + T.ok + '</button>'
            + '</div>';
        document.body.appendChild(dialog);
        monthsEl = dialog.querySelector('[data-dp-months]');
        flexEl = dialog.querySelector('[data-dp-flex]');

        dialog.addEventListener('click', function (e) {
            var t = e.target.closest('button');
            if (!t || !dialog.contains(t)) return;
            if (t.matches('[data-dp-prev]')) { viewDate.setMonth(viewDate.getMonth() - 1); render(); }
            else if (t.matches('[data-dp-next]')) { viewDate.setMonth(viewDate.getMonth() + 1); render(); }
            else if (t.matches('[data-dp-today]')) { viewDate = stripTime(new Date()); render(); }
            else if (t.matches('[data-dp-clear]')) { selection = { start: null, end: null }; apply(); close(); }
            else if (t.matches('[data-dp-cancel]')) { close(); }
            else if (t.matches('[data-dp-ok]')) {
                // For range mode, only apply when both endpoints are set — otherwise treat as cancel.
                if (isRange && selection.start && !selection.end) { close(); return; }
                apply(); close();
            }
            else if (t.matches('.mat-dp-chip')) { flex = +t.getAttribute('data-flex') || 0; paintFlex(); }
            else if (t.matches('.mat-dp-day') && t.hasAttribute('data-d')) {
                var d = new Date(+t.getAttribute('data-y'), +t.getAttribute('data-m'), +t.getAttribute('data-d'));
                if (!isRange) { selection = { start: d, end: null }; paint(); return; }
                // Range: 1st click = start (clears end); 2nd = end (or restart if before/equal start); 3rd restarts.
                if (!selection.start || (selection.start && selection.end)) selection = { start: d, end: null };
                else if (d < selection.start || sameDay(d, selection.start)) selection = { start: d, end: null };
                else selection.end = d;
                hoverDate = null;
                paint();
            }
        });
        // Hover preview: while a start is picked, hovering a later day previews the whole range.
        monthsEl.addEventListener('mouseover', function (e) {
            if (!isRange) return;
            var t = e.target.closest('.mat-dp-day');
            if (!t || !t.hasAttribute('data-d')) return;
            hoverDate = new Date(+t.getAttribute('data-y'), +t.getAttribute('data-m'), +t.getAttribute('data-d'));
            if (selection.start && !selection.end) paint();
        });
        monthsEl.addEventListener('mouseleave', function () { if (hoverDate) { hoverDate = null; paint(); } });
        dialog.addEventListener('close', function () { currentTrigger = null; hoverDate = null; });
    }

    // One month panel: title + Monday-first weekday header + 6×7 day grid.
    function buildMonth(y, m) {
        var panel = document.createElement('div');
        panel.className = 'mat-dp-month';
        var title = document.createElement('div');
        title.className = 'mat-dp-mtitle';
        title.textContent = FMT_MONTH.format(new Date(y, m, 1));
        panel.appendChild(title);
        var wd = document.createElement('div');
        wd.className = 'mat-dp-weekdays';
        for (var i = 0; i < 7; i++) { var s = document.createElement('span'); s.textContent = WD_FMT.format(new Date(2023, 0, 2 + i)); wd.appendChild(s); }
        panel.appendChild(wd);
        var grid = document.createElement('div');
        grid.className = 'mat-dp-grid';
        var startCol = (new Date(y, m, 1).getDay() + 6) % 7; // Monday=0
        var daysInMonth = new Date(y, m + 1, 0).getDate();
        for (var j = 0; j < 42; j++) {
            var dayNum = j - startCol + 1;
            var btn = document.createElement('button');
            btn.type = 'button'; btn.className = 'mat-dp-day';
            if (dayNum < 1 || dayNum > daysInMonth) { btn.classList.add('mat-dp-empty'); btn.disabled = true; }
            else { btn.textContent = dayNum; btn.setAttribute('data-y', y); btn.setAttribute('data-m', m); btn.setAttribute('data-d', dayNum); }
            grid.appendChild(btn);
        }
        panel.appendChild(grid);
        return panel;
    }

    function paintFlex() {
        if (!flexEl) return;
        flexEl.querySelectorAll('.mat-dp-chip').forEach(function (c) {
            c.classList.toggle('is-active', (+c.getAttribute('data-flex') || 0) === (flex || 0));
        });
    }

    function render() {
        monthsEl.innerHTML = '';
        var y = viewDate.getFullYear(), m = viewDate.getMonth();
        monthsEl.appendChild(buildMonth(y, m));
        if (isRange) { var n = new Date(y, m + 1, 1); monthsEl.appendChild(buildMonth(n.getFullYear(), n.getMonth())); }
        paint();
    }

    // Repaint selection/range classes on the existing cells (used on select + hover — no rebuild).
    function paint() {
        var today = stripTime(new Date());
        var s = selection.start, e = selection.end;
        var hi = (isRange && s && !e && hoverDate && hoverDate > s) ? hoverDate : e; // tentative end while hovering
        monthsEl.querySelectorAll('.mat-dp-day[data-d]').forEach(function (btn) {
            var c = new Date(+btn.getAttribute('data-y'), +btn.getAttribute('data-m'), +btn.getAttribute('data-d'));
            btn.classList.remove('mat-dp-selected', 'mat-dp-range-start', 'mat-dp-range-end', 'mat-dp-in-range', 'mat-dp-today');
            if (sameDay(c, today)) btn.classList.add('mat-dp-today');
            if (!isRange) { if (s && sameDay(c, s)) btn.classList.add('mat-dp-selected'); return; }
            if (s && sameDay(c, s)) btn.classList.add('mat-dp-selected', 'mat-dp-range-start');
            if (hi && sameDay(c, hi)) btn.classList.add('mat-dp-selected', 'mat-dp-range-end');
            if (s && hi && c > s && c < hi) btn.classList.add('mat-dp-in-range');
        });
    }

    function open(trigger) {
        ensureDialog();
        currentTrigger = trigger;
        var wrap = trigger.closest('.mat-dp');
        isRange = wrap.getAttribute('data-dp-mode') === 'range';
        var input = wrap.querySelector('[data-dp-input]');
        var parsed = parseValue(input.value, isRange);
        selection = { start: parsed.start, end: parsed.end };
        // Flexibility chips only when the field allows imprecise entries (data-dp-flex="1").
        var allowFlex = wrap.getAttribute('data-dp-flex') === '1';
        if (flexEl) flexEl.hidden = !allowFlex;
        flex = allowFlex ? (parsed.flex || 0) : 0;
        viewDate = stripTime(parsed.start || new Date());
        render();
        paintFlex();
        if (typeof dialog.showModal === 'function') dialog.showModal();
        else dialog.setAttribute('open', '');
    }
    function close() { if (dialog && dialog.open) dialog.close(); }

    function apply() {
        if (!currentTrigger) return;
        var wrap = currentTrigger.closest('.mat-dp');
        var input = wrap.querySelector('[data-dp-input]');
        var display = wrap.querySelector('[data-dp-display]');
        var placeholder = display.getAttribute('data-placeholder') || (isRange ? T.placeholderRange : T.placeholder);
        var value = '';
        if (isRange) {
            if (selection.start && selection.end) value = iso(selection.start) + '..' + iso(selection.end);
            else if (selection.start) value = iso(selection.start);
        } else if (selection.start) {
            value = iso(selection.start);
        }
        if (value && flex > 0) value += '~' + flex;   // encode ± flexibility (server keeps the raw string)
        input.value = value;
        var displayText = displayFor(selection, isRange, flex);
        if (displayText) { display.textContent = displayText; display.classList.remove('mat-dp-placeholder'); }
        else { display.textContent = placeholder; display.classList.add('mat-dp-placeholder'); }
        // Notify the outer form (validation + conditional visibility).
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    // Format any pre-filled values on load. The live-preview iframe in the builder swaps innerHTML on
    // each change, so we also re-init on click via ensureInit(wrap) — safe & idempotent.
    function ensureInit(wrap) {
        if (wrap.__matDpInit) return;
        wrap.__matDpInit = true;
        var input = wrap.querySelector('[data-dp-input]');
        var display = wrap.querySelector('[data-dp-display]');
        if (!input || !display) return;
        var isR = wrap.getAttribute('data-dp-mode') === 'range';
        var parsed = parseValue(input.value, isR);
        var text = displayFor(parsed, isR, parsed.flex);
        if (text) { display.textContent = text; display.classList.remove('mat-dp-placeholder'); }
    }
    function initAll(root) {
        (root || document).querySelectorAll('.mat-dp').forEach(ensureInit);
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { initAll(); });
    else initAll();

    // Delegated click so preview-swaps in the form builder don't lose the handler.
    document.addEventListener('click', function (e) {
        var t = e.target.closest('[data-dp-btn]');
        if (!t) return;
        e.preventDefault();
        var wrap = t.closest('.mat-dp');
        if (wrap) ensureInit(wrap);
        open(t);
    });
})();
