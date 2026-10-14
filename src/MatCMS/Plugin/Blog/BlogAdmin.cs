using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Plugin;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MatCMS.Plugin.Blog;

/// <summary>The blog's back office: the list of posts, the editor and the two helpers the editor calls (a post read from a link, an AI
/// teaser). Every screen is a tree of library components — nothing here writes a class name.</summary>
internal static class BlogAdmin
{
    public const string Section = "/admin/p/blog";
    private static string EditUrl(long id) => $"{Section}/posts/{id}";

    // ---- list ------------------------------------------------------------------------------------

    public static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var posts = ctx.Data.Table("posts");
        if (ctx.Request.IsPost && ctx.Request.Action == "delete" && long.TryParse(ctx.Request.F("id"), out var del))
        {
            await posts.DeleteAsync(del, ct);
            return PluginResult.Redirect(Section, ctx.T("blog.deleted"));
        }

        var now = DateTime.UtcNow;
        var rows = await BlogStore.AllAsync(posts, ct);
        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("pages.colTitle")), Ui.Column(ctx.T("pages.colUrl")), Ui.Column(ctx.T("posts.colDate")), Ui.Column(ctx.T("media.tags")),
                    Ui.Column(ctx.T("pages.colStatus")), Ui.Column("", actions: true) },
            rows.Select(p => Ui.RowFor(p.Title + " " + p.Slug + " " + p.Tags,
                Ui.Text(p.Title) with { Inline = true, Strong = true },
                Ui.Link("/blog/" + p.Slug, "/blog/" + p.Slug, newTab: true),
                p.PublishedAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                Ui.Chips(TagUtil.Split(p.Tags)),
                !p.IsPublished ? Ui.Badge(ctx.T("posts.draft"), UiTone.Muted)
                    : p.PublishedAt > now ? Ui.Badge(ctx.T("posts.scheduled"), UiTone.Info)
                    : Ui.Badge(ctx.T("posts.published"), UiTone.Success),
                Ui.Inline(
                    Ui.Button(ctx.T("action.edit"), EditUrl(p.Id), UiButtonKind.Ghost, small: true),
                    Ui.Button(ctx.T("posts.view"), "/blog/" + p.Slug, UiButtonKind.Ghost, icon: "ti-external-link", small: true) with { IconOnly = true, NewTab = true },
                    Ui.Action(ctx.T("action.delete"), "delete", p.Id.ToString(CultureInfo.InvariantCulture), UiButtonKind.Danger, confirm: ctx.T("posts.confirmDelete", p.Title), icon: "ti-trash")))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 25, emptyText: ctx.T("posts.none"));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("posts.title"), Ui.Button(ctx.T("posts.new"), EditUrl(0), small: true)),
            table), ctx.T("posts.title"));
    }

    // ---- editor ----------------------------------------------------------------------------------

    public static async Task<PluginResult> EditAsync(IPluginContext ctx, CancellationToken ct)
    {
        var id = long.TryParse(ctx.Request.R("id"), out var parsed) ? parsed : 0;
        var posts = ctx.Data.Table("posts");
        BlogPost post;
        if (id == 0) post = new BlogPost { PublishedAt = DateTime.UtcNow };
        else
        {
            var found = await BlogStore.GetAsync(posts, id, ct);
            if (found is null) return PluginResult.Redirect(Section);
            post = found;
        }

        string? error = null;
        if (ctx.Request.IsPost && ctx.Request.Action == "save")
        {
            var form = ctx.Request;
            if (string.IsNullOrWhiteSpace(form.F("title")))
            {
                error = ctx.T("blog.titleRequired");
                post = Read(form, post, form.F("slug").Trim());         // keep what was typed
            }
            else
            {
                var saved = await SaveAsync(ctx, posts, form, post, ct);
                return PluginResult.Redirect(EditUrl(saved), ctx.T("blog.saved"));
            }
        }
        return await EditorPageAsync(ctx, posts, post, error, ct);
    }

    /// <summary>The post as the form describes it (not saved).</summary>
    private static BlogPost Read(RequestData f, BlogPost current, string slug) => current with
    {
        Title = f.F("title").Trim(), Slug = slug,
        TitleImage = string.IsNullOrWhiteSpace(f.F("titleimage")) ? null : f.F("titleimage").Trim(),
        Excerpt = f.F("excerpt").Trim(), ContentHtml = f.F("content"), Tags = TagUtil.Normalize(f.F("tags")),
        AttachmentsJson = string.IsNullOrWhiteSpace(f.F("attachments")) ? "[]" : f.F("attachments"),
        GalleryJson = CleanGallery(f.F("gallery")), GalleryLayout = BlogStore.NormaliseGalleryLayout(f.F("gallerylayout")),
        // Only an http(s) link is a source; anything else (javascript:, a typo) is dropped, not stored.
        SourceUrl = PostImportService.NormalizeUrl(f.F("sourceurl")),
        IsPublished = f.Form.ContainsKey("published"), Locale = "de",
        PublishedAt = DateTime.TryParse(f.F("date"), out var d) ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow
    };

    private static async Task<long> SaveAsync(IPluginContext ctx, IDataTable posts, RequestData form, BlogPost post, CancellationToken ct)
    {
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(string.IsNullOrWhiteSpace(form.F("slug")) ? form.F("title") : form.F("slug"));
        if (string.IsNullOrWhiteSpace(slug)) slug = "beitrag";
        var baseSlug = slug; var n = 2;
        while (await BlogStore.SlugTakenAsync(posts, slug, "de", post.Id, ct)) slug = baseSlug + "-" + n++;

        var p = Read(form, post, slug);
        p = p with
        {
            SourceName = p.SourceUrl is null || string.IsNullOrWhiteSpace(form.F("sourcename")) ? null : form.F("sourcename").Trim(),
            UpdatedAt = DateTime.UtcNow, CreatedAt = post.Id == 0 ? DateTime.UtcNow : post.CreatedAt
        };
        var id = await BlogStore.SaveAsync(posts, p, ct);
        await ctx.Events.EmitAsync("blog.post.saved", new JsonObject { ["id"] = id, ["slug"] = p.Slug, ["published"] = p.IsPublished }, ct);
        return id;
    }

    private static async Task<PluginResult> EditorPageAsync(IPluginContext ctx, IDataTable posts, BlogPost p, string? error, CancellationToken ct)
    {
        var isNew = p.Id == 0;
        var ai = ctx.Unsafe.Services.GetRequiredService<AiService>();
        var tags = await BlogStore.AllTagsAsync(posts, ct);
        var title = ctx.T(isNew ? "posts.newTitle" : "posts.editTitle");

        var fields = new List<UiNode?>();
        if (error is not null) fields.Add(Ui.Alert(error, UiTone.Error));
        fields.Add(Ui.Input("title", ctx.T("posts.heading"), p.Title) with { Id = "post-title", Required = true, Placeholder = ctx.T("posts.headingPlaceholder") });
        fields.Add(Ui.Input("slug", ctx.T("posts.slug"), p.Slug) with { Id = "post-slug", Placeholder = ctx.T("posts.slugExample"), Help = ctx.T("posts.slugHint") });
        fields.Add(Ui.Image("titleimage", ctx.T("posts.titleImage"), p.TitleImage ?? "") with { Id = "post-titleimage" });
        fields.Add(Ui.MediaList("gallery", ctx.T("posts.gallery"), p.GalleryJson) with { Id = "post-gallery", Help = ctx.T("posts.galleryHint") });
        fields.Add(Ui.Select("gallerylayout", ctx.T("posts.galleryLayout"), p.GalleryLayout,
            Ui.Option("carousel", ctx.T("posts.galleryCarousel")), Ui.Option("single", ctx.T("posts.gallerySingle")), Ui.Option("grid", ctx.T("posts.galleryGrid"))));
        fields.Add(Ui.TextArea("excerpt", ctx.T("posts.excerpt"), p.Excerpt, 2) with { Id = "post-excerpt", Placeholder = ctx.T("posts.excerptPlaceholder"), Help = ctx.T("posts.excerptHint") });
        if (ai.Enabled)
            fields.Add(Ui.Button(ctx.T("editor.aiExcerptButton"), "#", UiButtonKind.Ghost, icon: "ti-sparkles", small: true) with
            {
                Href = null, NoSubmit = true,
                Data = new Dictionary<string, string>
                {
                    ["ai-seo"] = "", ["ai-url"] = $"{Section}/ai-excerpt?id={p.Id}", ["target"] = "post-excerpt",
                    ["content-from"] = "post-content-editor", ["title-from"] = "post-title", ["title"] = ctx.T("editor.aiExcerptTitle")
                }
            });
        fields.Add(Ui.RichText("content", ctx.T("posts.content"), p.ContentHtml) with { Id = "post-content" });
        fields.Add(Ui.Tags("tags", ctx.T("media.tags"), p.Tags, tags) with { Id = "post-tags", Help = ctx.T("posts.tagsHint") });
        fields.Add(Ui.MediaList("attachments", ctx.T("posts.attachments"), p.AttachmentsJson, files: true) with { Id = "post-attachments", Help = ctx.T("posts.attachmentsHint") });
        fields.Add(Ui.Checkbox("published", ctx.T("posts.publishedLabel"), p.IsPublished));
        fields.Add(Ui.Input("date", ctx.T("posts.date"), p.PublishedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), UiInputType.Date) with { Id = "post-date" });
        fields.Add(Ui.Inline(
            Ui.Input("sourceurl", ctx.T("posts.source"), p.SourceUrl ?? "") with { Id = "post-sourceurl", Placeholder = "https://www.instagram.com/p/…", Help = ctx.T("posts.sourceHint") },
            Ui.Input("sourcename", ctx.T("posts.sourceNamePlaceholder"), p.SourceName ?? "") with { Id = "post-sourcename" }));
        fields.Add(Ui.FormActions(
            Ui.Submit(ctx.T("action.save"), name: "action", value: "save"),
            isNew ? null : Ui.Button(ctx.T("posts.view"), "/blog/" + p.Slug, UiButtonKind.Ghost, icon: "ti-external-link") with { NewTab = true },
            Ui.Button(ctx.T("action.back"), Section, UiButtonKind.Ghost)));

        var importDialog = Ui.Dialog("pi-dialog", ctx.T("posts.import.heading"),
            Ui.Html($"<div id=\"pi-config\" data-url=\"{WebUtility.HtmlEncode($"{Section}/import-link")}\" data-busy=\"{WebUtility.HtmlEncode(ctx.T("posts.import.busy"))}\" " +
                    $"data-done=\"{WebUtility.HtmlEncode(ctx.T("posts.import.done"))}\" data-overwrite=\"{WebUtility.HtmlEncode(ctx.T("posts.import.overwrite"))}\" " +
                    $"data-failed=\"{WebUtility.HtmlEncode(ctx.T("posts.import.failed"))}\"></div>"),
            Ui.Inline(Ui.Input("pi-url", "", "", UiInputType.Url) with { Id = "pi-url", Placeholder = ctx.T("posts.import.placeholder") },
                      Ui.Button(ctx.T("posts.import.button"), "#", small: true) with { Href = null, NoSubmit = true, Data = new Dictionary<string, string> { ["pi-go"] = "" } }),
            Ui.Muted(ctx.T("posts.import.hint")),
            Ui.Html("<div id=\"pi-status\" class=\"post-import__status\" role=\"status\" hidden></div>"));

        var scripts = new List<UiNode?> { Ui.Html("<script src=\"/js/blog-editor.js\"></script>") };
        if (ai.Enabled)
        {
            var renderer = ctx.Unsafe.Services.GetRequiredService<RazorPartialRenderer>();
            scripts.Add(Ui.Html(await renderer.RenderAsync("_AiSeoDialog", new object(), ct)));
        }

        var tree = Ui.Stack(
            Ui.PageHead(null, title, Ui.Button(ctx.T("posts.import.heading"), "#", UiButtonKind.Ghost, icon: "ti-link", small: true) with { Href = null, Opens = "pi-dialog" }),
            importDialog,
            new UiForm(fields.Where(f => f is not null).Select(f => f!).ToList()) { Id = "post-form" },
            Ui.Stack(scripts.ToArray()));
        return PluginResult.Page(tree, title);
    }

    // ---- helpers the editor calls ----------------------------------------------------------------

    /// <summary>"Aus Link übernehmen": reads the post behind a link once and hands the editor its fields. Nothing is saved here — the
    /// images are already in the media library, everything else arrives in the form and is saved (or changed, or dropped) with the normal save.</summary>
    public static async Task<PluginResult> ImportLinkAsync(IPluginContext ctx, CancellationToken ct)
    {
        var import = ctx.Unsafe.Services.GetRequiredService<PostImportService>();
        var r = await import.ImportAsync(ctx.Request.F("url"), ct);
        return PluginResult.Json(new JsonObject
        {
            ["ok"] = r.Ok, ["error"] = r.Error, ["title"] = r.Title, ["excerpt"] = r.Excerpt, ["html"] = r.Html,
            ["date"] = r.PublishedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["sourceUrl"] = r.SourceUrl, ["sourceName"] = r.SourceName,
            ["images"] = new JsonArray(r.Images.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()),
            ["notes"] = new JsonArray(r.Notes.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()), ["via"] = r.Via
        });
    }

    /// <summary>Proposes a teaser for the post, summarised from its content (relayed through the cloud). The current editor content is posted so
    /// unsaved edits count; falls back to the saved post. Nothing is saved — the client shows a before/after and writes the accepted teaser into
    /// the excerpt field, saved with the normal post save.</summary>
    public static async Task<PluginResult> AiExcerptAsync(IPluginContext ctx, CancellationToken ct)
    {
        var ai = ctx.Unsafe.Services.GetRequiredService<AiService>();
        if (!ai.Enabled) return Fail(ctx.T("blog.aiOff"));
        var html = ctx.Request.F("content");
        var heading = ctx.Request.F("title").Trim();
        if (string.IsNullOrWhiteSpace(html) && long.TryParse(ctx.Request.Q("id"), out var id) && id != 0
            && await BlogStore.GetAsync(ctx.Data.Table("posts"), id, ct) is { } saved)
        {
            html = saved.ContentHtml;
            if (heading.Length == 0) heading = saved.Title;
        }
        var text = Regex.Replace(html ?? "", "<.*?>", " ").Replace("\n", " ").Replace("&nbsp;", " ").Replace("  ", " ").Trim();
        if (text.Length < 10) return Fail(ctx.T("blog.aiTooShort"));

        var (ok, resp, error) = await ai.SummarizeForSeoAsync("excerpt", heading, text, ctx.Request.F("instruction"), ct);
        if (!ok) return Fail(error ?? ctx.T("blog.aiFailed"));
        var proposed = (resp ?? "").Trim();
        return proposed.Length == 0
            ? Fail(ctx.T("blog.aiNone"))
            : PluginResult.Json(new JsonObject { ["ok"] = true, ["proposed"] = proposed });

        static PluginResult Fail(string message) => PluginResult.Json(new JsonObject { ["ok"] = false, ["error"] = message });
    }

    /// <summary>Keeps only well-formed gallery entries pointing at a local or http(s) image.</summary>
    internal static string CleanGallery(string? json)
    {
        var list = new JsonArray();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var u = el.TryGetProperty("url", out var uv) ? uv.GetString() ?? "" : "";
                if (!(u.StartsWith('/') || u.StartsWith("https://") || u.StartsWith("http://"))
                    || u.IndexOfAny(new[] { '"', '\'', '<', '>' }) >= 0) continue;
                var alt = el.TryGetProperty("alt", out var av) ? av.GetString() ?? "" : "";
                list.Add(new JsonObject { ["url"] = u, ["alt"] = alt });
            }
        }
        catch (JsonException) { /* malformed → empty gallery */ }
        return list.ToJsonString();
    }
}
