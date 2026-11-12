using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.Docs;

/// <summary>What one request sees of the docs: its language and version, the versions there are, and the tree (without drafts for a visitor).</summary>
internal sealed class DocsView
{
    public required IPluginContext Ctx { get; init; }
    public required DocsStore Store { get; init; }
    public required string Locale { get; init; }
    public required string Version { get; init; }
    public required string DefaultVersion { get; init; }
    public required IReadOnlyList<DocVersion> Versions { get; init; }
    public required DocTree Tree { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsSearch { get; set; }

    public string Home => DocsUrls.Home(Ctx, Locale, Version, DefaultVersion);
    public string PageUrl(DocNode n) => DocsUrls.Page(Ctx, Locale, Version, DefaultVersion, n.Path);
    public string SearchUrl => DocsUrls.Search(Ctx, Locale, Version, DefaultVersion);

    /// <summary>The view of a language and version; <paramref name="version"/> null = the default version.</summary>
    public static async Task<DocsView> CreateAsync(IPluginContext ctx, string locale, string? version, bool asAdmin, CancellationToken ct)
    {
        var store = new DocsStore(ctx.Data);
        var versions = await store.VersionsAsync(ct);
        var def = DocsStore.DefaultVersion(versions);
        var v = version ?? def;
        var tree = await store.TreeAsync(locale, v, ct);
        return new DocsView
        {
            Ctx = ctx, Store = store, Locale = locale, Version = v, DefaultVersion = def, Versions = versions, IsAdmin = asAdmin,
            Tree = asAdmin ? tree : DocsStore.Visible(tree)
        };
    }

    /// <summary>A doc: link's path resolved in this language and version (null when there is no such page — the link is drawn as broken).</summary>
    public string? Resolve(string path)
    {
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length == 0 ? Home : Tree.ByPath(segs) is { } n ? PageUrl(n) : null;
    }

    public DocsMarkdown.Options MarkdownOptions => new()
    {
        ResolveDoc = Resolve, AlertTitle = kind => Ctx.T("docs.alert." + kind), CopyLabel = Ctx.T("docs.copy"), CopiedLabel = Ctx.T("docs.copied")
    };

    /// <summary>A page's text drawn (Markdown or the editor's rich text).</summary>
    public RenderedDoc Render(DocPage p) => p.Format == "html" ? DocsMarkdown.FromHtml(p.Body) : DocsMarkdown.Render(p.Body, MarkdownOptions);
}

/// <summary>What the visitor sees: the docs' start page, a page, the search — and the blocks, the menu pieces and the answers to <c>site.search</c>.</summary>
internal static class DocsPublic
{
    private static string E(string? s) => DocsMarkdown.E(s);

    /// <summary><c>/docs</c> and everything below it. The path is read here: <c>v/{version}/…</c>, the search, or a page's path of slugs.</summary>
    public static async Task<PluginResult> RouteAsync(IPluginContext ctx, CancellationToken ct)
    {
        var locale = DocsUrls.Current(ctx);
        if (!DocsUrls.Publishes(ctx, locale)) return PluginResult.NotFound();
        var segs = ctx.Request.R("path").Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).ToList();

        string? version = null;
        if (segs.Count >= 2 && segs[0] == "v")
        {
            version = segs[1];
            segs = segs.Skip(2).ToList();
        }
        var view = await DocsView.CreateAsync(ctx, locale, version, ctx.User.IsAdmin, ct);
        if (version is not null)
        {
            if (!view.Versions.Any(x => x.Name == version)) return NotFound(view);
            // The default version has the plain addresses; its /v/… form is the same page under a second address.
            if (version == view.DefaultVersion) return PluginResult.RedirectPermanent(DocsUrls.Home(ctx, locale, version, view.DefaultVersion) + (segs.Count > 0 ? "/" + string.Join("/", segs) : "") + Query(ctx));
        }

        if (segs.Count == 0) return Home(view);
        if (segs.Count == 1 && segs[0] is "suche" or "search") return await SearchAsync(view, ct);
        var node = view.Tree.ByPath(segs);
        return node is null ? NotFound(view) : await PageOfAsync(view, node, ct);
    }

