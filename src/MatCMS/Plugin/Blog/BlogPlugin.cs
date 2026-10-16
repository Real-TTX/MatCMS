using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Plugin;
using MatCMS.Services;
using MatCMS.Services.Plugins;

namespace MatCMS.Plugin.Blog;

/// <summary>
/// The blog, as a plugin that ships with the CMS. It was a hard-wired part of the core (a model, a DbSet, admin pages, a public page,
/// a content block and a dozen references from elsewhere); now it is written against the same API as every other plugin, switched on in a
/// fresh site, and can be switched off — its pages, block and menu entry then disappear and its posts stay where they are.
/// <para>It <b>adopts</b> the CMS's existing <c>Posts</c> table: no data moved when the blog became a plugin, a site's posts are the same rows
/// they always were.</para>
/// </summary>
public sealed class BlogPlugin : IPlugin
{
    /// <summary>The plugin's identity in the list of built-in plugins.</summary>
    public static BuiltInPlugin Definition { get; } = new("blog", "Blog", "Beiträge mit Titelbild, Galerie, Tags und Terminierung, Beitragsseiten unter /blog und der Block „Beiträge“.", "1.0", () => new BlogPlugin());

    private const string SvgGrid = @"<rect x=""3"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""3"" y=""14"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""14"" width=""7"" height=""7"" rx=""1""/>";

    /// <inheritdoc />
    public void Configure(IPluginBuilder b)
    {
        // Texts, one table per language, embedded in the assembly.
        foreach (var culture in new[] { "de", "en" }) b.StringsJson(culture, ReadStrings(culture));

        // The table: the CMS's own Posts, taken over as it is.
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

        // The post page. Only on the plain address: the blog publishes in one language, so /en/blog/x stays a 404 as it always was.
        // The overview: every post, newest first, with the tag filter — what a menu item "Blog" points at.
        b.Route("/blog", BlogPublic.OverviewAsync, new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true });
        b.LinkTargets("blog.lt.pages", (ctx, ct) => Task.FromResult<IEnumerable<LinkTarget>>(new[] { new LinkTarget(ctx.T("blog.overview"), "/blog") }));
        b.LinkTargets("blog.lt.posts", async (ctx, ct) =>
            (await ctx.Data.Table("posts").QueryAsync(DataQuery.Everything.Eq("IsPublished", true).Eq("Locale", "de").Where("PublishedAt", DataOp.Le, DateTime.UtcNow).OrderBy("PublishedAt", descending: true).Page(0, 300), ct))
                .Select(BlogStore.From).Select(p => new LinkTarget(p.Title, "/blog/" + p.Slug)).ToList());
        b.Route("/blog/{slug}", BlogPublic.PostPageAsync, new PublicRouteOptions { Methods = PluginMethod.Get, DefaultLanguageOnly = true });

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

        // The sitemap lists the posts that are live.
        b.Sitemap(async (ctx, ct) =>
            (await ctx.Data.Table("posts").QueryAsync(DataQuery.Everything.Eq("IsPublished", true).Eq("Locale", "de").Where("PublishedAt", DataOp.Le, DateTime.UtcNow), ct))
                .Select(BlogStore.From).Select(p => new SitemapEntry("/blog/" + p.Slug, p.UpdatedAt)).Prepend(new SitemapEntry("/blog")));

        // The cloud's (and an agent's) "write a post".
        b.Operation("post.create", CreateFromCloudAsync);
    }

    private static string ReadStrings(string culture)
    {
        var asm = typeof(BlogPlugin).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith($".blog.{culture}.json", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Adds a post on behalf of the cloud. Add-only by slug, and the body is sanitised before it is stored — it comes from an AI and is
    /// shown raw on the public site, which is the whole reason <see cref="SafeHtml"/> exists.</summary>
    private static async Task<JsonNode?> CreateFromCloudAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var payload = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var title = (payload["title"]?.GetValue<string>() ?? "").Trim();
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(payload["slug"]?.GetValue<string>() ?? title);
        var publish = payload["publish"]?.GetValue<bool>() ?? true;
        static JsonObject Answer(string status, string message) => new() { ["status"] = status, ["message"] = message };
        if (string.IsNullOrWhiteSpace(title)) return Answer("failed", ctx.T("blog.noTitle"));
        if (string.IsNullOrWhiteSpace(slug)) return Answer("failed", ctx.T("blog.noSlug"));

        var posts = ctx.Data.Table("posts");
        if (await BlogStore.SlugTakenAsync(posts, slug, Localizer.DefaultCulture, 0, ct)) return Answer("skipped-exists", ctx.T("blog.exists", slug));

        await BlogStore.SaveAsync(posts, new BlogPost
        {
            Title = title, Slug = slug,
            Excerpt = SafeHtml.Sanitize(payload["excerpt"]?.GetValue<string>()),
            ContentHtml = SafeHtml.Sanitize(payload["contentHtml"]?.GetValue<string>()),
            Tags = (payload["tags"]?.GetValue<string>() ?? "").Trim(),
            Locale = Localizer.DefaultCulture, IsPublished = publish, PublishedAt = DateTime.UtcNow
        }, ct);
        return Answer("applied", ctx.T("blog.created", slug, ctx.T(publish ? "blog.createdPublished" : "blog.createdDraft")));
    }
}
