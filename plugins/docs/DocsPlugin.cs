using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Docs;

/// <summary>
/// Docs / Wiki: a documentation for a MatCMS site — sections, pages and sub-pages as a tree, the text in Markdown (or the editor's rich text), a table of
/// contents per page, previous/next, a sidebar, breadcrumbs and a full-text search; German and English (<c>/docs</c>, <c>/en/docs</c>), optional versions
/// (<c>/docs/v/1.0/…</c>). Template parts give it the site's look, blocks and a menu search box put it on any page, and an API fills it from outside.
/// <para>A native plugin, written against <c>MatCMS.Plugin.Abstractions</c> only — no host type, no reflection-based JSON over its own types.</para>
/// </summary>
public sealed partial class DocsPlugin : IPlugin
{
    private const string SvgBook = @"<path d=""M3 19a9 9 0 0 1 9 0a9 9 0 0 1 9 0""/><path d=""M3 6a9 9 0 0 1 9 0a9 9 0 0 1 9 0""/><path d=""M3 6v13""/><path d=""M12 6v13""/><path d=""M21 6v13""/>";
    private const string SvgSearch = @"<circle cx=""10"" cy=""10"" r=""7""/><path d=""M21 21l-6-6""/>";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        DocsStrings.Register(b);
        DocsStore.Declare(b.Data);
        DocsSettings.Declare(b.Settings);
        DocsHtml.RegisterParts(b);

        // ---- public pages: the start page, every page by its path, the search, older versions under /docs/v/{name}/… ------------------------------
        // In every language the site publishes in (/en/docs/…): no DefaultLanguageOnly — the handler answers 404 for a language the site does not have.
        var read = new PublicRouteOptions { Methods = PluginMethod.Get };
        b.Route(DocsUrls.Root, DocsPublic.RouteAsync, read);
        b.Route(DocsUrls.Root + "/{*path}", DocsPublic.RouteAsync, read);

        // ---- back office -------------------------------------------------------------------------------------------------
        b.Admin.Section("docs.nav", "ti-book-2", AdminGroup.Content, 35)
            .Page("pages", "docs.admin.pages", DocsAdmin.PagesAsync)
            .Page("pages/{id}", null, DocsAdmin.EditAsync)
            .Page("versions", "docs.admin.versions", DocsAdmin.VersionsAsync)
            .Page("convert", null, DocsAdmin.ConvertAsync)
            .Page("transfer", "docs.admin.transfer", DocsTransfer.PageAsync)
            .Link("docs.admin.pages", "pages", "ti-list-tree", also: new[] { "pages/{id}" })
            .Link("docs.admin.versions", "versions", "ti-versions")
            .Link("docs.admin.transfer", "transfer", "ti-file-zip")
            .Link("docs.admin.settings", "_settings", "ti-settings");
        b.Admin.Tile(DocsAdmin.TileAsync);
        b.Admin.RecentlyEdited(DocsAdmin.RecentAsync);

        // ---- blocks and menu pieces --------------------------------------------------------------------------------------
        b.Block(new BlockSpec("docs_sections", "docs.block.sections", "docs.block.sectionsDesc")
        {
            Category = "design", IconSvg = SvgBook,
            Fields = new[]
            {
                new FieldSpec("heading", "docs.block.heading"),
                new FieldSpec("layout", "docs.block.layout", FieldKind.Select)
                {
                    Default = "cards", Options = new[] { new FieldOption("cards", "docs.set.homeCards"), new FieldOption("list", "docs.set.homeList") }
                },
                new FieldSpec("limit", "docs.block.limit") { Help = "docs.block.limitHelp" }
            }
        }, DocsPublic.SectionsBlockAsync);
        b.Block(new BlockSpec("docs_search", "docs.block.search", "docs.block.searchDesc")
        {
            Category = "form", IconSvg = SvgSearch, Fields = new[] { new FieldSpec("heading", "docs.block.heading") }
        }, DocsPublic.SearchBlockAsync);
        b.MenuElement(new MenuElementSpec("search", "docs.el.search") { Help = "docs.el.searchHelp", Icon = "search" }, DocsPublic.SearchElementAsync);
        b.MenuElement(new MenuElementSpec("docs", "docs.el.docs") { Help = "docs.el.docsHelp", Icon = "book-2" }, DocsPublic.DocsElementAsync);

        // ---- the rest of the site ----------------------------------------------------------------------------------------
        b.Sitemap(SitemapAsync);
        b.LinkTargets("docs.lt.group", LinksAsync);
        b.MediaReferences(MediaAsync);
        b.On("site.search", DocsPublic.SiteSearchAsync);
        DocsOps.Register(b);
    }

    /// <summary>Per language: the start page and every published page of the DEFAULT version (an older version is not for search engines).</summary>
    private static async Task<IEnumerable<SitemapEntry>> SitemapAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!ctx.Settings.GetBool(DocsSettings.Indexing)) return Array.Empty<SitemapEntry>();
        var list = new List<SitemapEntry>();
        foreach (var l in DocsUrls.Languages(ctx))
        {
            var v = await DocsView.CreateAsync(ctx, l, null, false, ct);
            if (v.Tree.Flat.Count == 0 && l != DocsUrls.Default(ctx)) continue;
            list.Add(new SitemapEntry(v.Home));
            list.AddRange(v.Tree.Flat.Select(n => new SitemapEntry(v.PageUrl(n), n.Page.UpdatedAt > DateTime.MinValue ? n.Page.UpdatedAt : null)));
        }
        return list;
    }

    private static async Task<IEnumerable<LinkTarget>> LinksAsync(IPluginContext ctx, CancellationToken ct)
    {
        var langs = DocsUrls.Languages(ctx);
        var list = new List<LinkTarget>();
        foreach (var l in langs)
        {
            var suffix = langs.Count > 1 ? " (" + l.ToUpperInvariant() + ")" : "";
            var v = await DocsView.CreateAsync(ctx, l, null, false, ct);
            list.Add(new LinkTarget(DocsSettings.TitleOf(ctx) + suffix, v.Home));
            list.Add(new LinkTarget(ctx.T("docs.search") + suffix, v.SearchUrl));
            list.AddRange(v.Tree.Flat.Select(n => new LinkTarget(string.Concat(Enumerable.Repeat("– ", n.Depth)) + n.Page.Title + suffix, v.PageUrl(n))));
        }
        return list.Take(300).ToList();
    }

    /// <summary>The media-library files the pages show or link (<c>/uploads/…</c>), so the cleanup does not offer them as unused.</summary>
    private static async Task<IEnumerable<string>> MediaAsync(IPluginContext ctx, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in await new DocsStore(ctx.Data).AllPagesAsync(ct))
            foreach (Match m in UploadRx().Matches(p.Body)) found.Add(m.Value);
        return found;
    }

    [GeneratedRegex(@"/uploads/[^\s""'()<>\]]+")]
    private static partial Regex UploadRx();
}