    private static string Query(IPluginContext ctx) =>
        ctx.Request.Query.Count == 0 ? "" : "?" + string.Join("&", ctx.Request.Query.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    private static string NavLabel(IPluginContext ctx) => ctx.T("docs.nav.open");

    /// <summary>The other languages' address of the same thing, for the site's language switcher.</summary>
    private static List<KeyValuePair<string, string>> HomeAlternates(DocsView v) =>
        DocsUrls.Languages(v.Ctx).Where(l => l != v.Locale).Select(l => new KeyValuePair<string, string>(l, DocsUrls.Home(v.Ctx, l, v.Version, v.DefaultVersion))).ToList();

    // ---- start page ---------------------------------------------------------------------------------------------------

    private static PluginResult Home(DocsView v)
    {
        var ctx = v.Ctx;
        var title = DocsSettings.TitleOf(ctx);
        var html = ctx.RenderPart("home", new Dictionary<string, string>
        {
            ["title"] = title, ["intro"] = DocsSettings.IntroOf(ctx), ["nav_label"] = NavLabel(ctx),
            ["search_html"] = DocsHtml.SearchBox(ctx, v.SearchUrl),
            ["versions_html"] = DocsHtml.Versions(v, x => DocsUrls.Home(ctx, v.Locale, x.Name, v.DefaultVersion)),
            ["sidebar_html"] = DocsHtml.Sidebar(v, null),
            ["notice_html"] = OldVersionNotice(v),
            ["sections_html"] = DocsHtml.Sections(v, ctx.Settings.Get(DocsSettings.HomeLayout))
        });
        var page = DocsHtml.Page(ctx, title, html, DocsSettings.IntroOf(ctx), noIndex: v.Version != v.DefaultVersion);
        return DocsUrls.WithTranslations(page, HomeAlternates(v));
    }

    /// <summary>On an older version: a line that says so and leads to the current one.</summary>
    private static string OldVersionNotice(DocsView v)
    {
        if (v.Version == v.DefaultVersion || v.Versions.Count < 2) return "";
        var cur = v.Versions.FirstOrDefault(x => x.Name == v.DefaultVersion);
        return $"<p class=\"docs-notice\">{E(v.Ctx.T("docs.oldVersion", v.Versions.First(x => x.Name == v.Version).Shown))} <a href=\"{E(DocsUrls.Home(v.Ctx, v.Locale, v.DefaultVersion, v.DefaultVersion))}\">{E(v.Ctx.T("docs.toCurrent", cur?.Shown ?? v.DefaultVersion))}</a></p>";
    }

    // ---- a page -------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> PageOfAsync(DocsView v, DocNode node, CancellationToken ct)
    {
        var ctx = v.Ctx;
        var versionTargets = await SamePageInVersionsAsync(v, node, ct);
        var p = node.Page;
        var doc = v.Render(p);
        var notice = new StringBuilder(OldVersionNotice(v));
        if (!p.Published || node.Ancestors().Any(a => !a.Page.Published)) notice.Append($"<p class=\"docs-notice\">{E(ctx.T("docs.draftNotice"))}</p>");
        var edit = v.IsAdmin ? $"<a class=\"docs-edit\" href=\"{DocsAdmin.EditUrl(p.Id)}\">{E(ctx.T("docs.editPage"))}</a>" : "";
        var html = ctx.RenderPart("page", new Dictionary<string, string>
        {
            ["title"] = p.Title, ["summary"] = p.Summary, ["nav_label"] = NavLabel(ctx),
            ["updated"] = ctx.Settings.GetBool(DocsSettings.LastUpdated) && p.UpdatedAt > DateTime.MinValue ? ctx.T("docs.updated", DocsHtml.Date(p.UpdatedAt, v.Locale)) : "",
            ["crumbs_html"] = DocsHtml.Crumbs(v, node),
            ["notice_html"] = notice.ToString(),
            ["sidebar_html"] = DocsHtml.Sidebar(v, node),
            ["search_html"] = DocsHtml.SearchBox(ctx, v.SearchUrl),
            ["versions_html"] = DocsHtml.Versions(v, x => versionTargets.TryGetValue(x.Name, out var u) ? u : DocsUrls.Home(ctx, v.Locale, x.Name, v.DefaultVersion)),
            ["content_html"] = doc.Html,
            ["children_html"] = DocsHtml.Children(v, node),
            ["toc_html"] = ctx.Settings.GetBool(DocsSettings.Toc) ? DocsHtml.Toc(ctx, doc.Toc) : "",
            ["prevnext_html"] = ctx.Settings.GetBool(DocsSettings.PrevNext) ? DocsHtml.PrevNext(v, node) : "",
            ["edit_html"] = edit
        });
        var description = p.Summary.Length > 0 ? p.Summary : Excerpt(DocsMarkdown.Plain(doc.Html), 160);
        var page = DocsHtml.Page(ctx, p.Title + " – " + DocsSettings.TitleOf(ctx), html, description, noIndex: v.Version != v.DefaultVersion || !p.Published);
        return DocsUrls.WithTranslations(page, await AlternatesAsync(v, p, ct));
    }

    /// <summary>For the version switcher: the same page in every other version (by its path), else that version's start page.</summary>
    private static async Task<Dictionary<string, string>> SamePageInVersionsAsync(DocsView v, DocNode node, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (v.Versions.Count < 2) return map;
        var path = node.Path.Split('/');
        foreach (var x in v.Versions)
        {
            if (x.Name == v.Version) { map[x.Name] = v.PageUrl(node); continue; }
            var other = DocsStore.Visible(await v.Store.TreeAsync(v.Locale, x.Name, ct));
            map[x.Name] = other.ByPath(path) is { } n ? DocsUrls.Page(v.Ctx, v.Locale, x.Name, v.DefaultVersion, n.Path) : DocsUrls.Home(v.Ctx, v.Locale, x.Name, v.DefaultVersion);
        }
        return map;
    }

    /// <summary>The page's translations (the same group, published, in a language the site publishes in) for the language switcher.</summary>
    private static async Task<List<KeyValuePair<string, string>>> AlternatesAsync(DocsView v, DocPage p, CancellationToken ct)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (p.GroupKey.Length == 0) return HomeAlternates(v);
        foreach (var l in DocsUrls.Languages(v.Ctx).Where(l => l != v.Locale))
        {
            var tree = DocsStore.Visible(await v.Store.TreeAsync(l, v.Version, ct));
            var match = tree.Flat.FirstOrDefault(n => n.Page.GroupKey == p.GroupKey);
            list.Add(new(l, match is null ? DocsUrls.Home(v.Ctx, l, v.Version, v.DefaultVersion) : DocsUrls.Page(v.Ctx, l, v.Version, v.DefaultVersion, match.Path)));
        }
        return list;
    }

