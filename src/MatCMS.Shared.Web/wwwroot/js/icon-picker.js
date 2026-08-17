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
    var applyBtn = null, cancelBtn = null;
    var owner = null;        // die Auswahl, die den Dialog geöffnet hat
    var picked = '';         // die Wahl IM Dialog — sie wird erst beim Übernehmen übertragen
    var filtered = [];       // die aktuelle Trefferliste
    var drawn = 0;           // wie viele davon schon gezeichnet sind
    var noResults = false;   // die „kein Treffer“-Zeile steht schon da
    var pinned = false;      // das gesetzte Symbol ist oben angeheftet
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

        // Unten stehen nur noch Abbrechen und Übernehmen. „Kein Symbol“ ist KEIN Knopf mehr,
        // sondern die erste Kachel im Raster (siehe tile()) — das Leeren ist eine Wahl wie jede
        // andere und gehört dorthin, wo man wählt.
        foot = el('div', 'icon-dialog-foot');
        var count = el('span', 'icon-dialog-count');
        applyBtn = el('button', 'btn btn-sm');
        applyBtn.type = 'button';
        cancelBtn = el('button', 'btn btn-sm btn-ghost');
        cancelBtn.type = 'button';
        foot.appendChild(count);
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
        // Escape schließt ein <dialog> von selbst. Am Wert wird dabei NICHTS getan — den fasst
        // ausschließlich apply() an. Was hier geschieht, ist nur das Zurücklegen des Fokus auf den
        // Knopf, von dem aus geöffnet wurde: sonst steht der Fokus nach dem Schließen wieder am
        // Seitenanfang und man tastet sich mit der Tastatur ein zweites Mal dorthin.
        dlg.addEventListener('close', function () {
            var back = owner;
            owner = null;
            if (back && back.btn) back.btn.focus();
        });
    }

    // ---- Zeichnen ---------------------------------------------------------------------------
    // name === '' ist die Kachel „kein Symbol“: dieselbe Größe, dieselbe Stelle im Raster und
    // dasselbe Verhalten wie jede andere — sie wird angeklickt und übernommen wie ein Symbol.
    // Unterschieden wird sie über die Fläche (.icon-tile-none) und das X. Sie ist damit eine WAHL
    // und kein Sonderknopf daneben; ein Knopf unter dem Dialog behauptete, das Leeren sei etwas
    // anderes als das Auswählen, und stand außerdem dort, wo man ihn beim Suchen nicht sieht.
    function tile(name, note) {
        var none = !name;
        var b = el('button', 'icon-tile' + (none ? ' icon-tile-none' : '') + (name === picked ? ' sel' : ''));
        b.type = 'button';
        b.setAttribute('data-icon', name);
        b.title = none ? (TXT.none || '') : (name + (note ? ' — ' + note : ''));
        if (none) b.setAttribute('aria-label', TXT.none || '');
        var i = el('i', 'ti ' + (none ? 'ti-x' : 'ti-' + name));
        i.setAttribute('aria-hidden', 'true');
        b.appendChild(i);
        return b;
    }

    function filter(q) {
        q = (q || '').trim().toLowerCase();
        filtered = q ? ALL.filter(function (n) { return n.indexOf(q) !== -1; }) : ALL.slice();
        grid.innerHTML = '';
        grid.scrollTop = 0;
        drawn = 0;
        noResults = false;
        pinned = false;

        // „Kein Symbol“ ist die ERSTE Kachel — IMMER, auch während einer Suche. Sie ist kein
        // Suchtreffer, sondern die stehende Möglichkeit, das Feld zu leeren; sie beim Tippen
        // verschwinden zu lassen hieße, dass ausgerechnet der eine Eintrag, den man nie eintippen
        // kann, genau dann weg ist, wenn man tippt. Dass sie kein Treffer ist, sagt ihre eigene
        // Fläche und das X — und der Zähler unten zählt nur die echten Symbole.
        grid.appendChild(tile(''));

        // Das gesetzte Symbol steht GANZ OBEN, angeheftet, statt dass der Dialog zu ihm hinscrollt.
        // Hinscrollen hieß, alles bis dahin zu zeichnen — bei „star“ waren das rund 4000 Kacheln,
        // also genau das, was das stückweise Zeichnen vermeiden soll. Oben angeheftet sieht man
        // sofort, was gilt, und gezeichnet werden trotzdem nur die ersten 180.
        // Der Sonderfall, um den es dabei vor allem geht: ein Name, den die Schrift NICHT kennt.
        // Er wäre in der Liste gar nicht zu finden — angeheftet ist er sichtbar, benannt und
        // vorausgewählt, und wer abbricht, behält ihn.
        var cur = owner ? (owner.input.value || '').trim() : '';
        if (cur && (!q || cur.toLowerCase().indexOf(q) !== -1)) {
            var known = ALL.indexOf(cur) !== -1;
            var note = known ? (TXT.current || '') : (TXT.unknown || '');
            grid.appendChild(tile(cur, note));
            grid.appendChild(el('div', 'icon-more', note));
            pinned = true;
            // Aus der Liste darunter nehmen, sonst stünde dasselbe Symbol zweimal da.
            var ix = filtered.indexOf(cur);
            if (ix !== -1) filtered.splice(ix, 1);
        }
        draw();
    }

    function draw() {
        if (drawn >= filtered.length) {
            // Kein Treffer UND kein behaltener unbekannter Name: dann steht hier, dass nichts
            // gefunden wurde — genau einmal, sonst hinge die Zeile bei jedem Bildlauf ein weiteres
            // Mal darunter.
            if (filtered.length === 0 && !noResults && !pinned) {
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
        // clientHeight > 0 ist dabei die eigentliche Bedingung: ein noch geschlossenes <dialog> ist
        // display:none, also sind BEIDE Höhen 0 und "passt nicht aus" war immer wahr — der Dialog
        // zeichnete beim Öffnen alle 4962 Kacheln in einem Zug, genau das, was das stückweise
        // Zeichnen verhindern soll. Am laufenden System gemessen: 752 ms und das ganze Raster.
        if (grid.clientHeight > 0 && grid.scrollHeight <= grid.clientHeight && drawn < filtered.length) draw();
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
        // Kein Symbol heißt nicht "leerer Knopf": der Knopf zeigt dann dasselbe X auf derselben
        // abweichenden Fläche wie die erste Kachel im Dialog. So sieht man ohne Öffnen, dass nichts
        // gewählt ist — und man hat trotzdem etwas Sichtbares zum Anklicken statt eines leeren
        // Kästchens, das auch ein nicht geladenes Symbol sein könnte.
        if (!name) {
            var x = el('i', 'ti ti-x');
            x.setAttribute('aria-hidden', 'true');
            p.btn.appendChild(x);
        }
        p.btn.classList.toggle('is-empty', !name);
        p.nameEl.textContent = name || p.txt.empty;
        // Ein unbekannter Name wird benannt, nicht weggeräumt: sonst sieht man nur ein leeres
        // Kästchen und hält es für "kein Symbol" — und speichert es dann versehentlich weg.
        p.nameEl.classList.toggle('is-unknown', !!name && !known);
        if (name && !known) p.nameEl.textContent = name + ' — ' + p.txt.unknown;
    }

    function open(p) {
        if (!dlg) build();
        owner = p;
        TXT = p.txt;
        picked = (p.input.value || '').trim();
        titleEl.textContent = TXT.title;
        search.placeholder = TXT.search;
        search.value = '';
        applyBtn.textContent = TXT.apply;
        cancelBtn.textContent = TXT.cancel;
        // ERST öffnen, DANN zeichnen: vorher hat das Raster keine Höhe (siehe draw()), und die
        // Nachlade-Schleife hielte sich für „noch nicht voll“, bis alles gezeichnet ist.
        dlg.showModal();
        filter('');
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
            txt: txt
        };
        if (!p.btn || !p.nameEl) return;

        // Jetzt erst umschalten: bis hierher stand das Textfeld da und war benutzbar.
        row.hidden = false;
        input.hidden = true;

        p.btn.addEventListener('click', function () { open(p); });
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
