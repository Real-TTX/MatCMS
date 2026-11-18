/* Narrenfigur: a small Fasnet figure in the corner of the page. A click equips her (confetti bag, broom, bells, mulled wine);
   with something in her hand a click makes her use it. Everything is drawn here (one SVG, no picture files), nothing leaves the
   browser, the choice is remembered in localStorage. Texts are German and English (taken from <html lang>). */
(function () {
  'use strict';
  var doc = document, body = doc.body;
  if (!body || window.top !== window.self || doc.querySelector('.nf')) return;
  var en = /^en/i.test(doc.documentElement.lang || '');
  var reduce = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  // The words that name the figure are per figure (hexe, narr); the rest is shared.
  var T = en ? {
    figures: 'Figure', none: 'Empty hand', confetti: 'Confetti bag', broom: 'Broom', bells: 'Bells', wine: 'Mulled wine',
    hello: ['Click me!', 'Narri – Narro!', 'Give me something!'], throwSay: ['Narri!', 'Narro!', 'Catch!'], flySay: 'Wheee!', ringSay: 'Jingle!', wineSay: ['Cheers!', 'Mmh, warm!'],
    hexe: { name: 'Wurmberg witch', equip: 'Equip the witch', hint: 'Then click the witch!', hide: 'Send the witch away', show: 'Bring the witch back' },
    narr: { name: 'Schalkstein jester', equip: 'Equip the jester', hint: 'Then click the jester!', hide: 'Send the jester away', show: 'Bring the jester back' }
  } : {
    figures: 'Figur', none: 'Leere Hand', confetti: 'Konfettibeutel', broom: 'Besen', bells: 'Schellen', wine: 'Glühwein',
    hello: ['Klick mich!', 'Narri – Narro!', 'Gib mir was in die Hand!'], throwSay: ['Narri!', 'Narro!', 'Fang!'], flySay: 'Huiii!', ringSay: 'Schelle, schelle!', wineSay: ['Prost!', 'Mmh, schön warm!'],
    hexe: { name: 'Wurmberghexe', equip: 'Hexe ausrüsten', hint: 'Dann die Hexe anklicken!', hide: 'Hexe wegschicken', show: 'Hexe zurückholen' },
    narr: { name: 'Schalksteinnarr', equip: 'Narr ausrüsten', hint: 'Dann den Narr anklicken!', hide: 'Narr wegschicken', show: 'Narr zurückholen' }
  };
  var COLORS = ['#013daf', '#8a1f33', '#1d7f45', '#f4b400', '#ffffff', '#d9621c'];
  var KEY = 'narrenfigur.item', GONE = 'narrenfigur.gone', WHO = 'narrenfigur.figure';
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
  // The Schalksteinnarr as the club wears him: smiling wooden mask with red nose and cheeks, shaggy light-brown hair, a hood red on one
  // side and blue on the other, a pointed collar in the same two colours with bells, a cream Häs embroidered with the vineyard, grapes and
  // a wine glass, red and blue points at the hem and at the trouser cuffs, a white fur over one shoulder, dark red gloves, light shoes.
  // Same frame as the witch (hand at 151/178, waving arm from 71/121), so items, flight and animations fit both.
  var NARR = ''
    + '<ellipse cx="100" cy="306" rx="52" ry="7" fill="rgba(0,0,0,.18)"/>'
    + '<g class="nf-ride"><rect x="8" y="232" width="150" height="6" rx="3" fill="#9a6a34"/><path d="M150 222l44-8v42l-44-8z" fill="#d9a441" stroke="#8a5a1c" stroke-width="2"/><rect x="146" y="221" width="8" height="28" rx="2" fill="#8a1f33"/></g>'
    // lower legs, cuffs with points and bells, shoes
    + '<g class="nf-legs"><path d="M69 246h29v32H69zM102 246h29v32h-29z" fill="#f3ead6" stroke="#d8cbb0" stroke-width="1.500"/>'
    + '<path d="M68 276h31v6H68zM101 276h31v6h-31z" fill="#2f4fc4"/>'
    + '<path d="M68 282l5 9 5-9zM78 282l5 9 5-9zM88 282l5 9 5-9zM101 282l5 9 5-9zM111 282l5 9 5-9zM121 282l5 9 5-9z" fill="#a3203a"/>'
    + '<g fill="#f4b400" stroke="#b48300" stroke-width=".8"><circle cx="73" cy="292" r="2.200"/><circle cx="93" cy="292" r="2.200"/><circle cx="106" cy="292" r="2.200"/><circle cx="126" cy="292" r="2.200"/></g>'
    + '<path d="M71 292h26v12H66q0-8 5-12zM103 292h26q5 4 5 12h-31z" fill="#d8c49a" stroke="#a8916a" stroke-width="1.500"/><path d="M66 302h31M103 302h31" stroke="#fff" stroke-width="2"/></g>'
    // trousers with the grapes and the wine glass
    + '<path d="M67 204h66l-2 46H69z" fill="#f3ead6" stroke="#d8cbb0" stroke-width="1.500"/><path d="M100 214v36" stroke="#d8cbb0" stroke-width="1.500"/>'
    + '<path d="M86 222q8-6 12 0q-6 4-12 0z" fill="#3d8a4a"/><path d="M86 222q-2 6 2 10" stroke="#5a3a12" stroke-width="1.200" fill="none"/>'
    + '<g fill="#3b4fb8"><circle cx="80" cy="228" r="3.200"/><circle cx="86" cy="228" r="3.200"/><circle cx="83" cy="233" r="3.200"/><circle cx="77" cy="233" r="3.200"/><circle cx="89" cy="233" r="3"/><circle cx="80" cy="238" r="3.200"/><circle cx="86" cy="238" r="3.200"/><circle cx="83" cy="243" r="3"/></g>'
    + '<path d="M107 220h18q0 14-9 16q-9-2-9-16z" fill="#b0263a" stroke="#7a1b25" stroke-width="1.200"/><path d="M116 236v9M110 246h12" stroke="#3d8a4a" stroke-width="3" stroke-linecap="round"/>'
    // jacket with the vineyard on the chest, hem in red and blue points with bells
    + '<path d="M70 118q30-10 60 0l8 82H64z" fill="#f3ead6" stroke="#d8cbb0" stroke-width="1.500"/>'
    + '<circle cx="90" cy="164" r="13" fill="#e6ddc8" stroke="#a9a48c" stroke-width="1.200"/>'
    + '<g fill="#3d8a4a"><circle cx="83" cy="158" r="1.800"/><circle cx="89" cy="157" r="1.800"/><circle cx="95" cy="158" r="1.800"/><circle cx="82" cy="164" r="1.800"/><circle cx="88" cy="163" r="1.800"/><circle cx="94" cy="164" r="1.800"/><circle cx="99" cy="165" r="1.800"/><circle cx="84" cy="170" r="1.800"/><circle cx="90" cy="169" r="1.800"/></g>'
    + '<path d="M95 166h4v8h-4z" fill="#f4b400"/><circle cx="97" cy="164" r="1.800" fill="#5a3a12"/>'
    + '<path d="M112 150h12v10h-12z" fill="#8a6a44" opacity=".85"/><path d="M114 176q6-6 10 0v10h-10z" fill="#a8845a" opacity=".85"/>'
    + '<path d="M63 196h75v7H63z" fill="#2f4fc4"/>'
    + '<path d="M63 203l5 13 5-13zM83 203l5 13 5-13zM103 203l5 13 5-13zM123 203l5 13 5-13z" fill="#a3203a"/><path d="M73 203l5 13 5-13zM93 203l5 13 5-13zM113 203l5 13 5-13zM133 203l2.500 13 2.500-13z" fill="#2f4fc4"/>'
    + '<g fill="#f4b400" stroke="#b48300" stroke-width=".8"><circle cx="68" cy="217" r="2.200"/><circle cx="88" cy="217" r="2.200"/><circle cx="108" cy="217" r="2.200"/><circle cx="128" cy="217" r="2.200"/></g>'
    // the white fur over his shoulder, hanging down his side
    + '<path d="M60 112q14-8 26 0q-8 20-10 52q-2 34 4 56q-8 10-16 4q-10-6-12-20q-6-46 8-92z" fill="#eef0f4" stroke="#c4c6d0" stroke-width="1.500"/>'
    + '<path d="M66 126q4 5 1 11M62 150q4 5 1 11M63 176q4 5 1 11M67 200q4 5 1 11M76 118q3 5 0 10" stroke="#c4c6d0" stroke-width="1.500" fill="none" stroke-linecap="round"/>'
    // the arm that holds something, and the thing
    + '<g class="nf-hold"><path d="M129 121q25 16 30 52l-14 6q-6-26-24-40z" fill="#f3ead6" stroke="#d8cbb0" stroke-width="1.500"/><path d="M144 172l14-5 2 6-14 6z" fill="#2f4fc4"/><g class="nf-item"></g><circle cx="151" cy="178" r="9.500" fill="#7a2430"/></g>'
    // the waving arm with the bells at the wrist
    + '<g class="nf-wave"><path d="M71 121q-28-12-36-48l14-5q8 28 30 38z" fill="#f3ead6" stroke="#d8cbb0" stroke-width="1.500"/><path d="M34 74l14-5 2 6-14 5z" fill="#a3203a"/><circle cx="40" cy="64" r="10" fill="#7a2430"/>'
    + '<path d="M33 58v-9M38 55V44M43 55V45M48 58v-8" stroke="#7a2430" stroke-width="4.500" stroke-linecap="round"/>'
    + '<g fill="#f4b400" stroke="#b48300" stroke-width="1"><circle cx="35" cy="80" r="3.500"/><circle cx="42" cy="82" r="3.500"/><circle cx="49" cy="80" r="3.500"/></g></g>'
    // the pointed collar, red on one side and blue on the other, bells at the points
    + '<path d="M100 106L64 110l-8 30 14-7 7 18 11-16 12 13z" fill="#a3203a" stroke="#7a1b25" stroke-width="1.200"/>'
    + '<path d="M100 106l36 4 8 30-14-7-7 18-11-16-12 13z" fill="#2f4fc4" stroke="#213a99" stroke-width="1.200"/>'
    + '<g fill="#f4b400" stroke="#b48300" stroke-width=".8"><circle cx="56" cy="140" r="2.600"/><circle cx="77" cy="151" r="2.600"/><circle cx="144" cy="140" r="2.600"/><circle cx="123" cy="151" r="2.600"/><circle cx="100" cy="145" r="2.600"/></g>'
    // the head, a little tilted: hood (red | blue), the mask, the shaggy hair
    + '<g transform="rotate(-6 100 80)">'
    + '<path d="M64 74q2-50 36-50v86H72q-10-14-8-36z" fill="#a3203a"/><path d="M136 74q-2-50-36-50v86h28q10-14 8-36z" fill="#2f4fc4"/>'
    + '<path d="M76 64q24-12 48 0q7 28-3 44q-21 16-42 0q-10-16-3-44z" fill="#efcc93" stroke="#a8763a" stroke-width="2"/>'
    + '<path d="M83 82q5-6 11 0M106 82q5-6 11 0" fill="none" stroke="#2a1418" stroke-width="3.200" stroke-linecap="round"/>'
    + '<circle cx="84" cy="94" r="6.500" fill="#d0473a" opacity=".7"/><circle cx="116" cy="94" r="6.500" fill="#d0473a" opacity=".7"/>'
    + '<circle cx="100" cy="92" r="6.500" fill="#c8302a"/><circle cx="98" cy="90" r="1.800" fill="#fff" opacity=".7"/>'
    + '<path d="M85 101q15 15 30 0q-15 5-30 0z" fill="#7a1b25" stroke="#5a3a12" stroke-width="1.200"/><path d="M90 103h20v3H90z" fill="#fffaf0"/>'
    + '<path d="M70 72q-4-24 13-31l-3-14 11 10 4-16 6 14 7-15 3 16 10-11-2 15q16 8 13 32q-6-9-12-6q-5-9-11-4q-6-9-13-2q-7-7-13 1q-6-5-10 3q-7-2-13 8z" fill="#ad7d4e" stroke="#7e5530" stroke-width="1.500" stroke-linejoin="round"/>'
    + '<path d="M84 50l4 12M96 44l1 14M106 46l-2 13M117 52l-4 10" stroke="#7e5530" stroke-width="1.400" stroke-linecap="round"/>'
    + '</g>';
  var FIGURES = { hexe: HEXE, narr: NARR };

  // ---------- build ----------
  function el(tag, cls, text) { var e = doc.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  // figure: the site's choice; choose: visitors may switch to the other figures (their choice is kept in the browser)
  var cfg = { figure: 'hexe', side: 'right', homeOnly: false, phone: true, choose: true };
  var HEAD = '60 14 80 110';   // the part of the drawing that peeks over the edge when the figure is sent away

  function start() {
    if (cfg.homeOnly && !/^\/([a-z]{2}\/?)?$/.test(location.pathname)) return;
    if (!cfg.phone && innerWidth < 700) return;
    var item = store(KEY); if (!ITEMS.hasOwnProperty(item || '')) item = 'none';
    var who = cfg.choose && FIGURES.hasOwnProperty(store(WHO) || '') ? store(WHO) : (FIGURES.hasOwnProperty(cfg.figure) ? cfg.figure : 'hexe');
    var root = el('div', 'nf nf--' + (cfg.side === 'left' ? 'left' : 'right'));
    var fig = el('button', 'nf__fig'); fig.type = 'button';
    var slot;
    var gear = el('button', 'nf__gear'); gear.type = 'button'; gear.setAttribute('aria-expanded', 'false');
    gear.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 7V5a4 4 0 0 1 8 0v2h2.500a1.500 1.500 0 0 1 1.500 1.700l-1.300 10a2 2 0 0 1-2 1.800H7.300a2 2 0 0 1-2-1.800L4 8.700A1.500 1.500 0 0 1 5.500 7zm2 0h4V5a2 2 0 0 0-4 0z" fill="currentColor"/></svg>';
    var close = el('button', 'nf__close'); close.type = 'button'; close.textContent = '×';
    var back = el('button', 'nf__back'); back.type = 'button';
    var menu = el('div', 'nf__menu'); menu.hidden = true; menu.setAttribute('role', 'group');
    var heading = el('strong'); menu.appendChild(heading);
    var list = el('div', 'nf__items'); menu.appendChild(list);
    Object.keys(ITEMS).forEach(function (k) {
      var b = el('button', 'nf__item'); b.type = 'button'; b.setAttribute('data-item', k); b.innerHTML = ICON(k); b.appendChild(el('span', null, T[k]));
      b.addEventListener('click', function () { equip(k); openMenu(false); if (k !== 'none') setTimeout(function () { say(T[who].hint); }, 150); });
      list.appendChild(b);
    });
    // the figures to switch between: a small head each
    var picks = null;
    if (cfg.choose) {
      menu.appendChild(el('span', 'nf__label', T.figures));
      picks = el('div', 'nf__figs'); menu.appendChild(picks);
      Object.keys(FIGURES).forEach(function (k) {
        var b = el('button', 'nf__pick'); b.type = 'button'; b.setAttribute('data-figure', k);
        b.innerHTML = '<svg viewBox="' + HEAD + '" aria-hidden="true">' + FIGURES[k] + '</svg>'; b.appendChild(el('span', null, T[k].name));
        b.addEventListener('click', function () { if (k !== who) { become(k); store(WHO, k); pulse('is-hop', 700); } openMenu(false); });
        picks.appendChild(b);
      });
    }
    root.appendChild(menu); root.appendChild(fig); root.appendChild(gear); root.appendChild(close); root.appendChild(back);
    body.appendChild(root);

    // draws the figure and names every button after it; what she holds stays in her hand
    function become(k) {
      who = k; var t = T[k];
      fig.innerHTML = '<svg viewBox="0 0 200 316" aria-hidden="true">' + FIGURES[k] + '</svg>';
      slot = fig.querySelector('.nf-item'); slot.innerHTML = ITEMS[item];
      back.innerHTML = '<svg viewBox="' + HEAD + '" aria-hidden="true">' + FIGURES[k] + '</svg>';
      fig.setAttribute('aria-label', t.name + ' – ' + t.equip);
      gear.setAttribute('aria-label', t.equip); gear.title = t.equip;
      close.setAttribute('aria-label', t.hide); close.title = t.hide;
      back.setAttribute('aria-label', t.show); back.title = t.show;
      menu.setAttribute('aria-label', t.equip); heading.textContent = t.equip;
      root.setAttribute('data-figure', k);
      if (picks) [].forEach.call(picks.children, function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-figure') === k ? 'true' : 'false'); });
    }
    become(who);

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
