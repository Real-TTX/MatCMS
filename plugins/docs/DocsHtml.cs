using System.Globalization;
using System.Text;

namespace MatCMS.Plugin.Docs;

/// <summary>The pieces every docs page is made of (sidebar, breadcrumbs, table of contents, previous/next), the template parts' defaults and the stylesheet.
/// Every value is encoded where it is written; the template parts get finished markup only in their <c>*_html</c> placeholders.</summary>
internal static class DocsHtml
{
    public static string E(string? s) => DocsMarkdown.E(s);

    // ---- pieces ---------------------------------------------------------------------------------------------------

    /// <summary>The navigation tree. The branch of the current page is open, the current page is marked.</summary>
    public static string Sidebar(DocsView v, DocNode? current)
    {
        if (v.Tree.Roots.Count == 0) return "";
        var open = current is null ? new HashSet<long>() : current.Ancestors().Select(a => a.Page.Id).Append(current.Page.Id).ToHashSet();
        var sb = new StringBuilder($"<nav class=\"docs-nav\" aria-label=\"{E(v.Ctx.T("docs.nav.label"))}\">");
        sb.Append($"<a class=\"docs-nav-home{(current is null && !v.IsSearch ? " active" : "")}\" href=\"{E(v.Home)}\">{E(DocsSettings.TitleOf(v.Ctx))}</a>");
        sb.Append("<ul class=\"docs-nav-root\">");
        foreach (var root in v.Tree.Roots) Item(sb, v, root, current, open);
        return sb.Append("</ul></nav>").ToString();
    }