    private static PluginResult NotFound(DocsView v)
    {
        var ctx = v.Ctx;
        var html = ctx.RenderPart("search", new Dictionary<string, string>
        {
            ["title"] = ctx.T("docs.notFound"), ["nav_label"] = NavLabel(ctx), ["crumbs_html"] = DocsHtml.Crumbs(v, null, ctx.T("docs.notFound")),
            ["search_html"] = DocsHtml.SearchBox(ctx, v.SearchUrl), ["versions_html"] = "", ["sidebar_html"] = DocsHtml.Sidebar(v, null),
            ["count"] = ctx.T("docs.notFoundText"), ["results_html"] = ""
        });
        return DocsHtml.Page(ctx, ctx.T("docs.notFound"), html, status: 404, noIndex: true);
    }

    // ---- search -------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> SearchAsync(DocsView v, CancellationToken ct)
    {
        v.IsSearch = true;
        var ctx = v.Ctx;
        var q = ctx.Request.Q("q").Trim();
        if (q.Length > 200) q = q[..200];
        var hits = q.Length >= 2 ? Search(v, q, 50) : new List<Hit>();
        var results = new StringBuilder();
        if (hits.Count > 0)
        {
            results.Append("<ol class=\"docs-results\">");
            foreach (var h in hits)
            {
                var path = string.Join(" › ", h.Node.Ancestors().Reverse().Select(a => a.Page.Title));
                results.Append($"<li><a href=\"{E(v.PageUrl(h.Node))}\">{E(h.Node.Page.Title)}</a>");
                if (path.Length > 0) results.Append($"<div class=\"path\">{E(path)}</div>");
                if (h.Excerpt.Length > 0) results.Append($"<p>{Mark(h.Excerpt, Terms(q))}</p>");
                results.Append("</li>");
            }
            results.Append("</ol>");
        }
        var count = q.Length < 2 ? (q.Length == 0 ? "" : ctx.T("docs.search.short")) : hits.Count == 0 ? ctx.T("docs.search.none", q) : ctx.T(hits.Count == 1 ? "docs.search.one" : "docs.search.many", hits.Count, q);
        var html = ctx.RenderPart("search", new Dictionary<string, string>
        {
            ["title"] = ctx.T("docs.search"), ["count"] = count, ["nav_label"] = NavLabel(ctx),
            ["crumbs_html"] = DocsHtml.Crumbs(v, null, ctx.T("docs.search")),
            ["search_html"] = DocsHtml.SearchBox(ctx, v.SearchUrl, q),
            ["versions_html"] = DocsHtml.Versions(v, x => DocsUrls.Search(ctx, v.Locale, x.Name, v.DefaultVersion) + (q.Length > 0 ? "?q=" + Uri.EscapeDataString(q) : "")),
            ["sidebar_html"] = DocsHtml.Sidebar(v, null),
            ["results_html"] = results.ToString()
        });
        await Task.CompletedTask;
        return DocsHtml.Page(ctx, ctx.T("docs.search") + " – " + DocsSettings.TitleOf(ctx), html, noIndex: true);
    }

