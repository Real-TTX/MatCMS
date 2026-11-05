// The look of the store's pages. Every colour reads the SITE's variables first (--accent, --line, --bg-alt, --muted, and matmon.eu's
// --mm-* where they exist), so the pages follow the template, its dark mode included; the numbers after them are only fallbacks.
public static class MtCss
{
    public const string Css = """
        .mtpl{--t-line:var(--mm-line,var(--line,rgba(127,127,127,.25)));--t-accent:var(--accent,#2563eb);--t-soft:var(--bg-alt,rgba(127,127,127,.07));--t-muted:var(--mm-muted,var(--muted,#667085));--t-radius:12px;color:inherit}
        .mtpl h1{margin:0 0 8px;font-size:clamp(1.7rem,3.2vw,2.4rem)}.mtpl h2{font-size:1.15rem;margin:28px 0 10px}.mtpl h3{font-size:1rem;margin:0 0 8px}
        .mtpl a{color:inherit}.mtpl-muted{color:var(--t-muted);font-size:.92em}
        .mtpl-head{display:flex;flex-wrap:wrap;gap:16px 24px;align-items:flex-start;justify-content:space-between;margin:0 0 22px}
        .mtpl-head>div:first-child{flex:1 1 420px;min-width:0}.mtpl-lead{color:var(--t-muted);max-width:720px}.mtpl-lead p{margin:0 0 8px}
        .mtpl-actions{display:flex;flex-wrap:wrap;gap:8px;align-items:center}
        .mtpl-btn,.mtpl button.mtpl-btn{display:inline-flex;align-items:center;gap:6px;padding:10px 18px;border-radius:var(--btn-radius,8px);border:1px solid var(--t-accent);background:var(--t-accent);color:#fff;font:inherit;font-weight:600;cursor:pointer;text-decoration:none;white-space:nowrap}
        .mtpl a.mtpl-btn{color:#fff}.mtpl-btn.ghost,.mtpl a.mtpl-btn.ghost{background:transparent;color:inherit;border-color:var(--t-line)}
        .mtpl-btn:hover{filter:brightness(1.08)}
        .mtpl-bar{display:flex;flex-wrap:wrap;gap:8px;margin:0 0 18px}.mtpl-bar input[type=search]{flex:1 1 280px;min-width:0}
        .mtpl input,.mtpl select,.mtpl textarea{padding:9px 12px;border:1px solid var(--t-line);border-radius:8px;font:inherit;background:transparent;color:inherit;max-width:100%}
        .mtpl select option{color:#111}
        .mtpl-active{margin:-6px 0 16px;color:var(--t-muted)}
        .mtpl-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:16px}
        .mtpl-card{border:1px solid var(--t-line);border-radius:var(--t-radius);padding:18px;display:flex;flex-direction:column;gap:8px;background:var(--t-soft)}
        .mtpl-card h2{margin:0;font-size:1.08rem}.mtpl-card h2 a{text-decoration:none}.mtpl-card h2 a:hover{color:var(--t-accent)}.mtpl-card p{margin:0;color:var(--t-muted)}
        .mtpl-meta{display:flex;flex-wrap:wrap;gap:4px 14px;color:var(--t-muted);font-size:.88em;margin:6px 0}
        .mtpl-chips{display:flex;flex-wrap:wrap;gap:6px}
        .mtpl-chip{display:inline-block;padding:2px 10px;border-radius:999px;border:1px solid var(--t-line);font-size:.82em;text-decoration:none;color:inherit}
        a.mtpl-chip:hover{border-color:var(--t-accent);color:var(--t-accent)}
        .mtpl-empty{padding:40px 20px;text-align:center;border:1px dashed var(--t-line);border-radius:var(--t-radius);color:var(--t-muted)}
        .mtpl-pager{display:flex;gap:18px;justify-content:center;align-items:center;margin:24px 0 0}
        .mtpl-crumbs{margin:0 0 10px;font-size:.92em;color:var(--t-muted)}.mtpl-crumbs a{color:var(--t-muted)}
        .mtpl-cols{display:grid;grid-template-columns:minmax(0,1fr) 320px;gap:28px;align-items:start}
        .mtpl-cols aside{display:flex;flex-direction:column;gap:16px}
        .mtpl-box{border:1px solid var(--t-line);border-radius:var(--t-radius);padding:16px 18px;background:var(--t-soft)}.mtpl-box p{margin:0 0 8px}
        .mtpl-text p{margin:0 0 12px;max-width:72ch}
        .mtpl-table{width:100%;border-collapse:collapse;font-size:.95em}.mtpl-table th,.mtpl-table td{text-align:left;padding:9px 10px;border-bottom:1px solid var(--t-line);vertical-align:top}
        .mtpl-table th{font-size:.8em;text-transform:uppercase;letter-spacing:.04em;color:var(--t-muted)}.mtpl-table code{font-size:.9em}
        .mtpl-versions{list-style:none;margin:0;padding:0}.mtpl-versions li{padding:8px 0;border-top:1px solid var(--t-line)}.mtpl-versions li:first-child{border-top:0}
        .mtpl-form{display:flex;flex-direction:column;gap:16px;max-width:760px}
        .mtpl-field{display:flex;flex-direction:column;gap:6px}.mtpl-field>span{font-weight:600}.mtpl-field small{color:var(--t-muted)}
        .mtpl-row{display:grid;grid-template-columns:2fr 1fr;gap:16px}
        .mtpl-check{display:flex;gap:10px;align-items:flex-start}.mtpl-check input{margin-top:4px}
        .mtpl-alert,.mtpl-ok{padding:12px 16px;border-radius:10px;margin:0 0 18px}
        .mtpl-alert{background:rgba(220,38,38,.1);border:1px solid rgba(220,38,38,.35)}.mtpl-ok{background:rgba(22,163,74,.1);border:1px solid rgba(22,163,74,.35)}
        .mtpl-badge{display:inline-block;padding:2px 9px;border-radius:999px;font-size:.82em;border:1px solid var(--t-line)}
        .mtpl-badge.ok{background:rgba(22,163,74,.12);border-color:rgba(22,163,74,.35)}.mtpl-badge.warn{background:rgba(217,119,6,.12);border-color:rgba(217,119,6,.35)}.mtpl-badge.err{background:rgba(220,38,38,.1);border-color:rgba(220,38,38,.35)}
        @media (max-width:820px){.mtpl-cols{grid-template-columns:1fr}.mtpl-row{grid-template-columns:1fr}}
        """;
}
