namespace MatCMS.Plugin.Verein;

/// <summary>The member area's and the events block's look — built on the site's own variables (accent, fonts), so it follows the template by itself.</summary>
public static class VereinCss
{
    public const string Css = """
.vm{--vm-accent:var(--verein-accent,var(--accent,#8a1f33));--vm-line:var(--line,rgba(0,0,0,.1));--vm-soft:var(--bg-alt,#f6f3ee);--vm-muted:var(--muted,#6b6b6b)}
.vm .vm-kicker{font-size:12.5px;font-weight:700;letter-spacing:.14em;text-transform:uppercase;color:var(--vm-accent);margin:0 0 6px}
.vm .vm-head{display:flex;justify-content:space-between;align-items:flex-end;gap:16px;flex-wrap:wrap;margin-bottom:22px}
.vm .vm-head h1{overflow-wrap:anywhere}
.vm .vm-head h1{margin:0;font-size:clamp(1.7rem,3vw,2.3rem)}
.vm .vm-me{display:inline-flex;align-items:center;gap:10px;color:inherit;font-size:14px;font-weight:600;text-decoration:none;padding:4px 12px 4px 4px;border:1px solid var(--vm-line);border-radius:999px;background:#fff}
@media (max-width:600px){.vm .vm-club{display:none}.vm .vm-head{flex-wrap:nowrap;align-items:center}.vm .vm-me>span:not(.vm-avatar){display:none}.vm .vm-me{padding:3px}.vm .vm-hint{padding:18px}.vm .vm-hint-icon{display:none}}
.vm .vm-kicker a{color:inherit;text-decoration:none}.vm .vm-kicker a:hover{text-decoration:underline}
.vm .vm-main{display:grid;gap:22px}
.vm .vm-hint{display:flex;gap:16px;align-items:flex-start;padding:20px 22px;border-radius:18px;background:linear-gradient(135deg,var(--vm-accent),color-mix(in srgb,var(--vm-accent) 70%,#000));color:#fff;box-shadow:0 12px 30px color-mix(in srgb,var(--vm-accent) 30%,transparent)}
.vm .vm-hint-icon{flex:none;width:52px;height:52px;border-radius:50%;background:rgba(255,255,255,.18);display:grid;place-items:center;font-size:26px}
.vm .vm-hint strong{font-size:1.2rem;display:block;margin-bottom:4px}.vm .vm-hint p{margin:0 0 12px;opacity:.92}
.vm .vm-hint .vm-btn{background:#fff;color:var(--vm-accent);border-color:#fff}.vm .vm-hint .vm-btn.ghost{background:transparent;color:#fff;border-color:rgba(255,255,255,.7)}
.vm .vm-up{display:grid;gap:14px}
.vm .vm-up-drop{position:relative;display:grid;justify-items:center;gap:6px;padding:30px 16px;border:2px dashed color-mix(in srgb,var(--vm-accent) 45%,transparent);border-radius:18px;background:color-mix(in srgb,var(--vm-accent) 6%,#fff);cursor:pointer;text-align:center}
.vm .vm-up-drop:hover{background:color-mix(in srgb,var(--vm-accent) 11%,#fff)}
.vm .vm-up-drop input{position:absolute;inset:0;opacity:0;cursor:pointer;width:100%;height:100%}
.vm .vm-up-big{display:inline-flex;align-items:center;gap:10px;padding:14px 26px;border-radius:999px;background:var(--vm-accent);color:#fff;font-weight:700;font-size:1.1rem}
.vm .vm-up-big .ti{font-size:24px}.vm .vm-up-drop small{color:var(--vm-muted)}
.vm .vm-up-tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(96px,1fr));gap:8px}.vm .vm-up-tiles:empty{display:none}
.vm .vm-up-tile{position:relative;aspect-ratio:1;border-radius:12px;overflow:hidden;background:var(--vm-soft);display:grid;place-items:center}
.vm .vm-up-tile img{width:100%;height:100%;object-fit:cover;opacity:.55;transition:opacity .3s}
.vm .vm-up-tile.ok img{opacity:1}
.vm .vm-up-tile.ok::after{content:"✓";position:absolute;right:6px;top:6px;width:24px;height:24px;border-radius:50%;background:#1e8e3e;color:#fff;display:grid;place-items:center;font-weight:700;font-size:14px}
.vm .vm-up-tile.bad{outline:3px solid #c62828}
.vm .vm-up-name{font-size:12px;padding:6px;word-break:break-all;text-align:center}
.vm .vm-up-bar{position:absolute;left:8px;right:8px;bottom:8px;height:6px;border-radius:9px;background:rgba(255,255,255,.75);overflow:hidden}
.vm .vm-up-bar b{display:block;height:100%;width:0;background:var(--vm-accent);transition:width .2s}
.vm .vm-up-tile.ok .vm-up-bar{display:none}
.vm .vm-up-err{position:absolute;inset:auto 0 0 0;background:rgba(198,40,40,.92);color:#fff;font-size:11px;padding:4px 6px;line-height:1.2}
.vm .vm-up-status{margin:0;font-weight:600}.vm .vm-up-status:empty{display:none}
.vm .vm-up-more summary{cursor:pointer;color:var(--vm-muted);font-size:14px}.vm .vm-up-more[open]{display:grid;gap:10px}
.vm .vm-up-event h2{margin:0 0 4px}.vm .vm-up-event p{margin:0}
.vm .small{font-size:13px}
.vm .vm-count{margin-left:auto;background:#d97706;color:#fff;border-radius:999px;font-size:12px;padding:1px 8px;font-weight:700}
.vm .vm-main{display:flex;flex-direction:column;gap:18px;min-width:0}
.vm .vm-card{background:#fff;border:1px solid var(--vm-line);border-radius:16px;padding:22px 24px;box-shadow:0 8px 24px rgba(0,0,0,.04)}
.vm .vm-card h2{display:flex;align-items:center;gap:8px;font-size:1.25rem;margin:0 0 14px}
.vm .vm-card h2 .ti{color:var(--vm-accent)}
.vm .vm-grid2{display:grid;grid-template-columns:1fr 1fr;gap:18px}
.vm .vm-back{margin:0 0 14px}.vm .vm-back a{text-decoration:none}
.vm .vm-event-page{display:grid;gap:14px;max-width:760px}.vm .vm-event-page.is-cancelled h1{text-decoration:line-through;opacity:.7}
.vm .vm-event-head{display:flex;gap:18px;align-items:center}.vm .vm-event-head h1{margin:0;font-size:clamp(1.6rem,3vw,2.2rem)}
.vm .vm-date.big{width:84px;padding:12px 0}.vm .vm-date.big b{font-size:2rem}
.vm .vm-tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}
.vm .vm-tile{display:grid;gap:4px;padding:16px;border-radius:16px;background:#fff;border:1px solid var(--vm-line);color:inherit;text-decoration:none;box-shadow:0 6px 18px rgba(0,0,0,.04);transition:transform .15s,box-shadow .15s}
.vm .vm-tile:hover{transform:translateY(-2px);box-shadow:0 10px 24px rgba(0,0,0,.08)}
.vm .vm-tile .ti{font-size:26px;color:var(--vm-accent)}
.vm .vm-tile strong{font-size:1rem}.vm .vm-tile small{color:var(--vm-muted);font-size:13px}
.vm .vm-tile.hot{border-color:var(--vm-accent);background:color-mix(in srgb,var(--vm-accent) 7%,#fff)}.vm .vm-tile.hot small{color:var(--vm-accent);font-weight:600}
.vm .vm-search{display:flex;align-items:center;gap:10px;padding:10px 14px;border:1px solid var(--vm-line);border-radius:12px;background:#fff}
.vm .vm-search input{border:0;outline:0;font:inherit;flex:1;background:transparent}
.vm .vm-person[hidden]{display:none}
@media (max-width:600px){.vm .vm-tiles{grid-template-columns:1fr 1fr}.vm .vm-grid2{grid-template-columns:1fr}}
.vm .vm-lead{margin:0;color:var(--vm-muted)}
.vm .vm-muted{color:var(--vm-muted);font-size:14.5px}
.vm .vm-empty{padding:28px;text-align:center;color:var(--vm-muted);background:var(--vm-soft);border-radius:14px}
.vm .vm-ok,.vm .vm-err{padding:12px 16px;border-radius:12px;font-weight:500}
.vm .vm-ok{background:#e9f6ec;color:#1e6b34}
.vm .vm-err{background:#fdecec;color:#9b1c1c}
.vm .vm-list{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:8px}
.vm .vm-list a{display:flex;flex-direction:column;padding:10px 12px;border-radius:10px;background:var(--vm-soft);text-decoration:none;color:inherit}
.vm .vm-list a span{font-size:14px;color:var(--vm-muted)}
.vm .vm-rows{display:flex;flex-direction:column;gap:8px}
.vm .vm-row{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:12px 14px;border:1px solid var(--vm-line);border-radius:12px;text-decoration:none;color:inherit;background:#fff}
.vm .vm-row:hover{border-color:var(--vm-accent)}
.vm .vm-row-main{display:flex;flex-direction:column;min-width:0}
.vm .vm-row-main small{color:var(--vm-muted)}
.vm .vm-badge{display:inline-block;padding:3px 10px;border-radius:999px;font-size:12.5px;font-weight:600;background:var(--vm-soft);color:var(--vm-muted);white-space:nowrap}
.vm .vm-badge.ok{background:#e9f6ec;color:#1e6b34}.vm .vm-badge.warn{background:#fff4e0;color:#8a5300}.vm .vm-badge.err{background:#fdecec;color:#9b1c1c}
.vm .vm-btn{display:inline-flex;align-items:center;gap:8px;padding:11px 18px;border-radius:var(--btn-radius,10px);border:2px solid var(--vm-accent);background:var(--vm-accent);color:#fff;font-weight:600;cursor:pointer;text-decoration:none;font:inherit;font-weight:600}
.vm .vm-btn.ghost{background:transparent;color:var(--vm-accent)}
.vm .vm-btn.small{padding:6px 12px;font-size:14px}
.vm .vm-link-btn{background:none;border:0;color:var(--vm-muted);cursor:pointer;padding:2px 6px;font-size:16px}
.vm .vm-link-btn:hover{color:#9b1c1c}
.vm .vm-inline{display:inline}
.vm .vm-form{display:flex;flex-direction:column;gap:14px}
.vm .vm-field{display:flex;flex-direction:column;gap:6px;font-weight:500}
.vm .vm-field small{font-weight:400;color:var(--vm-muted)}
.vm .vm-field input,.vm .vm-field select,.vm .vm-field textarea{padding:11px 13px;border:1px solid rgba(0,0,0,.18);border-radius:10px;font:inherit;background:#fff}
.vm .vm-check{display:flex;gap:10px;align-items:flex-start}
.vm .vm-check input{width:18px;height:18px;margin-top:3px;accent-color:var(--vm-accent)}
.vm .vm-row2{display:grid;grid-template-columns:1fr 1fr;gap:14px}
.vm .vm-actions{display:flex;gap:10px;flex-wrap:wrap}
.vm .vm-login{max-width:460px;margin:20px auto}
.vm .vm-login h1{margin:0 0 10px}
.vm .vm-text p{margin:0 0 12px;line-height:1.7}
.vm .vm-doc{font-size:16.5px}
.vm .vm-facts{display:grid;grid-template-columns:auto 1fr;gap:6px 18px;margin:0 0 14px}
.vm .vm-plan th{width:110px;font-variant-numeric:tabular-nums;color:var(--vm-accent);font-size:16px}
.vm .vm-facts dt{color:var(--vm-muted)}.vm .vm-facts dd{margin:0;font-weight:500}
.vm .vm-rsvp{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-top:12px;padding-top:14px;border-top:1px solid var(--vm-line)}
.vm .vm-rsvp span{font-weight:600;margin-right:6px}
.vm .vm-reply-row{border:1px solid var(--vm-line);border-radius:12px;padding:12px 14px;margin:0 0 12px;display:grid;gap:10px}
.vm .vm-reply-row legend{font-weight:700;padding:0 6px}
.vm .vm-choice{display:flex;flex-wrap:wrap;gap:8px}
.vm .vm-choice label{display:inline-flex;align-items:center;gap:6px;border:1px solid var(--vm-line);border-radius:999px;padding:8px 14px;cursor:pointer}
.vm .vm-choice input{accent-color:var(--vm-accent)}
.vm .vm-choice label:has(input:checked){border-color:var(--vm-accent);background:var(--vm-soft);font-weight:600}
.vm .vm-table{width:100%;border-collapse:collapse;font-size:15px}
.vm .vm-table th,.vm .vm-table td{padding:9px 10px;border-bottom:1px solid var(--vm-line);text-align:left;vertical-align:top}
.vm .vm-table th{font-size:13px;color:var(--vm-muted);font-weight:600}
.vm .vm-tabs{display:flex;gap:6px}
.vm .vm-tabs a{padding:8px 16px;border-radius:999px;text-decoration:none;color:inherit;background:var(--vm-soft);font-weight:600}
.vm .vm-tabs a.on{background:var(--vm-accent);color:#fff}
.vm .vm-thumbs{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:10px}
.vm .vm-gallery{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:14px}
.vm .vm-gallery figure{margin:0}
.vm .vm-gallery figcaption{font-size:13px;color:var(--vm-muted);margin-top:6px;display:flex;align-items:center;gap:4px;flex-wrap:wrap}
.vm .vm-thumb{display:block;position:relative;border-radius:12px;overflow:hidden;background:var(--vm-soft);aspect-ratio:1;text-decoration:none;color:inherit}
.vm .vm-thumb img{width:100%;height:100%;object-fit:cover;display:block}
.vm .vm-thumb span{position:absolute;left:0;right:0;bottom:0;padding:6px 8px;font-size:12.5px;color:#fff;background:linear-gradient(transparent,rgba(0,0,0,.65));white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.vm .vm-thumb.file{display:flex;flex-direction:column;align-items:center;justify-content:center;gap:8px}
.vm .vm-thumb.file .ti{font-size:34px;color:var(--vm-accent)}
.vm .vm-thumb.file span{position:static;background:none;color:inherit;text-align:center;white-space:normal}
.vm .vm-people{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:12px}
.vm .vm-person{display:flex;gap:12px;align-items:flex-start;padding:12px;border:1px solid var(--vm-line);border-radius:12px}
.vm .vm-person>span:last-child{display:flex;flex-direction:column;min-width:0}
.vm .vm-person small{color:var(--vm-muted)}
.vm .vm-person a{font-size:14px;overflow:hidden;text-overflow:ellipsis}
.vm .vm-avatar{flex:none;width:42px;height:42px;border-radius:50%;display:grid;place-items:center;background:var(--vm-accent);color:#fff;font-weight:700;overflow:hidden}
.vm .vm-avatar img{width:100%;height:100%;object-fit:cover;display:block}
.vm .vm-photo-row{display:flex;gap:18px;align-items:center;flex-wrap:wrap}.vm .vm-photo-row form{flex:1;min-width:220px}
.vm .vm-details summary{display:flex;align-items:center;gap:14px;cursor:pointer;list-style:none}
.vm .vm-details summary::-webkit-details-marker{display:none}
.vm .vm-details summary strong{flex:1}
.vm .vm-progress{width:140px;height:8px;border-radius:99px;background:var(--vm-soft);overflow:hidden}
.vm .vm-progress span{display:block;height:100%;background:var(--vm-accent)}
.vm .vm-details[open] summary{margin-bottom:12px}
.vm .vm-progress.big{width:100%;height:12px;margin:4px 0}
.vm .vm-progress-text{margin:6px 0 0;font-size:14px;color:var(--vm-muted)}
.vm .vm-chips-row{display:flex;flex-wrap:wrap;gap:8px}
.vm .vm-chips-row a{padding:6px 12px;border-radius:999px;background:var(--vm-soft);text-decoration:none;color:inherit;font-size:14px}
.vm .vm-chips-row a.on{background:var(--vm-accent);color:#fff}
.vm-events{list-style:none;margin:0;padding:0;display:flex;flex-direction:column;gap:12px}
.vm-events li{display:flex;gap:16px;align-items:flex-start}
.vm-events li.is-cancelled .vm-ev strong{text-decoration:line-through}
.vm-date{flex:none;width:64px;border-radius:12px;background:var(--verein-accent,var(--accent,#8a1f33));color:#fff;text-align:center;padding:8px 4px;line-height:1.05}
.vm-date b{display:block;font-size:1.6rem;line-height:1;margin-bottom:3px;font-family:var(--font-head,inherit);font-variant-numeric:lining-nums}
.vm-date small{display:block;text-transform:uppercase;font-size:12px;letter-spacing:.08em;font-weight:700}
.vm-date em{display:block;font-style:normal;font-size:11.5px;opacity:.85;margin-top:2px}
.vm-ev{display:flex;flex-direction:column;gap:3px;min-width:0}
.vm-ev strong{font-size:1.1rem}
.vm-ev small{color:var(--muted,#6b6b6b)}
.vm-ev-text{font-size:15px}
.vm-ev>.vm-badge{align-self:flex-start}
.vm-ev a{font-weight:600;color:var(--verein-accent,var(--accent,#8a1f33))}
.vm-badge{display:inline-block;padding:2px 9px;border-radius:999px;font-size:12px;font-weight:600;background:#eee;color:#555}
.vm-badge.err{background:#fdecec;color:#9b1c1c}
.verein-events .vm-events{max-width:820px;margin:0 auto}
.verein-events.cards .vm-events{max-width:none;display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:16px}
.verein-events.cards .vm-events li{background:#fff;border:1px solid var(--line,rgba(0,0,0,.1));border-radius:16px;padding:16px;box-shadow:0 8px 22px rgba(0,0,0,.05)}
.verein-events .verein-more{text-align:center;margin-top:22px}
@media (max-width:860px){
 .vm .vm-grid2,.vm .vm-row2{grid-template-columns:1fr}
 .vm .vm-card{padding:18px 16px}
 .vm .vm-progress{width:70px}
}
/* The member area's schedule (VereinPublic.AgendaAsync): month headings, one line per date, the answer at the right. */
.vm .vm-agenda{display:flex;flex-direction:column}
.vm .vm-agenda-month{margin:20px 0 4px;font-size:12.5px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:var(--vm-muted)}
.vm .vm-agenda-month:first-child{margin-top:2px}
.vm .vm-agenda-row{display:flex;align-items:center;gap:14px;padding:10px 0;border-top:1px solid var(--vm-line)}
.vm .vm-agenda-month+.vm-agenda-row{border-top:0}
.vm .vm-agenda-main{flex:1 1 auto;min-width:0;display:flex;align-items:center;gap:14px;color:inherit;text-decoration:none}
.vm .vm-agenda-main:hover strong{color:var(--vm-accent)}
.vm .vm-date.sm{width:50px;padding:6px 2px;border-radius:10px}
.vm .vm-date.sm b{font-size:1.3rem}
.vm .vm-date.sm em{font-size:11px;margin-top:1px}
.vm .vm-agenda-text{display:flex;flex-direction:column;gap:1px;min-width:0}
.vm .vm-agenda-text strong{font-size:1rem;line-height:1.3}
.vm .vm-agenda-text strong .ti{color:var(--vm-muted);font-size:.95em;vertical-align:-1px}
.vm .vm-agenda-text small{color:var(--vm-muted);font-size:13.5px;line-height:1.4;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.vm .vm-agenda-desc{font-size:13px!important;opacity:.85}
.vm .vm-agenda-row.is-cancelled .vm-agenda-text strong{text-decoration:line-through;opacity:.65}
.vm .vm-agenda-row.is-cancelled .vm-date{opacity:.45}
.vm .vm-agenda-side{flex:none;display:flex;align-items:center;gap:8px;flex-wrap:wrap;justify-content:flex-end}
.vm .vm-agenda-side a.vm-badge{text-decoration:none}
.vm .vm-quick{display:inline-flex;border:1px solid var(--vm-line);border-radius:999px;overflow:hidden;background:#fff}
.vm .vm-pick{border:0;background:transparent;padding:7px 13px;font:inherit;font-size:13.5px;font-weight:600;color:var(--vm-muted);cursor:pointer;display:inline-flex;align-items:center;gap:4px;white-space:nowrap}
.vm .vm-pick+.vm-pick{border-left:1px solid var(--vm-line)}
.vm .vm-pick:hover{background:var(--vm-soft);color:inherit}
.vm .vm-pick.yes.on{background:#1e7a3c;color:#fff}
.vm .vm-pick.no.on{background:#6b6b6b;color:#fff}
.vm .vm-quick.is-busy{opacity:.6;pointer-events:none}
.vm .vm-agenda-more{margin:12px 0 0;font-weight:600}
.vm .vm-agenda-more a{color:var(--vm-accent);text-decoration:none}
@media (max-width:640px){
 .vm .vm-agenda-row{flex-wrap:wrap;gap:8px 12px}
 .vm .vm-agenda-main{gap:12px}
 .vm .vm-agenda-side{width:100%;justify-content:flex-start;padding-left:62px}
 .vm .vm-agenda-text strong{white-space:normal}
 .vm .vm-pick{padding:9px 14px}
}
/* last, so it wins over the rules above: the head's picture keeps its size, the hint loses its icon on a phone */
.vm .vm-me{flex:none}.vm .vm-tile{align-content:start}
@media (max-width:600px){.vm .vm-hint .vm-hint-icon{display:none}.vm .vm-head h1{font-size:1.6rem}}
/* ---- 1.13: the member area on a phone ---- */
/* a field never sticks out of its card: its padding and border are part of its width */
.vm input,.vm select,.vm textarea,.vm button{box-sizing:border-box;max-width:100%}
.vm .vm-head h1{overflow-wrap:break-word;hyphens:auto}
/* an answer is one segmented row: Dabei | Vielleicht | Nicht dabei */
.vm .vm-reply-row{border:1px solid var(--vm-line);border-radius:12px;padding:10px 12px;margin:0 0 10px;display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:8px 12px}
.vm .vm-reply-name{font-weight:700}
.vm .vm-choice{display:inline-flex;flex-wrap:nowrap;gap:0;border:1px solid var(--vm-line);border-radius:999px;overflow:hidden;background:#fff;max-width:100%}
.vm .vm-choice label{position:relative;flex:1 1 auto;justify-content:center;border:0;border-radius:0;padding:8px 14px;white-space:nowrap;gap:0;font-size:14.5px}
.vm .vm-choice label+label{border-left:1px solid var(--vm-line)}
.vm .vm-choice input{position:absolute;opacity:0;pointer-events:none}
.vm .vm-choice label:has(input:checked){background:var(--vm-accent);color:#fff;font-weight:600}
.vm .vm-choice label:has(input:focus-visible){box-shadow:inset 0 0 0 2px var(--vm-accent)}
.vm .vm-reply-row .vm-note{grid-column:1/-1;padding:7px 11px;font-size:14px;border:1px solid var(--vm-line);border-radius:9px;width:100%}
/* the board's view of the answers: folded groups, names in columns */
.vm .vm-group{border-top:1px solid var(--vm-line);padding:8px 0}
.vm .vm-group summary{cursor:pointer;font-weight:600;display:flex;align-items:center;gap:8px}
.vm .vm-names{list-style:none;margin:8px 0 4px;padding:0;columns:2 220px;column-gap:24px;font-size:14.5px}
.vm .vm-names li{break-inside:avoid;padding:2px 0}
.vm .vm-names small{color:var(--vm-muted)}
/* a meeting's answer */
.vm .vm-rsvp{display:flex;align-items:center;gap:10px;flex-wrap:wrap}
.vm .vm-seg{display:inline-flex;border:1px solid var(--vm-line);border-radius:999px;overflow:hidden;background:#fff}
.vm .vm-seg .vm-pick+.vm-pick{border-left:1px solid var(--vm-line)}
.vm .vm-seg .vm-pick.on:not(.yes):not(.no){background:var(--vm-accent);color:#fff}
/* the member list: one line per person, the rest folds out */
.vm .vm-people{gap:10px;grid-template-columns:repeat(auto-fill,minmax(300px,1fr))}
.vm details.vm-person{display:block;padding:0;border:1px solid var(--vm-line);border-radius:12px;background:#fff}
.vm details.vm-person summary{display:flex;align-items:center;gap:12px;padding:10px 12px;cursor:pointer;list-style:none}
.vm details.vm-person summary::-webkit-details-marker{display:none}
.vm .vm-person-main{display:flex;flex-direction:column;min-width:0;flex:1 1 auto}
/* a name breaks between words (a card is wide enough for "Baumgärtner"); the roles may take two lines instead of being cut to "Mitglied, Kas…" */
.vm .vm-person-main strong{overflow-wrap:break-word;line-height:1.3}
.vm .vm-person-main small{display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;line-height:1.35}
.vm .vm-person-acts{display:flex;gap:6px;flex:none}
.vm .vm-icon-btn{display:inline-grid;place-items:center;width:38px;height:38px;border-radius:50%;border:1px solid var(--vm-line);color:var(--vm-accent);background:#fff;text-decoration:none;font-size:18px}
.vm .vm-icon-btn:hover{background:var(--vm-soft)}
.vm .vm-person-more{display:flex;flex-direction:column;gap:3px;padding:0 12px 12px 64px;font-size:14px}
.vm .vm-person-more small{color:var(--vm-muted)}
@media (max-width:600px){
 .vm .vm-tiles{grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}
 .vm .vm-tile{padding:12px 6px;justify-items:center;text-align:center;gap:4px;border-radius:14px}
 .vm .vm-tile .ti{font-size:24px}
 .vm .vm-tile strong{font-size:12.5px;line-height:1.25}
 .vm .vm-tile small{display:none}
 .vm .vm-tile.hot small{display:block;font-size:11px}
 .vm .vm-todo.is-empty{display:none}
 .vm .vm-reply-row{grid-template-columns:1fr}
 .vm .vm-choice{display:flex;width:100%}
 .vm .vm-choice label{padding:9px 6px}
 .vm .vm-rsvp .vm-seg{display:flex;width:100%}
 .vm .vm-rsvp .vm-pick{flex:1;justify-content:center}
 .vm .vm-people{grid-template-columns:1fr;gap:8px}
 .vm .vm-person-more{padding-left:12px}
}
/* a long word (an e-mail address, a name) may not widen its column — it made the profile's card and the member list wider than the phone */
.vm .vm-grid2>*,.vm .vm-row2>*,.vm .vm-people>*,.vm .vm-main>*{min-width:0}
.vm .vm-facts dd,.vm .vm-person-more a{overflow-wrap:break-word;min-width:0}
.vm .vm-reply h3{margin-top:22px}
/* the calendar (/mitglied/kalender): a month grid on a wide screen, the list on a phone — both from the site's own variables */
.vm .vm-cal{display:grid;gap:16px}
.vm .vm-cal-head{display:flex;justify-content:space-between;align-items:center;gap:12px;flex-wrap:wrap}
.vm .vm-cal-nav{display:flex;align-items:center;gap:8px}.vm .vm-cal-nav h2{margin:0 6px;font-size:1.35rem;min-width:9em;text-align:center}
.vm .vm-cal-grid{border:1px solid var(--vm-line);border-radius:14px;overflow:hidden;background:#fff}
.vm .vm-cal-row{display:grid;grid-template-columns:repeat(7,minmax(0,1fr))}
.vm .vm-cal-row+.vm-cal-row{border-top:1px solid var(--vm-line)}
.vm .vm-cal-dow span{padding:8px;font-size:12.5px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;color:var(--vm-muted);text-align:center;background:var(--vm-soft)}
.vm .vm-cal-day{min-height:96px;padding:6px;display:flex;flex-direction:column;gap:3px;min-width:0}
.vm .vm-cal-day+.vm-cal-day{border-left:1px solid var(--vm-line)}
.vm .vm-cal-day.out{background:var(--vm-soft)}.vm .vm-cal-day.out .vm-cal-num{opacity:.5}
.vm .vm-cal-num{font-size:13px;font-weight:700;width:26px;height:26px;display:grid;place-items:center;border-radius:50%}
.vm .vm-cal-day.today .vm-cal-num{background:var(--vm-accent);color:#fff}
.vm .vm-cal-item{display:block;font-size:12.5px;line-height:1.3;padding:3px 6px;border-radius:7px;text-decoration:none;color:inherit;background:color-mix(in srgb,var(--vm-accent) 10%,#fff);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.vm .vm-cal-item:hover{background:color-mix(in srgb,var(--vm-accent) 18%,#fff)}
.vm .vm-cal-item .ti{color:var(--vm-accent)}
.vm .vm-cal-item.t-sitzungen{background:var(--vm-soft)}
.vm .vm-cal-item.t-geburtstage,.vm .vm-cal-item.t-eintritte{background:transparent;border:1px dashed var(--vm-line)}
.vm .vm-cal-item.is-round{border-color:var(--vm-accent);font-weight:600}
.vm .vm-cal-item.is-cancelled{text-decoration:line-through;opacity:.65}
.vm .vm-cal-list:not(.is-list){display:none}
.vm .vm-agenda-text .ti{color:var(--vm-accent)}
@media (max-width:700px){.vm .vm-cal-grid{display:none}.vm .vm-cal-list:not(.is-list){display:block}.vm .vm-cal-nav h2{min-width:0;font-size:1.15rem}}
/* a piece of the member area placed by the block "Verein: Baustein" on a page the editors laid out */
.vm.verein-slot{display:grid;gap:18px}
""";
}
