using System.Globalization;
using System.Net;
using System.Text;

namespace MatCMS.Plugin.Forum;

/// <summary>The forum's addresses, in one place — never assembled by hand elsewhere.</summary>
internal static class ForumUrls
{
    public const string Home = "/forum";
    public static string Board(Board b, int page = 1) => "/forum/" + b.Slug + (page > 1 ? "?seite=" + page.ToString(CultureInfo.InvariantCulture) : "");
    public static string NewThread(Board b) => "/forum/" + b.Slug + "/neu";
    public static string Thread(long id, string title, int page = 1) =>
        "/forum/t/" + id.ToString(CultureInfo.InvariantCulture) + (ForumStore.Slugify(title) is { Length: > 0 } s ? "-" + s : "") + (page > 1 ? "?seite=" + page.ToString(CultureInfo.InvariantCulture) : "");
    public static string Thread(ForumThread t, int page = 1) => Thread(t.Id, t.Title, page);
    public static string Post(long id) => "/forum/p/" + id.ToString(CultureInfo.InvariantCulture);
    public static string EditPost(long id) => "/forum/p/" + id.ToString(CultureInfo.InvariantCulture) + "/bearbeiten";
    public static string ReportPost(long id) => "/forum/p/" + id.ToString(CultureInfo.InvariantCulture) + "/melden";
    public static string Member(long id) => "/forum/mitglied/" + id.ToString(CultureInfo.InvariantCulture);
    public const string Search = "/forum/suche", Latest = "/forum/aktuell", Register = "/forum/registrieren", Subscriptions = "/forum/abos";
    public static string SignIn(string returnUrl) => "/anmelden?returnUrl=" + Uri.EscapeDataString(returnUrl);