    internal sealed record Hit(DocNode Node, double Score, string Excerpt);

    // The plain text of a page, kept per (id, last change): the search reads every page of a language, and drawing them all each time is wasted.
    private static readonly ConcurrentDictionary<long, (DateTime Updated, string Text)> PlainCache = new();

    private static string PlainOf(DocsView v, DocPage p)
    {
        if (PlainCache.TryGetValue(p.Id, out var hit) && hit.Updated == p.UpdatedAt) return hit.Text;
        var text = DocsMarkdown.Plain(v.Render(p).Html);
        PlainCache[p.Id] = (p.UpdatedAt, text);
        return text;
    }

    private static List<string> Terms(string q) =>
        q.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"', '\'')).Where(t => t.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

    /// <summary>
    /// The pages that contain EVERY term (in the title, the summary or the text), best first: a term in the title counts most, then the summary, then how
    /// often it occurs in the text. Small sites have a few hundred pages at most, so this reads them all — in memory, from the tree kept there.
    /// </summary>
    internal static List<Hit> Search(DocsView v, string q, int max)
    {
        var terms = Terms(q);
        if (terms.Count == 0) return new List<Hit>();
        var hits = new List<Hit>();
        foreach (var n in v.Tree.Flat)
        {
            var p = n.Page;
            var text = PlainOf(v, p);
            double score = 0;
            var all = true;
            foreach (var t in terms)
            {
                var inTitle = p.Title.Contains(t, StringComparison.OrdinalIgnoreCase);
                var inSummary = p.Summary.Contains(t, StringComparison.OrdinalIgnoreCase);
                var count = Count(text, t);
                if (!inTitle && !inSummary && count == 0) { all = false; break; }
                score += (inTitle ? 10 : 0) + (p.Title.Equals(t, StringComparison.OrdinalIgnoreCase) ? 10 : 0) + (inSummary ? 4 : 0) + Math.Min(count, 10);
            }
            if (!all) continue;
            score += q.Length > 3 && p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ? 8 : 0;
            hits.Add(new Hit(n, score, Snippet(text.Length > 0 ? text : p.Summary, terms)));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Node.Depth).Take(max).ToList();
    }

