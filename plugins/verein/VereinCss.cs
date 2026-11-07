namespace MatCMS.Plugin.Verein;

/// <summary>The member area's and the events block's look — built on the site's own variables (accent, fonts), so it follows the template by itself.</summary>
public static class VereinCss
{
    public const string Css = """
.vm{--vm-accent:var(--verein-accent,var(--accent,#8a1f33));--vm-line:var(--line,rgba(0,0,0,.1));--vm-soft:var(--bg-alt,#f6f3ee);--vm-muted:var(--muted,#6b6b6b)}
.vm .vm-kicker{font-size:12.5px;font-weight:700;letter-spacing:.14em;text-transform:uppercase;color:var(--vm-accent);margin:0 0 6px}
.vm .vm-head{display:flex;justify-content:space-between;align-items:flex-end;gap:16px;flex-wrap:wrap;margin-bottom:22px}
.vm .vm-head h1{margin:0;font-size:clamp(1.7rem,3vw,2.3rem)}
.vm .vm-me{display:flex;align-items:center;gap:12px;color:var(--vm-muted);font-size:14px;flex-wrap:wrap}
.vm .vm-layout{display:grid;grid-template-columns:230px 1fr;gap:26px;align-items:start}
.vm .vm-nav{display:flex;flex-direction:column;gap:4px;position:sticky;top:100px}
.vm .vm-nav a{display:flex;align-items:center;gap:10px;padding:10px 12px;border-radius:10px;text-decoration:none;color:inherit;font-weight:500}
.vm .vm-nav a:hover{background:var(--vm-soft)}
.vm .vm-nav a.on{background:var(--vm-accent);color:#fff}
.vm .vm-nav .ti{font-size:19px}
.vm .vm-count{margin-left:auto;background:#d97706;color:#fff;border-radius:999px;font-size:12px;padding:1px 8px;font-weight:700}
.vm .vm-main{display:flex;flex-direction:column;gap:18px;min-width:0}
.vm .vm-card{background:#fff;border:1px solid var(--vm-line);border-radius:16px;padding:22px 24px;box-shadow:0 8px 24px rgba(0,0,0,.04)}
.vm .vm-card h2{display:flex;align-items:center;gap:8px;font-size:1.25rem;margin:0 0 14px}
.vm .vm-card h2 .ti{color:var(--vm-accent)}
.vm .vm-grid2{display:grid;grid-template-columns:1fr 1fr;gap:18px}
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
.vm-date b{display:block;font-size:1.6rem;font-family:var(--font-head,inherit)}
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
 .vm .vm-layout{grid-template-columns:1fr}
 .vm .vm-nav{position:static;flex-direction:row;overflow-x:auto;gap:6px;padding-bottom:4px}
 .vm .vm-nav a{white-space:nowrap;background:var(--vm-soft)}
 .vm .vm-grid2,.vm .vm-row2{grid-template-columns:1fr}
 .vm .vm-card{padding:18px 16px}
 .vm .vm-progress{width:70px}
}
""";
}
