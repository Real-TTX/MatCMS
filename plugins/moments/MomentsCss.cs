namespace MatCMS.Plugin.Moments;

/// <summary>
/// The guest area's look — built on the site's own variables (accent, lines, fonts), so it follows the template by itself; <c>--moments-accent</c> lets a
/// template choose another colour for it. Phone first: three photos a row edge to edge, the photo page full width, chips in one scrolling line.
/// </summary>
public static class MomentsCss
{
    public const string Css = """
.mo{--mo-accent:var(--moments-accent,var(--accent,#8f6747));--mo-line:var(--line,rgba(0,0,0,.12));--mo-soft:var(--bg-alt,#f5efe7);--mo-muted:var(--muted,#6f6259);--mo-ink:var(--ink,#2b211b)}
.mo .mo-kicker{font-size:12.5px;font-weight:600;letter-spacing:.16em;text-transform:uppercase;color:var(--mo-accent);margin:0 0 6px}
.mo .mo-kicker a{color:inherit;text-decoration:none}.mo .mo-kicker a:hover{text-decoration:underline}
.mo .mo-head{display:flex;justify-content:space-between;align-items:flex-end;gap:16px;margin-bottom:22px}
.mo .mo-head h1{margin:0;font-size:clamp(1.7rem,4vw,2.5rem);overflow-wrap:anywhere}
.mo .mo-me{display:inline-flex;align-items:center;gap:10px;color:inherit;font-size:14px;font-weight:600;text-decoration:none;padding:4px 14px 4px 4px;border:1px solid var(--mo-line);border-radius:999px;background:rgba(255,255,255,.6);flex:none}
.mo .mo-avatar{width:36px;height:36px;border-radius:50%;background:var(--mo-accent);color:#fff;display:inline-grid;place-items:center;font-size:13px;font-weight:700;letter-spacing:.04em;flex:none}
.mo .mo-avatar.small{width:30px;height:30px;font-size:11.5px}
.mo .mo-muted{color:var(--mo-muted)}.mo .small{font-size:13.5px}
.mo .mo-btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;padding:11px 22px;border-radius:999px;border:1.5px solid var(--mo-accent);background:var(--mo-accent);color:#fff;font:inherit;font-size:15px;font-weight:600;line-height:1.2;text-decoration:none;cursor:pointer;white-space:nowrap}
.mo .mo-btn:hover{filter:brightness(1.08)}
.mo .mo-btn.ghost{background:transparent;color:var(--mo-accent)}.mo .mo-btn.ghost:hover{background:color-mix(in srgb,var(--mo-accent) 9%,transparent);filter:none}
.mo .mo-btn.danger{border-color:#b3261e;background:#b3261e;color:#fff}.mo .mo-btn.ghost.danger{background:transparent;color:#b3261e}
.mo .mo-btn .ti{font-size:18px}
.mo .mo-alert{padding:12px 16px;border-radius:12px;background:var(--mo-soft);border:1px solid var(--mo-line);margin:0 0 18px}
.mo .mo-alert.ok{background:#e7f4ea;border-color:#b9dcc2;color:#1d5b2c}.mo .mo-alert.bad{background:#fbe9e7;border-color:#efc2bc;color:#8c1d18}
.mo .mo-alert a{color:inherit;font-weight:600}

/* sign-in: one card, one field */
.mo .mo-login{max-width:480px;margin:24px auto 40px;padding:34px 30px;border-radius:20px;background:#fff;border:1px solid var(--mo-line);box-shadow:0 18px 44px rgba(40,24,12,.10);text-align:center}
.mo .mo-login-title{margin:0 0 10px;font-size:clamp(1.8rem,5vw,2.4rem)}
.mo .mo-login-intro{color:var(--mo-muted);margin:0 0 22px}
.mo .mo-code{display:grid;gap:12px;text-align:left}
.mo .mo-code label{font-size:13px;font-weight:600;letter-spacing:.08em;text-transform:uppercase;color:var(--mo-muted)}
.mo .mo-code-input{font:inherit;font-size:1.6rem;letter-spacing:.22em;text-transform:uppercase;text-align:center;padding:14px 12px;border-radius:14px;border:1.5px solid var(--mo-line);background:#fff;width:100%;box-sizing:border-box;color:var(--mo-ink)}
.mo .mo-code-input:focus{outline:none;border-color:var(--mo-accent);box-shadow:0 0 0 4px color-mix(in srgb,var(--mo-accent) 18%,transparent)}
.mo .mo-code .mo-btn{padding:14px;font-size:16px}
.mo .mo-text-input{font:inherit;font-size:16px;padding:12px 14px;border-radius:12px;border:1.5px solid var(--mo-line);background:#fff;width:100%;box-sizing:border-box;color:var(--mo-ink)}
.mo .mo-text-input:focus{outline:none;border-color:var(--mo-accent);box-shadow:0 0 0 4px color-mix(in srgb,var(--mo-accent) 18%,transparent)}
.mo .mo-qr-hint{display:flex;gap:12px;align-items:flex-start;text-align:left;margin:18px 0 0;padding:14px 16px;border-radius:14px;background:var(--mo-soft);font-size:14.5px;line-height:1.45;color:var(--mo-ink)}
.mo .mo-qr-hint .ti{font-size:30px;color:var(--mo-accent);flex:none;line-height:1}
.mo .mo-alt{margin-top:18px;text-align:left}.mo .mo-alt summary{cursor:pointer;color:var(--mo-accent);font-weight:600;text-align:center;padding:6px 0}
.mo .mo-alt .mo-code{margin-top:10px}

/* welcome */
.mo .mo-welcome{display:flex;justify-content:space-between;align-items:center;gap:20px;padding:22px 26px;border-radius:18px;background:var(--mo-soft);margin-bottom:26px}
.mo .mo-welcome h2{margin:0 0 6px;font-size:1.45rem}.mo .mo-welcome p{margin:0}
.mo .mo-table{margin-top:10px!important;color:var(--mo-accent)}
.mo .mo-welcome-actions{display:flex;gap:10px;flex-wrap:wrap;flex:none}.mo .mo-welcome-actions.left{margin-bottom:20px}

/* highlights: a row that scrolls sideways */
.mo .mo-highlights{margin-bottom:26px}.mo .mo-highlights h2{font-size:1.15rem;margin:0 0 12px;display:flex;align-items:center;gap:8px}
.mo .mo-strip{display:grid;grid-auto-flow:column;grid-auto-columns:minmax(200px,240px);gap:12px;overflow-x:auto;scroll-snap-type:x mandatory;padding-bottom:6px}
.mo .mo-strip .mo-tile{aspect-ratio:4/5;scroll-snap-align:start;border-radius:14px}

/* the bar and the chips */
.mo .mo-bar{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:12px;flex-wrap:wrap}
.mo .mo-seg{display:inline-flex;border:1px solid var(--mo-line);border-radius:999px;padding:3px;background:#fff}
.mo .mo-seg a{padding:7px 14px;border-radius:999px;color:inherit;text-decoration:none;font-size:14px;font-weight:600}
.mo .mo-seg a.on{background:var(--mo-accent);color:#fff}
.mo .mo-count{color:var(--mo-muted);font-size:14px}
.mo .mo-filters,.mo .mo-active,.mo .mo-chiprow{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:12px}
.mo .mo-filters{margin-bottom:20px}
.mo .mo-chip{display:inline-flex;align-items:center;gap:6px;padding:6px 13px;border-radius:999px;border:1px solid var(--mo-line);background:#fff;color:inherit;text-decoration:none;font-size:14px;line-height:1.3;white-space:nowrap}
.mo .mo-chip small{color:var(--mo-muted);font-size:12px}
.mo .mo-chip.person{border-color:color-mix(in srgb,var(--mo-accent) 35%,transparent);background:color-mix(in srgb,var(--mo-accent) 7%,#fff)}
.mo .mo-chip.on{background:var(--mo-accent);border-color:var(--mo-accent);color:#fff}.mo .mo-chip.on small{color:#fff}
.mo .mo-chip a{color:inherit;text-decoration:none;display:inline-flex;align-items:center;gap:6px}
.mo .mo-chip form{display:inline;margin:0}.mo .mo-chip button{border:0;background:none;padding:0 0 0 2px;cursor:pointer;color:var(--mo-muted);font-size:15px;line-height:1}

/* the grid: square tiles, an hour heading across the row */
.mo .mo-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(160px,1fr));gap:8px}
.mo .mo-hour{grid-column:1/-1;margin:14px 0 2px;font-size:.95rem;font-weight:600;letter-spacing:.12em;text-transform:uppercase;color:var(--mo-accent)}
.mo .mo-hour:first-child{margin-top:0}
.mo .mo-tile{position:relative;display:block;aspect-ratio:1;border-radius:10px;overflow:hidden;background:var(--mo-soft)}
.mo .mo-tile img{width:100%;height:100%;object-fit:cover;display:block;transition:transform .35s}
.mo .mo-tile:hover img{transform:scale(1.04)}
.mo .mo-tile-meta{position:absolute;left:0;right:0;bottom:0;display:flex;gap:10px;padding:18px 8px 6px;background:linear-gradient(transparent,rgba(0,0,0,.55));color:#fff;font-size:12.5px;font-weight:600}
.mo .mo-tile-meta span{display:inline-flex;align-items:center;gap:3px}
.mo .mo-tile-badge{position:absolute;left:6px;top:6px;padding:3px 8px;border-radius:999px;background:rgba(255,255,255,.92);color:var(--mo-ink);font-size:11.5px;font-weight:600}
.mo .mo-empty{display:grid;justify-items:center;gap:10px;padding:50px 20px;border:2px dashed var(--mo-line);border-radius:18px;text-align:center;color:var(--mo-muted)}
.mo .mo-empty > .ti{font-size:42px;color:var(--mo-accent)}.mo .mo-empty p{margin:0}
.mo .mo-pager{display:flex;justify-content:center;align-items:center;gap:16px;margin-top:26px}

/* one photo */
.mo .mo-back{display:flex;justify-content:space-between;align-items:center;margin:0 0 12px}
.mo .mo-back a{color:var(--mo-accent);text-decoration:none;font-weight:600;display:inline-flex;align-items:center;gap:4px}
.mo .mo-pos{color:var(--mo-muted);font-size:14px}
.mo .mo-stage{position:relative;margin:0 0 22px;background:#16120f;border-radius:16px;display:grid;grid-template-columns:minmax(0,1fr);place-items:center;min-height:240px;overflow:hidden;touch-action:pan-y}
.mo .mo-stage img{display:block;min-width:0;max-width:100%;max-height:78vh;width:auto;height:auto;object-fit:contain}
.mo .mo-nav{position:absolute;top:50%;transform:translateY(-50%);width:46px;height:46px;border-radius:50%;background:rgba(255,255,255,.88);color:#222;display:grid;place-items:center;text-decoration:none;font-size:24px;box-shadow:0 4px 14px rgba(0,0,0,.25)}
.mo .mo-nav.prev{left:12px}.mo .mo-nav.next{right:12px}
.mo .mo-detail{display:grid;grid-template-columns:minmax(0,1.1fr) minmax(0,1fr);gap:28px;align-items:start}
.mo .mo-by{display:flex;gap:12px;align-items:center;margin-bottom:12px}.mo .mo-by small{display:block;color:var(--mo-muted)}
.mo .mo-caption{font-size:1.12rem;margin:0 0 14px}
.mo .mo-actions{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin-bottom:14px}
.mo .mo-inline{display:inline;margin:0}
.mo .mo-heart{display:inline-flex;align-items:center;gap:8px;padding:10px 20px;border-radius:999px;border:1.5px solid var(--mo-line);background:#fff;font:inherit;font-weight:600;font-size:15px;cursor:pointer;color:inherit}
.mo .mo-heart .ti{font-size:20px;color:#c2185b}.mo .mo-heart.on{border-color:#f3b6cb;background:#fdecf2}
.mo .mo-confirm{position:relative}.mo .mo-confirm summary{list-style:none}.mo .mo-confirm summary::-webkit-details-marker{display:none}
.mo .mo-confirm[open] form{position:absolute;z-index:5;top:calc(100% + 6px);left:0;min-width:240px;padding:14px;border-radius:14px;background:#fff;border:1px solid var(--mo-line);box-shadow:0 12px 30px rgba(0,0,0,.15)}
.mo .mo-confirm form p{margin:0 0 10px}
.mo .mo-edit{margin-bottom:14px}.mo .mo-edit summary{cursor:pointer;color:var(--mo-accent);font-weight:600}
.mo .mo-row{display:flex;gap:8px;margin-top:10px}
.mo .mo-row input,.mo .mo-comment-form textarea{flex:1;min-width:0;font:inherit;font-size:16px;padding:10px 14px;border-radius:12px;border:1.5px solid var(--mo-line);background:#fff;color:var(--mo-ink);box-sizing:border-box}
.mo .mo-row input:focus,.mo .mo-comment-form textarea:focus{outline:none;border-color:var(--mo-accent)}
.mo .mo-tags h2,.mo .mo-comments h2{font-size:1.1rem;margin:6px 0 12px}
.mo .mo-commentlist{list-style:none;margin:0 0 14px;padding:0;display:grid;gap:14px}
.mo .mo-commentlist li{display:grid;grid-template-columns:auto 1fr auto;gap:10px;align-items:start}
.mo .mo-commentlist li.waiting{opacity:.65}
.mo .mo-commentlist p{margin:0;overflow-wrap:anywhere}.mo .mo-comment-head small{color:var(--mo-muted)}
.mo .mo-x{border:0;background:none;color:var(--mo-muted);cursor:pointer;font-size:17px;padding:4px}
.mo .mo-comment-form{display:grid;gap:8px;justify-items:end}.mo .mo-comment-form textarea{width:100%;resize:vertical}
.mo .mo-photo-page .mo-alert.ok{position:fixed;left:50%;bottom:22px;transform:translateX(-50%);z-index:60;box-shadow:0 10px 30px rgba(0,0,0,.18);animation:mo-toast 3.4s forwards;pointer-events:none;white-space:nowrap}
@keyframes mo-toast{0%{opacity:0;transform:translate(-50%,10px)}8%{opacity:1;transform:translate(-50%,0)}80%{opacity:1}100%{opacity:0;visibility:hidden}}

/* upload */
.mo .mo-card{padding:22px;border-radius:18px;background:#fff;border:1px solid var(--mo-line)}
.mo .mo-upload form{display:grid;gap:14px}
.mo .mo-drop{position:relative;display:grid;justify-items:center;gap:8px;padding:38px 16px;border:2px dashed color-mix(in srgb,var(--mo-accent) 45%,transparent);border-radius:18px;background:color-mix(in srgb,var(--mo-accent) 6%,#fff);cursor:pointer;text-align:center}
.mo .mo-drop:hover{background:color-mix(in srgb,var(--mo-accent) 11%,#fff)}
.mo .mo-drop input{position:absolute;inset:0;opacity:0;cursor:pointer;width:100%;height:100%}
.mo .mo-big{display:inline-flex;align-items:center;gap:10px;padding:15px 28px;border-radius:999px;background:var(--mo-accent);color:#fff;font-weight:700;font-size:1.12rem}
.mo .mo-big .ti{font-size:26px}.mo .mo-drop small{color:var(--mo-muted)}
.mo .mo-up-tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(90px,1fr));gap:8px}.mo .mo-up-tiles:empty{display:none}
.mo .mo-up-tile{position:relative;aspect-ratio:1;border-radius:12px;overflow:hidden;background:var(--mo-soft)}
.mo .mo-up-tile img{width:100%;height:100%;object-fit:cover;opacity:.55;transition:opacity .3s}.mo .mo-up-tile.ok img{opacity:1}
.mo .mo-up-tile.ok::after{content:"✓";position:absolute;right:6px;top:6px;width:24px;height:24px;border-radius:50%;background:#1e8e3e;color:#fff;display:grid;place-items:center;font-weight:700;font-size:14px}
.mo .mo-up-tile.bad{outline:3px solid #c62828}
.mo .mo-up-bar{position:absolute;left:8px;right:8px;bottom:8px;height:6px;border-radius:9px;background:rgba(255,255,255,.8);overflow:hidden}
.mo .mo-up-bar b{display:block;height:100%;width:0;background:var(--mo-accent);transition:width .2s}.mo .mo-up-tile.ok .mo-up-bar{display:none}
.mo .mo-up-err{position:absolute;inset:auto 0 0 0;background:rgba(198,40,40,.92);color:#fff;font-size:11px;padding:4px 6px;line-height:1.2}
.mo .mo-up-status{margin:0;font-weight:600}.mo .mo-up-status:empty{display:none}
.mo .mo-up-done{margin:14px 0 0}

/* a block on an ordinary page (the guest area's entrance) */
.mo-block .mo-welcome{margin-bottom:18px}
.mo-block .mo-preview{display:grid;grid-template-columns:repeat(6,1fr);gap:6px}
.mo-block .mo-preview .mo-tile{border-radius:8px}

@media (max-width:760px){
  .mo .mo-detail{grid-template-columns:1fr;gap:10px}
  .mo .mo-welcome{flex-direction:column;align-items:stretch;padding:18px}
  .mo .mo-welcome-actions .mo-btn{flex:1}
  .mo-block .mo-preview{grid-template-columns:repeat(3,1fr)}
}
@media (max-width:440px){
  /* the three sort buttons fill a phone's width; the count would wrap into a line of its own */
  .mo .mo-count{display:none}
}
@media (max-width:600px){
  .section.mo{padding-top:22px;padding-bottom:36px}
  .mo .mo-strip{grid-auto-columns:42vw;gap:8px}
  .mo .mo-head{align-items:center}
  .mo .mo-me{padding:3px}.mo .mo-me-name{display:none}
  .mo .mo-grid{grid-template-columns:repeat(3,1fr);gap:3px;margin:0 -12px}
  .mo .mo-grid .mo-tile{border-radius:2px}
  .mo .mo-hour{padding:0 12px;font-size:.8rem}
  .mo .mo-tile-meta{font-size:11px;gap:6px;padding:14px 5px 4px}
  .mo .mo-filters{flex-wrap:nowrap;overflow-x:auto;margin:0 -12px 16px;padding:0 12px 4px;scrollbar-width:none}
  .mo .mo-filters::-webkit-scrollbar{display:none}
  .mo .mo-bar{flex-wrap:nowrap}
  .mo .mo-seg{min-width:0}
  .mo .mo-seg a{padding:7px 11px;font-size:13px;white-space:nowrap}
  .mo .mo-count{white-space:nowrap;font-size:13px}
  .mo .mo-stage{margin:0 -12px 16px;border-radius:0}
  .mo .mo-stage img{max-height:72vh}
  .mo .mo-nav{width:38px;height:38px;font-size:20px;background:rgba(255,255,255,.7)}
  .mo .mo-nav.prev{left:6px}.mo .mo-nav.next{right:6px}
  .mo .mo-login{padding:26px 18px;margin-top:8px}
  .mo .mo-card{padding:16px}
  .mo .mo-photo-page .mo-alert.ok{white-space:normal;width:calc(100% - 32px);text-align:center}
}
""";
}
