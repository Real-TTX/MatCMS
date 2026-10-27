/* ============================================================================
   MATTHIX ARCADE - KALTGANG
   Ein Raycaster: die Wand-Spalten werden mit einem DDA durch eine Gitterkarte
   gerechnet, so wie es die Spiele der fruehen Neunziger gemacht haben. Der
   Algorithmus ist Allgemeingut - die INHALTE hier sind es nicht, sie sind alle
   selbst erzeugt:

     * Karten: drei Stueck, je 24x24 Zeichen, in dieser Datei geschrieben.
     * Texturen: aus Code gerechnet - DREI Rackfronten mit je acht Einschueben aus
       fuenf Geraeteklassen, dazu Wand, Lochblech, Tuer, Bodenplatte und Decke.
     * Figuren: die Stoerungen (Bug, Glitch, Kernel Panic), Kaffee und USV-Modul
       werden beim Start auf eine kleine Leinwand gezeichnet. Keine Bilddatei.
     * Monitore: ihr Bild wird waehrend des Spiels gezeichnet und zeigt echte
       Werte aus dem laufenden Spiel.
     * Klaenge: aus dem Browser selbst (WebAudio-Oszillatoren), keine Datei.
     * Farben: die Palette dieser Website (#00adb5 / #17c8d6 / #096a72), dazu
       Bernstein fuer Waerme und Magenta fuer alles, was kaputt ist.
     * Kein fremdes Material: keine WAD, keine Textur, kein Sprite, kein Klang,
       kein Schriftzug und kein nachgebautes Level von irgendwoher.

   DREI LEVEL, eine Schicht im Rechenzentrum:
     1  KALTGANG   - vier Knoten beruehren, dann oeffnet sich die Tuer.
     2  HITZESTAU  - sechs Schraenke laufen heiss (Bernstein). Abschalten (E),
                     bevor der Kaltgang 40 Grad erreicht.
     3  NOTSTROM   - das Licht ist aus. Drei USV-Module einsammeln und die
                     Kernel Panic beheben, dann in den Kern.
   In jedem Level laufen STOERUNGEN herum. Sie kosten Uptime, wenn sie einen
   erwischen. Das Patchkabel in der Hand repariert sie (Leertaste / Klick).
   Kaffee fuellt die Uptime auf. Faellt sie auf null: Ausfall, Level von vorn.

   RUECKSICHTEN, und die sind hier wichtiger als anderswo, weil das Bild sich
   selbst bewegt:
     * Wer prefers-reduced-motion gesetzt hat, bekommt das Spiel NICHT einfach
       vorgesetzt - ein Hinweis mit Knopf steht davor.
     * Und dann bleibt alles Uebrige ruhig: die Leuchtpunkte stehen still, die
       Monitore zeigen ein festes Bild, Hand und Stoerungen wippen nicht, und
       ein Treffer faerbt den Rand nicht ein - nur der Uptime-Balken sinkt.
     * Das Blinken ist auch im Normalfall bewusst zahm: winzige Flaechen, jeder
       Punkt in eigenem Takt, langsam, und es blitzt nichts ueber die ganze Flaeche.
     * Klaenge sind leise und lassen sich in der Leiste abschalten ("Ton").
     * Nichts wird gespeichert: Punkte und Zeiten leben nur, solange das Spiel offen ist.
   Auf dem Telefon gibt es eine Beruehrsteuerung (drehen / vor / zurueck / E / Patch).
   ========================================================================== */
