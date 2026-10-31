/* ============================================================================
   MATTHIX ARCADE - LEBEN
   Conways "Game of Life" (1970), gesaet mit dem Kurvenzug des MATTHIX-Logos.
   Der Pfad wird nicht nachgezeichnet, sondern GERASTERT: dieselbe d-Angabe wie
   im Logo wird als Path2D auf ein Raster in Zellgroesse gestrichen und Zelle
   fuer Zelle abgetastet. Generation 0 IST die Signatur.

   Was hier anders ist als in einem Schulbeispiel: die Zellen springen nicht.
   Jede Zelle hat einen eigenen Sichtwert, der weich zu ihrem Ziel laeuft -
   Neugeborene wachsen aus der Mitte ein, Sterbende schrumpfen und verblassen.
   Das Rechnen (die Generation) und das Zeigen (60 Bilder/s) sind deshalb
   getrennt: das Brett rechnet zehnmal je Sekunde, gezeichnet wird dazwischen.

   Bei prefers-reduced-motion faellt beides weg: kein Ueberblenden, und das
   Brett laeuft nicht von selbst - "Schritt" rechnet eine Generation.
   Kein Blitzen: hoechstens 10 Generationen je Sekunde, Tuerkis auf Papier.
   ========================================================================== */
(function () {
  window.__mxGames = window.__mxGames || {};

  var LOGO = 'M7.6 159.8C10.3 160.7,23.5 165.3,23.9 165.3C24.4 165.4,13.4 161.5,10.2 160.1C7 158.7,6.3 159.3,4.9 156.8C3.4 154.2,2 150.8,1.6 144.8C1.3 138.8,1.6 129.8,2.6 120.8C3.7 111.8,5.9 99.6,8 90.8C10 82,12.1 75.7,15 68.2C17.9 60.7,22.6 51.2,25.4 45.8C28.2 40.4,29.5 38.8,31.6 35.8C33.7 32.9,35.8 30.2,37.8 28.1C39.9 25.9,42 24.1,43.6 23.2C45.2 22.2,46.3 22,47.6 22.2C48.9 22.5,50.2 23.2,51.2 24.8C52.2 26.4,53.1 27.8,53.5 31.8C53.9 35.8,53.7 43.8,53.6 48.8C53.4 53.8,53.9 49.8,52.6 61.8C51.2 73.8,46.8 109.6,45.6 120.8C44.5 132,45.6 127.2,45.8 128.8C46 130.4,46.2 130,46.8 130.4C47.4 130.7,48.5 131.3,49.6 131C50.6 130.7,49.5 134,53.2 128.7C57 123.4,64.8 109.7,71.9 99.2C78.9 88.7,86.5 77.3,95.4 65.6C104.3 53.8,117.8 37.3,125.3 28.6C132.8 19.9,136.8 16.9,140.4 13.5C144 10.2,144.4 10.1,146.7 8.4C149.1 6.8,152.6 4.7,154.7 3.8C156.9 2.9,158.3 2.9,159.6 3.2C160.9 3.5,161.8 4.2,162.6 5.8C163.4 7.4,164.1 9,164.5 12.8C164.8 16.6,166.2 15.1,164.6 28.8C162.9 42.4,158 75.5,154.5 94.8C150.9 114.1,147.7 127.5,143.5 144.8C139.2 162.1,131.5 189.7,129 198.8C126.4 207.9,129 199.5,128.2 199.6C127.5 199.7,127.2 199.9,124.3 199.1C121.4 198.4,110.3 194.7,110.9 195.2C111.4 195.6,124.8 200.7,127.6 201.8';
  var VB = { w: 167, h: 208.9 };
  var PAPER = '#eaf2f1';
  var STEP_MS = 100;      // 10 Generationen/s
  var TAU = 90;           // Halbwertszeit des Ueberblendens in ms
  var LEVELS = 10;        // Stufen, in die der Sichtwert einsortiert wird

  window.__mxGames.leben = {
    start: function (host) {
      var calm = host.calm;
      var cv = document.createElement('canvas');
      host.stage.appendChild(cv);
      var ctx = cv.getContext('2d');
      cv.style.cursor = 'crosshair';

      var cols = 0, rows = 0, cell = 10;
      var cur = null, nxt = null, age = null, vis = null;
      var gen = 0, pop = 0, running = false, raf = 0, last = 0, acc = 0, dead = true;
      var drawing = false, drawVal = 1;
      // Eimer fuer das Zeichnen: je Stufe und Farbe eine Liste. Einmal angelegt,
      // danach nur noch geleert - so entsteht je Bild kein neuer Speicher.
      var bins = []; for (var k = 0; k < LEVELS * 2; k++) bins.push([]);

      function grid() {
        var w = cv.clientWidth, h = cv.clientHeight, dpr = Math.min(devicePixelRatio || 1, 2);
        if (w < 2 || h < 2) return false;
        // Auf schmalen Geraeten kleinere Zellen: sonst ist der Logostrich nur zwei
        // Zellen breit und zerfaellt sofort zu Staub, statt sich zu entwickeln.
        cell = Math.max(w < 560 ? 5 : 7, Math.round(w / 132));
        cols = Math.max(24, Math.floor(w / cell));
        rows = Math.max(16, Math.floor(h / cell));
        cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        var n = cols * rows;
        cur = new Uint8Array(n); nxt = new Uint8Array(n);
        age = new Uint8Array(n); vis = new Float32Array(n);
        return true;
      }

      function seed(soft) {
        var off = document.createElement('canvas');
        off.width = cols; off.height = rows;
        var o = off.getContext('2d');
        var sc = Math.min(cols * 0.90 / VB.w, rows * 0.86 / VB.h);
        o.setTransform(sc, 0, 0, sc, (cols - VB.w * sc) / 2, (rows - VB.h * sc) / 2);
        o.strokeStyle = '#000';
        o.lineWidth = 8;             // die gemessene Strichbreite des Logos, in seinen eigenen Einheiten
        o.lineCap = 'round'; o.lineJoin = 'round';
        o.stroke(new Path2D(LOGO));
        var px = o.getImageData(0, 0, cols, rows).data;
        for (var i = 0; i < cols * rows; i++) {
          cur[i] = px[i * 4 + 3] > 70 ? 1 : 0;
          age[i] = cur[i] ? 2 : 0;   // die Saat ist keine Geburt: Logofarbe, nicht Glanzton
          // "soft" laesst die Signatur einwachsen statt zu erscheinen.
          vis[i] = (soft && !calm) ? 0 : cur[i];
        }
        gen = 0; count(); say();
      }

      function clear() {
        for (var i = 0; i < cur.length; i++) { cur[i] = 0; age[i] = 0; if (calm) vis[i] = 0; }
        gen = 0; pop = 0; say();
      }

      function count() { var p = 0; for (var i = 0; i < cur.length; i++) p += cur[i]; pop = p; }

      function step() {
        var c = cols, r = rows;
        for (var y = 0; y < r; y++) {
          var yu = ((y - 1 + r) % r) * c, yd = ((y + 1) % r) * c, ym = y * c;
          for (var x = 0; x < c; x++) {
            var xl = (x - 1 + c) % c, xr = (x + 1) % c;
            var n = cur[yu + xl] + cur[yu + x] + cur[yu + xr] +
                    cur[ym + xl] + cur[ym + xr] +
                    cur[yd + xl] + cur[yd + x] + cur[yd + xr];
            var i = ym + x, a = cur[i];
            var alive = a ? (n === 2 || n === 3) : (n === 3);
            nxt[i] = alive ? 1 : 0;
            age[i] = alive ? (a ? (age[i] < 9 ? age[i] + 1 : 9) : 1) : 0;
          }
        }
        var t = cur; cur = nxt; nxt = t;
        gen++; count(); say();
      }

      /* ---------- zeichnen: Sichtwert weich nachfuehren, dann in Eimern malen ---------- */
      function paint(dt) {
        var ease = calm ? 1 : (1 - Math.exp(-dt / TAU));
        var n = cols * rows, i, b;
        for (b = 0; b < bins.length; b++) bins[b].length = 0;
        for (i = 0; i < n; i++) {
          var tgt = cur[i], v = vis[i];
          if (v !== tgt) { v += (tgt - v) * ease; if (Math.abs(tgt - v) < 0.008) v = tgt; vis[i] = v; }
          if (v < 0.02) continue;
          var lvl = v >= 1 ? LEVELS - 1 : (v * LEVELS) | 0;
          bins[(age[i] === 1 && cur[i] ? LEVELS : 0) + lvl].push(i);
        }
        ctx.fillStyle = PAPER;
        ctx.fillRect(0, 0, cv.clientWidth, cv.clientHeight);
        var s = cell;
        for (b = 0; b < bins.length; b++) {
          var list = bins[b]; if (!list.length) continue;
          var lv = (b % LEVELS + 0.5) / LEVELS;               // Sichtwert dieses Eimers
          // Glanzton nur fuer Neugeborene; alles andere in der Logofarbe.
          ctx.fillStyle = (b >= LEVELS ? 'rgba(23,200,214,' : 'rgba(15,169,181,') + lv.toFixed(2) + ')';
          // Neue wachsen aus der Mitte ein, Sterbende schrumpfen: die Kantenlaenge
          // haengt am selben Sichtwert wie die Deckkraft.
          var d = Math.max(1, (s - 1) * (0.34 + 0.66 * lv)), off = (s - d) / 2;
          for (var j = 0; j < list.length; j++) {
            var idx = list[j];
            ctx.fillRect((idx % cols) * s + off, ((idx / cols) | 0) * s + off, d, d);
          }
        }
      }

      function settled() {                    // steht alles still? dann Schleife anhalten
        for (var i = 0; i < vis.length; i++) if (vis[i] !== cur[i]) return false;
        return true;
      }

      function frame(t) {
        if (dead) return;
        if (!last) last = t;
        var dt = Math.min(t - last, 120); last = t;
        if (running && !document.hidden) {
          acc += dt;
          if (acc >= STEP_MS) { acc = 0; step(); }
        }
        paint(dt);
        // Wenn nichts laeuft UND nichts mehr nachblendet, die Schleife schlafen legen.
        if (!running && settled()) { raf = 0; last = 0; return; }
        raf = requestAnimationFrame(frame);
      }
      function wake() { if (!raf && !dead) { last = 0; raf = requestAnimationFrame(frame); } }

      function play(on) {
        running = on;
        // Beim Anlaufen knapp eine Sekunde negativ vorspannen: die Signatur steht erst
        // lesbar da, dann faengt sie an zu leben.
        acc = on ? -900 : 0;
        buttons(); say(); wake();
      }

      function say() {
        host.setStatus('Generation ' + gen + ' · ' + pop + ' Zellen · ' + cols + '×' + rows +
                       (running ? '' : ' · steht'));
      }

      function buttons() {
        host.setButtons([
          { t: running ? 'Pause' : 'Weiter', a: 'play' },
          { t: 'Schritt', a: 'step' },
          { t: 'Logo', a: 'seed' },
          { t: 'Leeren', a: 'clear' }
        ], function (a) {
          if (a === 'play') play(!running);
          else if (a === 'step') { if (running) play(false); step(); wake(); }
          else if (a === 'seed') { seed(true); wake(); }
          else if (a === 'clear') { if (running) play(false); clear(); wake(); }
        });
      }

      /* ---------- zeichnen mit Finger/Maus ---------- */
      function at(e) {
        var b = cv.getBoundingClientRect();
        var x = Math.floor((e.clientX - b.left) / cell), y = Math.floor((e.clientY - b.top) / cell);
        if (x < 0 || y < 0 || x >= cols || y >= rows) return -1;
        return y * cols + x;
      }
      function put(i) {
        if (i < 0 || cur[i] === drawVal) return;
        cur[i] = drawVal; age[i] = drawVal ? 1 : 0; count(); say(); wake();
      }
      function down(e) {
        var i = at(e); if (i < 0) return;
        drawing = true; drawVal = cur[i] ? 0 : 1;
        try { cv.setPointerCapture(e.pointerId); } catch (x) { }
        put(i); e.preventDefault();
      }
      function move(e) { if (drawing) put(at(e)); }
      function up() { drawing = false; }
      cv.addEventListener('pointerdown', down);
      cv.addEventListener('pointermove', move);
      cv.addEventListener('pointerup', up);
      cv.addEventListener('pointercancel', up);

      /* ---------- los ---------- */
      host.setHint('Ziehen zeichnet Zellen · Leertaste Pause · S ein Schritt · R neu säen · ' +
                   'C leeren · Esc schließen' +
                   (calm ? ' — Bewegung ist bei dir abgeschaltet, darum steht das Brett und blendet nichts über.' : ''));
      dead = false;
      if (!grid()) { requestAnimationFrame(function () { grid(); seed(true); play(!calm); }); }
      else { seed(true); play(!calm); }

      return {
        stop: function () {
          dead = true; cancelAnimationFrame(raf); raf = 0;
          cv.removeEventListener('pointerdown', down);
          cv.removeEventListener('pointermove', move);
          cv.removeEventListener('pointerup', up);
          cv.removeEventListener('pointercancel', up);
        },
        resize: function () { if (grid()) { seed(false); wake(); } },
        key: function (e) {
          var k = e.key;
          if (k === ' ' || k === 'Spacebar') { play(!running); e.preventDefault(); }
          else if (k === 's' || k === 'S') { if (running) play(false); step(); wake(); }
          else if (k === 'r' || k === 'R') { seed(true); wake(); }
          else if (k === 'c' || k === 'C') { if (running) play(false); clear(); wake(); }
        }
      };
    }
  };
})();
