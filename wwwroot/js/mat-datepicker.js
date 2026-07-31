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
        de: { today: 'Heute', clear: 'Löschen', cancel: 'Abbrechen', ok: 'Übernehmen', prev: 'Vorheriger Monat', next: 'Nächster Monat', to: 'bis', placeholder: 'Datum wählen', placeholderRange: 'Zeitraum wählen', startLabel: 'Anreise', endLabel: 'Abreise' },
        en: { today: 'Today', clear: 'Clear', cancel: 'Cancel', ok: 'Apply', prev: 'Previous month', next: 'Next month', to: 'to', placeholder: 'Select date', placeholderRange: 'Select range', startLabel: 'Check-in', endLabel: 'Check-out' },
        hr: { today: 'Danas', clear: 'Obriši', cancel: 'Odustani', ok: 'Primijeni', prev: 'Prethodni mjesec', next: 'Sljedeći mjesec', to: 'do', placeholder: 'Odaberi datum', placeholderRange: 'Odaberi razdoblje', startLabel: 'Dolazak', endLabel: 'Odlazak' },
        sk: { today: 'Dnes', clear: 'Vymazať', cancel: 'Zrušiť', ok: 'Použiť', prev: 'Predchádzajúci mesiac', next: 'Nasledujúci mesiac', to: 'do', placeholder: 'Vyberte dátum', placeholderRange: 'Vyberte obdobie', startLabel: 'Príchod', endLabel: 'Odchod' }
    };
    var T = (window.matDpI18n && typeof window.matDpI18n === 'object') ? window.matDpI18n : (I18N_DEFAULTS[LANG] || I18N_DEFAULTS.de);

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

    // Split "YYYY-MM-DD..YYYY-MM-DD" or single "YYYY-MM-DD" into [start, end?] Dates or nulls.
    function parseValue(raw, isRange) {
        raw = (raw || '').trim();
        if (!raw) return { start: null, end: null };
        if (isRange) {
            var parts = raw.split('..');
            return { start: parseIso(parts[0] || ''), end: parseIso(parts[1] || '') };
        }
        return { start: parseIso(raw), end: null };
    }

    function displayFor(sel, isRange) {
        if (!sel.start) return '';
        if (!isRange) return fmt(sel.start);
        if (!sel.end) return fmt(sel.start) + ' – …';
        return fmt(sel.start) + ' – ' + fmt(sel.end);
    }

    // --- Dialog (built once, reused for every trigger) ------------------------------------------

    var dialog, monthLabel, weekdaysEl, gridEl, currentTrigger, viewDate, selection, isRange;

    function ensureDialog() {
        if (dialog) return;
        dialog = document.createElement('dialog');
        dialog.className = 'mat-dp-dialog';
        dialog.innerHTML =
            '<div class="mat-dp-head">'
                + '<button type="button" class="mat-dp-nav" data-dp-prev aria-label="' + T.prev + '">‹</button>'
                + '<span class="mat-dp-title" data-dp-title></span>'
                + '<button type="button" class="mat-dp-nav" data-dp-next aria-label="' + T.next + '">›</button>'
            + '</div>'
            + '<div class="mat-dp-weekdays" data-dp-wd></div>'
            + '<div class="mat-dp-grid" data-dp-grid></div>'
            + '<div class="mat-dp-foot">'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-ghost" data-dp-clear>' + T.clear + '</button>'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-ghost" data-dp-today>' + T.today + '</button>'
                + '<span class="mat-dp-spacer"></span>'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-ghost" data-dp-cancel>' + T.cancel + '</button>'
                + '<button type="button" class="mat-dp-btn mat-dp-btn-primary" data-dp-ok>' + T.ok + '</button>'
            + '</div>';
        document.body.appendChild(dialog);
        monthLabel = dialog.querySelector('[data-dp-title]');
        weekdaysEl = dialog.querySelector('[data-dp-wd]');
        gridEl = dialog.querySelector('[data-dp-grid]');

        // Monday-first weekday header (reference date 2023-01-02 is a Monday).
        var wdFmt = new Intl.DateTimeFormat(LOCALE, { weekday: 'short' });
        for (var i = 0; i < 7; i++) {
            var d = new Date(2023, 0, 2 + i);
            var cell = document.createElement('span');
            cell.textContent = wdFmt.format(d);
            weekdaysEl.appendChild(cell);
        }

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
            else if (t.matches('.mat-dp-day')) {
                var d = new Date(+t.getAttribute('data-y'), +t.getAttribute('data-m'), +t.getAttribute('data-d'));
                if (!isRange) { selection = { start: d, end: null }; render(); return; }
                // Range selection: 1st click sets start (clears end); 2nd click sets end (or restarts if
                // clicked date is before start); 3rd click restarts.
                if (!selection.start || (selection.start && selection.end)) {
                    selection = { start: d, end: null };
                } else if (d < selection.start) {
                    selection = { start: d, end: null };
                } else if (sameDay(d, selection.start)) {
                    selection = { start: d, end: null };
                } else {
                    selection.end = d;
                }
                render();
            }
        });
        dialog.addEventListener('close', function () { currentTrigger = null; });
    }

    function render() {
        var y = viewDate.getFullYear(), m = viewDate.getMonth();
        var title = FMT_MONTH.format(new Date(y, m, 1));
        monthLabel.textContent = title;

        var first = new Date(y, m, 1);
        var startCol = (first.getDay() + 6) % 7; // Monday=0
        var daysInMonth = new Date(y, m + 1, 0).getDate();
        var today = stripTime(new Date());

        gridEl.innerHTML = '';
        for (var i = 0; i < 42; i++) {
            var dayNum = i - startCol + 1;
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'mat-dp-day';
            if (dayNum < 1 || dayNum > daysInMonth) {
                btn.classList.add('mat-dp-empty');
                btn.disabled = true;
                btn.textContent = '';
            } else {
                var cellDate = new Date(y, m, dayNum);
                btn.textContent = dayNum;
                btn.setAttribute('data-y', y); btn.setAttribute('data-m', m); btn.setAttribute('data-d', dayNum);
                if (sameDay(cellDate, today)) btn.classList.add('mat-dp-today');

                if (isRange) {
                    var s = selection.start, e = selection.end;
                    if (s && sameDay(cellDate, s)) btn.classList.add('mat-dp-selected', 'mat-dp-range-start');
                    if (e && sameDay(cellDate, e)) btn.classList.add('mat-dp-selected', 'mat-dp-range-end');
                    if (s && e && cellDate > s && cellDate < e) btn.classList.add('mat-dp-in-range');
                } else if (selection.start && sameDay(cellDate, selection.start)) {
                    btn.classList.add('mat-dp-selected');
                }
            }
            gridEl.appendChild(btn);
        }
    }

    function open(trigger) {
        ensureDialog();
        currentTrigger = trigger;
        var wrap = trigger.closest('.mat-dp');
        isRange = wrap.getAttribute('data-dp-mode') === 'range';
        var input = wrap.querySelector('[data-dp-input]');
        var parsed = parseValue(input.value, isRange);
        selection = { start: parsed.start, end: parsed.end };
        viewDate = stripTime(parsed.start || new Date());
        render();
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
        input.value = value;
        var displayText = displayFor(selection, isRange);
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
        var text = displayFor(parsed, isR);
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
