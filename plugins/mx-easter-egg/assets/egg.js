/* ============================================================================
   MATTHIX ARCADE - die Huelle.
   Sie wird vom Zuender im Template geladen (Knopf "Spielen" im Kopf, Konami-Code,
   fuenfmal auf die Signatur, matthix() in der Konsole, /#matthix) und bringt
   selbst KEIN Spiel mit. Sie zeigt die Auswahl und laedt das gewaehlte Spiel
   danach als eigene Datei nach:

       egg.js             diese Huelle          (Auswahl, Leiste, Aufraeumen)
       spiel-leben.js     Conways Game of Life  (gesaet mit der Signatur)
       spiel-kaltgang.js  Raycaster im Rechenzentrum, drei Level

   Warum drei Dateien und nicht eine: styles.css und script.js des Templates
   werden in JEDE oeffentliche Seite eingebettet (Grenze 20 000 Zeichen,
   TemplateFonts.MaxInlineCode) - ein Spiel gehoert da nicht hinein. Wer nur die
   Seite liest, laedt nichts davon; wer die Auswahl oeffnet, laedt die Huelle;
   wer spielt, laedt genau ein Spiel.

   Faellt eine Datei aus, steht eine ruhige Meldung da und die Seite darunter
   bleibt unberuehrt - kein Fehler, kein halbes Bild, nichts Kaputtes.

   Rausgehen: Esc oder "Schliessen". Danach ist die Huelle restlos abgeraeumt
   (Schleifen gestoppt, Zuhoerer entfernt, DOM entfernt, Scrollsperre geloest).
   ========================================================================== */
