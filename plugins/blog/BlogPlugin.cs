using System.Globalization;

namespace MatCMS.Plugin.Blog;

/// <summary>
/// The blog: posts with title picture, gallery, tags and scheduling, post pages under <c>/blog</c>, the block "Beiträge". It shipped with the
/// CMS until 2026-11 (a built-in plugin); it is a native plugin of the MatCMS Store now (<c>docs/builtin-extraction.md</c>), written against the
/// plugin API and nothing else.
/// <para>It <b>adopts</b> the CMS's own <c>Posts</c> table — no data moved when the blog became a plugin, nor when it left the image. Adopting a
/// core table is allowed only to code signed by the MatCMS Store, so this plugin runs as the store's signed package.</para>
/// <para><b>Multilingual:</b> a post has a language (<c>Locale</c>); the default language's posts keep the addresses they always had
/// (<c>/blog/{slug}</c>), the others live under the site's prefix (<c>/en/blog/{slug}</c>, <see cref="BlogUrls"/>). Translations share a
/// <c>TranslationGroup</c> and the language switcher leads from a post to its translation.</para>
/// </summary>
public sealed class BlogPlugin : IPlugin
{
    private const string SvgGrid = @"<rect x=""3"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""3"" y=""14"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""14"" width=""7"" height=""7"" rx=""1""/>";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        // Texts, one table per language, embedded in the assembly.
        foreach (var culture in new[] { "de", "en" }) b.StringsJson(culture, ReadStrings(culture));

        // The table: the CMS's own Posts, taken over as it is (the column TranslationGroup is ADDED by this declaration on an older site).
        b.Data.Table("posts", t =>
        {
            t.Adopt("Posts", "Id");
            foreach (var (name, type, required) in BlogStore.Columns)
                switch (type)
                {
                    case ColumnType.Bool: t.Bool(name, required); break;
                    case ColumnType.DateTime: t.Timestamp(name, required); break;
                    default: t.Text(name, required); break;
                }
            t.UniqueIndex("Slug", "Locale");
        });

        // The overview and the post page — on the plain address for the default language and under every other language's prefix
        // (/en/blog/x): no DefaultLanguageOnly any more. The handlers answer 404 for a language the site does not publish in.
        b.Route("/blog", BlogPublic.OverviewAsync, new PublicRouteOptions { Methods = PluginMethod.Get });
        b.Route("/blog/{slug}", BlogPublic.PostPageAsync, new PublicRouteOptions { Methods = PluginMethod.Get });

        // The link picker: the overviews and the live posts of every language.
        b.LinkTargets("blog.lt.pages", (ctx, ct) => Task.FromResult<IEnumerable<LinkTarget>>(
            BlogUrls.Languages(ctx).Select(l => new LinkTarget(ctx.T("blog.overview") + Suffix(ctx, l), BlogUrls.Overview(ctx, l))).ToList()));
        b.LinkTargets("blog.lt.posts", async (ctx, ct) =>
        {
            var posts = ctx.Data.Table("posts");
            var all = new List<LinkTarget>();
            foreach (var l in BlogUrls.Languages(ctx))
                all.AddRange((await posts.QueryAsync(BlogStore.Live(l).OrderBy("PublishedAt", descending: true).Page(0, 300), ct))
                    .Select(BlogStore.From).Select(p => new LinkTarget(p.Title + Suffix(ctx, l), BlogUrls.Post(ctx, p))));
            return all.Take(300).ToList();
        });

        // The content block.
        b.Block(new BlockSpec("posts", "block.posts.name", "block.posts.desc")
        {
            Category = "design", IconSvg = SvgGrid,
            Fields = new[]
            {
                new FieldSpec("heading", "block.f.heading", FieldKind.Text),
                new FieldSpec("display", "block.posts.f.display", FieldKind.Select)
                {
                    Default = "cards", Help = "block.posts.f.display.help",
                    Options = new[] { new FieldOption("cards", "block.posts.opt.cards"), new FieldOption("blog", "block.posts.opt.blog") }
                },
                new FieldSpec("tag", "block.posts.f.tag", FieldKind.Text) { Help = "block.posts.f.tag.help" },
                new FieldSpec("columns", "block.gallery.f.columns", FieldKind.Select)
                {
                    Default = "3", Help = "block.posts.f.columns.help",
                    Options = new[] { new FieldOption("2", "block.opt.columns.2"), new FieldOption("3", "block.opt.columns.3") }
                },
                new FieldSpec("perPage", "block.posts.f.perPage", FieldKind.Text) { Help = "block.posts.f.perPage.help" },
                new FieldSpec("limit", "block.posts.f.limit", FieldKind.Text) { Help = "block.posts.f.limit.help" },
                new FieldSpec("showFilter", "block.gallery.f.showFilter", FieldKind.Select)
                {
                    Default = "no", Help = "block.posts.f.showFilter.help",
                    Options = new[] { new FieldOption("yes", "block.opt.yesno.yes"), new FieldOption("no", "block.opt.yesno.no") }
                }
            }
        }, BlogPublic.PostsBlockAsync);