    private static void Item(StringBuilder sb, DocsView v, DocNode n, DocNode? current, HashSet<long> open)
    {
        var isCurrent = current is not null && n.Page.Id == current.Page.Id;
        var cls = (n.Depth == 0 ? "section" : "page") + (isCurrent ? " active" : "") + (n.Page.Published ? "" : " draft");
        var link = $"<a href=\"{E(v.PageUrl(n))}\"{(isCurrent ? " aria-current=\"page\"" : "")}>{E(n.Page.Title)}</a>";
        if (n.Children.Count == 0) { sb.Append($"<li class=\"{cls}\">{link}</li>"); return; }
        // A section is always open; a page below it opens on the way to the current page.
        var isOpen = n.Depth == 0 || open.Contains(n.Page.Id);
        sb.Append($"<li class=\"{cls} has-kids\"><details{(isOpen ? " open" : "")}><summary>{link}</summary><ul>");
        foreach (var c in n.Children) Item(sb, v, c, current, open);
        sb.Append("</ul></details></li>");
    }

    public static string Crumbs(DocsView v, DocNode? current, string? last = null)
    {
        var sb = new StringBuilder($"<nav class=\"docs-crumbs\" aria-label=\"{E(v.Ctx.T("docs.crumbs"))}\"><ol>");
        sb.Append($"<li><a href=\"{E(v.Home)}\">{E(DocsSettings.TitleOf(v.Ctx))}</a></li>");
        if (current is not null)
        {
            foreach (var a in current.Ancestors().Reverse()) sb.Append($"<li><a href=\"{E(v.PageUrl(a))}\">{E(a.Page.Title)}</a></li>");
            sb.Append($"<li aria-current=\"page\">{E(current.Page.Title)}</li>");
        }
        else if (last is not null) sb.Append($"<li aria-current=\"page\">{E(last)}</li>");
        return sb.Append("</ol></nav>").ToString();
    }

    public static string Toc(IPluginContext ctx, IReadOnlyList<TocEntry> toc)
    {
        if (toc.Count < 2) return "";
        var sb = new StringBuilder($"<nav class=\"docs-toc-nav\" aria-label=\"{E(ctx.T("docs.toc"))}\"><p class=\"docs-toc-title\">{E(ctx.T("docs.toc"))}</p><ul>");
        foreach (var t in toc) sb.Append($"<li class=\"l{t.Level}\"><a href=\"#{E(t.Id)}\">{E(t.Text)}</a></li>");
        return sb.Append("</ul></nav>").ToString();
    }

    public static string PrevNext(DocsView v, DocNode current)
    {
        var flat = v.Tree.Flat;
        var i = flat.ToList().FindIndex(n => n.Page.Id == current.Page.Id);
        if (i < 0) return "";
        var prev = i > 0 ? flat[i - 1] : null;
        var next = i + 1 < flat.Count ? flat[i + 1] : null;
        if (prev is null && next is null) return "";
        var sb = new StringBuilder($"<nav class=\"docs-pager\" aria-label=\"{E(v.Ctx.T("docs.pager"))}\">");
        sb.Append(prev is null ? "<span></span>" : $"<a class=\"prev\" href=\"{E(v.PageUrl(prev))}\" rel=\"prev\"><span>{E(v.Ctx.T("docs.prev"))}</span><strong>{E(prev.Page.Title)}</strong></a>");
        sb.Append(next is null ? "<span></span>" : $"<a class=\"next\" href=\"{E(v.PageUrl(next))}\" rel=\"next\"><span>{E(v.Ctx.T("docs.next"))}</span><strong>{E(next.Page.Title)}</strong></a>");
        return sb.Append("</nav>").ToString();
    }

    /// <summary>The sub-pages of a page as a list of links with their summaries ("In this section").</summary>
    public static string Children(DocsView v, DocNode n)
    {
        if (n.Children.Count == 0) return "";
        var sb = new StringBuilder($"<section class=\"docs-children\"><h2 class=\"docs-children-title\">{E(v.Ctx.T("docs.inSection"))}</h2><ul>");
        foreach (var c in n.Children)
            sb.Append($"<li><a href=\"{E(v.PageUrl(c))}\"><strong>{E(c.Page.Title)}</strong>{(c.Page.Summary.Length > 0 ? $"<span>{E(c.Page.Summary)}</span>" : "")}</a></li>");
        return sb.Append("</ul></section>").ToString();
    }

    /// <summary>The sections as cards (or a plain list) — the docs' start page and the block "Docs: Bereiche".</summary>
    public static string Sections(DocsView v, string layout, int limit = 0)
    {
        var roots = limit > 0 ? v.Tree.Roots.Take(limit).ToList() : v.Tree.Roots.ToList();
        if (roots.Count == 0) return $"<p class=\"docs-empty\">{E(v.Ctx.T("docs.empty"))}</p>";
        var sb = new StringBuilder(layout == "list" ? "<ul class=\"docs-section-list\">" : "<div class=\"docs-cards\">");
        foreach (var r in roots)
        {
            var icon = Icon(r.Page.Icon);
            var sub = r.Children.Take(4).ToList();
            if (layout == "list")
            {
                sb.Append($"<li><a href=\"{E(v.PageUrl(r))}\">{E(r.Page.Title)}</a>{(r.Page.Summary.Length > 0 ? $" <span>– {E(r.Page.Summary)}</span>" : "")}</li>");
                continue;
            }
            sb.Append($"<a class=\"docs-card\" href=\"{E(v.PageUrl(r))}\">");
            if (icon.Length > 0) sb.Append($"<span class=\"docs-card-icon\" aria-hidden=\"true\"><i class=\"ti ti-{E(icon)}\"></i></span>");
            sb.Append($"<strong class=\"docs-card-title\">{E(r.Page.Title)}</strong>");
            if (r.Page.Summary.Length > 0) sb.Append($"<span class=\"docs-card-text\">{E(r.Page.Summary)}</span>");
            if (sub.Count > 0) sb.Append("<span class=\"docs-card-sub\">" + string.Join(" · ", sub.Select(c => E(c.Page.Title))) + (r.Children.Count > sub.Count ? " …" : "") + "</span>");
            sb.Append("</a>");
        }
        return sb.Append(layout == "list" ? "</ul>" : "</div>").ToString();
    }

    /// <summary>A Tabler icon name as stored (letters, digits and "-"), without a "ti-" in front; anything else is no icon.</summary>
    public static string Icon(string? icon)
    {
        var s = (icon ?? "").Trim().ToLowerInvariant();
        if (s.StartsWith("ti ", StringComparison.Ordinal)) s = s[3..].Trim();
        if (s.StartsWith("ti-", StringComparison.Ordinal)) s = s[3..];
        return s.Length is > 0 and <= 40 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? s : "";
    }

    /// <summary>The version switcher: a small menu of the versions, each leading to the same page there when it exists, else to its start page.</summary>
    public static string Versions(DocsView v, Func<DocVersion, string> target)
    {
        if (v.Versions.Count < 2) return "";
        var cur = v.Versions.FirstOrDefault(x => x.Name == v.Version);
        var sb = new StringBuilder($"<details class=\"docs-versions\"><summary><span>{E(v.Ctx.T("docs.version"))}</span> <strong>{E(cur?.Shown ?? v.Version)}</strong></summary><ul>");
        foreach (var x in v.Versions)
        {
            var badge = x.IsDefault ? $" <em>{E(v.Ctx.T("docs.versionCurrent"))}</em>" : "";
            sb.Append($"<li{(x.Name == v.Version ? " class=\"active\"" : "")}><a href=\"{E(target(x))}\">{E(x.Shown)}{badge}</a></li>");
        }
        return sb.Append("</ul></details>").ToString();
    }

    public static string SearchBox(IPluginContext ctx, string action, string q = "") =>
        ctx.RenderPart("searchbox", new Dictionary<string, string>
        {
            ["action"] = action, ["q"] = q, ["placeholder"] = ctx.T("docs.search.placeholder"), ["label"] = ctx.T("docs.search"), ["button"] = ctx.T("docs.search.button")
        });

    public static string Date(DateTime utc, string locale)
    {
        var d = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return locale == "de" ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The page: the docs' stylesheet (plus the operator's own CSS), the content, the small script (copy buttons, the current heading).</summary>
    public static PluginResult Page(IPluginContext ctx, string title, string body, string? description = null, int status = 200, bool noIndex = false)
    {
        var own = (ctx.Settings.Get(DocsSettings.CustomCss) ?? "").Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
        var script = $"<script src=\"{E(ctx.AssetUrl("docs.js"))}\" defer></script>";
        var html = $"<style>{Css}{own}</style><section class=\"section docs-section\"><div class=\"container docs-container\"><div class=\"docs\">{body}</div></div></section>{script}";
        var meta = new List<KeyValuePair<string, string>>();
        if (noIndex || !ctx.Settings.GetBool(DocsSettings.Indexing)) meta.Add(new("robots", "noindex"));
        var r = PluginResult.Page(html, title, description, meta);
        return status == 200 ? r : r.WithStatus(status);
    }

    // ---- template parts ------------------------------------------------------------------------------------------

    public const string PageDefault = """
        <div class="docs-layout{{#if toc_html}} has-toc{{/if}}">
        <input type="checkbox" id="docs-nav-open" class="docs-nav-cb">
        <label for="docs-nav-open" class="docs-nav-btn">{{nav_label}}</label>
        <aside class="docs-sidebar">{{search_html}}{{versions_html}}{{sidebar_html}}</aside>
        <article class="docs-main">
        {{crumbs_html}}
        {{notice_html}}
        <header class="docs-head"><h1>{{title}}</h1>{{#if summary}}<p class="docs-lead">{{summary}}</p>{{/if}}</header>
        <div class="docs-content">{{content_html}}</div>
        {{children_html}}
        <footer class="docs-foot">{{#if updated}}<span class="docs-updated">{{updated}}</span>{{/if}}{{edit_html}}</footer>
        {{prevnext_html}}
        </article>
        {{#if toc_html}}<aside class="docs-toc">{{toc_html}}</aside>{{/if}}
        </div>
        """;

    public const string HomeDefault = """
        <div class="docs-layout">
        <input type="checkbox" id="docs-nav-open" class="docs-nav-cb">
        <label for="docs-nav-open" class="docs-nav-btn">{{nav_label}}</label>
        <aside class="docs-sidebar">{{versions_html}}{{sidebar_html}}</aside>
        <div class="docs-main">
        <header class="docs-hero"><h1>{{title}}</h1>{{#if intro}}<p class="docs-lead">{{intro}}</p>{{/if}}{{search_html}}</header>
        {{notice_html}}
        {{sections_html}}
        </div>
        </div>
        """;

    public const string SearchDefault = """
        <div class="docs-layout">
        <input type="checkbox" id="docs-nav-open" class="docs-nav-cb">
        <label for="docs-nav-open" class="docs-nav-btn">{{nav_label}}</label>
        <aside class="docs-sidebar">{{versions_html}}{{sidebar_html}}</aside>
        <div class="docs-main">
        {{crumbs_html}}
        <header class="docs-head"><h1>{{title}}</h1></header>
        {{search_html}}
        {{#if count}}<p class="docs-count">{{count}}</p>{{/if}}
        {{results_html}}
        </div>
        </div>
        """;

    public const string SearchBoxDefault = """
        <form class="docs-search" action="{{action}}" method="get" role="search"><input type="search" name="q" value="{{q}}" placeholder="{{placeholder}}" aria-label="{{label}}"><button type="submit">{{button}}</button></form>
        """;

    public static readonly string[] PagePlaceholders =
        ["title", "summary", "updated", "nav_label", "crumbs_html", "notice_html", "sidebar_html", "search_html", "versions_html", "content_html", "children_html", "toc_html", "prevnext_html", "edit_html"];
    public static readonly string[] HomePlaceholders = ["title", "intro", "nav_label", "search_html", "versions_html", "sidebar_html", "notice_html", "sections_html"];
    public static readonly string[] SearchPlaceholders = ["title", "count", "nav_label", "crumbs_html", "search_html", "versions_html", "sidebar_html", "results_html"];
    public static readonly string[] SearchBoxPlaceholders = ["action", "q", "placeholder", "label", "button"];

    public static void RegisterParts(IPluginBuilder b)
    {
        static TemplatePlaceholder[] P(string[] names) => names.Select(n => new TemplatePlaceholder(n, "docs.ph." + n, n.EndsWith("_html", StringComparison.Ordinal))).ToArray();
        b.TemplatePart(new TemplatePartSpec("page", "docs.part.page", PageDefault) { Help = "docs.part.pageHelp", Placeholders = P(PagePlaceholders) });
        b.TemplatePart(new TemplatePartSpec("home", "docs.part.home", HomeDefault) { Help = "docs.part.homeHelp", Placeholders = P(HomePlaceholders) });
        b.TemplatePart(new TemplatePartSpec("search", "docs.part.search", SearchDefault) { Help = "docs.part.searchHelp", Placeholders = P(SearchPlaceholders) });
        b.TemplatePart(new TemplatePartSpec("searchbox", "docs.part.searchbox", SearchBoxDefault) { Help = "docs.part.searchboxHelp", Placeholders = P(SearchBoxPlaceholders) });
    }

    // ---- stylesheet -----------------------------------------------------------------------------------------------
    // Scoped under .docs; every colour, line and font reads the SITE's variable behind the docs' own (--docs-x → --accent / --line / --font-head …),
    // so the docs take on a site's look by themselves and a template can still set --docs-accent and friends. No colour of its own except tints.

    public const string Css = """
        .container.docs-container{max-width:var(--docs-width,1360px);width:100%}
        .docs{--d-line:var(--docs-line,var(--line,rgba(127,127,127,.22)));--d-accent:var(--docs-accent,var(--accent,#2563eb));--d-on-accent:var(--docs-on-accent,#fff);--d-soft:var(--docs-soft,rgba(127,127,127,.07));--d-code-bg:var(--docs-code-bg,rgba(127,127,127,.1));--d-radius:var(--docs-radius,10px);--d-top:calc(var(--mat-adminbar-h,0px) + var(--docs-sticky-top,84px));color:inherit}
        .docs-layout{display:grid;grid-template-columns:minmax(0,260px) minmax(0,1fr);gap:40px;align-items:start}
        .docs-layout.has-toc{grid-template-columns:minmax(0,260px) minmax(0,1fr) minmax(0,220px)}
        .docs-sidebar,.docs-toc{position:sticky;top:var(--d-top);max-height:calc(100vh - var(--d-top) - 16px);overflow:auto;font-size:.93em;padding-bottom:16px}
        .docs-main{min-width:0}
        .docs-nav-cb{position:absolute;opacity:0;pointer-events:none}.docs-nav-btn{display:none}
        .docs-nav a{text-decoration:none;color:inherit;display:block;padding:5px 10px;border-radius:6px;line-height:1.35}
        .docs-nav a:hover{background:var(--d-soft)}.docs-nav .active>a,.docs-nav .active>details>summary>a,.docs-nav-home.active{color:var(--d-accent);background:var(--d-soft);font-weight:650}
        .docs-nav-home{font-weight:700;margin:0 0 8px}
        .docs-nav ul{list-style:none;margin:0;padding:0}.docs-nav ul ul{margin:2px 0 4px 12px;padding-left:8px;border-left:1px solid var(--d-line)}
        .docs-nav-root>li.section{margin:0 0 10px}.docs-nav-root>li.section>a,.docs-nav-root>li.section>details>summary>a{font-weight:650;text-transform:none}
        .docs-nav summary{list-style:none;display:flex;align-items:center;cursor:pointer}.docs-nav summary::-webkit-details-marker{display:none}
        .docs-nav summary>a{flex:1}.docs-nav summary::after{content:"";width:7px;height:7px;margin:0 8px 0 4px;border-right:1.5px solid currentColor;border-bottom:1.5px solid currentColor;transform:rotate(-45deg);opacity:.5;transition:transform .15s;flex:none}
        .docs-nav details[open]>summary::after{transform:rotate(45deg)}
        .docs-nav li.draft>a,.docs-nav li.draft>details>summary>a{opacity:.55;font-style:italic}
        .docs-search{display:flex;gap:6px;margin:0 0 14px}.docs-search input{flex:1;min-width:0;padding:8px 12px;border:1px solid var(--d-line);border-radius:8px;font:inherit;background:transparent;color:inherit}
        .docs-search button{padding:8px 14px;border-radius:var(--btn-radius,8px);border:1px solid var(--d-accent);background:var(--d-accent);color:var(--d-on-accent);font:inherit;font-weight:600;cursor:pointer}
        .docs-hero .docs-search{max-width:560px;margin:18px 0 0}.docs-hero .docs-search input{padding:12px 16px;font-size:1.05em}
        .docs-versions{margin:0 0 14px;border:1px solid var(--d-line);border-radius:8px}.docs-versions summary{cursor:pointer;padding:7px 12px}.docs-versions summary span{opacity:.7}
        .docs-versions ul{list-style:none;margin:0;padding:4px}.docs-versions a{display:block;padding:5px 8px;border-radius:6px;text-decoration:none;color:inherit}.docs-versions .active a{color:var(--d-accent);font-weight:650}
        .docs-versions em{font-style:normal;font-size:.8em;opacity:.7}
        .docs-crumbs ol{display:flex;flex-wrap:wrap;gap:6px;list-style:none;margin:0 0 14px;padding:0;font-size:.88em;opacity:.8}
        .docs-crumbs li+li::before{content:"›";margin-right:6px;opacity:.6}.docs-crumbs a{text-decoration:none}
        .docs-head h1,.docs-hero h1{margin:0 0 8px;font-family:var(--docs-font-head,var(--font-head,inherit));font-size:clamp(1.7rem,3vw,2.4rem);line-height:1.2}
        .docs-lead{font-size:1.12em;opacity:.8;margin:0 0 8px}.docs-head{margin:0 0 22px;padding:0 0 16px;border-bottom:1px solid var(--d-line)}
        .docs-hero{margin:0 0 30px}
        .docs-notice{padding:10px 14px;border-radius:8px;margin:0 0 16px;background:var(--d-soft);border:1px solid var(--d-line)}
        .docs-content{line-height:1.7;overflow-wrap:anywhere}
        .docs-content h2,.docs-content h3,.docs-content h4{font-family:var(--docs-font-head,var(--font-head,inherit));line-height:1.3;scroll-margin-top:var(--d-top)}
        .docs-content h2{margin:2em 0 .6em;font-size:1.5em;padding-bottom:.3em;border-bottom:1px solid var(--d-line)}.docs-content h3{margin:1.6em 0 .5em;font-size:1.2em}
        .docs-content>:first-child{margin-top:0}
        .docs-anchor{margin-left:8px;opacity:0;text-decoration:none;font-weight:400}.docs-content h2:hover .docs-anchor,.docs-content h3:hover .docs-anchor,.docs-content h4:hover .docs-anchor{opacity:.45}
        .docs-content a{color:var(--docs-link,var(--d-accent))}
        .docs-content code{font-family:var(--docs-font-code,ui-monospace,SFMono-Regular,Menlo,Consolas,monospace);font-size:.88em;background:var(--d-code-bg);padding:.12em .38em;border-radius:5px}
        .docs-code{position:relative;margin:0 0 1.2em}.docs-code pre{margin:0;padding:14px 16px;border-radius:var(--d-radius);background:var(--docs-pre-bg,#0f172a);color:var(--docs-pre-fg,#e2e8f0);overflow:auto;line-height:1.55;font-size:.9em}
        .docs-code pre code{background:none;padding:0;font-size:inherit;color:inherit}
        .docs-code-lang{position:absolute;top:6px;right:62px;font-size:.72em;text-transform:uppercase;letter-spacing:.06em;color:var(--docs-pre-fg,#e2e8f0);opacity:.55}
        .docs-copy{position:absolute;top:6px;right:8px;padding:2px 8px;font:inherit;font-size:.75em;border-radius:6px;border:1px solid rgba(255,255,255,.25);background:transparent;color:var(--docs-pre-fg,#e2e8f0);cursor:pointer}
        .docs-content blockquote{margin:0 0 1.2em;padding:8px 16px;border-left:3px solid var(--d-line);opacity:.9}
        .docs-callout{margin:0 0 1.2em;padding:12px 16px;border-radius:var(--d-radius);border:1px solid var(--d-line);border-left-width:4px;background:var(--d-soft)}
        .docs-callout>:last-child{margin-bottom:0}.docs-callout-title{font-weight:700;margin:0 0 4px}
        .docs-callout.note{border-left-color:var(--d-accent)}.docs-callout.tip{border-left-color:#16a34a;background:rgba(22,163,74,.07)}
        .docs-callout.important{border-left-color:#7c3aed;background:rgba(124,58,237,.07)}.docs-callout.warning{border-left-color:#d97706;background:rgba(217,119,6,.08)}
        .docs-callout.caution{border-left-color:#dc2626;background:rgba(220,38,38,.07)}
        .docs-table{overflow-x:auto;margin:0 0 1.2em}.docs-table table{border-collapse:collapse;width:100%;font-size:.94em}
        .docs-table th,.docs-table td{border:1px solid var(--d-line);padding:7px 10px;text-align:left;vertical-align:top}.docs-table th{background:var(--d-soft)}
        .docs-content img{max-width:100%;height:auto;border-radius:6px}
        .docs-content li.task{list-style:none;margin-left:-1.3em}.docs-broken{text-decoration:line-through dotted;opacity:.7}
        .docs-children{margin:2em 0 0}.docs-children-title{font-size:1.1em;margin:0 0 .6em}
        .docs-children ul{list-style:none;margin:0;padding:0;display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:10px}
        .docs-children a{display:block;height:100%;box-sizing:border-box;padding:12px 14px;border:1px solid var(--d-line);border-radius:var(--d-radius);text-decoration:none;color:inherit}
        .docs-children a:hover,.docs-card:hover{border-color:var(--d-accent)}.docs-children span{display:block;margin-top:4px;font-size:.9em;opacity:.75}
        .docs-foot{display:flex;flex-wrap:wrap;gap:12px;justify-content:space-between;margin:2.4em 0 0;padding:12px 0 0;border-top:1px solid var(--d-line);font-size:.86em;opacity:.8}
        .docs-pager{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin:18px 0 0}
        .docs-pager a{display:block;padding:12px 16px;border:1px solid var(--d-line);border-radius:var(--d-radius);text-decoration:none;color:inherit}.docs-pager a:hover{border-color:var(--d-accent)}
        .docs-pager .next{text-align:right;grid-column:2}.docs-pager span{display:block;font-size:.82em;opacity:.7}.docs-pager strong{color:var(--d-accent)}
        .docs-toc-title{font-weight:700;margin:0 0 8px;font-size:.85em;text-transform:uppercase;letter-spacing:.05em;opacity:.7}
        .docs-toc ul{list-style:none;margin:0;padding:0 0 0 10px;border-left:1px solid var(--d-line)}.docs-toc li.l3{padding-left:12px}
        .docs-toc a{display:block;padding:3px 0;text-decoration:none;color:inherit;opacity:.75}.docs-toc a:hover,.docs-toc a.active{opacity:1;color:var(--d-accent)}
        .docs-cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:16px}
        .docs-card{display:flex;flex-direction:column;gap:6px;padding:20px;border:1px solid var(--d-line);border-radius:var(--d-radius);text-decoration:none;color:inherit;background:var(--docs-card-bg,transparent);transition:border-color .15s,transform .15s}
        .docs-card:hover{transform:translateY(-2px)}
        .docs-card-icon{width:40px;height:40px;border-radius:10px;display:grid;place-items:center;background:var(--d-soft);color:var(--d-accent);font-size:1.3em}
        .docs-card-title{font-size:1.1em}.docs-card-text{opacity:.8;font-size:.94em}.docs-card-sub{margin-top:auto;padding-top:6px;font-size:.82em;opacity:.6}
        .docs-section-list{line-height:1.9}
        .docs-results{list-style:none;margin:0;padding:0}.docs-results li{padding:14px 0;border-top:1px solid var(--d-line)}
        .docs-results a{font-weight:650;font-size:1.05em;text-decoration:none}.docs-results .path{font-size:.82em;opacity:.65;margin:2px 0}.docs-results p{margin:4px 0 0;opacity:.85}
        .docs-results mark{background:rgba(250,204,21,.35);color:inherit;border-radius:3px;padding:0 2px}
        .docs-empty,.docs-count{opacity:.75}
        @media(max-width:1180px){.docs-layout.has-toc{grid-template-columns:minmax(0,240px) minmax(0,1fr)}.docs-toc{display:none}}
        @media(max-width:860px){.docs-layout,.docs-layout.has-toc{grid-template-columns:minmax(0,1fr);gap:16px}
          .docs-nav-btn{display:inline-flex;align-items:center;gap:8px;justify-self:start;padding:8px 14px;border:1px solid var(--d-line);border-radius:8px;cursor:pointer;font-weight:600}
          .docs-nav-btn::before{content:"";width:14px;height:10px;border-top:2px solid currentColor;border-bottom:2px solid currentColor;box-shadow:inset 0 4px 0 -2px currentColor}
          .docs-sidebar{display:none;position:static;max-height:none;border:1px solid var(--d-line);border-radius:var(--d-radius);padding:12px}
          .docs-nav-cb:checked~.docs-sidebar{display:block}.docs-pager{grid-template-columns:1fr}.docs-pager .next{grid-column:1}}
        """;
}
