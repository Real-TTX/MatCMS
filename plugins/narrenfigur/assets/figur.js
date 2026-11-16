/* Narrenfigur: a small Fasnet figure in the corner of the page. A click equips her (confetti bag, broom, bells, mulled wine);
   with something in her hand a click makes her use it. Everything is drawn here (one SVG, no picture files), nothing leaves the
   browser, the choice is remembered in localStorage. Texts are German and English (taken from <html lang>). */
(function () {
  'use strict';
  var doc = document, body = doc.body;
  if (!body || window.top !== window.self || doc.querySelector('.nf')) return;
  var en = /^en/i.test(doc.documentElement.lang || '');
  var reduce = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  var T = en ? {
    figure: 'Wurmberg witch', equip: 'Equip the witch', hint: 'Then click the witch!', hide: 'Send the witch away', show: 'Bring the witch back',
    none: 'Empty hand', confetti: 'Confetti bag', broom: 'Broom', bells: 'Bells', wine: 'Mulled wine',
    hello: ['Click me!', 'Narri – Narro!', 'Give me something!'], throwSay: ['Narri!', 'Narro!', 'Catch!'], flySay: 'Wheee!', ringSay: 'Jingle!', wineSay: ['Cheers!', 'Mmh, warm!']
  } : {
    figure: 'Wurmberghexe', equip: 'Hexe ausrüsten', hint: 'Dann die Hexe anklicken!', hide: 'Hexe wegschicken', show: 'Hexe zurückholen',
    none: 'Leere Hand', confetti: 'Konfettibeutel', broom: 'Besen', bells: 'Schellen', wine: 'Glühwein',
    hello: ['Klick mich!', 'Narri – Narro!', 'Gib mir was in die Hand!'], throwSay: ['Narri!', 'Narro!', 'Fang!'], flySay: 'Huiii!', ringSay: 'Schelle, schelle!', wineSay: ['Prost!', 'Mmh, schön warm!']
  };
  var COLORS = ['#013daf', '#8a1f33', '#1d7f45', '#f4b400', '#ffffff', '#d9621c'];
  var KEY = 'narrenfigur.item', GONE = 'narrenfigur.gone';
  var store = function (k, v) { try { if (v === undefined) return localStorage.getItem(k); localStorage.setItem(k, v); } catch (e) { return null; } };
  var rnd = function (a, b) { return a + Math.random() * (b - a); };
  var pick = function (x) { return typeof x === 'string' ? x : x[Math.floor(Math.random() * x.length)]; };

  // ---------- the drawings ----------
  // What she holds, drawn around her right hand (151, 178). The small copies in the menu use the same drawings.
  var ITEMS = {
    none: '',
    confetti: '<g class="nf-i"><path d="M139 172q-6 14 0 24q12 8 24 0q6-10 0-24q-12 6-24 0z" fill="#b98a4e" stroke="#7a5527" stroke-width="2"/><path d="M139 172q12 7 24 0" fill="none" stroke="#7a5527" stroke-width="2.4"/>'
      + '<circle cx="146" cy="168" r="2.6" fill="#f4b400"/><circle cx="153" cy="165" r="2.6" fill="#8a1f33"/><circle cx="159" cy="168" r="2.6" fill="#1d7f45"/><rect x="149" y="160" width="5" height="3" rx="1" fill="#013daf" transform="rotate(20 151 161)"/></g>',
    broom: '<g class="nf-i"><rect x="148.500" y="92" width="5" height="150" rx="2.5" fill="#9a6a34"/><path d="M139 236h24l8 44q-20 8-40 0z" fill="#d9a441" stroke="#8a5a1c" stroke-width="2"/>'
      + '<path d="M143 246l-4 32M151 246v36M159 246l4 32" stroke="#8a5a1c" stroke-width="1.6" fill="none"/><rect x="138" y="234" width="26" height="7" rx="2" fill="#8a1f33"/></g>',
    bells: '<g class="nf-i"><rect x="148.500" y="120" width="5" height="76" rx="2.5" fill="#9a6a34"/><path d="M151 122q-18 2-20 16M151 122q18 2 20 16M151 122v-10" stroke="#8a1f33" stroke-width="3" fill="none" stroke-linecap="round"/>'
      + '<g fill="#f4b400" stroke="#b48300" stroke-width="1.4"><circle cx="131" cy="141" r="6"/><circle cx="171" cy="141" r="6"/><circle cx="151" cy="109" r="6"/></g>'
      + '<g stroke="#7a5a00" stroke-width="1.4"><path d="M127 142h8M167 142h8M147 110h8"/></g></g>',
    wine: '<g class="nf-i"><path d="M139 160h24v24q0 6-6 6h-12q-6 0-6-6z" fill="#fffaf0" stroke="#8a1f33" stroke-width="2.4"/><path d="M163 166q10 0 10 8t-10 8" fill="none" stroke="#8a1f33" stroke-width="2.4"/>'
      + '<path d="M141.500 165h19v6h-19z" fill="#8a1f33"/><g class="nf-steam" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity=".85"><path d="M146 154q-4-6 0-12"/><path d="M156 154q-4-6 0-12"/></g></g>'
  };
  var ICON = function (key) {
    if (key === 'none') return '<svg viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="22" r="10" fill="#4a4038"/><path d="M13 16v-7M18 14V6M23 14V7M28 17v-6" stroke="#4a4038" stroke-width="4" stroke-linecap="round"/></svg>';
    var box = { confetti: '130 156 42 46', broom: '116 88 70 196', bells: '120 100 62 100', wine: '134 138 44 56' }[key];
    return '<svg viewBox="' + box + '" aria-hidden="true">' + ITEMS[key] + '</svg>';
  };

  // The Wurmberghexe as the club wears her: wooden mask, long dark hair under a blue head scarf with a green fringe, dark jacket,
  // a white apron with a green and a blue patch over the wine-red skirt, ringed stockings, bells at the wrist.
  var HEXE = ''
    + '<ellipse cx="100" cy="306" rx="52" ry="7" fill="rgba(0,0,0,.18)"/>'
    + '<g class="nf-ride"><rect x="8" y="232" width="150" height="6" rx="3" fill="#9a6a34"/><path d="M150 222l44-8v42l-44-8z" fill="#d9a441" stroke="#8a5a1c" stroke-width="2"/><rect x="146" y="221" width="8" height="28" rx="2" fill="#8a1f33"/></g>'
    // back hair
    + '<path d="M63 62C54 112 53 170 59 212c8 6 17 3 23-7h36c6 10 15 13 23 7c6-42 5-100-4-150z" fill="#33211a"/>'
    // stockings and shoes
    + '<g class="nf-legs"><rect x="80" y="258" width="14" height="40" fill="#fffaf0"/><rect x="106" y="258" width="14" height="40" fill="#fffaf0"/>'
    + '<path d="M80 266h14M80 276h14M80 286h14M106 266h14M106 276h14M106 286h14" stroke="#8a1f33" stroke-width="4"/>'
    + '<path d="M74 298h22v8H70q0-6 4-8zM104 298h22q4 2 4 8h-26z" fill="#2a1418"/></g>'
    // skirt with the green fringe at the hem
    + '<path d="M66 188h68l14 80q-48 14-96 0z" fill="#8a1f33"/><path d="M53 268q47 13 94 0" fill="none" stroke="#1d7f45" stroke-width="5" stroke-dasharray="2 3"/>'
    // jacket
    + '<path d="M68 118q32-10 64 0l6 78q-38 8-76 0z" fill="#27304a"/>'
    // apron with its patches
    + '<path d="M80 174h40l7 88q-27 7-54 0z" fill="#f6f1e4" stroke="#d8cfbb" stroke-width="1.5"/>'
    + '<rect x="85" y="196" width="17" height="17" fill="#1d7f45" transform="rotate(-6 93 204)"/><rect x="104" y="218" width="17" height="17" fill="#013daf" transform="rotate(5 112 226)"/>'
    + '<rect x="86" y="238" width="13" height="13" fill="#f4b400" transform="rotate(8 92 244)"/><path d="M80 176h40" stroke="#8a1f33" stroke-width="5"/>'
    // the arm that holds something, and the thing
    + '<g class="nf-hold"><path d="M129 121q25 16 30 52l-14 6q-6-26-24-40z" fill="#27304a"/><g class="nf-item"></g><circle cx="151" cy="178" r="9.500" fill="#4a4038"/></g>'
    // the waving arm with the bells at the wrist
    + '<g class="nf-wave"><path d="M71 121q-28-12-36-48l14-5q8 28 30 38z" fill="#27304a"/><circle cx="40" cy="64" r="10" fill="#4a4038"/>'
    + '<path d="M33 58v-9M38 55V44M43 55V45M48 58v-8" stroke="#4a4038" stroke-width="4.500" stroke-linecap="round"/>'
    + '<g fill="#f4b400" stroke="#b48300" stroke-width="1"><circle cx="35" cy="77" r="3.500"/><circle cx="42" cy="79" r="3.500"/><circle cx="49" cy="77" r="3.500"/></g></g>'
    // head scarf, its ends on the shoulders
    + '<path d="M64 98q-10 22 2 36l14-18zM136 98q10 22-2 36l-14-18z" fill="#1f55c9"/><path d="M62 128l8 8M132 136l8-8" stroke="#1d7f45" stroke-width="4" stroke-dasharray="2 2"/>'
    + '<path d="M60 76Q100 14 140 76q-2 18-10 28H70q-8-10-10-28z" fill="#1f55c9"/><path d="M61 78q39-26 78 0" fill="none" stroke="#1d7f45" stroke-width="4" stroke-dasharray="2 2.500"/>'
    // hair in front, left and right of the mask
    + '<path d="M72 70q-10 60-4 132q8 4 12-2q-6-66 2-126zM128 70q10 60 4 132q-8 4-12-2q6-66-2-126z" fill="#3d281f"/>'
    // the wooden mask
    + '<path d="M77 64q23-16 46 0q7 26-4 46q-19 16-38 0q-11-20-4-46z" fill="#d9a441" stroke="#8a5a1c" stroke-width="2"/>'
    + '<path d="M80 72q9-9 17-2M103 70q8-7 17 2" fill="none" stroke="#5a3a12" stroke-width="4.500" stroke-linecap="round"/>'
    + '<ellipse cx="89" cy="80" rx="5.500" ry="4.500" fill="#2a1418"/><ellipse cx="111" cy="80" rx="5.500" ry="4.500" fill="#2a1418"/><circle cx="90.500" cy="79" r="1.300" fill="#fff"/><circle cx="112.500" cy="79" r="1.300" fill="#fff"/>'
    + '<circle cx="84" cy="96" r="6" fill="#c8442e" opacity=".55"/><circle cx="116" cy="96" r="6" fill="#c8442e" opacity=".55"/>'
    + '<path d="M99 80q-9 18-1 25q13 2 10-8q-2-9-9-17z" fill="#c48a2c" stroke="#8a5a1c" stroke-width="1.800"/>'
    + '<path d="M84 106q16 13 32 0q-16 6-32 0z" fill="#7a1b25" stroke="#5a3a12" stroke-width="1.500"/><rect x="91" y="107" width="4.500" height="5" fill="#fffaf0"/><rect x="106" y="107.500" width="4.500" height="4.500" fill="#fffaf0"/>'
    + '<path d="M82 88q3 3 7 2M118 88q-3 3-7 2M94 116q6 3 12 0" fill="none" stroke="#8a5a1c" stroke-width="1.500" stroke-linecap="round"/><circle cx="109" cy="113" r="1.800" fill="#8a5a1c"/>';
  var FIGURES = { hexe: HEXE };

  // ---------- build ----------
  function el(tag, cls, text) { var e = doc.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var cfg = { figure: 'hexe', side: 'right', homeOnly: false, phone: true };

  function start() {
    if (cfg.homeOnly && !/^\/([a-z]{2}\/?)?$/.test(location.pathname)) return;
    if (!cfg.phone && innerWidth < 700) return;
    var item = store(KEY); if (!ITEMS.hasOwnProperty(item || '')) item = 'none';
    var root = el('div', 'nf nf--' + (cfg.side === 'left' ? 'left' : 'right'));
    var fig = el('button', 'nf__fig'); fig.type = 'button'; fig.setAttribute('aria-label', T.figure + ' – ' + T.equip);
    fig.innerHTML = '<svg viewBox="0 0 200 316" aria-hidden="true">' + (FIGURES[cfg.figure] || HEXE) + '</svg>';
    var slot = fig.querySelector('.nf-item');
    var gear = el('button', 'nf__gear'); gear.type = 'button'; gear.setAttribute('aria-label', T.equip); gear.title = T.equip; gear.setAttribute('aria-expanded', 'false');
    gear.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 7V5a4 4 0 0 1 8 0v2h2.500a1.500 1.500 0 0 1 1.500 1.700l-1.300 10a2 2 0 0 1-2 1.800H7.300a2 2 0 0 1-2-1.800L4 8.700A1.500 1.500 0 0 1 5.500 7zm2 0h4V5a2 2 0 0 0-4 0z" fill="currentColor"/></svg>';
    var close = el('button', 'nf__close'); close.type = 'button'; close.setAttribute('aria-label', T.hide); close.title = T.hide; close.textContent = '×';
    var back = el('button', 'nf__back'); back.type = 'button'; back.setAttribute('aria-label', T.show); back.title = T.show;
    back.innerHTML = '<svg viewBox="60 14 80 110" aria-hidden="true">' + HEXE + '</svg>';
    var menu = el('div', 'nf__menu'); menu.hidden = true; menu.setAttribute('role', 'group'); menu.setAttribute('aria-label', T.equip);
    menu.appendChild(el('strong', null, T.equip));
    var list = el('div', 'nf__items'); menu.appendChild(list);
    Object.keys(ITEMS).forEach(function (k) {
      var b = el('button', 'nf__item'); b.type = 'button'; b.setAttribute('data-item', k); b.innerHTML = ICON(k); b.appendChild(el('span', null, T[k]));
      b.addEventListener('click', function () { equip(k); openMenu(false); if (k !== 'none') setTimeout(function () { say(T.hint); }, 150); });
      list.appendChild(b);
    });
    root.appendChild(menu); root.appendChild(fig); root.appendChild(gear); root.appendChild(close); root.appendChild(back);
    body.appendChild(root);

    function equip(k) {
      item = k; store(KEY, k); slot.innerHTML = ITEMS[k]; root.setAttribute('data-item', k);
      [].forEach.call(list.children, function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-item') === k ? 'true' : 'false'); });
    }
    function openMenu(on) { menu.hidden = !on; gear.setAttribute('aria-expanded', on ? 'true' : 'false'); if (on) bubble && bubble.remove(); }
    var bubble, bubbleTimer;
    function say(text) {
      if (bubble) bubble.remove();
      bubble = el('span', 'nf__say', text); root.appendChild(bubble);
      clearTimeout(bubbleTimer); bubbleTimer = setTimeout(function () { bubble && bubble.remove(); bubble = null; }, 2600);
    }
    function pulse(cls, ms) { fig.classList.remove(cls); void fig.offsetWidth; fig.classList.add(cls); setTimeout(function () { fig.classList.remove(cls); }, ms); }
    function hand() { var r = (slot.firstChild || fig).getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 3 }; }

    function confetti(n, x, y) {
      if (reduce) n = Math.min(n, 14);
      var box = el('div', 'nf-confetti'); box.setAttribute('aria-hidden', 'true'); body.appendChild(box);
      var dir = cfg.side === 'left' ? 1 : -1, H = innerHeight;
      for (var i = 0; i < n; i++) {
        var p = el('i'); p.style.background = COLORS[i % COLORS.length]; if (i % 3 === 0) p.style.borderRadius = '50%';
        box.appendChild(p);
        var a = rnd(-Math.PI * .92, -Math.PI * .38), v = rnd(160, 520), dx = Math.cos(a) * v * (dir < 0 ? 1 : -1), dy = Math.sin(a) * v, r = rnd(-720, 720);
        p.animate([
          { transform: 'translate(' + x + 'px,' + y + 'px) rotate(0deg)', opacity: 1 },
          { transform: 'translate(' + (x + dx) + 'px,' + (y + dy) + 'px) rotate(' + r / 2 + 'deg)', opacity: 1, offset: .38 },
          { transform: 'translate(' + (x + dx * 1.5 + rnd(-70, 70)) + 'px,' + (H + 40) + 'px) rotate(' + r + 'deg)', opacity: .9 }
        ], { duration: rnd(1900, 3400), easing: 'cubic-bezier(.2,.6,.4,1)', fill: 'forwards' });
      }
      setTimeout(function () { box.remove(); }, 3800);
    }
    // sleigh bells made in the browser (no sound file): short metallic hits with a quick decay
    var audio;
    function jingle() {
      try { audio = audio || new (window.AudioContext || window.webkitAudioContext)(); } catch (e) { return; }
      if (audio.state === 'suspended') audio.resume();
      var t0 = audio.currentTime + .02;
      for (var i = 0; i < 10; i++) {
        var t = t0 + i * .065 + rnd(0, .03);
        [2637, 3951, 5274].forEach(function (f, k) {
          var o = audio.createOscillator(), g = audio.createGain();
          o.type = 'sine'; o.frequency.value = f * rnd(.985, 1.015);
          g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(k ? .018 : .035, t + .005); g.gain.exponentialRampToValueAtTime(0.0001, t + rnd(.18, .32));
          o.connect(g).connect(audio.destination); o.start(t); o.stop(t + .4);
        });
      }
    }
    var flying = false;
    function fly() {
      if (flying) return; flying = true; say(T.flySay); fig.classList.add('is-riding');
      var W = innerWidth, H = innerHeight, d = cfg.side === 'left' ? 1 : -1, far = d * (W + 260);
      if (reduce) { pulse('is-hop', 700); setTimeout(function () { fig.classList.remove('is-riding'); flying = false; }, 900); return; }
      var out = fig.animate([
        { transform: 'translate(0,0) rotate(0deg)' },
        { transform: 'translate(' + far * .3 + 'px,' + -H * .34 + 'px) rotate(' + d * 16 + 'deg)', offset: .35 },
        { transform: 'translate(' + far * .68 + 'px,' + -H * .2 + 'px) rotate(' + d * 6 + 'deg)', offset: .7 },
        { transform: 'translate(' + far + 'px,' + -H * .42 + 'px) rotate(' + d * 18 + 'deg)' }
      ], { duration: 2600, easing: 'cubic-bezier(.45,.05,.55,.95)', fill: 'forwards' });
      out.onfinish = function () {
        var home = fig.animate([
          { transform: 'translate(' + -d * 320 + 'px,' + -H * .3 + 'px) rotate(' + d * 14 + 'deg)' },
          { transform: 'translate(0,-26px) rotate(' + d * 4 + 'deg)', offset: .8 },
          { transform: 'translate(0,0) rotate(0deg)' }
        ], { duration: 1500, easing: 'cubic-bezier(.2,.7,.3,1)' });
        out.cancel();
        home.onfinish = function () { fig.classList.remove('is-riding'); flying = false; };
      };
    }
    function act() {
      if (flying) return;
      if (item === 'none') { pulse('is-wave', 900); if (menu.hidden) { openMenu(true); } else openMenu(false); return; }
      openMenu(false);
      if (item === 'confetti') { pulse('is-throw', 700); say(pick(T.throwSay)); setTimeout(function () { var h = hand(); confetti(46, h.x, h.y); }, 180); }
      else if (item === 'broom') fly();
      else if (item === 'bells') { pulse('is-ring', 900); say(T.ringSay); jingle(); }
      else if (item === 'wine') { pulse('is-prost', 1000); say(pick(T.wineSay)); }
    }
    fig.addEventListener('click', act);
    gear.addEventListener('click', function () { openMenu(menu.hidden); });
    close.addEventListener('click', function () { openMenu(false); root.classList.add('is-gone'); store(GONE, '1'); });
    back.addEventListener('click', function () { root.classList.remove('is-gone'); store(GONE, '0'); pulse('is-hop', 700); });
    doc.addEventListener('keydown', function (e) { if (e.key === 'Escape') openMenu(false); });
    doc.addEventListener('click', function (e) { if (!menu.hidden && !root.contains(e.target)) openMenu(false); });

    equip(item);
    if (store(GONE) === '1') root.classList.add('is-gone');
    else if (!store(KEY + '.seen')) { store(KEY + '.seen', '1'); setTimeout(function () { say(T.hello[0]); }, 1800); }
  }

  var src = (doc.currentScript && doc.currentScript.getAttribute('data-config')) || '/narrenfigur/config';
  fetch(src, { credentials: 'omit' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (c) { if (c) for (var k in c) cfg[k] = c[k]; start(); }, start);
})();
