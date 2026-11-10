// Custom date picker used by form fields of type "date" (single) and "daterange" (Anreise–Abreise).
// The hidden <input> holds the canonical value: ISO "YYYY-MM-DD" for single, "YYYY-MM-DD..YYYY-MM-DD"
// for range. Only the visible trigger button carries the locale-formatted display, so the server side
// stays untouched. Guard against multiple loads via the self-init flag.
//
// Availability (a booking form; general, any page may use it) — attributes on the .mat-dp wrapper:
//   data-dp-min / data-dp-max   "YYYY-MM-DD" or "today": nothing before / after can be picked
//   data-dp-min-nights          a range needs at least this many nights (end − start)
//   data-dp-busy-url            GET url&von=YYYY-MM-DD&bis=YYYY-MM-DD (bis exclusive, the months on screen) answers
//                               { taken: [days], noStart: [days], minNights: n, min: { day: n } }. A day in "taken" is a NIGHT that is
//                               not free: it cannot be inside a range, but it can be the END (someone else arrives that day — the
//                               change-over). "noStart" days cannot be the start, "min" is the least stay for a start on that day.
//                               Changing the attribute (another unit) is enough: answers are kept per url.
// window.MatDatepicker.setValue(wrap, "a..b") fills a field from code (and fires input/change like a pick).
(function () {
    if (window.__matDatepickerReady) return;
    window.__matDatepickerReady = true;

    var LANG = (document.documentElement.lang || 'de').slice(0, 2).toLowerCase();
    var LOCALE_MAP = { de: 'de-DE', en: 'en-GB', hr: 'hr-HR', sk: 'sk-SK', fr: 'fr-FR', it: 'it-IT', es: 'es-ES', nl: 'nl-NL', pl: 'pl-PL' };
    var LOCALE = LOCALE_MAP[LANG] || 'de-DE';

    var I18N_DEFAULTS = {
        de: { today: 'Heute', clear: 'Löschen', cancel: 'Abbrechen', ok: 'Übernehmen', prev: 'Vorheriger Monat', next: 'Nächster Monat', to: 'bis', placeholder: 'Datum wählen', placeholderRange: 'Zeitraum wählen', startLabel: 'Anreise', endLabel: 'Abreise', exact: 'Genaue Zeitangabe', day: 'Tag', days: 'Tage', flexTitle: 'Flexible Datumsoptionen', taken: 'belegt', minNights: 'Mindestens {n} Nächte', noStart: 'An diesem Tag keine Anreise', loading: 'Belegung wird geladen …', checkout: 'Abreise möglich' },
        en: { today: 'Today', clear: 'Clear', cancel: 'Cancel', ok: 'Apply', prev: 'Previous month', next: 'Next month', to: 'to', placeholder: 'Select date', placeholderRange: 'Select range', startLabel: 'Check-in', endLabel: 'Check-out', exact: 'Exact date', day: 'day', days: 'days', flexTitle: 'Flexible date options', taken: 'booked', minNights: 'At least {n} nights', noStart: 'No arrival on this day', loading: 'Loading availability …', checkout: 'Check-out possible' },
        hr: { today: 'Danas', clear: 'Obriši', cancel: 'Odustani', ok: 'Primijeni', prev: 'Prethodni mjesec', next: 'Sljedeći mjesec', to: 'do', placeholder: 'Odaberi datum', placeholderRange: 'Odaberi razdoblje', startLabel: 'Dolazak', endLabel: 'Odlazak', exact: 'Točan datum', day: 'dan', days: 'dana', flexTitle: 'Fleksibilni datumi', taken: 'zauzeto', minNights: 'Najmanje {n} noćenja', noStart: 'Nema dolaska tog dana', loading: 'Učitavanje zauzetosti …', checkout: 'Odlazak moguć' },
        sk: { today: 'Dnes', clear: 'Vymazať', cancel: 'Zrušiť', ok: 'Použiť', prev: 'Predchádzajúci mesiac', next: 'Nasledujúci mesiac', to: 'do', placeholder: 'Vyberte dátum', placeholderRange: 'Vyberte obdobie', startLabel: 'Príchod', endLabel: 'Odchod', exact: 'Presný dátum', day: 'deň', days: 'dní', flexTitle: 'Flexibilné dátumy', taken: 'obsadené', minNights: 'Najmenej {n} nocí', noStart: 'V tento deň bez príchodu', loading: 'Načítava sa obsadenosť …', checkout: 'Odchod možný' }
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

    var overlay, dialog, monthsEl, flexEl, infoEl, currentTrigger, viewDate, selection, isRange, hoverDate, flex;
    // The open field's rules: earliest/latest day, least nights, and its availability (null = none asked).
    var minDate = null, maxDate = null, minNightsAttr = 1, busy = null;
    var busyCache = {};   // url → { taken, noStart, min, minNights, months, loading }

    function addDays(d, n) { var x = new Date(d); x.setDate(x.getDate() + n); return x; }
    function dayDiff(a, b) { return Math.round((stripTime(b) - stripTime(a)) / 86400000); }
    function readDay(v) { return v === 'today' ? stripTime(new Date()) : parseIso(v || ''); }
    function monthKey(y, m) { return y + '-' + pad2(m + 1); }

    // Asks the availability of three months from (y, m) once per url; repaints when it comes.
    function loadBusy(url, y, m) {
        var c = busyCache[url] || (busyCache[url] = { taken: {}, noStart: {}, min: {}, minNights: 0, months: {}, loading: 0 });
        var from = null;
        for (var i = 0; i < 3; i++) { var d = new Date(y, m + i, 1); if (!c.months[monthKey(d.getFullYear(), d.getMonth())]) { from = from || d; } }
        if (!from) return c;
        var to = new Date(y, m + 3, 1);
        for (var k = new Date(from); k < to; k.setMonth(k.getMonth() + 1)) c.months[monthKey(k.getFullYear(), k.getMonth())] = true;
        c.loading++;
        fetch(url + (url.indexOf('?') >= 0 ? '&' : '?') + 'von=' + iso(from) + '&bis=' + iso(to), { headers: { Accept: 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (j) {
                if (!j) return;
                (j.taken || []).forEach(function (x) { c.taken[x] = true; });
                (j.noStart || []).forEach(function (x) { c.noStart[x] = true; });
                Object.keys(j.min || {}).forEach(function (x) { c.min[x] = +j.min[x] || 0; });
                if (j.minNights) c.minNights = +j.minNights;
            })
            .catch(function () { /* no availability: the server still checks on submit */ })
            .then(function () { c.loading--; if (busy === c && !overlay.hidden) paint(); });
        return c;
    }

    function outside(c) { return (minDate && c < minDate) || (maxDate && c > maxDate); }
    function isTaken(c) { return !!(busy && busy.taken[iso(c)]); }
    function canStart(c) { return !outside(c) && !isTaken(c) && !(busy && busy.noStart[iso(c)]); }
    function leastNights(start) { return Math.max(1, (busy && start && busy.min[iso(start)]) || (busy && busy.minNights) || minNightsAttr || 1); }
    // Whether c can be the end of a range from s: later, no taken night in [s, c), long enough. c itself may be taken (change-over day).
    function freeBetween(s, c) { for (var d = new Date(s); d < c; d = addDays(d, 1)) { if (isTaken(d)) return false; } return true; }
    function canEnd(s, c) { return c > s && !(maxDate && c > addDays(maxDate, 1)) && freeBetween(s, c) && dayDiff(s, c) >= leastNights(s); }
    var WD_FMT = new Intl.DateTimeFormat(LOCALE, { weekday: 'short' });   // Monday-first header labels

    function ensureDialog() {
        if (dialog) return;
        // Plain <div> overlay + panel (same robust mechanism as the rich-select) — NOT a native
        // <dialog>/showModal, whose top-layer/backdrop behaviour is flaky on mobile browsers.
        overlay = document.createElement('div');
        overlay.className = 'mat-dp-overlay';
        overlay.hidden = true;
        dialog = document.createElement('div');
        dialog.className = 'mat-dp-dialog';
        dialog.innerHTML =
            // Mobile-only header: a title + ✕ close so the sheet is always easy to dismiss
            // (hidden on desktop via CSS). data-dp-cancel reuses the "close without applying" handler.
            '<div class="mat-dp-head">'
                + '<span class="mat-dp-title" data-dp-title></span>'
                + '<button type="button" class="mat-dp-close" data-dp-close aria-label="' + T.cancel + '"><i class="ti ti-x" aria-hidden="true"></i></button>'
            + '</div>'
            + '<div class="mat-dp-body">'
                + '<button type="button" class="mat-dp-nav mat-dp-nav-prev" data-dp-prev aria-label="' + T.prev + '">‹</button>'
                + '<div class="mat-dp-months" data-dp-months></div>'
                + '<button type="button" class="mat-dp-nav mat-dp-nav-next" data-dp-next aria-label="' + T.next + '">›</button>'
            + '</div>'
            + '<div class="mat-dp-info" data-dp-info hidden></div>'
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
                + '<button type="button" class="mat-dp-btn mat-dp-btn-primary" data-dp-ok>' + T.ok + '</button>'
            + '</div>';
        overlay.appendChild(dialog);
        document.body.appendChild(overlay);
        monthsEl = dialog.querySelector('[data-dp-months]');
        flexEl = dialog.querySelector('[data-dp-flex]');
        infoEl = dialog.querySelector('[data-dp-info]');

        // Tap on the dim area outside the panel = cancel & close.
        overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });

        dialog.addEventListener('click', function (e) {
            var t = e.target.closest('button');
            if (!t || !dialog.contains(t)) return;
            if (t.matches('[data-dp-prev]')) { viewDate.setMonth(viewDate.getMonth() - 1); render(); }
            else if (t.matches('[data-dp-next]')) { viewDate.setMonth(viewDate.getMonth() + 1); render(); }
            else if (t.matches('[data-dp-today]')) { viewDate = stripTime(new Date()); render(); }
            else if (t.matches('[data-dp-clear]')) { selection = { start: null, end: null }; apply(); close(); }
            else if (t.matches('[data-dp-close]')) { close(); }
            else if (t.matches('[data-dp-ok]')) {
                // For range mode, only apply when both endpoints are set — otherwise treat as cancel.
                if (isRange && selection.start && !selection.end) { close(); return; }
                apply(); close();
            }
            else if (t.matches('.mat-dp-chip')) { flex = +t.getAttribute('data-flex') || 0; paintFlex(); }
            else if (t.matches('.mat-dp-day') && t.hasAttribute('data-d')) {
                var d = new Date(+t.getAttribute('data-y'), +t.getAttribute('data-m'), +t.getAttribute('data-d'));
                // Auto-confirm on pick unless the flexible-date options are shown (then the user still
                // needs to choose ± days and press Übernehmen). flexEl.hidden === true ⇒ no flex.
                var autoClose = flexEl && flexEl.hidden;
                if (!isRange) {
                    selection = { start: d, end: null }; paint();
                    if (autoClose) { apply(); close(); }
                    return;
                }
                // Range: 1st click = start (clears end); 2nd = end when it can be one (free nights, long enough), else a new start; 3rd restarts.
                if (selection.start && !selection.end && canEnd(selection.start, d)) selection.end = d;
                else if (canStart(d)) selection = { start: d, end: null };
                else return;
                hoverDate = null;
                paint();
                // A completed range auto-confirms (booking-style) when there are no flex options.
                if (autoClose && selection.start && selection.end) { apply(); close(); }
            }
        });
        // Hover preview: while a start is picked, hovering a later day previews the whole range.
        monthsEl.addEventListener('mouseover', function (e) {
            if (!isRange) return;
            var t = e.target.closest('.mat-dp-day');
            if (!t || !t.hasAttribute('data-d')) return;
            var h = new Date(+t.getAttribute('data-y'), +t.getAttribute('data-m'), +t.getAttribute('data-d'));
            hoverDate = selection.start && canEnd(selection.start, h) ? h : null;   // only a day that can be the end previews a range
            if (selection.start && !selection.end) paint();
        });
        monthsEl.addEventListener('mouseleave', function () { if (hoverDate) { hoverDate = null; paint(); } });
        // Escape closes (native <dialog> used to do this for free).
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') close(); });
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
        var url = currentTrigger && currentTrigger.closest('.mat-dp').getAttribute('data-dp-busy-url');
        busy = url ? loadBusy(url, y, m) : null;
        // No way back to months that are wholly before the earliest day.
        var prev = dialog.querySelector('[data-dp-prev]');
        if (prev) prev.disabled = !!(minDate && new Date(y, m, 1) <= new Date(minDate.getFullYear(), minDate.getMonth(), 1));
        paint();
    }

    // The line under the months: what a struck day means and the least stay — only where the field has such rules.
    function paintInfo() {
        if (!infoEl) return;
        var least = leastNights(selection.start);
        infoEl.hidden = !busy && least <= 1;
        if (infoEl.hidden) return;
        infoEl.textContent = '';
        if (busy) {
            var key = document.createElement('span'); key.className = 'mat-dp-key';
            var sw = document.createElement('i'); sw.className = 'mat-dp-key-taken'; sw.setAttribute('aria-hidden', 'true');
            key.appendChild(sw); key.appendChild(document.createTextNode(T.taken)); infoEl.appendChild(key);
        }
        if (least > 1) { var mn = document.createElement('span'); mn.textContent = T.minNights.replace('{n}', least); infoEl.appendChild(mn); }
        if (busy && busy.loading > 0) { var ld = document.createElement('span'); ld.className = 'mat-dp-loading'; ld.textContent = T.loading; infoEl.appendChild(ld); }
    }

    // Repaint selection/range classes on the existing cells (used on select + hover — no rebuild).
    function paint() {
        var today = stripTime(new Date());
        var s = selection.start, e = selection.end;
        var hi = (isRange && s && !e && hoverDate && hoverDate > s) ? hoverDate : e; // tentative end while hovering
        var picking = isRange && s && !e;   // a start is chosen, the end is next
        monthsEl.querySelectorAll('.mat-dp-day[data-d]').forEach(function (btn) {
            var c = new Date(+btn.getAttribute('data-y'), +btn.getAttribute('data-m'), +btn.getAttribute('data-d'));
            btn.classList.remove('mat-dp-selected', 'mat-dp-range-start', 'mat-dp-range-end', 'mat-dp-in-range', 'mat-dp-today',
                'mat-dp-taken', 'mat-dp-off', 'mat-dp-short', 'mat-dp-checkout', 'mat-dp-nostart');
            btn.removeAttribute('title');
            if (sameDay(c, today)) btn.classList.add('mat-dp-today');
            // What may be clicked: a start (free night, allowed arrival) — or, while picking, an end (which may be a taken night: change-over).
            var taken = isTaken(c), end = picking && canEnd(s, c), start = isRange ? canStart(c) : !outside(c) && !taken;
            if (taken) { btn.classList.add('mat-dp-taken'); btn.title = T.taken; }
            if (end && taken) { btn.classList.add('mat-dp-checkout'); btn.title = T.checkout; }
            if (!taken && busy && busy.noStart[iso(c)] && !end) { btn.classList.add('mat-dp-nostart'); btn.title = T.noStart; }
            var short = picking && c > s && !end && !taken && freeBetween(s, c);   // reachable, but too short a stay (or past the latest day)
            if (short) btn.classList.add('mat-dp-short');
            btn.disabled = short || !(end || start);
            if (btn.disabled || (picking && c > s && !end)) btn.classList.add('mat-dp-off');
            if (!isRange) { if (s && sameDay(c, s)) btn.classList.add('mat-dp-selected'); return; }
            if (s && sameDay(c, s)) btn.classList.add('mat-dp-selected', 'mat-dp-range-start');
            if (hi && sameDay(c, hi)) btn.classList.add('mat-dp-selected', 'mat-dp-range-end');
            if (s && hi && c > s && c < hi) btn.classList.add('mat-dp-in-range');
        });
        paintInfo();
    }

    // Apply per-field text overrides (data-dp-t-<key>) to the shared dialog, falling back to the
    // localized defaults for any key the field doesn't override. Runs on every open() since the
    // dialog is reused across triggers.
    function applyTexts(wrap) {
        function ov(key, def) { var v = wrap.getAttribute('data-dp-t-' + key); return (v != null && v !== '') ? v : def; }
        var map = [
            ['[data-dp-ok]', 'ok', T.ok],
            ['[data-dp-clear]', 'clear', T.clear],
            ['[data-dp-today]', 'today', T.today],
            ['.mat-dp-flex-title', 'flextitle', T.flexTitle],
            ['[data-flex="0"]', 'exact', T.exact]
        ];
        map.forEach(function (m) {
            var node = dialog.querySelector(m[0]);
            if (node) node.textContent = ov(m[1], m[2]);
        });
    }

    function open(trigger) {
        ensureDialog();
        currentTrigger = trigger;
        var wrap = trigger.closest('.mat-dp');
        applyTexts(wrap);
        // Mobile header title = the field's label (falls back to the trigger's placeholder).
        var titleEl = dialog.querySelector('[data-dp-title]');
        if (titleEl) {
            var field = wrap.closest('.form-field');
            var lbl = field && field.querySelector('label');
            var disp = wrap.querySelector('[data-dp-display]');
            titleEl.textContent = (lbl && lbl.textContent.trim())
                || (disp && disp.getAttribute('data-placeholder')) || '';
        }
        isRange = wrap.getAttribute('data-dp-mode') === 'range';
        var input = wrap.querySelector('[data-dp-input]');
        var parsed = parseValue(input.value, isRange);
        selection = { start: parsed.start, end: parsed.end };
        // Flexibility chips only when the field allows imprecise entries (data-dp-flex="1").
        var allowFlex = wrap.getAttribute('data-dp-flex') === '1';
        if (flexEl) flexEl.hidden = !allowFlex;
        flex = allowFlex ? (parsed.flex || 0) : 0;
        minDate = readDay(wrap.getAttribute('data-dp-min'));
        maxDate = readDay(wrap.getAttribute('data-dp-max'));
        minNightsAttr = parseInt(wrap.getAttribute('data-dp-min-nights') || '1', 10) || 1;
        var first = parsed.start || new Date();
        if (minDate && first < minDate) first = minDate;
        viewDate = stripTime(first);
        viewDate.setDate(1);
        render();
        paintFlex();
        overlay.hidden = false;
    }
    function close() {
        if (!overlay || overlay.hidden) return;
        overlay.hidden = true;
        currentTrigger = null;
        hoverDate = null;
    }

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

    // From code (e.g. a calendar on the same page that picks dates): writes the ISO value and its display, fires input/change.
    window.MatDatepicker = {
        setValue: function (wrap, value) {
            if (!wrap) return;
            var input = wrap.querySelector('[data-dp-input]'), display = wrap.querySelector('[data-dp-display]');
            if (!input || !display) return;
            var isR = wrap.getAttribute('data-dp-mode') === 'range';
            input.value = value || '';
            var parsed = parseValue(input.value, isR), text = displayFor(parsed, isR, parsed.flex);
            display.textContent = text || display.getAttribute('data-placeholder') || '';
            display.classList.toggle('mat-dp-placeholder', !text);
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };
})();