    /// <summary>The id at the start of a thread segment ("12-mein-thema" → 12).</summary>
    public static long IdOf(string segment)
    {
        var digits = new string((segment ?? "").TakeWhile(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }
}

/// <summary>Markup helpers, the template parts' defaults and the stylesheet.</summary>
internal static class ForumHtml
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string N(long n) => n.ToString("#,0", CultureInfo.GetCultureInfo("de-DE"));

    // ---- time -----------------------------------------------------------------------------------------------------

    private static TimeZoneInfo Zone(IPluginContext ctx)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(ctx.Settings.Get(ForumSettings.TimeZone) is { Length: > 0 } z ? z : "Europe/Berlin"); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>A moment as the reader of the site reads it: "heute, 14:05", "gestern, 09:12", else the date.</summary>
    public static string When(IPluginContext ctx, DateTime? utc)
    {
        if (utc is not { } u || u == DateTime.MinValue) return "";
        var zone = Zone(ctx);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return ctx.T("forum.time.today", time);
        if (local.Date == today.AddDays(-1)) return ctx.T("forum.time.yesterday", time);
        return local.ToString(ctx.Locale == "de" ? "dd.MM.yyyy, HH:mm" : "yyyy-MM-dd, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The machine-readable form for &lt;time datetime&gt;.</summary>
    public static string Iso(DateTime? utc) => utc is { } u && u != DateTime.MinValue ? DateTime.SpecifyKind(u, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "";

    public static string TimeTag(IPluginContext ctx, DateTime? utc) =>
        utc is null || utc == DateTime.MinValue ? "" : $"<time datetime=\"{Iso(utc)}\">{E(When(ctx, utc))}</time>";

    // ---- small pieces ---------------------------------------------------------------------------------------------

    public static string Initials(string name)
    {
        var parts = (name ?? "").Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var s = parts.Length switch { 0 => "?", 1 => parts[0][..Math.Min(2, parts[0].Length)], _ => parts[0][..1] + parts[^1][..1] };
        return s.ToUpperInvariant();
    }

    /// <summary>A name that links to the member's page — or, for a deleted account (id 0), just the name.</summary>
    public static string MemberLink(long id, string name) =>
        id > 0 ? $"<a class=\"forum-member\" href=\"{E(ForumUrls.Member(id))}\">{E(name)}</a>" : $"<span class=\"forum-member gone\">{E(name)}</span>";

    /// <summary>Links to the pages of a list: first, the ones around the current, last.</summary>
    public static string Pager(IPluginContext ctx, int page, int pages, Func<int, string> href)
    {
        if (pages <= 1) return "";
        var sb = new StringBuilder($"<nav class=\"forum-pager\" aria-label=\"{E(ctx.T("forum.pages"))}\">");
        if (page > 1) sb.Append($"<a href=\"{E(href(page - 1))}\" rel=\"prev\" aria-label=\"{E(ctx.T("forum.prev"))}\">‹</a>");
        var shown = new SortedSet<int> { 1, pages };
        for (var i = page - 2; i <= page + 2; i++) if (i >= 1 && i <= pages) shown.Add(i);
        var last = 0;
        foreach (var i in shown)
        {
            if (i - last > 1) sb.Append("<span class=\"gap\">…</span>");
            sb.Append(i == page ? $"<span class=\"cur\" aria-current=\"page\">{i}</span>" : $"<a href=\"{E(href(i))}\">{i}</a>");
            last = i;
        }
        if (page < pages) sb.Append($"<a href=\"{E(href(page + 1))}\" rel=\"next\" aria-label=\"{E(ctx.T("forum.next"))}\">›</a>");
        return sb.Append("</nav>").ToString();
    }

    /// <summary>The small page links behind a thread's title in a list ("1 2 3 … 9"), for a thread with several pages.</summary>
    public static string MiniPages(ForumThread t, int perPage)
    {
        var pages = (int)Math.Ceiling((t.ReplyCount + 1) / (double)Math.Max(1, perPage));
        if (pages <= 1) return "";
        var list = pages <= 4 ? Enumerable.Range(1, pages).ToList() : new List<int> { 1, 2, -1, pages };
        var sb = new StringBuilder("<span class=\"forum-minipages\">");
        foreach (var p in list) sb.Append(p < 0 ? "<span>…</span>" : $"<a href=\"{E(ForumUrls.Thread(t, p))}\">{p}</a>");
        return sb.Append("</span>").ToString();
    }

    public static string Crumbs(IPluginContext ctx, params (string Label, string? Href)[] items)
    {
        var sb = new StringBuilder($"<nav class=\"forum-crumbs\" aria-label=\"{E(ctx.T("forum.crumbs"))}\"><a href=\"{ForumUrls.Home}\">{E(ForumSettings.Title(ctx))}</a>");
        foreach (var (label, href) in items)
            sb.Append("<span aria-hidden=\"true\">›</span>").Append(href is null ? $"<span>{E(label)}</span>" : $"<a href=\"{E(href)}\">{E(label)}</a>");
        return sb.Append("</nav>").ToString();
    }

    /// <summary>The strip above every forum page: search, the newest posts, and who is signed in (or the way to sign in and register).</summary>
    public static string Toolbar(IPluginContext ctx, Viewer v, string returnUrl, string q = "")
    {
        var sb = new StringBuilder("<div class=\"forum-toolbar\">");
        sb.Append($"<form class=\"forum-search\" action=\"{ForumUrls.Search}\" method=\"get\" role=\"search\"><input type=\"search\" name=\"q\" value=\"{E(q)}\" placeholder=\"{E(ctx.T("forum.search.placeholder"))}\" aria-label=\"{E(ctx.T("forum.search"))}\"><button type=\"submit\">{E(ctx.T("forum.search"))}</button></form>");
        sb.Append("<div class=\"forum-me\">");
        sb.Append($"<a href=\"{ForumUrls.Latest}\">{E(ctx.T("forum.latest"))}</a>");
        if (v.Member is { } m)
        {
            sb.Append($"<a href=\"{ForumUrls.Subscriptions}\">{E(ctx.T("forum.subs"))}</a>");
            sb.Append($"<a class=\"forum-me-name\" href=\"{E(ForumUrls.Member(m.Id))}\"><span class=\"forum-avatar sm\" aria-hidden=\"true\">{E(Initials(ForumStore.AuthorName(m)))}</span>{E(ForumStore.AuthorName(m))}</a>");
        }
        else
        {
            sb.Append($"<a href=\"{E(ForumUrls.SignIn(returnUrl))}\">{E(ctx.T("forum.signin"))}</a>");
            if (ForumSettings.RegistrationOpen(ctx)) sb.Append($"<a class=\"forum-btn sm\" href=\"{ForumUrls.Register}?returnUrl={Uri.EscapeDataString(returnUrl)}\">{E(ctx.T("forum.register"))}</a>");
        }
        sb.Append("</div></div>");
        return sb.ToString();
    }

    public static string Notice(string text, string kind = "info") => text.Length == 0 ? "" : $"<div class=\"forum-notice {kind}\" role=\"status\">{E(text)}</div>";

    /// <summary>The page: the forum's stylesheet (plus the operator's own CSS), the content. Every value in <paramref name="body"/> was encoded where it was written.</summary>
    public static PluginResult Page(IPluginContext ctx, string title, string body, string? description = null, int status = 200, bool noIndex = false)
    {
        var own = (ctx.Settings.Get(ForumSettings.CustomCss) ?? "").Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
        var html = $"<style>{Css}{own}</style><section class=\"section\"><div class=\"container\"><div class=\"forum\">{body}</div></div></section>";
        var meta = noIndex ? new[] { new KeyValuePair<string, string>("robots", "noindex") } : null;
        var r = PluginResult.Page(html, title, description, meta);
        return status == 200 ? r : r.WithStatus(status);
    }

    // ---- template parts ------------------------------------------------------------------------------------------

    public const string IndexDefault = """
        {{toolbar_html}}
        <div class="forum-head"><div><h1>{{title}}</h1>{{#if intro}}<p class="forum-muted">{{intro}}</p>{{/if}}</div>{{actions_html}}</div>
        {{notice_html}}
        {{categories_html}}
        {{latest_html}}
        {{stats_html}}
        """;

    public const string BoardDefault = """
        {{toolbar_html}}
        {{crumbs_html}}
        <div class="forum-head"><div><h1>{{title}}</h1>{{#if description}}<p class="forum-muted">{{description}}</p>{{/if}}</div>{{actions_html}}</div>
        {{notice_html}}
        {{threads_html}}
        {{pager_html}}
        """;

    public const string ThreadDefault = """
        {{toolbar_html}}
        {{crumbs_html}}
        <div class="forum-head"><div><h1>{{title}}</h1><p class="forum-muted forum-small">{{badges_html}}{{meta}}</p></div>{{actions_html}}</div>
        {{notice_html}}
        {{pager_html}}
        <div class="forum-posts">{{posts_html}}</div>
        {{pager_html}}
        {{reply_html}}
        {{mod_html}}
        """;

    public const string PostDefault = """
        <article class="forum-post{{#if is_first}} first{{/if}}" id="p{{id}}">
        <aside class="forum-author"><span class="forum-avatar" aria-hidden="true">{{initials}}</span><div>{{author_html}}<div class="forum-small forum-muted">{{author_meta}}</div></div></aside>
        <div class="forum-post-main">
        <header class="forum-post-head"><a class="forum-post-no" href="{{url}}">#{{number}}</a>{{date_html}}</header>
        <div class="forum-body">{{body_html}}</div>
        {{#if edited}}<p class="forum-edited forum-small forum-muted">{{edited}}</p>{{/if}}
        {{#if signature_html}}<div class="forum-signature">{{signature_html}}</div>{{/if}}
        {{#if actions_html}}<footer class="forum-post-actions">{{actions_html}}</footer>{{/if}}
        </div>
        </article>
        """;

    public static void RegisterParts(IPluginBuilder b)
    {
        static TemplatePlaceholder P(string name, bool html = false) => new(name, "forum.ph." + name, html);
        b.TemplatePart(new TemplatePartSpec("index", "forum.part.index", IndexDefault)
        {
            Help = "forum.part.indexHelp",
            Placeholders = [P("title"), P("intro"), P("toolbar_html", true), P("actions_html", true), P("notice_html", true), P("categories_html", true), P("latest_html", true), P("stats_html", true)]
        });
        b.TemplatePart(new TemplatePartSpec("board", "forum.part.board", BoardDefault)
        {
            Help = "forum.part.boardHelp",
            Placeholders = [P("title"), P("description"), P("toolbar_html", true), P("crumbs_html", true), P("actions_html", true), P("notice_html", true), P("threads_html", true), P("pager_html", true)]
        });
        b.TemplatePart(new TemplatePartSpec("thread", "forum.part.thread", ThreadDefault)
        {
            Help = "forum.part.threadHelp",
            Placeholders = [P("title"), P("meta"), P("badges_html", true), P("toolbar_html", true), P("crumbs_html", true), P("actions_html", true), P("notice_html", true), P("posts_html", true), P("pager_html", true), P("reply_html", true), P("mod_html", true)]
        });
        b.TemplatePart(new TemplatePartSpec("post", "forum.part.post", PostDefault)
        {
            Help = "forum.part.postHelp",
            Placeholders = [P("id"), P("number"), P("url"), P("initials"), P("author_html", true), P("author_meta"), P("date_html", true), P("body_html", true), P("edited"), P("signature_html", true), P("actions_html", true), P("is_first")]
        });
    }

    // ---- stylesheet -----------------------------------------------------------------------------------------------
    // Scoped under .forum, and every colour, line and font reads the SITE's variable behind the forum's own (--forum-x → --accent / --line / --bg / --font-head),
    // so the forum takes on a site's look by itself and a template can still set --forum-accent and friends.

    public const string Css = """
        .forum{--f-line:var(--forum-line,var(--line,rgba(127,127,127,.25)));--f-accent:var(--forum-accent,var(--accent,#2563eb));--f-on-accent:var(--forum-on-accent,#fff);--f-soft:var(--forum-soft,rgba(127,127,127,.07));--f-radius:var(--forum-radius,10px);color:inherit}
        .forum h1,.forum h2,.forum h3{font-family:var(--forum-font-head,var(--font-head,inherit))}.forum h1{margin:0 0 6px;font-size:clamp(1.5rem,3vw,2.1rem)}
        .forum a{color:var(--forum-link,inherit)}.forum-muted{opacity:.7}.forum-small{font-size:.86em}
        .forum-toolbar{display:flex;flex-wrap:wrap;gap:12px;align-items:center;justify-content:space-between;margin:0 0 18px;padding:0 0 14px;border-bottom:1px solid var(--f-line)}
        .forum-search{display:flex;gap:6px;flex:1 1 260px;max-width:420px}.forum-search input{flex:1;min-width:0;padding:8px 12px;border:1px solid var(--f-line);border-radius:8px;font:inherit;background:transparent;color:inherit}
        .forum-search button,.forum-btn,.forum button.forum-btn{display:inline-flex;align-items:center;gap:6px;padding:9px 16px;border-radius:var(--btn-radius,8px);border:1px solid var(--f-accent);background:var(--f-accent);color:var(--f-on-accent);font:inherit;font-weight:600;cursor:pointer;text-decoration:none;white-space:nowrap}
        .forum a.forum-btn{color:var(--f-on-accent)}.forum-btn.sm{padding:6px 12px;font-size:.9em}.forum-btn.ghost,.forum a.forum-btn.ghost{background:transparent;color:inherit;border-color:var(--f-line)}
        .forum-btn.danger,.forum a.forum-btn.danger{background:transparent;color:#b42318;border-color:rgba(180,35,24,.4)}.forum-btn:hover{filter:brightness(.94)}
        .forum-me{display:flex;flex-wrap:wrap;gap:14px;align-items:center}.forum-me a{text-decoration:none}.forum-me-name{display:inline-flex;align-items:center;gap:8px;font-weight:600}
        .forum-crumbs{display:flex;flex-wrap:wrap;gap:8px;align-items:center;font-size:.9em;margin:0 0 12px;opacity:.85}.forum-crumbs a{text-decoration:none}
        .forum-head{display:flex;flex-wrap:wrap;gap:12px 24px;align-items:flex-end;justify-content:space-between;margin:0 0 18px}.forum-head p{margin:0}
        .forum-actions{display:flex;flex-wrap:wrap;gap:8px}
        .forum-cat{margin:0 0 22px;border:1px solid var(--f-line);border-radius:var(--f-radius);overflow:hidden}
        .forum-cat>h2{margin:0;padding:12px 16px;font-size:1.05em;background:var(--f-soft);border-bottom:1px solid var(--f-line)}
        .forum-row{display:grid;grid-template-columns:34px minmax(0,1fr) 90px 90px minmax(0,230px);gap:14px;align-items:center;padding:14px 16px;border-top:1px solid var(--f-line)}
        .forum-cat>h2+.forum-row,.forum-list>.forum-row:first-child{border-top:0}
        .forum-row .ico{width:34px;height:34px;border-radius:50%;display:grid;place-items:center;background:var(--f-soft);font-size:1rem}.forum-row .ico.new{background:var(--f-accent);color:var(--f-on-accent)}
        .forum-row .ico svg{width:18px;height:18px;fill:none;stroke:currentColor;stroke-width:2;stroke-linecap:round;stroke-linejoin:round}
        .forum-row .name{font-weight:650;text-decoration:none;font-size:1.04em}.forum-row .desc{margin:2px 0 0;font-size:.9em;opacity:.75}
        .forum-row .num{text-align:center;font-size:.9em}.forum-row .num b{display:block;font-size:1.1em}.forum-row .last{font-size:.86em;min-width:0}
        .forum-row .last a{text-decoration:none}.forum-row .last .t{display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:600}
        .forum-list{border:1px solid var(--f-line);border-radius:var(--f-radius);overflow:hidden;margin:0 0 16px}
        .forum-list .forum-row{grid-template-columns:34px minmax(0,1fr) 80px 80px minmax(0,200px)}
        .forum-list .forum-row.sticky{background:var(--f-soft)}
        .forum-tag{display:inline-block;padding:1px 8px;margin-right:6px;border-radius:99px;font-size:.75em;font-weight:600;background:var(--f-soft);border:1px solid var(--f-line);vertical-align:middle}
        .forum-tag.new{background:var(--f-accent);border-color:var(--f-accent);color:var(--f-on-accent)}
        .forum-minipages{margin-left:8px;font-size:.8em}.forum-minipages a,.forum-minipages span{margin-right:4px;text-decoration:none;opacity:.8}
        @media(max-width:760px){.forum-row,.forum-list .forum-row{grid-template-columns:30px minmax(0,1fr)}.forum-row .num{display:none}.forum-row .last{grid-column:2}}
        .forum-posts{display:grid;gap:16px;margin:0 0 16px}
        .forum-post{display:grid;grid-template-columns:180px minmax(0,1fr);border:1px solid var(--f-line);border-radius:var(--f-radius);overflow:hidden;scroll-margin-top:calc(var(--mat-adminbar-h,0px) + 90px)}
        .forum-post:target{box-shadow:0 0 0 2px var(--f-accent)}
        .forum-author{padding:16px;background:var(--f-soft);border-right:1px solid var(--f-line);display:flex;flex-direction:column;gap:8px;align-items:flex-start;min-width:0;overflow-wrap:anywhere}
        .forum-author .forum-member{font-weight:650;text-decoration:none}
        .forum-avatar{width:48px;height:48px;border-radius:50%;display:inline-grid;place-items:center;background:var(--f-accent);color:var(--f-on-accent);font-weight:700;letter-spacing:.02em;flex:none}
        .forum-avatar.sm{width:28px;height:28px;font-size:.75em}
        .forum-post-main{padding:14px 18px;min-width:0;display:flex;flex-direction:column}
        .forum-post-head{display:flex;gap:12px;justify-content:space-between;font-size:.85em;opacity:.75;margin:0 0 8px}.forum-post-no{text-decoration:none}
        .forum-body{line-height:1.65;overflow-wrap:anywhere}.forum-body img.forum-img{max-width:100%;height:auto;border-radius:6px}
        .forum-quote{margin:0 0 12px;padding:10px 14px;border-left:3px solid var(--f-accent);background:var(--f-soft);border-radius:0 8px 8px 0}
        .forum-quote cite{display:block;font-style:normal;font-weight:600;font-size:.88em;margin:0 0 4px}
        .forum-code{margin:0 0 12px;padding:12px 14px;border-radius:8px;background:rgba(127,127,127,.12);overflow:auto;font-size:.9em}
        .forum-signature{margin-top:14px;padding-top:10px;border-top:1px dashed var(--f-line);font-size:.86em;opacity:.8}
        .forum-post-actions{display:flex;flex-wrap:wrap;gap:6px;margin-top:auto;padding-top:12px}
        .forum-post-actions form{display:inline}.forum-post-actions a,.forum-post-actions button{padding:4px 10px;border:1px solid var(--f-line);border-radius:6px;background:transparent;color:inherit;font:inherit;font-size:.85em;cursor:pointer;text-decoration:none}
        @media(max-width:700px){.forum-post{grid-template-columns:1fr}.forum-author{flex-direction:row;align-items:center;border-right:0;border-bottom:1px solid var(--f-line);padding:10px 14px}.forum-avatar{width:36px;height:36px}}
        .forum-editor{border:1px solid var(--f-line);border-radius:var(--f-radius);padding:16px;margin:0 0 16px}.forum-editor h2{margin:0 0 10px;font-size:1.15em}
        .forum-editor label{display:block;font-weight:600;margin:10px 0 6px}.forum-editor input[type=text],.forum-editor input[type=email],.forum-editor input[type=password],.forum-editor textarea,.forum-editor select{width:100%;box-sizing:border-box;padding:10px 12px;border:1px solid var(--f-line);border-radius:8px;font:inherit;background:transparent;color:inherit}
        .forum-editor textarea{min-height:180px;resize:vertical;line-height:1.55}
        .forum-bb{display:flex;flex-wrap:wrap;gap:4px;margin:0 0 6px}.forum-bb button{min-width:34px;padding:4px 8px;border:1px solid var(--f-line);border-radius:6px;background:transparent;color:inherit;font:inherit;font-size:.85em;cursor:pointer}
        .forum-editor .row{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin-top:12px}.forum-check{display:inline-flex;gap:8px;align-items:center;font-weight:400!important;margin:0!important}
        .forum-hint{font-size:.85em;opacity:.7;margin:6px 0 0}
        .forum-notice{padding:10px 14px;border-radius:8px;margin:0 0 16px;background:var(--f-soft);border:1px solid var(--f-line)}.forum-notice.ok{background:rgba(22,163,74,.1);border-color:rgba(22,163,74,.3)}.forum-notice.err{background:rgba(220,38,38,.08);border-color:rgba(220,38,38,.3)}
        .forum-pager{display:flex;flex-wrap:wrap;gap:6px;margin:0 0 16px}.forum-pager a,.forum-pager span{min-width:34px;padding:6px 10px;text-align:center;border:1px solid var(--f-line);border-radius:8px;text-decoration:none}
        .forum-pager .cur{background:var(--f-accent);border-color:var(--f-accent);color:var(--f-on-accent);font-weight:700}.forum-pager .gap{border:0}
        .forum-stats{display:flex;flex-wrap:wrap;gap:8px 22px;padding:14px 16px;border:1px solid var(--f-line);border-radius:var(--f-radius);font-size:.9em}
        .forum-box{border:1px solid var(--f-line);border-radius:var(--f-radius);margin:0 0 22px}.forum-box>h2{margin:0;padding:12px 16px;font-size:1.05em;background:var(--f-soft);border-bottom:1px solid var(--f-line)}
        .forum-box ul{list-style:none;margin:0;padding:0}.forum-box li{padding:10px 16px;border-top:1px solid var(--f-line)}.forum-box li:first-child{border-top:0}.forum-box li a{text-decoration:none;font-weight:600}
        .forum-hit{padding:12px 16px;border-top:1px solid var(--f-line)}.forum-hit:first-child{border-top:0}.forum-hit p{margin:4px 0 0;font-size:.92em;opacity:.85}
        .forum-profile{display:flex;gap:16px;align-items:center;margin:0 0 18px}.forum-profile .forum-avatar{width:64px;height:64px;font-size:1.3em}
        .forum-mod{margin:24px 0 0;padding:14px 16px;border:1px dashed var(--f-line);border-radius:var(--f-radius)}.forum-mod h2{margin:0 0 10px;font-size:1em}
        .forum-mod form{display:inline-flex;gap:6px;align-items:center;margin:0 8px 8px 0}.forum-mod select{padding:7px 10px;border:1px solid var(--f-line);border-radius:8px;font:inherit;background:transparent;color:inherit}
        .forum-member.gone{opacity:.7;font-style:italic}
        .forum-confirm{display:inline-block;margin:0 8px 8px 0;vertical-align:top}.forum-confirm>summary{list-style:none;display:inline-block;padding:4px 10px;border:1px solid rgba(180,35,24,.4);border-radius:6px;color:#b42318;cursor:pointer;font-size:.85em}
        .forum-confirm>summary::-webkit-details-marker{display:none}.forum-confirm[open]>summary{opacity:.6}.forum-confirm>span{display:inline-block;margin:0 8px;font-size:.88em}.forum-confirm form{display:inline}
        .forum-mod .forum-confirm>summary{padding:7px 12px;font-size:.9em;border-radius:var(--btn-radius,8px)}
        """;
}