        // The back office: one entry between "Seiten" and "Menüs", one list, one editor, and the two helpers the editor calls.
        b.Admin.Section("nav.posts", "ti-news", AdminGroup.Content, 20)
            .Page("posts", "posts.title", BlogAdmin.ListAsync)
            .Page("posts/{id}", null, BlogAdmin.EditAsync)
            .Page("import-link", null, BlogAdmin.ImportLinkAsync)
            .Page("ai-excerpt", null, BlogAdmin.AiExcerptAsync);

        // The start page: the count, the drafts, and the posts worked on last.
        b.Admin.Tile(async (ctx, ct) =>
        {
            var posts = ctx.Data.Table("posts");
            var drafts = await posts.CountAsync(DataQuery.Everything.Eq("IsPublished", false), ct);
            return Ui.Stat(ctx.T("dashboard.posts"), (await posts.CountAsync(null, ct)).ToString(CultureInfo.InvariantCulture), ctx.T("dashboard.drafts", drafts), href: BlogAdmin.Section);
        });
        b.Admin.RecentlyEdited(async (ctx, ct) =>
            (await ctx.Data.Table("posts").QueryAsync(DataQuery.Everything.OrderBy("UpdatedAt", descending: true).Page(0, 30), ct))
                .Select(BlogStore.From)
                .Select(p => new RecentItem("ti-news", p.Title, p.IsPublished, p.UpdatedAt, $"{BlogAdmin.Section}/posts/{p.Id}")));

        // The sitemap: per language its overview (the default one always, the others once they have a post) and its live posts.
        b.Sitemap(async (ctx, ct) =>
        {
            var posts = ctx.Data.Table("posts");
            var entries = new List<SitemapEntry>();
            foreach (var l in BlogUrls.Languages(ctx))
            {
                var live = (await posts.QueryAsync(BlogStore.Live(l), ct)).Select(BlogStore.From).ToList();
                if (live.Count == 0 && l != BlogUrls.Default(ctx)) continue;
                entries.Add(new SitemapEntry(BlogUrls.Overview(ctx, l)));
                entries.AddRange(live.Select(p => new SitemapEntry(BlogUrls.Post(ctx, p), p.UpdatedAt)));
            }
            return entries;
        });

        // Posts for the cloud, MCP and the site's AI assistant (BlogOps): described, so the assistant can use them; every write answers with its undo.
        b.Operation(BlogOps.List, BlogOps.ListAsync);
        b.Operation(BlogOps.Read, BlogOps.ReadAsync);
        b.Operation(BlogOps.Create, BlogOps.CreateAsync);
        b.Operation(BlogOps.Update, BlogOps.UpdateAsync);
        b.Operation(BlogOps.Delete, BlogOps.DeleteAsync);
        b.Operation(BlogOps.Restore, BlogOps.RestoreAsync);

        // A site-wide search (the AI bot) asks every plugin for what it holds: the blog answers with its published posts.
        b.On("site.search", BlogSearch.SearchAsync);
        b.On("content.item", BlogPublic.ContentItemAsync);

        // The built-in blog's editor wrote every post as "de" whatever the site's language was. On a site whose default language is another
        // one and that does not publish in German at all, those posts are the default language's: relabelled once, so they keep their addresses.
        b.Lifecycle.OnLoad(async (ctx, ct) => await RelabelGermanAsync(ctx, ct));
    }

    /// <summary>" (EN)" behind a label on a site with more than one language; nothing on a site with one.</summary>
    private static string Suffix(IPluginContext ctx, string locale) => BlogUrls.Languages(ctx).Count > 1 ? " (" + locale.ToUpperInvariant() + ")" : "";

    private static async Task RelabelGermanAsync(IPluginContext ctx, CancellationToken ct)
    {
        var def = BlogUrls.Default(ctx);
        if (def == "de" || BlogUrls.Languages(ctx).Contains("de")) return;
        var posts = ctx.Data.Table("posts");
        foreach (var p in (await posts.QueryAsync(DataQuery.Everything.Eq("Locale", "de"), ct)).Select(BlogStore.From))
            if (!await BlogStore.SlugTakenAsync(posts, p.Slug, def, p.Id, ct))
                await posts.UpdateAsync(p.Id, new Dictionary<string, object?> { ["Locale"] = def }, ct);
    }

    private static string ReadStrings(string culture)
    {
        var asm = typeof(BlogPlugin).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith($".blog.{culture}.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