(function () {
  window.__mxGames = window.__mxGames || {};

  /* ---------- Die drei Level ----------
     Karte: '#' Wand, 'r' Schrank, 'h' heisser Schrank (Level 2), 'g' Lochblech, 'D' Tuer.
     Alles andere auf der Karte (Knoten, Monitore, Stoerungen, Gegenstaende) steht in Listen
     daneben - so bleibt die Karte lesbar und ein Gegenstand steht nie IN einer Wand. */
  var LEVELS = [
    {
      name: 'KALTGANG', sub: 'Level 1 · Halle A',
      goal: 'nodes',
      intro: 'Vier Knoten sind aus dem Takt. Berühre alle vier — dann öffnet sich die Tür. ' +
             'Unterwegs sind ein paar Störungen unterwegs: das Patchkabel repariert sie.',
      map: [
        '########################',
        '#......#........#......#',
        '#.rrrr.#.rrrrrr.#.rrrr.#',
        '#.rrrr.g.rrrrrr.g.rrrr.#',
        '#......#...............#',
        '#.rrrr.#.rrrrrr.#.rrrr.#',
        '#.rrrr.#.rrrrrr.#.rrrr.#',
        '#......#........#......#',
        '####.#######.###########',
        '#......................#',
        '#.gggg.####.####.gggg..#',
        '#......#......#........#',
        '#.rrrr.#.rrrr.#.rrrrrr.#',
        '#.rrrr.#.rrrr.#.rrrrrr.#',
        '#......#......#........#',
        '######.########.########',
        '#......................#',
        '#.rrrrrrrr.#.rrrrrrrr..#',
        '#.rrrrrrrr.#.rrrrrrrr..#',
        '#..........#...........#',
        '#.rrrrrrrr.#.rrrrrrrr..#',
        '#.rrrrrrrr.#.rrrrrrrr..#',
        '#..........#.......D...#',
        '########################'
      ],
      start: { x: 2.5, y: 9.5, a: 0 },
      nodes: [[5.5, 4.5], [20.5, 9.5], [6.5, 16.5], [2.5, 19.5]],
      mons: [
        { x: 1.55, y: 1.55, kind: 'last' }, { x: 22.45, y: 1.55, kind: 'temp' },
        { x: 1.55, y: 16.5, kind: 'auftrag' }, { x: 22.4, y: 19.5, kind: 'log' }
      ],
      foes: [['bug', 12.5, 9.5], ['bug', 20.5, 4.5], ['bug', 3.5, 11.5], ['glitch', 12.5, 16.5], ['glitch', 19.5, 19.5]],
      items: [['kaffee', 10.5, 11.5], ['kaffee', 2.5, 22.5]],
      log: ['MATTHIX RZ', 'HALLE A', 'PROBE OK', 'LUFT 12C', 'USV 100%', 'LINK 10G']
    },
    {
      name: 'HITZESTAU', sub: 'Level 2 · Halle B',
      goal: 'heat',
      intro: 'In Halle B laufen sechs Schränke heiß — sie leuchten bernstein. Schalte sie mit E ab, ' +
             'bevor der Kaltgang 40 °C erreicht. Erst dann gibt die Klimatisierung die Tür frei.',
      map: [
        '########################',
        '#......................#',
        '#.rrhrrr.rrrrrr.rrrhrr.#',
        '#.rrrrrr.rrrrrr.rrrrrr.#',
        '#......................#',
        '#.rrrrrr.rrhrrr.rrrrrr.#',
        '#.rrrrrr.rrrrrr.rrrrrr.#',
        '#......................#',
        '####.########gg####.####',
        '#......#........#......#',
        '#.rrrr.#.rrhrr..#.hrrr.#',
        '#.rrrr.#.rrrrr..#.rrrr.#',
        '#......g........g......#',
        '#.rrrr.#.rrrrr..#.rrrr.#',
        '#.rrhr.#.rrrrr..#.rrrr.#',
        '#......#........#......#',
        '###.########.#######.###',
        '#......................#',
        '#.gggg.rrrrrrrrrr.gggg.#',
        '#......................#',
        '#.rrrrrrrr....rrrrrrrr.#',
        '#.rrrrrrrr....rrrrrrrr.#',
        '#...........D..........#',
        '########################'
      ],
      start: { x: 2.5, y: 1.5, a: 0 },
      nodes: [],
      mons: [
        { x: 1.55, y: 4.45, kind: 'temp' }, { x: 22.45, y: 1.55, kind: 'auftrag' },
        { x: 1.55, y: 22.4, kind: 'last' }, { x: 22.45, y: 22.4, kind: 'log' }
      ],
      foes: [['bug', 12.5, 4.5], ['bug', 20.5, 7.5], ['glitch', 11.5, 12.5], ['glitch', 4.5, 17.5], ['bug', 19.5, 19.5], ['glitch', 13.5, 20.5]],
      items: [['kaffee', 8.5, 9.5], ['kaffee', 22.5, 12.5]],
      log: ['HALLE B', 'KLIMA WARN', 'ZULUFT 19C', 'ABLUFT 41C', 'LAST HOCH', 'E = AUS']
    },
    {
      name: 'NOTSTROM', sub: 'Level 3 · Kern',
      goal: 'power', dark: true,
      intro: 'Stromausfall. Nur das Notlicht brennt. Sammle die drei USV-Module ein und behebe die ' +
             'Kernel Panic vor dem Kern — dann öffnet sich die Tür, und die Schicht ist geschafft.',
      map: [
        '########################',
        '#........#.....#.......#',
        '#.rrrrrr.#.rrr.#.rrrrr.#',
        '#.rrrrrr...rrr...rrrrr.#',
        '#........#.....#.......#',
        '####.#####.###.#####.###',
        '#..........#...........#',
        '#.gggg.rr..#..rr.gggg..#',
        '#.......rr.....rr......#',
        '#.rrrr.......#.......r.#',
        '#.rrrr.####..#..####.r.#',
        '#......#.........#.....#',
        '###.####.rrrrrr..####.##',
        '#.......rrrrrr.........#',
        '#.rrrr..........rrrrr..#',
        '#.rrrr.##.####.##rrrrr.#',
        '#......#......#........#',
        '#####.##.####.#####.####',
        '#......................#',
        '#.rrrrrrr.###D##.rrrrr.#',
        '#.rrrrrrr.#....#.rrrrr.#',
        '#.........#....#.......#',
        '#.........######.......#',
        '########################'
      ],
      start: { x: 1.5, y: 1.5, a: 0 },
      nodes: [],
      mons: [
        { x: 1.55, y: 4.45, kind: 'auftrag' }, { x: 22.45, y: 4.45, kind: 'last' },
        { x: 1.55, y: 21.4, kind: 'log' }, { x: 22.45, y: 18.45, kind: 'temp' }
      ],
      foes: [['bug', 5.5, 6.5], ['bug', 18.5, 8.5], ['bug', 15.5, 13.5], ['glitch', 10.5, 11.5], ['glitch', 20.5, 16.5],
             ['glitch', 3.5, 18.5], ['panic', 12.5, 18.4]],
      items: [['usv', 22.5, 1.5], ['usv', 1.5, 13.5], ['usv', 22.5, 21.5], ['kaffee', 11.5, 8.5], ['kaffee', 6.5, 18.5]],
      log: ['NOTSTROM', 'USV 3%', 'KERN OFFLINE', 'PANIC!', 'DIESEL ?', 'LICHT AUS']
    }
  ];
  var MW = 24, MH = 24;
  var CODE = { '#': 2, 'r': 1, 'h': 1, 'g': 3, 'D': 4 };

  // Die Stoerungen: wie schnell, wie zaeh, wie bissig, wie gross.
  var FOE = {
    bug:    { hp: 1,  speed: 2.1, dmg: 9,  h: 0.42, w: 0.46, score: 100, ground: true },
    glitch: { hp: 3,  speed: 1.45, dmg: 14, h: 0.55, w: 0.55, score: 250 },
    panic:  { hp: 14, speed: 0.85, dmg: 22, h: 0.95, w: 0.95, score: 2000, ground: true, shoots: true }
  };

  function C(r, g, b) { return ((255 << 24) | (b << 16) | (g << 8) | r) >>> 0; }
  function clamp(v, a, b) { return v < a ? a : (v > b ? b : v); }

  /* ---------- Texturen, aus Code ----------
     Eine Rackfront ist 64x64 gross und steht im Spiel auf 600 Bildpunkten: jeder Texel wird zehnmal
     breiter. Eine Front mit ein paar Streifen zerfaellt dabei zu Streifen - deshalb tragen die acht
     Einschuebe hier FUENF VERSCHIEDENE Geraetearten (Plattenschublade, Server, Patchfeld,
     Blindblende, Switch), und es gibt drei Fronten mit verschobener Reihenfolge, damit nicht jeder
     Schrank gleich aussieht. Welche ein Schrank zeigt, entscheidet seine Position auf der Karte.

     Neben der Farbe entsteht je Front eine zweite Karte: welcher Texel gehoert zu welchem
     Leuchtpunkt. Damit ist derselbe Texel mal hell und mal dunkel, ohne die Textur zu verdoppeln -
     und ein ausgeschalteter Schrank ist einfach einer, dessen Punkte alle aus sind. Werte ueber 127
     bedeuten BERNSTEIN statt Tuerkis: ohne einen zweiten Farbton liest das Auge in einem Bild aus
     lauter Tuerkis keine Tiefe mehr. */
  function rackFront(shift) {
    var S = 64, tex = new Uint32Array(S * S), led = new Uint8Array(S * S), x, y;
    var idc = 1;
    for (var slot = 0; slot < 8; slot++) {
      var kind = (slot + shift) % 5;
      var top = slot * 8;
      for (y = top; y < top + 8; y++) {
        var sy = y - top;
        for (x = 0; x < S; x++) {
          var curve = 1 - Math.abs(x - 32) / 46;              // die Front ist leicht gewoelbt
          var v = (32 + ((x * 7 + y * 13) % 5)) * (0.84 + 0.28 * curve);
          var c = C(v - 12, v + 7, v + 17);
          if (sy === 0) c = C(8, 18, 23);                     // Fuge oben
          if (sy === 7) c = C(12, 26, 32);                    // Fuge unten, eine Spur heller
          if (x < 4 || x > 59) c = C(22, 58, 68);             // Schiene
          if ((x === 1 || x === 62) && (sy === 3 || sy === 5)) c = C(74, 124, 136); // Schraube

          if (sy > 0 && sy < 7 && x > 4 && x < 60) {
            if (kind === 0) {
              // Plattenschublade: zwoelf Einschuebe, jeder mit Griffmulde und einem winzigen Punkt
              var bay = ((x - 6) / 4) | 0, inBay = (x - 6) % 4;
              if (bay >= 0 && bay < 13) {
                if (inBay === 3) c = C(10, 22, 28);                     // Spalt
                else if (sy === 2 || sy === 5) c = C(46, 84, 96);       // Griffmulde
                else c = C(v - 6, v + 12, v + 22);
                if (inBay === 0 && sy === 3) { c = C(15, 169, 181); led[y * S + x] = idc; }
              }
            } else if (kind === 1) {
              // Ein Server: Griff links, Lueftungsgitter, zwei Punkte rechts
              if (x > 6 && x < 17 && sy > 2 && sy < 5) c = C(62, 108, 120);
              if (x > 6 && x < 17 && sy === 5) c = C(24, 52, 62);
              if (x > 20 && x < 45 && sy > 1 && sy < 6 && (x % 2 === 0)) c = C(16, 34, 42);
              if (sy === 3 || sy === 4) {
                if (x === 49 || x === 50) { c = C(15, 169, 181); led[y * S + x] = idc; }
                if (x === 53 || x === 54) { c = C(190, 130, 40); led[y * S + x] = idc + 1 + 128; }
              }
            } else if (kind === 2) {
              // Patchfeld: sechzehn Buchsen, jede mit einem Punkt darueber
              var port = ((x - 6) / 3) | 0, inPort = (x - 6) % 3;
              if (port >= 0 && port < 16) {
                if (sy > 3 && sy < 6 && inPort < 2) c = C(9, 20, 26);   // die Buchse selbst
                else if (sy === 6 && inPort < 2) c = C(40, 74, 86);     // Kante darunter
                if (sy === 2 && inPort === 0) {
                  var amber = (port % 3) === 0;
                  c = amber ? C(190, 130, 40) : C(15, 169, 181);
                  led[y * S + x] = amber ? (idc + 128) : idc;
                }
              }
            } else if (kind === 3) {
              // Blindblende: nichts drin, nur Blech und zwei Schrauben. Genau das gibt der Front
              // ihren Rhythmus - eine Wand aus lauter Technik liest sich als Tapete.
              c = C(v - 14, v + 2, v + 10);
              if ((x === 12 || x === 52) && sy === 4) c = C(70, 116, 128);
            } else {
              // Switch: eine Reihe Portlampen, dazu zwei Uplinks
              if (sy === 3 || sy === 4) {
                var slotIx = ((x - 8) / 4) | 0;
                if (x >= 8 && x < 40 && ((x - 8) % 4) < 2 && slotIx >= 0) {
                  var am = (slotIx % 4) === 1;
                  c = am ? C(190, 130, 40) : C(15, 169, 181);
                  led[y * S + x] = am ? (idc + slotIx + 128) : (idc + slotIx);
                }
                if (x === 50 || x === 51) { c = C(23, 200, 214); led[y * S + x] = idc + 12; }
                if (x === 55 || x === 56) { c = C(23, 200, 214); led[y * S + x] = idc + 13; }
              }
              if (sy === 6 && x > 8 && x < 56) c = C(18, 40, 48);
            }
          }
          tex[y * S + x] = c;
        }
      }
      idc += 16;                                   // je Einschub ein eigener Nummernbereich
      if (idc > 100) idc = 1;
    }
    return { tex: tex, led: led };
  }

  function textures() {
    var S = 64, x, y;
    var racks = [rackFront(0), rackFront(2), rackFront(4)];

    // Wand: glatter Beton mit leiser Koernung, einer waagerechten Fuge und einem Sockel
    var wall = new Uint32Array(S * S);
    for (y = 0; y < S; y++) for (x = 0; x < S; x++) {
      var n = ((x * 31 + y * 17) % 11) - 5, b = 58 + n;
      var c = C(b - 12, b, b + 10);
      if (y === 31 || y === 32) c = C(26, 44, 52);
      if (y > 56) c = C(34, 56, 64);                          // Sockelleiste
      if (y === 56) c = C(70, 104, 114);
      wall[y * S + x] = c;
    }
    // Gitter: Lochblech mit Rahmen
    var grate = new Uint32Array(S * S);
    for (y = 0; y < S; y++) for (x = 0; x < S; x++) {
      var on = (x % 8 < 2) || (y % 8 < 2);
      var c2 = on ? C(52, 104, 118) : C(13, 28, 35);
      if (x < 2 || x > 61 || y < 2 || y > 61) c2 = C(60, 112, 124);
      grate[y * S + x] = c2;
    }
    // Tuer: Tuerkisfeld mit Rahmen, hellem Band und zwei Griffmulden
    var door = new Uint32Array(S * S);
    for (y = 0; y < S; y++) for (x = 0; x < S; x++) {
      var edge = (x < 4 || x > 59 || y < 4 || y > 59);
      var band = (y > 28 && y < 36);
      var c3 = edge ? C(9, 106, 114) : (band ? C(23, 200, 214) : C(15, 169, 181));
      if (!edge && !band && (x === 30 || x === 33) && y > 38 && y < 50) c3 = C(9, 90, 98);
      door[y * S + x] = c3;
    }
    // Boden: Doppelbodenplatte mit Lochbild und Fuge
    var floor = new Uint32Array(S * S);
    for (y = 0; y < S; y++) for (x = 0; x < S; x++) {
      var g0 = 40 + ((x * 5 + y * 11) % 5);
      var cc = C(g0 - 8, g0 + 4, g0 + 9);
      var hx = x % 8, hy = y % 8;
      if (hx > 2 && hx < 6 && hy > 2 && hy < 6) cc = C(14, 28, 35);
      if (hx === 2 && hy > 1 && hy < 7) cc = C(20, 40, 48);
      if (x < 2 || y < 2 || x > 61 || y > 61) cc = C(50, 84, 94);
      floor[y * S + x] = cc;
    }
    // Decke: dunkel, mit Kabelpritsche quer und Kabeln darin
    var ceil = new Uint32Array(S * S);
    for (y = 0; y < S; y++) for (x = 0; x < S; x++) {
      var d = 18 + ((x * 3 + y * 7) % 4);
      var cd = C(d - 6, d + 5, d + 9);
      if (y > 26 && y < 38) cd = C(26, 44, 52);
      if (y === 27 || y === 37) cd = C(40, 66, 76);
      if (y > 28 && y < 36 && (x % 6 < 3)) cd = C(20, 52, 60);
      ceil[y * S + x] = cd;
    }
    return { racks: racks, wall: wall, grate: grate, door: door, floor: floor, ceil: ceil };
  }

  /* ---------- Figuren, aus Code ----------
     32x32, auf eine kleine Leinwand gemalt und als Bildpunkte ausgelesen - mit Durchsicht, damit
     sie als Billboard im Raum stehen. Je Figur zwei bis vier Bilder fuer die Bewegung.
     Eine Stoerung ist MAGENTA: die eine Farbe, die es in dieser Palette sonst nicht gibt, sodass
     man sie zwischen Tuerkis und Bernstein sofort sieht. */
  var TS = 32;
  function sprites() {
    var cv = document.createElement('canvas'); cv.width = TS; cv.height = TS;
    var g = cv.getContext('2d', { willReadFrequently: true });
    function grab() { var d = g.getImageData(0, 0, TS, TS).data; return new Uint32Array(d.buffer.slice(0)); }
    var out = { bug: [], glitch: [], panic: [], kaffee: [], usv: [] }, f, i;

    // Bug: ein Kaefer aus kaputten Pixeln, sechs Beine im Wechselschritt
    for (f = 0; f < 2; f++) {
      g.clearRect(0, 0, TS, TS);
      g.strokeStyle = '#ff4fb0'; g.lineWidth = 1.4;
      for (i = 0; i < 3; i++) {
        var ly = 15 + i * 4, sw = (i % 2 === f) ? 3 : -2;
        g.beginPath(); g.moveTo(9, ly); g.lineTo(3, ly + sw); g.stroke();
        g.beginPath(); g.moveTo(23, ly); g.lineTo(29, ly - sw); g.stroke();
      }
      g.fillStyle = '#2b0d22'; g.beginPath(); g.ellipse(16, 19, 9, 10, 0, 0, 6.283); g.fill();
      g.strokeStyle = '#ff4fb0'; g.lineWidth = 1.2; g.stroke();
      g.fillStyle = '#ff4fb0'; g.fillRect(15, 10, 2, 18);                    // Naht ueber dem Ruecken
      g.fillStyle = '#ffd1ea'; g.fillRect(10 + f, 22, 3, 3); g.fillRect(19 - f, 15, 2, 4);   // Bildfehler
      g.fillStyle = '#c0186e'; g.fillRect(19, 24, 4, 2); g.fillRect(9, 15, 3, 2);
      g.fillStyle = '#2b0d22'; g.beginPath(); g.ellipse(16, 9, 6, 5, 0, 0, 6.283); g.fill();  // Kopf
      g.fillStyle = '#8ffcff'; g.fillRect(12, 7, 3, 3); g.fillRect(17, 7, 3, 3);              // Augen
      g.strokeStyle = '#ff4fb0'; g.beginPath(); g.moveTo(13, 5); g.lineTo(10, 1); g.moveTo(19, 5); g.lineTo(22, 1); g.stroke();
      out.bug.push(grab());
    }
    // Glitch: ein Wuerfel aus verrutschten Bloecken, der nicht stillhaelt
    var cols = ['#ff4fb0', '#c0186e', '#8ffcff', '#ffffff', '#5a1240'];
    for (f = 0; f < 4; f++) {
      g.clearRect(0, 0, TS, TS);
      for (i = 0; i < 16; i++) {
        var bx = 4 + (i % 4) * 6, by = 4 + ((i / 4) | 0) * 6;
        var jx = ((i * 7 + f * 3) % 5) - 2, jy = ((i * 3 + f * 5) % 3) - 1;
        g.fillStyle = cols[(i * 5 + f * 2) % cols.length];
        g.fillRect(bx + jx, by + jy, 6, 6);
      }
      g.fillStyle = '#ffffff'; g.fillRect(8 + f, 12, 3, 3); g.fillRect(20 - f, 12, 3, 3);    // zwei Augen
      g.fillStyle = 'rgba(143,252,255,.85)'; g.fillRect(0, 9 + f * 4, 32, 1);                  // Zeilensprung
      out.glitch.push(grab());
    }
    // Kernel Panic: ein roter Kern mit Ring und Zacken, der sich dreht, und ein Ausrufezeichen
    for (f = 0; f < 4; f++) {
      g.clearRect(0, 0, TS, TS);
      g.save(); g.translate(16, 16); g.rotate(f * Math.PI / 8);
      g.fillStyle = '#ff7a3d';
      for (i = 0; i < 8; i++) { g.rotate(Math.PI / 4); g.fillRect(-2, -16, 4, 6); }
      g.restore();
      g.fillStyle = '#3a0a10'; g.beginPath(); g.arc(16, 16, 11, 0, 6.283); g.fill();
      g.strokeStyle = '#ff4a3d'; g.lineWidth = 2; g.beginPath(); g.arc(16, 16, 10, 0, 6.283); g.stroke();
      g.fillStyle = f % 2 ? '#ff4a3d' : '#ff7a5a'; g.beginPath(); g.arc(16, 16, 7, 0, 6.283); g.fill();
      g.fillStyle = '#fff4ea'; g.fillRect(15, 10, 3, 8); g.fillRect(15, 20, 3, 3);
      out.panic.push(grab());
    }
    // Kaffee: ein Becher mit tuerkisem Band und Dampf
    g.clearRect(0, 0, TS, TS);
    g.strokeStyle = 'rgba(234,252,255,.7)'; g.lineWidth = 1.2;
    g.beginPath(); g.moveTo(12, 9); g.quadraticCurveTo(10, 6, 12, 3); g.moveTo(17, 9); g.quadraticCurveTo(19, 6, 17, 2); g.stroke();
    g.fillStyle = '#eaf2f1'; g.fillRect(8, 11, 14, 17);
    g.strokeStyle = '#eaf2f1'; g.lineWidth = 2.4; g.beginPath(); g.arc(23, 19, 4, -1.4, 1.4); g.stroke();
    g.fillStyle = '#17c8d6'; g.fillRect(8, 17, 14, 4);
    g.fillStyle = '#3b2416'; g.fillRect(9, 11, 12, 2);
    out.kaffee.push(grab());
    // USV-Modul: ein Akku in Bernstein mit Plus und zwei Punkten
    g.clearRect(0, 0, TS, TS);
    g.fillStyle = '#4a2f0c'; g.fillRect(6, 8, 20, 20);
    g.fillStyle = '#ffb44a'; g.fillRect(7, 9, 18, 18);
    g.fillStyle = '#4a2f0c'; g.fillRect(12, 5, 8, 4);
    g.fillStyle = '#fff4dc'; g.fillRect(14, 13, 4, 10); g.fillRect(11, 16, 10, 4);
    g.fillStyle = '#17c8d6'; g.fillRect(9, 24, 2, 2); g.fillRect(21, 24, 2, 2);
    out.usv.push(grab());
    return out;
  }

  window.__mxGames.kaltgang = {
    start: function (host) {
      var calm = host.calm, started = false, dead = false;
      var wrap = document.createElement('div');
      wrap.style.cssText = 'position:relative;flex:1 1 auto;min-height:0;display:flex';
      host.stage.appendChild(wrap);
      host.stage.style.background = '#0b1218';

      /* --- Bewegungshinweis fuer abgeschaltete Bewegung --- */
      if (calm) {
        var gate = document.createElement('div');
        gate.className = 'mxg-msg';
        gate.style.color = '#cfe5e4';
        gate.innerHTML = 'KALTGANG bewegt das ganze Bild — es ist ein Spiel aus der Ich-Perspektive.<br>' +
          'Du hast Bewegung abgeschaltet, darum fängt es nicht von selbst an.<br>' +
          'Startest du trotzdem, bleibt alles Übrige ruhig: nichts blinkt, nichts wippt.<br><br>' +
          '<button type="button" style="font:inherit;font-weight:600;padding:.5rem 1rem;cursor:pointer;' +
          'background:transparent;color:#17c8d6;border:1px solid #0fa9b5">Trotzdem starten</button>';
        wrap.appendChild(gate);
        gate.querySelector('button').addEventListener('click', function () {
          if (dead) return; wrap.removeChild(gate); boot();
        });
        host.setTitle('KALTGANG');
        host.setStatus('wartet');
        host.setHint('Nichts bewegt sich, bis du es willst.');
      }

      var cv, ctx, off, octx, img, buf, W = 0, H = 0, zbuf = null, TX = textures(), SP = sprites();
      var px = 0, py = 0, dirX = 1, dirY = 0, planeX = 0, planeY = 0.66;
      var keys = {}, open = false;
      var t0 = 0, last = 0, raf = 0, pulse = 0, now = 0;
      var bob = 0, plug = 0, map = true, look = null, offs = {}, offCount = 0;

      // Der Stand der Schicht: welches Level, welche Phase, was bisher geschah.
      var lvl = 0, L = LEVELS[0], MAP = L.map, NODES = [], MONS = [], racks = 0;
      var state = 'intro';                    // intro | play | won | dead | done
      var taken = [], foes = [], items = [], shots = [], hot = {}, hotTotal = 0;
      var uptime = 100, score = 0, total = 0, totalTime = 0, levelScore = 0, kills = 0;
      var heat = 27, modules = 0, cool = 0, zap = 0, hurtT = 0, msgT = 0, msgText = '';
      var sound = true, AC = null, overlay = null, locked = false;

      function cell(x, y) {
        if (x < 0 || y < 0 || x >= MW || y >= MH) return 2;
        var ch = MAP[y | 0].charAt(x | 0);
        var c = CODE[ch] || 0;
        if (c === 4 && open) return 0;          // offene Tuer ist begehbar
        return c;
      }
      var isOff = function (x, y) { return offs[(x | 0) + ',' + (y | 0)] === true; };
      var isHot = function (x, y) { return hot[(x | 0) + ',' + (y | 0)] === true && !isOff(x, y); };

      /* ---------- Klang, aus dem Browser selbst ----------
         Jeder Klang ist ein einzelner Oszillator mit einer kurzen Huellkurve - kein Sample, keine
         Datei. Leise mit Absicht: ein Easter Egg auf einer Visitenkarte soll nicht losbruellen.
         Der AudioContext entsteht erst beim ersten Klang, und das ist immer nach einem Klick. */
      function tone(type, f0, f1, dur, vol, delay) {
        if (!sound || dead) return;
        try { AC = AC || new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { return; }
        var t = AC.currentTime + (delay || 0), o = AC.createOscillator(), g = AC.createGain();
        o.type = type; o.frequency.setValueAtTime(f0, t); o.frequency.exponentialRampToValueAtTime(Math.max(20, f1), t + dur);
        g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(vol, t + 0.01); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
        o.connect(g); g.connect(AC.destination); o.start(t); o.stop(t + dur + 0.02);
      }
      function sfx(k) {
        if (k === 'zap') tone('square', 1200, 260, 0.11, 0.035);
        else if (k === 'hit') tone('triangle', 420, 160, 0.09, 0.05);
        else if (k === 'kill') { tone('sawtooth', 300, 40, 0.28, 0.04); tone('square', 900, 1400, 0.08, 0.025, 0.05); }
        else if (k === 'pick') { tone('sine', 660, 990, 0.12, 0.06); tone('sine', 990, 1320, 0.12, 0.05, 0.08); }
        else if (k === 'hurt') tone('sawtooth', 170, 70, 0.22, 0.06);
        else if (k === 'node') tone('sine', 520, 780, 0.18, 0.06);
        else if (k === 'switch') tone('square', 240, 120, 0.06, 0.03);
        else if (k === 'door') { tone('sine', 220, 440, 0.35, 0.05); tone('sine', 330, 660, 0.35, 0.04, 0.12); }
        else if (k === 'shot') tone('square', 180, 110, 0.2, 0.03);
        else if (k === 'win') { [523, 659, 784, 1046].forEach(function (f, i) { tone('triangle', f, f, 0.18, 0.05, i * 0.11); }); }
        else if (k === 'down') { tone('sawtooth', 400, 50, 0.9, 0.06); }
      }

      /* ---------- Monitore ----------
         Jeder Monitor hat eine eigene kleine Leinwand. Darauf wird das Bild
         gezeichnet und danach als Textur ausgelesen. Das ist der Grund, warum
         sie echte Werte zeigen koennen und nicht nur ein gemaltes Muster:
         gezeichnet wird waehrend des Spiels, aus denselben Variablen, die auch
         die Anzeige oben fuellen. */
      var MS = 64;
      function makeMon(m) {
        m.cv = document.createElement('canvas'); m.cv.width = MS; m.cv.height = MS;
        m.c2 = m.cv.getContext('2d', { willReadFrequently: true });
        m.tex = new Uint32Array(MS * MS);
        m.next = 0; m.hist = [];
        return m;
      }
      function tempNow() { return L.goal === 'heat' ? heat : 18 + (racks - offCount) * 0.42 + (L.dark ? 6 : 0); }
      function drawMon(m) {
        var g = m.c2;
        g.clearRect(0, 0, MS, MS);
        // Fuss und Saeule
        g.fillStyle = '#12222a'; g.fillRect(24, 56, 16, 3); g.fillRect(30, 50, 4, 7);
        // Gehaeuse
        g.fillStyle = '#16262e'; g.fillRect(6, 8, 52, 42);
        g.fillStyle = '#0c161c'; g.fillRect(8, 10, 48, 36);
        // Schirm
        var sx = 9, sy = 11, sw = 46, sh = 34;
        g.fillStyle = '#04222a'; g.fillRect(sx, sy, sw, sh);
        g.save(); g.beginPath(); g.rect(sx, sy, sw, sh); g.clip();
        g.font = '7px monospace'; g.textBaseline = 'top';

        if (m.kind === 'last') {
          // Wie viele Schraenke laufen - und ein Balken je Reihe
          var on = racks - offCount;
          g.fillStyle = '#17c8d6'; g.fillText('LAST', sx + 2, sy + 1);
          g.fillStyle = '#8fe6ee'; g.fillText(on + '/' + racks, sx + 26, sy + 1);
          for (var i = 0; i < 12; i++) {
            var full = (i / 12) < (on / racks);
            g.fillStyle = full ? '#0fa9b5' : '#0a3a42';
            var hgt = 3 + ((i * 5) % 7);
            g.fillRect(sx + 2 + i * 3.6, sy + 30 - hgt, 2.6, hgt);
          }
          g.fillStyle = '#5fd3de'; g.fillText('KW ' + (2.4 + on * 0.35).toFixed(1), sx + 2, sy + 10);
        } else if (m.kind === 'temp') {
          // Die Temperatur haengt an der Last (und in Halle B an den heissen Schraenken):
          // schaltet man ab, faellt die Kurve. Das ist der Sinn des Schalters - man sieht ihn hier.
          var tC = tempNow();
          if (m.hist.length > 40) m.hist.shift();
          m.hist.push(tC);
          g.fillStyle = '#17c8d6'; g.fillText(L.goal === 'heat' ? 'HALLE B' : 'KALTGANG', sx + 2, sy + 1);
          g.fillStyle = tC > 32 ? '#ff8a6a' : (tC > 26 ? '#ffb4a2' : '#8fe6ee');
          g.fillText(tC.toFixed(1) + 'C', sx + 2, sy + 9);
          g.strokeStyle = tC > 32 ? '#ff8a6a' : '#0fa9b5'; g.lineWidth = 1; g.beginPath();
          for (var k = 0; k < m.hist.length; k++) {
            var vx = sx + 2 + k * (sw - 4) / 40;
            var vy = sy + 31 - clamp((m.hist[k] - 16) * 0.6, 0, 13);
            if (k === 0) g.moveTo(vx, vy); else g.lineTo(vx, vy);
          }
          g.stroke();
        } else if (m.kind === 'auftrag') {
          var o = objective();
          g.fillStyle = '#17c8d6'; g.fillText('AUFTRAG', sx + 2, sy + 1);
          for (var j = 0; j < o.total; j++) {
            g.fillStyle = j < o.done ? '#17c8d6' : '#0a3a42';
            g.fillRect(sx + 2 + j * (o.total > 4 ? 6 : 8), sy + 11, o.total > 4 ? 5 : 6, 6);
          }
          g.fillStyle = '#8fe6ee'; g.fillText(o.short, sx + 2, sy + 20);
          g.fillStyle = open ? '#17c8d6' : '#4a7c86';
          g.fillText(open ? 'TUER OFFEN' : 'TUER ZU', sx + 2, sy + 27);
        } else {
          // Ein mitlaufendes Protokoll. Ohne Bewegung steht es still.
          var lines = L.log;
          var shift = calm ? 0 : ((now * 0.6) | 0);
          for (var q = 0; q < 4; q++) {
            var li = lines[(q + shift) % lines.length];
            g.fillStyle = q === 0 ? '#17c8d6' : '#4e9aa4';
            g.fillText(li, sx + 2, sy + 1 + q * 8);
          }
        }
        // Glasreflex: ein heller Streifen schraeg ueber den Schirm
        g.globalAlpha = 0.06; g.fillStyle = '#ffffff';
        g.beginPath(); g.moveTo(sx, sy + 26); g.lineTo(sx + sw, sy + 4);
        g.lineTo(sx + sw, sy); g.lineTo(sx, sy + 20); g.closePath(); g.fill();
        g.globalAlpha = 1;
        g.restore();
        // Rahmenlicht
        g.strokeStyle = '#0fa9b5'; g.lineWidth = 1; g.strokeRect(6.5, 8.5, 51, 41);

        var d = g.getImageData(0, 0, MS, MS).data;
        var u = new Uint32Array(d.buffer);
        m.tex.set(u);
      }

      /* Entfernung nimmt Helligkeit UND Farbe: was weit weg ist, verschwindet in einem
         dunklen Tuerkis. Das gibt dem Gang Tiefe, ohne eine einzige Textur mehr zu kosten. */
      var FR = 10, FG = 44, FB = 52;
      function shade(c, f) {
        var m = 1 - f;
        var r = (c & 255) * f + FR * m, g = ((c >>> 8) & 255) * f + FG * m, b = ((c >>> 16) & 255) * f + FB * m;
        return ((255 << 24) | ((b | 0) << 16) | ((g | 0) << 8) | (r | 0)) >>> 0;
      }
      function blend(i, r, g, b, a) {
        var o = buf[i], ia = 1 - a;
        buf[i] = ((255 << 24) |
          ((((o >>> 16) & 255) * ia + b * a) << 16) |
          ((((o >>> 8) & 255) * ia + g * a) << 8) |
          (((o & 255) * ia + r * a))) >>> 0;
      }
      // Ein heisser Schrank gluehlt: seine Farbe wird in Richtung Bernstein gezogen.
      function warm(c, a) {
        var r = (c & 255), g = (c >>> 8) & 255, b = (c >>> 16) & 255;
        return C(r + (255 - r) * a, g + (140 - g) * a * 0.8, b * (1 - a * 0.7));
      }

      /* Der Takt eines Leuchtpunktes haengt an seiner Position UND an seiner
         Nummer im Rack. Nichts laeuft dadurch synchron - genau das ist der
         Unterschied zwischen "es flackert im Raum" und "der Bildschirm blitzt". */
      function ledOn(mx, my, id) {
        if (isOff(mx, my)) return 0;
        var h = (mx * 73856093 ^ my * 19349663 ^ id * 83492791) >>> 0;
        var phase = (h % 1000) / 1000;
        var speed = 0.6 + (h % 7) * 0.22;
        if (calm) return (h % 5) === 0 ? 0.55 : 1;         // steht still, aber lebt optisch
        var s = Math.sin((now * speed + phase * 6.283) * 1.7);
        if ((h % 11) === 0) return s > 0.86 ? 1 : 0.28;    // einer je Rack pickt seltener
        return s > 0 ? 1 : 0.42;
      }

      /* ---------- Licht ----------
         Alles, was man sieht, wird nach Entfernung UND nach Naehe zur naechsten Leuchte
         abgeblendet: Lichtbaender ueber jeder vierten Plattenreihe, ein Teil davon tot.
         Im Notstrom-Level brennt nur das Notlicht - dieselbe Rechnung, viel weniger Licht,
         und die Baender gluehen bernstein statt weiss. */
      function lampAlive(row) { return ((row * 2654435761) >>> 0) % 5 !== 0; }
      function lampPool(wy) {
        var t = (wy - 1.5) / 4, fr = t - Math.floor(t);
        var d = Math.min(fr, 1 - fr) * 4;                  // 0 = direkt darunter, 2 = dazwischen
        var row = Math.round((wy - 1.5) / 4) * 4 + 1;
        var v = (1.06 - 0.15 * d) * (lampAlive(row) ? 1 : 0.62);
        return L.dark ? v * (0.42 + (d < 0.6 ? 0.18 : 0)) : v;
      }

      function size() {
        var w = wrap.clientWidth, h = wrap.clientHeight;
        if (w < 2 || h < 2) return false;
        cv.width = Math.round(w); cv.height = Math.round(h);
        cv.style.width = '100%'; cv.style.height = '100%';
        // Innen wird klein gerechnet und gross gezeichnet: das ist billig, sieht
        // absichtlich grob aus und laeuft auch auf einem Telefon fluessig.
        W = Math.max(160, Math.min(420, Math.round(w / (w < 700 ? 2.6 : 3.2))));
        H = Math.max(100, Math.round(W * h / w));
        off = off || document.createElement('canvas');
        off.width = W; off.height = H;
        octx = off.getContext('2d');
        img = octx.createImageData(W, H);
        buf = new Uint32Array(img.data.buffer);
        zbuf = new Float32Array(W);
        ctx.imageSmoothingEnabled = false;
        return true;
      }

      /* ---------- Boden und Decke ----------
         Fuer jede Zeile unter dem Horizont steht die Entfernung fest, und daraus laufen zwei
         Zeiger ueber die Karte. Das kostet einen Blick pro Bildpunkt, bringt aber den ganzen
         Raum: man sieht, dass man ueber Platten laeuft. */
      function floorCeil(horizon) {
        var texF = TX.floor, texC = TX.ceil;
        for (var y = 0; y < H; y++) {
          var isFloor = y > horizon;
          var p = isFloor ? (y - horizon) : (horizon - y);
          if (p < 1) p = 1;
          var posZ = 0.5 * H;
          var rowD = posZ / p;
          var rdx0 = dirX - planeX, rdy0 = dirY - planeY;
          var rdx1 = dirX + planeX, rdy1 = dirY + planeY;
          var stepX = rowD * (rdx1 - rdx0) / W, stepY = rowD * (rdy1 - rdy0) / W;
          var fx = px + rowD * rdx0, fy = py + rowD * rdy0;
          var fRow = 0.40 + 0.60 / (1 + rowD * 0.20);
          var o = y * W;
          for (var x = 0; x < W; x++) {
            var cx = fx | 0, cy = fy | 0;
            var tx = ((fx - cx) * 64) | 0, ty = ((fy - cy) * 64) | 0;
            var c;
            var f = fRow * lampPool(fy);
            if (isFloor) {
              c = texF[(ty & 63) * 64 + (tx & 63)];
            } else {
              c = texC[(ty & 63) * 64 + (tx & 63)];
              // Licht kommt aus einem durchgehenden Band ueber jeder vierten Plattenreihe - so
              // haengt es in einer Halle wirklich und laeuft mit dem Gang mit.
              if ((cy & 3) === 1) {
                var b2 = ty & 63, alive = lampAlive(cy);
                if (L.dark) {
                  if (b2 > 29 && b2 < 35 && alive && (cx % 3 === 0)) { c = C(196, 120, 46); f = 0.85; }
                } else {
                  if (b2 > 27 && b2 < 37) c = alive ? C(150, 214, 222) : C(30, 52, 58);
                  else if (b2 > 22 && b2 < 42) c = alive ? C(64, 126, 136) : C(24, 44, 50);
                  if (alive) f = 1;                        // die Leuchte selbst wird nicht abgeblendet
                }
              }
            }
            buf[o + x] = shade(c, f > 1 ? 1 : f);
            fx += stepX; fy += stepY;
          }
        }
      }

      /* ---------- Ein Billboard ----------
         Monitore, Stoerungen und Gegenstaende stehen alle gleich im Raum: zur Kamera gedreht,
         am z-Puffer geprueft (verschwinden hinter Racks), mit Durchsicht. lift hebt den Fuss
         vom Boden (0 = steht), hgt ist die Hoehe in Wandhoehen. */
      function billboard(tex, size, trX, trY, hgt, wid, lift, bright, white, alpha) {
        var scX = ((W / 2) * (1 + trX / trY)) | 0;
        var sh = Math.abs((H / trY) * hgt) | 0, sw = Math.abs((H / trY) * wid) | 0;
        if (sw < 2 || sh < 2) return;
        var bottom = ((H / 2) + (H / trY) * (0.5 - lift)) | 0;
        var top = bottom - sh, x0 = scX - (sw >> 1);
        var xa = x0 < 0 ? 0 : x0, xb = x0 + sw > W ? W : x0 + sw;
        var ya = top < 0 ? 0 : top, yb = bottom > H ? H : bottom;
        for (var x = xa; x < xb; x++) {
          if (trY >= zbuf[x]) continue;
          var u = (((x - x0) * size / sw) | 0); if (u >= size) u = size - 1;
          for (var y = ya; y < yb; y++) {
            var v = (((y - top) * size / sh) | 0); if (v >= size) v = size - 1;
            var c = tex[v * size + u];
            if ((c >>> 24) < 40) continue;
            var lit = ((c & 255) + ((c >>> 8) & 255) + ((c >>> 16) & 255)) > 330;
            if (white > 0) c = C((c & 255) + (255 - (c & 255)) * white, ((c >>> 8) & 255) + (255 - ((c >>> 8) & 255)) * white, ((c >>> 16) & 255) + (255 - ((c >>> 16) & 255)) * white);
            var col = shade(c, lit ? Math.min(1, bright + 0.4) : bright);
            if (alpha < 1) blend(y * W + x, col & 255, (col >>> 8) & 255, (col >>> 16) & 255, alpha);
            else buf[y * W + x] = col;
          }
        }
      }

      function render() {
        var x, y, i;
        var horizon = (H / 2) | 0;
        floorCeil(horizon);

        for (x = 0; x < W; x++) {
          var camX = 2 * x / W - 1;
          var rdx = dirX + planeX * camX, rdy = dirY + planeY * camX;
          var mx = px | 0, my = py | 0;
          var ddx = rdx === 0 ? 1e30 : Math.abs(1 / rdx), ddy = rdy === 0 ? 1e30 : Math.abs(1 / rdy);
          var sx, sy, sdx, sdy, hit = 0, side = 0, tile = 0;
          if (rdx < 0) { sx = -1; sdx = (px - mx) * ddx; } else { sx = 1; sdx = (mx + 1 - px) * ddx; }
          if (rdy < 0) { sy = -1; sdy = (py - my) * ddy; } else { sy = 1; sdy = (my + 1 - py) * ddy; }
          var guard = 0;
          while (!hit && guard++ < 64) {
            if (sdx < sdy) { sdx += ddx; mx += sx; side = 0; } else { sdy += ddy; my += sy; side = 1; }
            tile = cell(mx, my);
            if (tile > 0) hit = 1;
          }
          if (!hit) { zbuf[x] = 1e30; continue; }
          var pd = side === 0 ? (sdx - ddx) : (sdy - ddy);
          if (pd < 0.02) pd = 0.02;
          zbuf[x] = pd;
          var lh = (H / pd) | 0;
          var ds = (-lh / 2 + H / 2) | 0, de = (lh / 2 + H / 2) | 0;
          var y0 = ds < 0 ? 0 : ds, y1 = de > H ? H : de;
          var wallX = side === 0 ? py + pd * rdy : px + pd * rdx;
          wallX -= wallX | 0;
          var tx = (wallX * 64) | 0;
          if ((side === 0 && rdx > 0) || (side === 1 && rdy < 0)) tx = 63 - tx;
          var rk = null;
          if (tile === 1) {
            // Welche der drei Fronten dieser Schrank zeigt, folgt aus seiner Position: gleiche
            // Zelle, gleiche Front - der Raum bleibt also wiedererkennbar und flackert nicht.
            rk = TX.racks[((mx * 7 + my * 13) >>> 0) % 3];
          }
          var tex = rk ? rk.tex : (tile === 2 ? TX.wall : (tile === 3 ? TX.grate : TX.door));
          var f = 0.46 + 0.54 / (1 + pd * 0.15); if (side === 1) f *= 0.86;
          f *= lampPool(py + pd * rdy);
          if (f > 1) f = 1;
          var rack = tile === 1, dark = rack && isOff(mx, my);
          if (dark) f *= 0.62;                        // ein toter Schrank ist auch dunkler
          var glowH = rack && isHot(mx, my) ? (calm ? 0.32 : 0.26 + 0.1 * Math.sin(now * 2.2 + mx + my)) : 0;
          if (glowH) f = Math.max(f, 0.62);            // ein heisser Schrank leuchtet aus sich selbst
          var aimed = (look && look.x === mx && look.y === my) ? 1.14 : 1;
          var stp = 64 / lh, tp = (y0 - ds) * stp;
          var gradTop = 1.10, gradRange = 0.26, invLh = 1 / (lh || 1);
          for (y = y0; y < y1; y++) {
            var ty = tp | 0; if (ty > 63) ty = 63; tp += stp;
            var ti = ty * 64 + tx;
            var fp = f * (gradTop - gradRange * ((y - ds) * invLh));
            if (fp > 1) fp = 1; else if (fp < 0.05) fp = 0.05;
            var c = tex[ti];
            if (rk) {
              var id = rk.led[ti];
              if (id) {
                var amber = id > 127 || glowH > 0;
                var on = ledOn(mx, my, id > 127 ? (id - 128) : id);
                if (amber) {
                  c = on > 0.9 ? C(255, 186, 90) : (on > 0.3 ? C(190, 130, 40) : C(48, 34, 16));
                } else {
                  c = on > 0.9 ? C(23, 200, 214) : (on > 0.3 ? C(15, 169, 181) : C(14, 40, 48));
                }
                if (on > 0.9) { buf[y * W + x] = shade(c, Math.min(1, fp * 1.9)); continue; }
              }
              if (glowH) c = warm(c, glowH);
            }
            buf[y * W + x] = shade(c, Math.min(1, fp * aimed));
          }
        }

        /* ---------- Was im Raum steht ----------
           Knoten, Monitore, Gegenstaende, Stoerungen und Schuesse - von hinten nach vorn. */
        var order = [];
        function put(ox, oy, kind, ref) { var dx = ox - px, dy = oy - py; order.push([dx * dx + dy * dy, kind, ref, ox, oy]); }
        for (i = 0; i < NODES.length; i++) if (!taken[i]) put(NODES[i][0], NODES[i][1], 'n', i);
        for (i = 0; i < MONS.length; i++) put(MONS[i].x, MONS[i].y, 'm', MONS[i]);
        for (i = 0; i < items.length; i++) if (!items[i].gone) put(items[i].x, items[i].y, 'i', items[i]);
        for (i = 0; i < foes.length; i++) if (!foes[i].dead || foes[i].fade > 0) put(foes[i].x, foes[i].y, 'f', foes[i]);
        for (i = 0; i < shots.length; i++) put(shots[i].x, shots[i].y, 's', shots[i]);
        order.sort(function (a, b) { return b[0] - a[0]; });
        var invDet = 1 / (planeX * dirY - dirX * planeY);

        for (var k = 0; k < order.length; k++) {
          var it = order[k];
          var rx = it[3] - px, ry = it[4] - py;
          var trX = invDet * (dirY * rx - dirX * ry);
          var trY = invDet * (-planeY * rx + planeX * ry);
          if (trY <= 0.15) continue;
          var light = (0.40 + 0.60 / (1 + trY * 0.14)) * (L.dark ? 0.7 : 1); if (light > 1) light = 1;

          if (it[1] === 'm') {
            // Ein Monitor STEHT: die Unterkante liegt auf dem Boden. Rechnet man sie hoeher,
            // schwebt das Geraet - und genau daran erkennt man einen aufgeklebten Sprite.
            billboard(it[2].tex, MS, trX, trY, 0.78, 0.78, 0, light, 0, 1);
            continue;
          }
          if (it[1] === 'i') {
            var itm = it[2], hov = calm ? 0.05 : 0.05 + 0.03 * Math.sin(now * 2.4 + itm.x);
            billboard(SP[itm.t][0], TS, trX, trY, 0.32, 0.32, hov, Math.max(light, 0.75), 0, 1);
            continue;
          }
          if (it[1] === 'f') {
            var e = it[2], spec = FOE[e.t], frames = SP[e.t];
            var fr = calm ? 0 : ((now * (e.t === 'bug' ? 8 : 5) + e.seed) | 0) % frames.length;
            var lift = spec.ground ? 0 : 0.18 + (calm ? 0 : 0.05 * Math.sin(now * 3 + e.seed));
            var a = 1, scale = 1;
            if (e.dead) { a = e.fade / 0.45; scale = 0.6 + 0.4 * a; }
            billboard(frames[fr], TS, trX, trY, spec.h * scale, spec.w * scale, lift, Math.max(light, 0.8),
                      e.hitT > 0 ? 0.6 : 0, a);
            continue;
          }
          var scX = ((W / 2) * (1 + trX / trY)) | 0;
          if (it[1] === 's') {
            // Ein Paket der Kernel Panic: eine gluehende Kugel in Hueft-Hoehe
            var rs = Math.abs((H / trY) * 0.16) | 0; if (rs < 1) continue;
            var cy0 = (H / 2 + (H / trY) * 0.05) | 0;
            for (x = scX - rs; x < scX + rs; x++) {
              if (x < 0 || x >= W || trY >= zbuf[x]) continue;
              for (y = cy0 - rs; y < cy0 + rs; y++) {
                if (y < 0 || y >= H) continue;
                var q = ((x - scX) * (x - scX) + (y - cy0) * (y - cy0)) / (rs * rs);
                if (q > 1) continue;
                blend(y * W + x, 255, 140 - q * 60, 60, (1 - q) * 0.95);
              }
            }
            continue;
          }
          // Ein Knoten: ein weiches Leuchten in Tuerkis, das langsam atmet
          var sh2 = Math.abs((H / trY) * 0.55) | 0, sw = (sh2 * 0.34) | 0;
          if (sw < 1) continue;
          var vy = (H / 2 + (H / trY) * 0.20) | 0;
          var x0 = scX - (sw >> 1), x1 = x0 + sw, ytop = vy - sh2, ybot = vy;
          var glow = 0.72 + (calm ? 0 : 0.14 * Math.sin(pulse));
          for (x = x0 < 0 ? 0 : x0; x < (x1 > W ? W : x1); x++) {
            if (trY >= zbuf[x]) continue;
            var uu = (x - x0) / sw * 2 - 1;
            for (y = ytop < 0 ? 0 : ytop; y < (ybot > H ? H : ybot); y++) {
              var vv = (y - ytop) / (ybot - ytop);
              var rr = uu * uu + (vv * 2 - 1) * (vv * 2 - 1) * 0.16;
              if (rr > 1) continue;
              var al = (1 - rr) * glow;
              blend(y * W + x, 23, 200, 214, al > 1 ? 1 : al);
            }
          }
        }

        octx.putImageData(img, 0, 0);
        beam();
        hand();
        if (map) minimap();
        ctx.drawImage(off, 0, 0, W, H, 0, 0, cv.width, cv.height);
        hud();
      }

      /* ---------- Der Patch-Strahl ----------
         Vom Stecker in der Hand zur Bildmitte, kurz und hell. Auf der kleinen Leinwand, damit er
         dieselbe Koernung hat wie alles andere. */
      function beam() {
        if (zap <= 0) return;
        var g = octx, s = H / 112, a = zap / 0.14;
        var sx = W * 0.5 - 3 * s, sy = H - 26 * s;
        g.save();
        g.globalAlpha = 0.85 * a; g.strokeStyle = '#8ffcff'; g.lineWidth = Math.max(1, 1.6 * s); g.lineCap = 'round';
        g.beginPath(); g.moveTo(sx, sy);
        var mx = W / 2, my = H / 2, segs = 6;
        for (var i = 1; i <= segs; i++) {
          var t = i / segs, j = (i < segs && !calm) ? ((Math.random() - 0.5) * 3 * s) : 0;
          g.lineTo(sx + (mx - sx) * t + j, sy + (my - sy) * t + j * 0.6);
        }
        g.stroke();
        g.globalAlpha = 0.35 * a; g.lineWidth = Math.max(2, 4 * s); g.strokeStyle = '#17c8d6'; g.stroke();
        g.globalAlpha = 0.7 * a; g.fillStyle = '#eafcff';
        g.beginPath(); g.arc(mx, my, Math.max(1.5, 2.6 * s), 0, 6.283); g.fill();
        g.restore();
      }

      /* ---------- Die Hand mit dem Patchkabel ----------
         Gezeichnet wird auf die KLEINE Leinwand, nicht auf die grosse: dadurch
         hat die Hand dieselbe grobe Koernung wie der Rest und klebt nicht als
         saubere Vektorgrafik auf einem Pixelbild. Sie wippt beim Gehen, beim
         Schalten schiebt sie den Stecker vor, beim Patchen zuckt sie zurueck. */
      function hand() {
        var g = octx;
        var s = H / 112;
        var bx = W * 0.5 - 7 * s, by = H - 10 * s;
        var wob = calm ? 0 : Math.sin(bob) * 2.4 * s;
        var wob2 = calm ? 0 : Math.cos(bob * 2) * 1.5 * s;
        var push = plug > 0 ? Math.sin(plug * Math.PI) : 0;  // 0..1..0
        var kick = zap > 0 && !calm ? (zap / 0.14) * 3 * s : 0;
        g.save();
        g.translate(bx + wob2, by + wob - push * 7 * s + kick);

        // Kabel: haengt aus dem Bild herein, in einer weichen Schlaufe zur Hand
        g.lineCap = 'round'; g.lineJoin = 'round';
        g.strokeStyle = '#0b6f78'; g.lineWidth = 3.2 * s;
        g.beginPath();
        g.moveTo(-40 * s, 40 * s);
        g.bezierCurveTo(-34 * s, 4 * s, -6 * s, 16 * s, 2 * s, -6 * s);
        g.stroke();
        g.strokeStyle = '#17c8d6'; g.lineWidth = 1.1 * s;   // Glanzkante auf dem Kabel
        g.beginPath();
        g.moveTo(-40 * s, 39 * s);
        g.bezierCurveTo(-34 * s, 3 * s, -6 * s, 15 * s, 2 * s, -7 * s);
        g.stroke();

        // Unterarm im Aermel, mit Bund und einer Falte
        g.fillStyle = '#123c44';
        g.beginPath();
        g.moveTo(7 * s, 46 * s); g.lineTo(31 * s, 46 * s);
        g.lineTo(25 * s, 7 * s); g.lineTo(5 * s, 11 * s);
        g.closePath(); g.fill();
        g.fillStyle = '#0d2b31';
        g.fillRect(6 * s, 9 * s, 21 * s, 3.4 * s);           // Aermelbund
        g.fillStyle = '#17555e';
        g.beginPath();                                       // Falte im Stoff
        g.moveTo(10 * s, 44 * s); g.lineTo(14 * s, 44 * s); g.lineTo(11 * s, 14 * s);
        g.closePath(); g.fill();

        // Der Handruecken
        g.fillStyle = '#c08a72';
        g.beginPath();
        g.moveTo(4 * s, 13 * s);
        g.quadraticCurveTo(-5 * s, 4 * s, 1 * s, -5 * s);
        g.quadraticCurveTo(11 * s, -12 * s, 22 * s, -4 * s);
        g.quadraticCurveTo(28 * s, 3 * s, 25 * s, 13 * s);
        g.closePath(); g.fill();

        // Vier Finger, quer ueber die ganze Faust, nach unten kuerzer
        for (var fi = 0; fi < 4; fi++) {
          var fy = (-3.5 + fi * 4.3) * s;
          var fx0 = (0.5 + fi * 0.7) * s, fx1 = (25 - fi * 1.9) * s;
          g.fillStyle = ['#c9917a', '#c28a73', '#bb836d', '#b47c67'][fi];
          g.beginPath();
          g.moveTo(fx0, fy + 4 * s);
          g.quadraticCurveTo(fx0 - 2.6 * s, fy + 2 * s, fx0, fy);
          g.lineTo(fx1, fy);
          g.quadraticCurveTo(fx1 + 3.2 * s, fy + 2 * s, fx1, fy + 4 * s);
          g.closePath(); g.fill();
          g.fillStyle = '#8a5b46';
          g.fillRect(fx0 - 1 * s, fy + 3.7 * s, fx1 - fx0 + 3 * s, 1.1 * s);
          g.fillStyle = '#d9a88f';
          g.fillRect(fx0 + 1 * s, fy + 0.6 * s, fx1 - fx0 - 2 * s, 0.9 * s);
          g.fillStyle = '#d6a58c';
          g.beginPath(); g.arc(fx0 + 0.6 * s, fy + 2 * s, 1.7 * s, 0, 6.283); g.fill();
        }
        // Daumen
        g.fillStyle = '#dcac93';
        g.beginPath();
        g.moveTo(24 * s, 12 * s);
        g.quadraticCurveTo(26 * s, 2 * s, 16 * s, -2.5 * s);
        g.quadraticCurveTo(11 * s, -4 * s, 10 * s, 0.5 * s);
        g.quadraticCurveTo(14 * s, 4 * s, 19 * s, 12 * s);
        g.closePath(); g.fill();
        g.fillStyle = '#b07f68';
        g.beginPath();
        g.ellipse(12.6 * s, -0.6 * s, 2.6 * s, 1.7 * s, -0.5, 0, 6.283);
        g.fill();

        // Der Stecker: Knickschutz, Gehaeuse, Rastnase, acht Kontakte
        g.fillStyle = '#0a5a63';
        g.beginPath();
        g.moveTo(-1 * s, -5 * s); g.lineTo(10 * s, -5 * s);
        g.lineTo(8.5 * s, -9 * s); g.lineTo(0.5 * s, -9 * s);
        g.closePath(); g.fill();
        g.fillStyle = '#083f47';
        for (var ri = 0; ri < 3; ri++) g.fillRect(0 * s, (-5.8 - ri * 1.2) * s, 9 * s, 0.6 * s);
        g.fillStyle = '#0e343b';
        g.fillRect(-1 * s, -18 * s, 11 * s, 9.4 * s);
        g.fillStyle = '#14505a';
        g.fillRect(-1 * s, -18 * s, 1.6 * s, 9.4 * s);
        g.fillStyle = '#0b6f78';
        g.fillRect(2.5 * s, -20.6 * s, 4 * s, 2.8 * s);
        g.fillStyle = '#8fe6ee';
        for (var i = 0; i < 8; i++) g.fillRect((-0.2 + i * 1.3) * s, -16.4 * s, 0.8 * s, 3 * s);
        g.fillStyle = '#062a30';
        g.fillRect(-1 * s, -12.6 * s, 11 * s, 0.8 * s);

        // Beim Schalten oder Patchen glimmt der Stecker kurz auf.
        var glowA = Math.max(push, zap > 0 ? zap / 0.14 : 0);
        if (glowA > 0.05) {
          g.globalAlpha = 0.5 * glowA;
          g.fillStyle = '#17c8d6';
          g.beginPath(); g.arc(4 * s, -15 * s, 9 * s, 0, 6.283); g.fill();
          g.globalAlpha = 1;
        }
        g.restore();
      }

      /* ---------- Karte oben rechts ----------
         Klein, ruhig, und sie zeigt genau das, was man sonst im Kopf behalten
         muesste: wo man steht, wohin man schaut, welche Schraenke aus oder heiss
         sind, was noch fehlt, wo die Stoerungen sind und wo die Tuer ist. */
      function minimap() {
        var g = octx;
        var cellPx = Math.max(2, Math.round(H / 115));
        var w = MW * cellPx, pad = Math.round(cellPx * 1.2);
        var ox = W - w - pad, oy = pad;
        g.save();
        g.globalAlpha = 0.72;
        g.fillStyle = '#06131a'; g.fillRect(ox - 2, oy - 2, w + 4, w + 4);
        g.strokeStyle = 'rgba(15,169,181,.55)'; g.lineWidth = 1;
        g.strokeRect(ox - 2.5, oy - 2.5, w + 5, w + 5);
        for (var y = 0; y < MH; y++) {
          var row = MAP[y];
          for (var x = 0; x < MW; x++) {
            var ch = row.charAt(x), col = null;
            if (ch === '#') col = '#1b3038';
            else if (ch === 'g') col = '#26525d';
            else if (ch === 'r' || ch === 'h') col = isOff(x, y) ? '#14262d' : (isHot(x, y) ? '#c8862e' : '#0a5a63');
            else if (ch === 'D') col = open ? '#17c8d6' : '#0a5b63';
            if (col) { g.fillStyle = col; g.fillRect(ox + x * cellPx, oy + y * cellPx, cellPx, cellPx); }
          }
        }
        function dot(x, y, c, r) { g.fillStyle = c; g.fillRect(ox + x * cellPx - cellPx * r, oy + y * cellPx - cellPx * r, cellPx * r * 2, cellPx * r * 2); }
        for (var i = 0; i < NODES.length; i++) if (!taken[i]) dot(NODES[i][0], NODES[i][1], '#8fe6ee', 0.4);
        for (i = 0; i < MONS.length; i++) dot(MONS[i].x, MONS[i].y, '#4e9aa4', 0.3);
        for (i = 0; i < items.length; i++) if (!items[i].gone) dot(items[i].x, items[i].y, items[i].t === 'usv' ? '#ffb44a' : '#eaf2f1', 0.35);
        for (i = 0; i < foes.length; i++) if (!foes[i].dead) dot(foes[i].x, foes[i].y, '#ff4fb0', foes[i].t === 'panic' ? 0.55 : 0.32);
        // Blickkegel und Standpunkt
        var mx0 = ox + px * cellPx, my0 = oy + py * cellPx, Lc = cellPx * 3.4;
        g.globalAlpha = 0.30; g.fillStyle = '#17c8d6';
        g.beginPath(); g.moveTo(mx0, my0);
        g.lineTo(mx0 + (dirX - planeX * 0.7) * Lc, my0 + (dirY - planeY * 0.7) * Lc);
        g.lineTo(mx0 + (dirX + planeX * 0.7) * Lc, my0 + (dirY + planeY * 0.7) * Lc);
        g.closePath(); g.fill();
        g.globalAlpha = 1; g.fillStyle = '#eafcff';
        g.beginPath(); g.arc(mx0, my0, Math.max(1.4, cellPx * 0.45), 0, 6.283); g.fill();
        g.restore();
      }

      /* ---------- Die Anzeige ----------
         Auf der GROSSEN Leinwand, damit Schrift scharf bleibt: Level und Auftrag oben links,
         Uptime und Punkte unten links, ein kleines Fadenkreuz in der Mitte. */
      function hud() {
        var g = ctx, cw = cv.width, ch = cv.height, s = Math.max(1, Math.min(cw, ch * 1.6) / 900);
        g.save();
        // Treffer: der Rand wird kurz rot - nicht in der ruhigen Fassung, dort sinkt nur der Balken.
        if (hurtT > 0 && !calm) {
          var gr = g.createRadialGradient(cw / 2, ch / 2, Math.min(cw, ch) * 0.35, cw / 2, ch / 2, Math.max(cw, ch) * 0.75);
          gr.addColorStop(0, 'rgba(255,40,80,0)'); gr.addColorStop(1, 'rgba(255,40,80,' + (0.35 * hurtT / 0.45).toFixed(3) + ')');
          g.fillStyle = gr; g.fillRect(0, 0, cw, ch);
        }
        if (state === 'play') {
          g.strokeStyle = 'rgba(143,252,255,.85)'; g.lineWidth = 1.5 * s;
          var c = 6 * s, gp = 3 * s;
          g.beginPath();
          g.moveTo(cw / 2 - c - gp, ch / 2); g.lineTo(cw / 2 - gp, ch / 2);
          g.moveTo(cw / 2 + gp, ch / 2); g.lineTo(cw / 2 + gp + c, ch / 2);
          g.moveTo(cw / 2, ch / 2 - c - gp); g.lineTo(cw / 2, ch / 2 - gp);
          g.moveTo(cw / 2, ch / 2 + gp); g.lineTo(cw / 2, ch / 2 + gp + c);
          g.stroke();
        }
        var o = objective(), pad = 14 * s;
        g.textBaseline = 'top';
        g.fillStyle = 'rgba(6,19,26,.62)';
        g.fillRect(pad - 6 * s, pad - 6 * s, 270 * s, (L.goal === 'heat' ? 62 : 46) * s);
        g.font = '700 ' + (11 * s) + 'px system-ui,sans-serif'; g.fillStyle = '#17c8d6';
        g.fillText(L.sub.toUpperCase() + ' · ' + L.name, pad, pad);
        g.font = '500 ' + (13 * s) + 'px system-ui,sans-serif'; g.fillStyle = '#eafcff';
        g.fillText(o.text, pad, pad + 17 * s);
        if (L.goal === 'heat') {
          var t = clamp((heat - 22) / (40 - 22), 0, 1);
          g.fillStyle = 'rgba(255,255,255,.12)'; g.fillRect(pad, pad + 38 * s, 200 * s, 7 * s);
          g.fillStyle = heat > 34 ? '#ff6a5a' : (heat > 30 ? '#ffb44a' : '#17c8d6');
          g.fillRect(pad, pad + 38 * s, 200 * s * t, 7 * s);
          g.font = '600 ' + (10 * s) + 'px system-ui,sans-serif'; g.fillStyle = '#eafcff';
          g.fillText(heat.toFixed(1) + ' °C / 40', pad + 206 * s, pad + 36 * s);
        }
        // unten links: Uptime und Punkte
        var by = ch - pad - 40 * s;
        g.fillStyle = 'rgba(6,19,26,.62)'; g.fillRect(pad - 6 * s, by - 6 * s, 236 * s, 46 * s);
        g.font = '700 ' + (10 * s) + 'px system-ui,sans-serif'; g.fillStyle = '#17c8d6';
        g.fillText('UPTIME', pad, by);
        g.fillStyle = 'rgba(255,255,255,.12)'; g.fillRect(pad, by + 15 * s, 160 * s, 9 * s);
        g.fillStyle = uptime > 50 ? '#17c8d6' : (uptime > 25 ? '#ffb44a' : '#ff6a5a');
        g.fillRect(pad, by + 15 * s, 160 * s * clamp(uptime / 100, 0, 1), 9 * s);
        g.font = '600 ' + (11 * s) + 'px system-ui,sans-serif'; g.fillStyle = '#eafcff';
        g.fillText(Math.max(0, Math.round(uptime)) + ' %', pad + 168 * s, by + 13 * s);
        g.font = '500 ' + (10 * s) + 'px system-ui,sans-serif'; g.fillStyle = 'rgba(234,252,255,.75)';
        g.fillText('Punkte ' + (total + levelScore), pad + 60 * s, by);
        // Eine Meldung in der Mitte oben, kurz ("+30 % Uptime", "Tür offen")
        if (msgT > 0) {
          g.globalAlpha = Math.min(1, msgT * 2);
          g.font = '700 ' + (14 * s) + 'px system-ui,sans-serif'; g.textAlign = 'center';
          g.fillStyle = '#8ffcff'; g.fillText(msgText, cw / 2, ch * 0.18);
          g.textAlign = 'left'; g.globalAlpha = 1;
        }
        g.restore();
      }
      function say(t) { msgText = t; msgT = 1.6; }

      /* ---------- Was zu tun ist ---------- */
      function objective() {
        if (L.goal === 'nodes') {
          var n = taken.filter(Boolean).length;
          return { done: n, total: NODES.length, short: n + '/' + NODES.length + ' KNOTEN',
                   text: open ? 'Tür offen — raus hier.' : 'Knoten berühren: ' + n + ' von ' + NODES.length };
        }
        if (L.goal === 'heat') {
          var off = hotTotal - hotRunning();
          return { done: off, total: hotTotal, short: off + '/' + hotTotal + ' HEISS AUS',
                   text: open ? 'Klima stabil — zur Tür.' : 'Heiße Schränke abschalten: ' + off + ' von ' + hotTotal };
        }
        var boss = foes.filter(function (f) { return f.t === 'panic'; })[0];
        var bossDone = !boss || boss.dead;
        return { done: modules + (bossDone ? 1 : 0), total: 4, short: modules + '/3 USV' + (bossDone ? ' +K' : ''),
                 text: open ? 'Kern frei — rein da.' :
                       'USV-Module: ' + modules + ' von 3 · Kernel Panic: ' + (bossDone ? 'behoben' : 'aktiv') };
      }
      function hotRunning() { var n = 0; for (var k in hot) if (hot[k] && !offs[k]) n++; return n; }

      function check() {
        if (open) return;
        var ok = false;
        if (L.goal === 'nodes') ok = taken.every(Boolean);
        else if (L.goal === 'heat') ok = hotRunning() === 0;
        else {
          var boss = foes.filter(function (f) { return f.t === 'panic'; })[0];
          ok = modules >= 3 && (!boss || boss.dead);
        }
        if (ok) { open = true; sfx('door'); say('Die Tür ist offen'); }
        status();
      }
      function status() {
        if (state !== 'play') return;
        var o = objective();
        host.setStatus((lvl + 1) + '/3 ' + L.name + ' · ' + o.text + ' · Uptime ' + Math.max(0, Math.round(uptime)) + ' %');
      }

      /* ---------- Gucken, gehen, schalten ---------- */
      function aim() {
        var mx = px | 0, my = py | 0;
        var ddx = dirX === 0 ? 1e30 : Math.abs(1 / dirX), ddy = dirY === 0 ? 1e30 : Math.abs(1 / dirY);
        var sx, sy, sdx, sdy;
        if (dirX < 0) { sx = -1; sdx = (px - mx) * ddx; } else { sx = 1; sdx = (mx + 1 - px) * ddx; }
        if (dirY < 0) { sy = -1; sdy = (py - my) * ddy; } else { sy = 1; sdy = (my + 1 - py) * ddy; }
        for (var g = 0; g < 8; g++) {
          var side;
          if (sdx < sdy) { sdx += ddx; mx += sx; side = 0; } else { sdy += ddy; my += sy; side = 1; }
          // Der Abstand ist der zur VORDEREN Kante der betretenen Zelle - sdx/sdy zeigen nach dem
          // Schritt schon auf die naechste Kante dahinter.
          var d = side === 0 ? (sdx - ddx) : (sdy - ddy);
          if (d > 1.9) return null;
          var t = cell(mx, my);
          if (t === 1) return { x: mx, y: my, d: d };
          if (t > 0) return null;                       // eine Wand davor: nichts zu schalten
        }
        return null;
      }
      // Wie weit man durch die Bildmitte sieht, bis etwas Festes kommt (fuer den Patch-Strahl).
      function sight() {
        var mx = px | 0, my = py | 0;
        var ddx = dirX === 0 ? 1e30 : Math.abs(1 / dirX), ddy = dirY === 0 ? 1e30 : Math.abs(1 / dirY);
        var sx, sy, sdx, sdy, side = 0;
        if (dirX < 0) { sx = -1; sdx = (px - mx) * ddx; } else { sx = 1; sdx = (mx + 1 - px) * ddx; }
        if (dirY < 0) { sy = -1; sdy = (py - my) * ddy; } else { sy = 1; sdy = (my + 1 - py) * ddy; }
        for (var g = 0; g < 40; g++) {
          if (sdx < sdy) { sdx += ddx; mx += sx; side = 0; } else { sdy += ddy; my += sy; side = 1; }
          if (cell(mx, my) > 0) return side === 0 ? (sdx - ddx) : (sdy - ddy);
        }
        return 40;
      }
      // Sieht A den Punkt B? Abgetastet in kleinen Schritten - fuer zwei Dutzend Figuren reicht das.
      function los(ax, ay, bx, by) {
        var dx = bx - ax, dy = by - ay, d = Math.sqrt(dx * dx + dy * dy), n = Math.ceil(d / 0.2);
        for (var i = 1; i < n; i++) { if (cell(ax + dx * i / n, ay + dy * i / n) > 0) return false; }
        return true;
      }
      function toggle() {
        if (state !== 'play' || !look) return;
        var k = look.x + ',' + look.y;
        if (offs[k]) { delete offs[k]; offCount--; } else { offs[k] = true; offCount++; }
        plug = 1;                                       // die Hand schiebt den Stecker vor
        sfx('switch');
        if (hot[k] && offs[k]) { levelScore += 150; say('Heißer Schrank aus'); }
        check();
      }

      /* ---------- Patchen ----------
         Ein Treffer ist, was in der Bildmitte steht und naeher ist als die naechste Wand. */
      function fire() {
        if (state !== 'play' || cool > 0) return;
        cool = 0.3; zap = 0.14; sfx('zap');
        var wallD = sight(), invDet = 1 / (planeX * dirY - dirX * planeY), best = null, bd = 1e9;
        for (var i = 0; i < foes.length; i++) {
          var e = foes[i]; if (e.dead) continue;
          var rx = e.x - px, ry = e.y - py;
          var trX = invDet * (dirY * rx - dirX * ry), trY = invDet * (-planeY * rx + planeX * ry);
          if (trY <= 0.2 || trY > wallD || trY > 11) continue;
          var scX = (W / 2) * (1 + trX / trY), half = (H / trY) * FOE[e.t].w * 0.55;
          if (Math.abs(scX - W / 2) < half + 1 && trY < bd) { bd = trY; best = e; }
        }
        if (!best) return;
        best.hp--; best.hitT = 0.12; best.awake = true;
        if (best.hp <= 0) {
          best.dead = true; best.fade = 0.45; kills++;
          levelScore += FOE[best.t].score; sfx('kill');
          if (best.t === 'panic') { say('Kernel Panic behoben'); check(); }
        } else sfx('hit');
      }

      function hurt(d) {
        if (state !== 'play') return;
        uptime -= d; hurtT = 0.45; sfx('hurt');
        if (uptime <= 0) { uptime = 0; finish('dead'); }
        status();
      }

      function walk(dt) {
        var sp = 2.9 * dt, rt = 2.3 * dt;
        if (keys.l) turn(-rt); if (keys.r) turn(rt);
        var mvx = 0, mvy = 0;
        if (keys.f) { mvx += dirX * sp; mvy += dirY * sp; }
        if (keys.b) { mvx -= dirX * sp * 0.8; mvy -= dirY * sp * 0.8; }
        if (keys.sl) { mvx += dirY * sp * 0.8; mvy -= dirX * sp * 0.8; }
        if (keys.sr) { mvx -= dirY * sp * 0.8; mvy += dirX * sp * 0.8; }
        var moving = (mvx || mvy);
        if (moving && !calm) bob += dt * 9.2;
        var R = 0.22;
        if (!cell(px + mvx + (mvx > 0 ? R : -R), py) && !blockedBy(px + mvx, py)) px += mvx;
        if (!cell(px, py + mvy + (mvy > 0 ? R : -R)) && !blockedBy(px, py + mvy)) py += mvy;

        for (var i = 0; i < NODES.length; i++) if (!taken[i]) {
          var dx = NODES[i][0] - px, dy = NODES[i][1] - py;
          if (dx * dx + dy * dy < 0.30) { taken[i] = true; levelScore += 200; sfx('node'); say('Knoten ' + taken.filter(Boolean).length + ' von ' + NODES.length); check(); }
        }
        for (i = 0; i < items.length; i++) {
          var it = items[i]; if (it.gone) continue;
          var ix = it.x - px, iy = it.y - py;
          if (ix * ix + iy * iy < 0.32) {
            if (it.t === 'kaffee') {
              if (uptime >= 100) continue;                 // voll: der Kaffee bleibt fuer spaeter stehen
              uptime = Math.min(100, uptime + 30); say('Kaffee: +30 % Uptime');
            } else { modules++; levelScore += 300; say('USV-Modul ' + modules + ' von 3'); check(); }
            it.gone = true; sfx('pick'); status();
          }
        }
        if (open && MAP[py | 0].charAt(px | 0) === 'D') finish('won');
      }
      // Ein Monitor ist ein Gegenstand, kein Nebel: man laeuft nicht hindurch.
      function blockedBy(x, y) {
        for (var i = 0; i < MONS.length; i++) {
          var dx = MONS[i].x - x, dy = MONS[i].y - y;
          if (dx * dx + dy * dy < 0.13) return true;
        }
        return false;
      }
      function turn(a) {
        var ox = dirX, opx = planeX, c = Math.cos(a), s = Math.sin(a);
        dirX = ox * c - dirY * s; dirY = ox * s + dirY * c;
        planeX = opx * c - planeY * s; planeY = opx * s + planeY * c;
      }

      /* ---------- Die Stoerungen ----------
         Schlafen, bis sie einen sehen (oder getroffen werden), dann kommen sie direkt auf einen
         zu - der Glitch im Zickzack, die Kernel Panic langsam und mit Paketen. An Waenden rutschen
         sie entlang statt stehenzubleiben. Wer zu nah kommt, kostet Uptime, mit kurzer Pause. */
      function foesStep(dt) {
        for (var i = 0; i < foes.length; i++) {
          var e = foes[i], spec = FOE[e.t];
          if (e.dead) { if (e.fade > 0) e.fade -= dt; continue; }
          e.hitT -= dt; e.cd -= dt; e.t2 += dt;
          var dx = px - e.x, dy = py - e.y, d = Math.sqrt(dx * dx + dy * dy) || 0.001;
          if (!e.awake && d < (L.dark ? 6.5 : 9) && los(e.x, e.y, px, py)) e.awake = true;
          if (!e.awake) continue;
          if (d > 0.62) {
            var vx = dx / d, vy = dy / d;
            if (e.t === 'glitch') { var z = Math.sin(e.t2 * 4 + e.seed) * 0.8; var tx = vx - vy * z, ty = vy + vx * z; var n = Math.sqrt(tx * tx + ty * ty); vx = tx / n; vy = ty / n; }
            var sp = spec.speed * dt * (e.hitT > 0 ? 0.3 : 1), R = 0.24;
            var nx = e.x + vx * sp, ny = e.y + vy * sp;
            if (!cell(nx + (vx > 0 ? R : -R), e.y)) e.x = nx;
            if (!cell(e.x, ny + (vy > 0 ? R : -R))) e.y = ny;
          } else if (e.cd <= 0) {
            e.cd = 0.9; hurt(spec.dmg);
          }
          if (spec.shoots && d < 12 && e.cd2 !== undefined) {
            e.cd2 -= dt;
            if (e.cd2 <= 0 && los(e.x, e.y, px, py)) {
              e.cd2 = 2.3;
              shots.push({ x: e.x, y: e.y, vx: dx / d * 4.2, vy: dy / d * 4.2, life: 4 });
              sfx('shot');
            }
          }
        }
        for (i = shots.length - 1; i >= 0; i--) {
          var s = shots[i];
          s.x += s.vx * dt; s.y += s.vy * dt; s.life -= dt;
          var hx = s.x - px, hy = s.y - py;
          if (hx * hx + hy * hy < 0.14) { shots.splice(i, 1); hurt(12); continue; }
          if (s.life <= 0 || cell(s.x, s.y) > 0) shots.splice(i, 1);
        }
      }

      /* ---------- Der Takt ---------- */
      function frame(t) {
        if (dead) return;
        raf = requestAnimationFrame(frame);
        if (!last) last = t;
        var dt = Math.min((t - last) / 1000, 0.05); last = t;
        now += dt;
        pulse += dt * 1.6;
        if (plug > 0) { plug -= dt * 2.6; if (plug < 0) plug = 0; }
        if (zap > 0) zap -= dt; if (cool > 0) cool -= dt; if (hurtT > 0) hurtT -= dt; if (msgT > 0) msgT -= dt;
        if (state === 'play') {
          walk(dt);
          if (state === 'play') foesStep(dt);
          if (keys.fire) fire();
          if (L.goal === 'heat' && !open && state === 'play') {
            // Jeder heisse Schrank heizt, und ohne einen einzigen erholt sich die Halle langsam.
            var run = hotRunning();
            heat += dt * (run ? 0.031 * run : -0.25);
            if (heat < 24) heat = 24;
            if (heat >= 40) { heat = 40; finish('dead', 'Notabschaltung — der Kaltgang hat 40 °C erreicht.'); }
          }
          if (((now * 4) | 0) !== (((now - dt) * 4) | 0)) status();
        }
        look = state === 'play' ? aim() : null;
        // Die Monitore werden nur ein paar Mal je Sekunde neu gezeichnet.
        for (var i = 0; i < MONS.length; i++) {
          if (now >= MONS[i].next) { MONS[i].next = now + (calm ? 1e9 : 0.22 + i * 0.03); drawMon(MONS[i]); }
        }
        render();
      }

      /* ---------- Level laden, gewinnen, verlieren ---------- */
      function loadLevel(n) {
        lvl = n; L = LEVELS[n]; MAP = L.map;
        // Im Notstrom-Level verschwindet die Ferne in fast Schwarz statt in Tuerkis: sonst wird es nur blasser, nicht dunkler.
        if (L.dark) { FR = 3; FG = 10; FB = 13; } else { FR = 10; FG = 44; FB = 52; }
        NODES = L.nodes.slice(); taken = NODES.map(function () { return false; });
        MONS = L.mons.map(function (m) { return makeMon({ x: m.x, y: m.y, kind: m.kind }); });
        foes = L.foes.map(function (f, i) {
          var s = FOE[f[0]];
          return { t: f[0], x: f[1], y: f[2], hp: s.hp, dead: false, fade: 0, hitT: 0, cd: 0, cd2: s.shoots ? 1.5 : undefined,
                   awake: false, seed: i * 1.7, t2: 0 };
        });
        items = L.items.map(function (it) { return { t: it[0], x: it[1], y: it[2], gone: false }; });
        shots = []; hot = {}; hotTotal = 0; racks = 0; offs = {}; offCount = 0;
        for (var y = 0; y < MH; y++) for (var x = 0; x < MW; x++) {
          var ch = MAP[y].charAt(x);
          if (ch === 'r' || ch === 'h') racks++;
          if (ch === 'h') { hot[x + ',' + y] = true; hotTotal++; }
        }
        px = L.start.x; py = L.start.y; dirX = 1; dirY = 0; planeX = 0; planeY = 0.66;
        if (L.start.a) turn(L.start.a);
        open = false; heat = 27; modules = 0; uptime = 100; levelScore = 0; kills = 0;
        plug = 0; bob = 0; zap = 0; cool = 0; hurtT = 0; msgT = 0; keys = {};
        for (var i = 0; i < MONS.length; i++) drawMon(MONS[i]);
        t0 = performance.now();
        host.setHint(hint());
      }

      function finish(how, why) {
        if (state !== 'play') return;
        state = how; keys = {}; unlock();
        var secs = (performance.now() - t0) / 1000;
        if (how === 'won') {
          var bonus = 1000 + Math.max(0, Math.round((240 - secs) * 4)) + Math.round(uptime * 8);
          levelScore += bonus; total += levelScore; totalTime += secs;
          var gained = levelScore; levelScore = 0;           // jetzt in total - die Anzeige zaehlt sonst doppelt
          sfx('win');
          if (lvl === LEVELS.length - 1) {
            state = 'done';
            card('Schicht geschafft',
                 'Alle drei Hallen laufen wieder. ' + total + ' Punkte in ' + fmt(totalTime) + '.<br>' +
                 'Das Rechenzentrum dankt — und die Kaffeemaschine auch.',
                 'Noch eine Schicht', function () { total = 0; totalTime = 0; go(0); });
          } else {
            card(L.name + ' geschafft',
                 fmt(secs) + ' · ' + kills + ' Störungen behoben · Uptime ' + Math.round(uptime) + ' %<br>' +
                 '+' + gained + ' Punkte (Bonus ' + bonus + ') · insgesamt ' + total,
                 'Weiter: ' + LEVELS[lvl + 1].name, function () { go(lvl + 1); });
          }
        } else {
          sfx('down');
          card('Ausfall',
               (why || 'Die Uptime ist auf null gefallen.') + '<br>Das Level fängt von vorn an — die Punkte davor bleiben.',
               'Nochmal', function () { go(lvl); });
        }
        host.setStatus(how === 'won' ? 'geschafft' : 'Ausfall');
      }
      function fmt(s) { var m = (s / 60) | 0, r = Math.round(s % 60); return (m ? m + ' min ' : '') + r + ' s'; }

      // Der Vorhang zwischen den Leveln: ein Kasten mit Text und EINEM Knopf. Enter tut dasselbe.
      function card(title, text, button, then) {
        closeCard();
        overlay = document.createElement('div');
        overlay.style.cssText = 'position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:1rem;' +
          'background:rgba(6,19,26,.72);z-index:2';
        overlay.innerHTML = '<div style="max-width:30rem;width:100%;padding:1.4rem 1.5rem;border:1px solid rgba(23,200,214,.55);' +
          'background:#0b1a21;color:#cfe5e4;font:400 15px/1.55 system-ui,sans-serif">' +
          '<div style="font:700 .68rem/1 system-ui;letter-spacing:.2em;text-transform:uppercase;color:#17c8d6;margin-bottom:.6rem">' +
          'MATTHIX Arcade · Kaltgang</div>' +
          '<div style="font:700 1.6rem/1.15 system-ui;color:#eafcff;margin-bottom:.6rem">' + title + '</div>' +
          '<div style="margin-bottom:1.1rem">' + text + '</div>' +
          '<button type="button" style="font:600 .9rem/1 system-ui;padding:.65rem 1.1rem;cursor:pointer;background:#0b7f8a;' +
          'color:#fff;border:1px solid #17c8d6">' + button + '</button></div>';
        wrap.appendChild(overlay);
        var b = overlay.querySelector('button');
        overlay._go = function () { closeCard(); then(); };
        b.addEventListener('click', overlay._go);
        setTimeout(function () { if (b) b.focus(); }, 30);
      }
      function closeCard() { if (overlay && overlay.parentNode) overlay.parentNode.removeChild(overlay); overlay = null; }

      function go(n) {
        loadLevel(n);
        state = 'intro';
        card(LEVELS[n].sub + ' · ' + LEVELS[n].name, LEVELS[n].intro, 'Los', function () {
          state = 'play'; t0 = performance.now(); status(); say(LEVELS[n].name);
        });
      }

      function hint() {
        return 'W A S D / Pfeiltasten — gehen und drehen · Leertaste oder Klick — patchen · ' +
               'E schaltet den Schrank vor dir · M die Karte · Esc schließt' +
               (touch ? ' · auf dem Telefon: die Knöpfe unten' : ' · Klick ins Bild: mit der Maus umsehen');
      }

      /* ---------- Maus ----------
         Ein Klick ins Bild fängt die Maus (Pointer Lock): dann dreht sie, und jeder weitere Klick
         patcht. Esc gibt sie wieder frei - der Browser macht das von selbst. */
      function onMove(e) { if (locked && state === 'play') turn(e.movementX * 0.0023); }
      function onLock() { locked = document.pointerLockElement === cv; if (!locked) keys.fire = false; }
      function unlock() { if (document.pointerLockElement === cv && document.exitPointerLock) document.exitPointerLock(); }

      var touch = (navigator.maxTouchPoints > 0) || matchMedia('(pointer: coarse)').matches;
      var pad = null;
      function buildPad() {
        pad = document.createElement('div');
        pad.style.cssText = 'position:absolute;left:0;right:0;bottom:0;display:flex;gap:.5rem;' +
          'justify-content:center;padding:.6rem;pointer-events:none;z-index:1';
        [['◀', 'l'], ['▲', 'f'], ['▼', 'b'], ['▶', 'r'], ['E', 'e'], ['⚡', 'fire']].forEach(function (b) {
          var el = document.createElement('button');
          el.type = 'button'; el.textContent = b[0];
          el.setAttribute('aria-label', { l: 'links drehen', f: 'vorwärts', b: 'rückwärts',
                                          r: 'rechts drehen', e: 'Schrank schalten', fire: 'patchen' }[b[1]]);
          el.style.cssText = 'pointer-events:auto;font:600 20px/1 system-ui;width:3.4rem;height:3.4rem;' +
            'background:' + (b[1] === 'fire' ? 'rgba(255,79,176,.18)' : 'rgba(0,173,181,.16)') + ';' +
            'color:' + (b[1] === 'fire' ? '#ff9fd3' : '#17c8d6') + ';border:1px solid rgba(23,200,214,.5);' +
            'border-radius:50%;cursor:pointer;touch-action:none;-webkit-user-select:none;user-select:none';
          if (b[1] === 'e') {
            // Der Schalter ist ein Druck, kein Halten - sonst schaltet er im Takt der Bilder hin und her.
            el.addEventListener('pointerdown', function (e) { toggle(); e.preventDefault(); });
          } else {
            var set = function (v) { return function (e) { keys[b[1]] = v; e.preventDefault(); }; };
            el.addEventListener('pointerdown', set(true));
            el.addEventListener('pointerup', set(false));
            el.addEventListener('pointercancel', set(false));
            el.addEventListener('pointerleave', set(false));
          }
          pad.appendChild(el);
        });
        wrap.appendChild(pad);
      }
      function boot() {
        if (started || dead) return;
        started = true;
        cv = document.createElement('canvas');
        cv.style.cssText = 'flex:1 1 auto;min-height:0;display:block;width:100%;image-rendering:pixelated;touch-action:none;cursor:crosshair';
        wrap.appendChild(cv);
        ctx = cv.getContext('2d');
        if (touch) buildPad();
        cv.addEventListener('mousedown', function (e) {
          if (state !== 'play' || touch) return;
          if (!locked && cv.requestPointerLock) { try { cv.requestPointerLock(); } catch (x) { } }
          keys.fire = true; e.preventDefault();
        });
        document.addEventListener('mouseup', mouseUp);
        document.addEventListener('mousemove', onMove);
        document.addEventListener('pointerlockchange', onLock);
        host.setTitle('KALTGANG');
        host.setButtons([{ t: 'Neu', a: 'reset' }, { t: 'Karte', a: 'map' }, { t: 'Ton: an', a: 'sound' }], function (a, el) {
          if (a === 'reset') { state = 'play'; finishSilently(); go(lvl); }
          if (a === 'map') map = !map;
          if (a === 'sound') { sound = !sound; el.textContent = 'Ton: ' + (sound ? 'an' : 'aus'); }
        });
        if (!size()) { requestAnimationFrame(function () { size(); }); }
        go(0);
        raf = requestAnimationFrame(frame);
      }
      function mouseUp() { keys.fire = false; }
      function finishSilently() { closeCard(); unlock(); }

      if (!calm) boot();

      return {
        stop: function () {
          dead = true; cancelAnimationFrame(raf); raf = 0; keys = {};
          unlock(); closeCard();
          document.removeEventListener('mouseup', mouseUp);
          document.removeEventListener('mousemove', onMove);
          document.removeEventListener('pointerlockchange', onLock);
          if (AC && AC.close) { try { AC.close(); } catch (e) { } }
          AC = null;
        },
        resize: function () { if (started) size(); },
        key: function (e) {
          if (!started) return;
          var k = e.key;
          if (overlay && (k === 'Enter' || k === ' ') && !e.repeat) { overlay._go(); e.preventDefault(); return; }
          if (k === 'ArrowUp' || k === 'w' || k === 'W') { keys.f = true; e.preventDefault(); }
          else if (k === 'ArrowDown' || k === 's' || k === 'S') { keys.b = true; e.preventDefault(); }
          else if (k === 'ArrowLeft') { keys.l = true; e.preventDefault(); }
          else if (k === 'ArrowRight') { keys.r = true; e.preventDefault(); }
          else if (k === 'a' || k === 'A') keys.sl = true;
          else if (k === 'd' || k === 'D') keys.sr = true;
          else if (k === ' ' || k === 'f' || k === 'F') { keys.fire = true; e.preventDefault(); }
          // Wiederholung ignorieren: eine gehaltene Taste soll nicht dauernd schalten.
          else if ((k === 'e' || k === 'E') && !e.repeat) { toggle(); e.preventDefault(); }
          else if ((k === 'm' || k === 'M') && !e.repeat) { map = !map; e.preventDefault(); }
        },
        keyup: function (e) {
          var k = e.key;
          if (k === 'ArrowUp' || k === 'w' || k === 'W') keys.f = false;
          else if (k === 'ArrowDown' || k === 's' || k === 'S') keys.b = false;
          else if (k === 'ArrowLeft') keys.l = false;
          else if (k === 'ArrowRight') keys.r = false;
          else if (k === 'a' || k === 'A') keys.sl = false;
          else if (k === 'd' || k === 'D') keys.sr = false;
          else if (k === ' ' || k === 'f' || k === 'F') keys.fire = false;
        }
      };
    }
  };
})();