    private static int Count(string text, string term)
    {
        var n = 0;
        for (var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0 && n < 50; i = text.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>About 200 characters around the first term found (plain text).</summary>
    internal static string Snippet(string text, IReadOnlyList<string> terms)
    {
        if (text.Length <= 220) return text;
        var at = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 70);
        if (start > 0) { var sp = text.IndexOf(' ', start); if (sp > 0 && sp < at) start = sp + 1; }
        var end = Math.Min(text.Length, start + 220);
        if (end < text.Length) { var sp = text.LastIndexOf(' ', end); if (sp > at) end = sp; }
        return (start > 0 ? "… " : "") + text[start..end] + (end < text.Length ? " …" : "");
    }

    /// <summary>The excerpt encoded, the terms wrapped in &lt;mark&gt;.</summary>
    internal static string Mark(string text, IReadOnlyList<string> terms)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var best = -1;
            var len = 0;
            foreach (var t in terms)
            {
                var k = text.IndexOf(t, i, StringComparison.OrdinalIgnoreCase);
                if (k >= 0 && (best < 0 || k < best)) { best = k; len = t.Length; }
            }
            if (best < 0) { sb.Append(E(text[i..])); break; }
            sb.Append(E(text[i..best])).Append("<mark>").Append(E(text.Substring(best, len))).Append("</mark>");
            i = best + len;
        }
        return sb.ToString();
    }

    internal static string Excerpt(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max);
        return text[..(cut > 40 ? cut : max)] + " …";
    }

    // ---- blocks, menu, search of the site ------------------------------------------------------------------------

    /// <summary>The block "Docs: Bereiche" — the sections as cards (or a list), e.g. on the site's own start page.</summary>
    public static async Task<PluginResult> SectionsBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var v = await DocsView.CreateAsync(ctx, DocsUrls.Current(ctx), null, false, ct);
        var heading = ctx.Block?.Get("heading") ?? "";
        var layout = ctx.Block?.Get("layout") is "list" ? "list" : "cards";
        var limit = int.TryParse(ctx.Block?.Get("limit"), NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 0, 60) : 0;
        var html = (heading.Length > 0 ? $"<h2 class=\"docs-block-title\">{E(heading)}</h2>" : "") + DocsHtml.Sections(v, layout, limit);
        return PluginResult.Fragment($"<style>{DocsHtml.Css}</style><section class=\"section\"><div class=\"container\"><div class=\"docs\">{html}</div></div></section>");
    }

    /// <summary>The block "Docs: Suche" — the search box, leading to the docs' search.</summary>
    public static Task<PluginResult> SearchBlockAsync(IPluginContext ctx, CancellationToken ct)
    {
        var locale = DocsUrls.Current(ctx);
        var heading = ctx.Block?.Get("heading") ?? "";
        var html = (heading.Length > 0 ? $"<h2 class=\"docs-block-title\">{E(heading)}</h2>" : "") + DocsHtml.SearchBox(ctx, DocsUrls.Prefix(ctx, locale) + DocsUrls.Root + "/" + DocsUrls.SearchSegment(locale));
        return Task.FromResult(PluginResult.Fragment($"<style>{DocsHtml.Css}</style><section class=\"section\"><div class=\"container\"><div class=\"docs docs-block-search\">{html}</div></div></section>"));
    }

    /// <summary>The menu element "Docs-Suche": the search box in a menu (drawn through the template part, so a template can restyle it).</summary>
    public static Task<MenuEntry?> SearchElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var locale = DocsUrls.Current(ctx);
        var action = DocsUrls.Prefix(ctx, locale) + DocsUrls.Root + "/" + DocsUrls.SearchSegment(locale);
        return Task.FromResult<MenuEntry?>(new MenuEntry(ctx.T("docs.search"), action) { Icon = "search", Html = "<style>" + SearchElementCss + "</style>" + DocsHtml.SearchBox(ctx, action) });
    }

    private const string SearchElementCss = ".mat-menu .docs-search,.docs-search{display:flex;gap:6px;margin:0}.docs-search input{min-width:0;width:12em;padding:6px 10px;border:1px solid var(--line,rgba(127,127,127,.3));border-radius:8px;font:inherit;background:transparent;color:inherit}.docs-search button{padding:6px 12px;border-radius:8px;border:1px solid var(--accent,#2563eb);background:var(--accent,#2563eb);color:#fff;font:inherit;cursor:pointer}";

    /// <summary>The menu element "Dokumentation": a link to the docs with the sections as a dropdown.</summary>
    public static async Task<MenuEntry?> DocsElementAsync(IPluginContext ctx, CancellationToken ct)
    {
        var v = await DocsView.CreateAsync(ctx, DocsUrls.Current(ctx), null, false, ct);
        return new MenuEntry(DocsSettings.TitleOf(ctx), v.Home)
        {
            Icon = "book-2", Children = v.Tree.Roots.Select(r => new MenuEntry(r.Page.Title, v.PageUrl(r))).ToList()
        };
    }

    /// <summary>
    /// The site's search (and the visitors' chat bot) asks every plugin: <c>{q,max,locale}</c> → <c>{items:[{title,url,text,kind}]}</c>. Only published pages of
    /// the default version in the asked language answer.
    /// </summary>
    public static async Task<JsonNode?> SiteSearchAsync(IPluginContext ctx, JsonNode? payload, CancellationToken ct)
    {
        var q = payload?["q"]?.GetValue<string>() ?? "";
        var max = payload?["max"] is JsonValue m && m.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 20) : 5;
        var locale = payload?["locale"]?.GetValue<string>() is { Length: > 0 } l ? l : DocsUrls.Default(ctx);
        if (q.Trim().Length < 2) return null;
        var v = await DocsView.CreateAsync(ctx, locale, null, false, ct);
        var hits = Search(v, q, max);
        if (hits.Count == 0) return null;
        var items = new JsonArray();
        foreach (var h in hits)
            items.Add(new JsonObject { ["title"] = h.Node.Page.Title, ["url"] = v.PageUrl(h.Node), ["text"] = h.Excerpt, ["kind"] = ctx.T("docs.search.kind"), ["score"] = h.Score / 10.0 });
        return new JsonObject { ["items"] = items };
    }
}
