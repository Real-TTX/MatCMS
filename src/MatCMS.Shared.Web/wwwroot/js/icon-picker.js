// Die Symbolauswahl von _IconField.cshtml. Der Knopf zeigt das Symbol, der Dialog sucht eines aus.
//
// Zwei Regeln tragen das Ganze:
//  1. Geschrieben wird NUR beim Übernehmen. Abbrechen, Escape und der Klick auf den Schleier lassen
//     das abgeschickte Feld unverändert — hier wird ausgesucht, nicht gearbeitet, und ein Dialog
//     ohne echtes Zurück ist keiner.
//  2. Ein gespeicherter Name, den die Schrift nicht kennt, wird ANGEZEIGT und BEHALTEN. Er steht im
//     Dialog ganz oben und ist vorausgewählt; wer abbricht, hat ihn unverändert im Feld.
//
// Gezeichnet wird in Stücken. 4962 Kacheln auf einmal in den Baum zu hängen hält den Browser für
// mehrere Sekunden an — beim Tippen in der Suche einmal pro Tastendruck.
(function () {
    if (window.__matIconPicker) return;          // die Partial darf mehrfach auf der Seite stehen
    window.__matIconPicker = true;

    var ALL = window.TABLER_ICONS || [];
    var CHUNK = 180;                             // so viele Kacheln je Durchgang

    // ---- Der eine Dialog -------------------------------------------------------------------
    // Einer für alle Auswahlen der Seite: das Raster ist der teure Teil, und ihn je Feld ein zweites
    // Mal aufzubauen kostet nur Speicher. Er hängt an <body> und NICHT dort, wo die Auswahl steht —
    // die steht mitten in einem <form>, und ein Dialog mit eigenem Formular darin wäre ein
    // verschachteltes <form>, das der Browser wegwirft. Aus demselben Grund ist jeder Knopf hier
    // ausdrücklich type="button": ohne das schickt er beim Anklicken das Formular ab, in dem er steht.
    var dlg = null, grid = null, search = null, foot = null, titleEl = null;
    var applyBtn = null, cancelBtn = null, noneBtn = null;
    var owner = null;        // die Auswahl, die den Dialog geöffnet hat
    var picked = '';         // die Wahl IM Dialog — sie wird erst beim Übernehmen übertragen
    var filtered = [];       // die aktuelle Trefferliste
    var drawn = 0;           // wie viele davon schon gezeichnet sind
    var noResults = false;   // die „kein Treffer“-Zeile steht schon da
    var TXT = {};

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
    }

    function build() {
        dlg = el('dialog', 'mat-dialog icon-dialog');

        var head = el('div', 'icon-dialog-head');
        titleEl = el('h3', null, '');
        titleEl.style.margin = '0';
        head.appendChild(titleEl);
        dlg.appendChild(head);

        search = document.createElement('input');
        search.type = 'text';
        search.className = 'icon-search';
        search.autocomplete = 'off';
        search.spellcheck = false;
        // KEIN name-Attribut: der Dialog liegt zwar an <body>, aber ein benanntes Feld hier hätte
        // beim ersten Verschieben des Dialogs in ein Formular still mit abgeschickt.
        dlg.appendChild(search);

        grid = el('div', 'icon-grid');
        dlg.appendChild(grid);

        foot = el('div', 'icon-dialog-foot');
        var count = el('span', 'icon-dialog-count');
        noneBtn = el('button', 'btn btn-sm btn-ghost');
        noneBtn.type = 'button';
        applyBtn = el('button', 'btn btn-sm');
        applyBtn.type = 'button';
        cancelBtn = el('button', 'btn btn-sm btn-ghost');
        cancelBtn.type = 'button';
        foot.appendChild(count);
        foot.appendChild(noneBtn);
        foot.appendChild(cancelBtn);
        foot.appendChild(applyBtn);
        dlg.appendChild(foot);
        dlg.__count = count;

        document.body.appendChild(dlg);

        // Tippen filtert; gezeichnet wird verzögert, damit jeder Tastendruck nicht ein volles
        // Neuzeichnen auslöst.
        var deb;
        search.addEventListener('input', function () {
            clearTimeout(deb);
            deb = setTimeout(function () { filter(search.value); }, 120);
        });
        // Enter im Suchfeld nimmt den ersten Treffer — und schickt vor allem NICHT das Formular ab,
        // in dem die Auswahl steht. Das ist der Unterschied zwischen "Symbol gewählt" und "Seite
        // gespeichert, während der Dialog offen war".
        search.addEventListener('keydown', function (ev) {
            if (ev.key !== 'Enter') return;
            ev.preventDefault();
            if (filtered.length) { picked = filtered[0]; mark(); }
        });

        grid.addEventListener('click', function (ev) {
            var t = ev.target.closest('.icon-tile');
            if (!t) return;
            picked = t.getAttribute('data-icon') || '';
            mark();
        });
        // Ein Doppelklick ist "das da, und fertig" — derselbe Weg wie Übernehmen, nur kürzer.
        grid.addEventListener('dblclick', function (ev) {
            var t = ev.target.closest('.icon-tile');
            if (!t) return;
            picked = t.getAttribute('data-icon') || '';
            apply();
        });
        // Nachladen, wenn das Ende des Rasters in Sicht kommt.
        grid.addEventListener('scroll', function () {
            if (grid.scrollTop + grid.clientHeight >= grid.scrollHeight - 120) draw();
        });

        noneBtn.addEventListener('click', function () { picked = ''; mark(); });
        applyBtn.addEventListener('click', apply);
        cancelBtn.addEventListener('click', function () { dlg.close(); });
        // Klick auf den Schleier schließt — wie Abbrechen, also ohne zu schreiben. Verglichen wird
        // mit dem Rechteck des Dialogs und nicht mit ev.target: der Schleier IST das <dialog>, und
        // ein Klick auf dessen Innenabstand hätte ihn sonst mitgeschlossen.
        dlg.addEventListener('click', function (ev) {
            var r = dlg.getBoundingClientRect();
            var inside = ev.clientX >= r.left && ev.clientX <= r.right &&
                         ev.clientY >= r.top && ev.clientY <= r.bottom;
            if (!inside) dlg.close();
        });
        // Escape schließt ein <dialog> von selbst. Hier steht nur, was danach zu tun ist: NICHTS.
        // Das Feld behält seinen Wert, weil ausschließlich apply() ihn anfasst.
        dlg.addEventListener('close', function () { owner = null; });
    }

    // ---- Zeichnen ---------------------------------------------------------------------------
    function tile(name, note) {
        var b = el('button', 'icon-tile' + (name === picked ? ' sel' : ''));
        b.type = 'button';
        b.setAttribute('data-icon', name);
        b.title = name + (note ? ' — ' + note : '');
        var i = el('i', 'ti ti-' + name);
        i.setAttribute('aria-hidden', 'true');
        b.appendChild(i);
        return b;
    }

    function filter(q) {
        q = (q || '').trim().toLowerCase();
        filtered = q ? ALL.filter(function (n) { return n.indexOf(q) !== -1; }) : ALL.slice();
        // Der gespeicherte Name, den die Schrift nicht kennt, steht ganz vorn und ist damit
        // überhaupt auffindbar — sonst wäre die einzige Auskunft über ihn, dass die Kachel leer ist.
        grid.innerHTML = '';
        grid.scrollTop = 0;
        drawn = 0;
        noResults = false;
        if (owner && owner.unknown && (!q || owner.unknown.toLowerCase().indexOf(q) !== -1)) {
            var row = el('div', 'icon-more', TXT.unknown || '');
            grid.appendChild(tile(owner.unknown, TXT.unknown));
            grid.appendChild(row);
        }
        draw();
    }

    function draw() {
        if (drawn >= filtered.length) {
            // Kein Treffer UND kein behaltener unbekannter Name: dann steht hier, dass nichts
            // gefunden wurde — genau einmal, sonst hinge die Zeile bei jedem Bildlauf ein weiteres
            // Mal darunter.
            if (filtered.length === 0 && !noResults && !(owner && owner.unknown)) {
                noResults = true;
                grid.appendChild(el('div', 'icon-more', TXT.noResults || ''));
            }
            count();
            return;
        }
        var frag = document.createDocumentFragment();
        var end = Math.min(drawn + CHUNK, filtered.length);
        for (var i = drawn; i < end; i++) frag.appendChild(tile(filtered[i]));
        grid.appendChild(frag);
        drawn = end;
        count();
        // Passt das Raster noch nicht einmal seine eigene Höhe aus, kommt nie ein Bildlauf und damit
        // nie ein Nachladen — deshalb hier gleich weiter, bis es voll ist.
        if (grid.scrollHeight <= grid.clientHeight && drawn < filtered.length) draw();
    }

    function count() {
        if (!dlg.__count) return;
        dlg.__count.textContent = (TXT.count || '{0}/{1}')
            .replace('{0}', drawn).replace('{1}', filtered.length);
    }

    function mark() {
        Array.prototype.forEach.call(grid.querySelectorAll('.icon-tile'), function (t) {
            t.classList.toggle('sel', t.getAttribute('data-icon') === picked);
        });
    }

    // Der EINZIGE Weg vom Dialog in das Formular.
    function apply() {
        if (owner) {
            owner.input.value = picked;
            // Damit mithörende Skripte (Vorschauen, Änderungswächter) es mitbekommen — ein direkt
            // gesetztes .value löst von sich aus kein Ereignis aus.
            owner.input.dispatchEvent(new Event('input', { bubbles: true }));
            owner.input.dispatchEvent(new Event('change', { bubbles: true }));
            paint(owner);
        }
        dlg.close();
    }

    // ---- Eine Auswahl auf der Seite ----------------------------------------------------------
    function paint(p) {
        var name = (p.input.value || '').trim();
        var known = ALL.indexOf(name) !== -1;
        p.btn.innerHTML = '';
        if (name) {
            var i = el('i', 'ti ti-' + name);
            i.setAttribute('aria-hidden', 'true');
            p.btn.appendChild(i);
        }
        p.btn.classList.toggle('is-empty', !name);
        p.nameEl.textContent = name || p.txt.empty;
        // Ein unbekannter Name wird benannt, nicht weggeräumt: sonst sieht man nur ein leeres
        // Kästchen und hält es für "kein Symbol" — und speichert es dann versehentlich weg.
        p.nameEl.classList.toggle('is-unknown', !!name && !known);
        if (name && !known) p.nameEl.textContent = name + ' — ' + p.txt.unknown;
        p.unknown = (name && !known) ? name : '';
    }

    function open(p) {
        if (!dlg) build();
        owner = p;
        TXT = p.txt;
        picked = (p.input.value || '').trim();
        titleEl.textContent = TXT.title;
        search.placeholder = TXT.search;
        search.value = '';
        noneBtn.textContent = TXT.none;
        applyBtn.textContent = TXT.apply;
        cancelBtn.textContent = TXT.cancel;
        filter('');
        dlg.showModal();
        // Zum aktuellen Symbol scrollen, statt am Anfang der 4962 zu stehen.
        var sel = grid.querySelector('.icon-tile.sel');
        if (sel) sel.scrollIntoView({ block: 'center' });
        search.focus();
    }

    function init(wrap) {
        var input = wrap.querySelector('[data-icon-input]');
        var row = wrap.querySelector('[data-icon-row]');
        if (!input || !row) return;
        var txt;
        try { txt = JSON.parse(wrap.getAttribute('data-icon-labels') || '{}'); } catch (e) { txt = {}; }

        var p = {
            wrap: wrap, input: input, row: row,
            btn: row.querySelector('[data-icon-open]'),
            nameEl: row.querySelector('[data-icon-name]'),
            txt: txt, unknown: ''
        };
        if (!p.btn || !p.nameEl) return;

        // Jetzt erst umschalten: bis hierher stand das Textfeld da und war benutzbar.
        row.hidden = false;
        input.hidden = true;

        p.btn.addEventListener('click', function () { open(p); });
        wrap.querySelector('[data-icon-clear]').addEventListener('click', function () {
            input.value = '';
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
            paint(p);
        });
        // Wer das Feld doch von Hand füllt (Rohform, eingefügter Wert), soll den Knopf mitwandern
        // sehen.
        input.addEventListener('input', function () { paint(p); });
        paint(p);

        // Sichtbarkeitsregel: das Symbol eines Menüpunkts gilt nur für die obere Leiste. Generisch
        // gehalten (Auswahlfeld + Wert), damit die Regel nicht in der geteilten Datei als
        // Sonderfall des CMS steht.
        var whenId = wrap.getAttribute('data-icon-when');
        var whenVal = wrap.getAttribute('data-icon-when-value') || '';
        if (whenId) {
            var sel = document.getElementById(whenId);
            if (sel) {
                var sync = function () { wrap.hidden = sel.value !== whenVal; };
                sel.addEventListener('change', sync);
                sync();
            }
        }
    }

    function scan() {
        Array.prototype.forEach.call(document.querySelectorAll('[data-icon-picker]'), function (w) {
            if (w.dataset.iconReady === '1') return;
            w.dataset.iconReady = '1';
            init(w);
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', scan);
    else scan();
})();