(function () {
  var BASE = '/plugin-assets/mx-easter-egg/';

  var CAT = [
    { key: 'leben', file: 'spiel-leben.js', name: 'LEBEN',
      tag: 'Conway, 1970 · gesät mit der Signatur',
      desc: 'Vier Regeln, sonst nichts. Die Startaufstellung ist das Logo — Zelle für Zelle aus ' +
            'demselben Pfad gerastert, den der Kopf dieser Seite trägt. Dann zerfällt es in Blöcke, ' +
            'Blinker und Gleiter. Zeichnen erlaubt.' },
    { key: 'kaltgang', file: 'spiel-kaltgang.js', name: 'KALTGANG',
      tag: 'Raycaster · drei Level, eigene Karten, eigene Texturen',
      desc: 'Eine Schicht im Rechenzentrum, Spalte für Spalte aus Gitterkarten gerechnet — die ' +
            'Mathematik der frühen Neunziger, alles darin selbst erzeugt. Halle A: vier Knoten finden. ' +
            'Halle B: heiße Schränke abschalten, bevor der Kaltgang 40 °C erreicht. Der Kern: Notstrom, ' +
            'drei USV-Module und eine Kernel Panic. Unterwegs Störungen, die das Patchkabel repariert, ' +
            'Kaffee für die Uptime, Monitore mit echten Werten — und M legt eine Karte darüber.' }
  ];

  var PAPER = '#eaf2f1', TEAL = '#0fa9b5', INK = '#16232e', TEALINK = '#096a72';
  var LOGO = 'M7.6 159.8C10.3 160.7,23.5 165.3,23.9 165.3C24.4 165.4,13.4 161.5,10.2 160.1C7 158.7,6.3 159.3,4.9 156.8C3.4 154.2,2 150.8,1.6 144.8C1.3 138.8,1.6 129.8,2.6 120.8C3.7 111.8,5.9 99.6,8 90.8C10 82,12.1 75.7,15 68.2C17.9 60.7,22.6 51.2,25.4 45.8C28.2 40.4,29.5 38.8,31.6 35.8C33.7 32.9,35.8 30.2,37.8 28.1C39.9 25.9,42 24.1,43.6 23.2C45.2 22.2,46.3 22,47.6 22.2C48.9 22.5,50.2 23.2,51.2 24.8C52.2 26.4,53.1 27.8,53.5 31.8C53.9 35.8,53.7 43.8,53.6 48.8C53.4 53.8,53.9 49.8,52.6 61.8C51.2 73.8,46.8 109.6,45.6 120.8C44.5 132,45.6 127.2,45.8 128.8C46 130.4,46.2 130,46.8 130.4C47.4 130.7,48.5 131.3,49.6 131C50.6 130.7,49.5 134,53.2 128.7C57 123.4,64.8 109.7,71.9 99.2C78.9 88.7,86.5 77.3,95.4 65.6C104.3 53.8,117.8 37.3,125.3 28.6C132.8 19.9,136.8 16.9,140.4 13.5C144 10.2,144.4 10.1,146.7 8.4C149.1 6.8,152.6 4.7,154.7 3.8C156.9 2.9,158.3 2.9,159.6 3.2C160.9 3.5,161.8 4.2,162.6 5.8C163.4 7.4,164.1 9,164.5 12.8C164.8 16.6,166.2 15.1,164.6 28.8C162.9 42.4,158 75.5,154.5 94.8C150.9 114.1,147.7 127.5,143.5 144.8C139.2 162.1,131.5 189.7,129 198.8C126.4 207.9,129 199.5,128.2 199.6C127.5 199.7,127.2 199.9,124.3 199.1C121.4 198.4,110.3 194.7,110.9 195.2C111.4 195.6,124.8 200.7,127.6 201.8';

  var calm = matchMedia('(prefers-reduced-motion: reduce)').matches;
  var root, stage, elT, elS, elHint, elGB, running = null, keyed = null, resizeT = 0, scrollY = 0;
  var loaded = {};

  /* ---------- Stil, einmal, erst beim ersten Oeffnen ---------- */
  function style() {
    if (document.getElementById('mxg-style')) return;
    var s = document.createElement('style');
    s.id = 'mxg-style';
    s.textContent = [
      '#mxg{position:fixed;inset:0;z-index:2147483000;overflow:hidden;background:' + PAPER + ';',
      '  display:flex;flex-direction:column;font:400 14px/1.45 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;color:' + INK + '}',
      '#mxg .mxg-stage{flex:1 1 auto;min-height:0;position:relative;overflow:hidden;display:flex}',
      '#mxg .mxg-stage>canvas{flex:1 1 auto;display:block;width:100%;min-height:0;touch-action:none}',
      '#mxg .mxg-bar{flex:0 0 auto;display:flex;align-items:center;flex-wrap:wrap;gap:.45rem .7rem;',
      '  padding:.6rem clamp(.7rem,3vw,1.2rem);padding-bottom:max(.6rem,env(safe-area-inset-bottom));',
      '  border-top:1px solid rgba(15,169,181,.34);background:rgba(237,243,242,.96)}',
      '#mxg .mxg-t{font-size:.68rem;letter-spacing:.16em;text-transform:uppercase;color:' + TEALINK + ';font-weight:700}',
      '#mxg .mxg-s{font-size:.72rem;font-variant-numeric:tabular-nums;color:#2f3d47;opacity:.85}',
      '#mxg .mxg-sp{flex:1 1 auto;min-width:0}',
      '#mxg .mxg-gb{display:flex;gap:.45rem;flex-wrap:wrap}',
      '#mxg button{font:inherit;font-size:.78rem;font-weight:600;padding:.42rem .8rem;cursor:pointer;',
      '  background:transparent;color:' + INK + ';border:1px solid rgba(15,169,181,.5);border-radius:0}',
      '#mxg button:hover{background:rgba(15,169,181,.12)}',
      '#mxg button:focus-visible{outline:2px solid ' + TEALINK + ';outline-offset:2px}',
      '#mxg .mxg-x{background:#0b7f8a;color:#fff;border-color:#0b7f8a}',
      '#mxg .mxg-x:hover{background:' + TEALINK + '}',
      '#mxg .mxg-hint{flex:0 0 auto;padding:0 clamp(.7rem,3vw,1.2rem) .65rem;font-size:.7rem;color:#2f3d47;opacity:.7}',
      '#mxg .mxg-menu{flex:1 1 auto;min-height:0;overflow:auto;display:flex;flex-direction:column;',
      '  justify-content:center;gap:clamp(1rem,3vw,1.6rem);padding:clamp(1.2rem,5vw,3rem);position:relative}',
      '#mxg .mxg-wash{position:absolute;right:-4%;top:50%;transform:translateY(-50%);height:min(76vh,34rem);width:auto;',
      '  opacity:.07;pointer-events:none}',
      '#mxg .mxg-wash path{fill:none;stroke:' + TEAL + ';stroke-width:8;stroke-linecap:round;stroke-linejoin:round}',
      '#mxg .mxg-lead{margin:0;font-size:.7rem;letter-spacing:.24em;text-transform:uppercase;color:' + TEALINK + ';font-weight:700;position:relative}',
      '#mxg .mxg-cards{display:grid;gap:clamp(.8rem,2vw,1.2rem);grid-template-columns:repeat(auto-fit,minmax(min(100%,20rem),1fr));position:relative}',
      '#mxg .mxg-card{display:block;width:100%;text-align:left;padding:clamp(1rem,2.5vw,1.5rem);',
      '  border:1px solid rgba(15,169,181,.4);background:rgba(255,255,255,.55);cursor:pointer;font:inherit;color:inherit}',
      '#mxg .mxg-card:hover{background:rgba(15,169,181,.10);border-color:' + TEAL + '}',
      '#mxg .mxg-card h3{margin:0 0 .2rem;font-size:clamp(1.4rem,4vw,2.1rem);letter-spacing:.04em;color:' + INK + ';font-weight:700}',
      '#mxg .mxg-card .tg{display:block;font-size:.68rem;letter-spacing:.16em;text-transform:uppercase;color:' + TEALINK + ';font-weight:700;margin-bottom:.6rem}',
      '#mxg .mxg-card p{margin:0;font-size:.86rem;line-height:1.6;color:#2f3d47}',
      '#mxg .mxg-msg{margin:auto;padding:2rem;max-width:34rem;text-align:center;font-size:.95rem;line-height:1.6;color:#2f3d47}',
      '@media(max-width:700px){#mxg .mxg-t{width:100%}#mxg .mxg-s{width:100%}',
      '  #mxg .mxg-sp{display:none}#mxg .mxg-bar{gap:.4rem}#mxg .mxg-bar>button{flex:1 1 auto}',
      '  #mxg .mxg-hint{display:none}#mxg .mxg-wash{display:none}}'
    ].join('');
    document.head.appendChild(s);
  }

  /* ---------- Der Wirt, den ein Spiel bekommt ---------- */
  var host = {
    calm: calm,
    stage: null,
    setTitle: function (t) { if (elT) elT.textContent = t || ''; },
    setStatus: function (t) { if (elS) elS.textContent = t || ''; },
    setHint: function (t) { if (elHint) elHint.textContent = t || ''; },
    setButtons: function (list, onclick) {
      if (!elGB) return;
      elGB.innerHTML = '';
      (list || []).forEach(function (b) {
        var el = document.createElement('button');
        el.textContent = b.t; el.setAttribute('data-ga', b.a);
        el.addEventListener('click', function () { onclick(b.a, el); });
        elGB.appendChild(el);
      });
    },
    close: function () { close(); }
  };

  function stopGame() {
    if (running && running.stop) { try { running.stop(); } catch (e) { } }
    running = null;
    if (stage) { stage.innerHTML = ''; stage.style.background = ''; }
    if (elGB) elGB.innerHTML = '';
    host.setStatus(''); host.setHint('');
  }

  /* ---------- Auswahl ---------- */
  function menu() {
    stopGame();
    host.setTitle('MATTHIX · Arcade');
    host.setHint('Zwei Spiele. Esc schließt, „Menü“ kommt hierher zurück.');
    var wrap = document.createElement('div');
    wrap.className = 'mxg-menu';
    var svg = '<svg class="mxg-wash" viewBox="0 0 167 208.9" aria-hidden="true"><path d="' + LOGO + '"></path></svg>';
    var cards = CAT.map(function (g) {
      return '<button class="mxg-card" data-g="' + g.key + '">' +
             '<h3>' + g.name + '</h3><span class="tg">' + g.tag + '</span><p>' + g.desc + '</p></button>';
    }).join('');
    wrap.innerHTML = svg + '<p class="mxg-lead">Wähle</p><div class="mxg-cards">' + cards + '</div>';
    stage.appendChild(wrap);
    wrap.addEventListener('click', function (e) {
      var b = e.target.closest('.mxg-card'); if (b) play(b.getAttribute('data-g'));
    });
    var first = wrap.querySelector('.mxg-card'); if (first) first.focus();
  }

  function msg(text) { stage.innerHTML = '<div class="mxg-msg">' + text + '</div>'; }

  /* ---------- Ein Spiel laden und starten ---------- */
  function play(key) {
    var g = CAT.filter(function (x) { return x.key === key; })[0];
    if (!g) return;
    stopGame();
    host.setTitle(g.name);
    if (loaded[key] && window.__mxGames && window.__mxGames[key]) return begin(g);
    msg('Lädt …');
    var s = document.createElement('script');
    s.src = BASE + g.file; s.async = true;
    s.onload = function () {
      loaded[key] = true;
      if (window.__mxGames && window.__mxGames[key]) begin(g); else fail(g);
    };
    s.onerror = function () { fail(g); };
    document.head.appendChild(s);
  }

  function fail(g) {
    // Kein Fehler nach aussen, keine halbe Buehne: eine ruhige Zeile und der Weg zurueck.
    msg('„' + g.name + '“ lässt sich gerade nicht laden.<br>Die Seite darunter ist davon unberührt — ' +
        '„Menü“ oder Esc bringt dich zurück.');
    host.setStatus('');
  }

  function begin(g) {
    stage.innerHTML = '';
    host.stage = stage;
    try { running = window.__mxGames[g.key].start(host) || null; }
    catch (e) { running = null; fail(g); }
  }

  /* ---------- auf und zu ---------- */
  function build() {
    style();
    root = document.createElement('div');
    root.id = 'mxg';
    root.setAttribute('role', 'dialog');
    root.setAttribute('aria-modal', 'true');
    root.setAttribute('aria-label', 'MATTHIX Arcade');
    root.setAttribute('tabindex', '-1');
    root.innerHTML =
      '<div class="mxg-stage"></div>' +
      '<div class="mxg-bar"><span class="mxg-t"></span><span class="mxg-s"></span>' +
      '<span class="mxg-sp"></span><span class="mxg-gb"></span>' +
      '<button data-a="menu">Menü</button>' +
      '<button data-a="close" class="mxg-x">Schließen</button></div>' +
      '<div class="mxg-hint"></div>';
    document.body.appendChild(root);
    stage = root.querySelector('.mxg-stage');
    elT = root.querySelector('.mxg-t'); elS = root.querySelector('.mxg-s');
    elGB = root.querySelector('.mxg-gb'); elHint = root.querySelector('.mxg-hint');
    host.stage = stage;

    root.querySelector('.mxg-bar').addEventListener('click', function (e) {
      var b = e.target.closest('button[data-a]'); if (!b) return;
      if (b.getAttribute('data-a') === 'menu') menu(); else close();
    });

    keyed = function (e) {
      if (e.key === 'Escape') { close(); e.preventDefault(); return; }
      if (running && running.key) running.key(e);
    };
    addEventListener('keydown', keyed);
    addEventListener('keyup', keyup);
    addEventListener('resize', onresize);
  }

  function keyup(e) { if (running && running.keyup) running.keyup(e); }

  function onresize() {
    clearTimeout(resizeT);
    resizeT = setTimeout(function () { if (running && running.resize) running.resize(); }, 200);
  }

  function close() {
    stopGame();
    removeEventListener('keydown', keyed);
    removeEventListener('keyup', keyup);
    removeEventListener('resize', onresize);
    clearTimeout(resizeT);
    if (root && root.parentNode) root.parentNode.removeChild(root);
    root = stage = elT = elS = elGB = elHint = null;
    host.stage = null;
    document.documentElement.style.overflow = '';
    document.body.style.overflow = '';
    window.scrollTo(0, scrollY);
  }

  // which: 'leben' | 'kaltgang' | nichts (dann die Auswahl)
  window.__mxEgg = function (which) {
    if (root) { if (which) play(which); return; }
    scrollY = window.scrollY || 0;
    document.documentElement.style.overflow = 'hidden';
    document.body.style.overflow = 'hidden';
    build();
    root.focus();
    if (which && CAT.some(function (g) { return g.key === which; })) play(which);
    else menu();
  };
})();
