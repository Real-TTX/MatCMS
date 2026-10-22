using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Services;

namespace MatCMS.Plugin.Blog;

/// <summary>
/// The blog's operations for the cloud, MCP and the site's AI assistant: list, read, write, change and delete posts. Every write answers with
/// an <c>undo</c> — the operation that puts things back (the assistant journals it, its "Rückgängig" runs it) — and every body an AI sends goes
/// through <see cref="SafeHtml"/>, because posts are shown raw on the public site.
/// </summary>
internal static class BlogOps
{
    public static readonly PluginOperationSpec List = new("post.list",
        "Blogbeiträge auflisten (neueste zuerst): id, Titel, Adresse, veröffentlicht, Datum, Schlagwörter, Anriss.",
        """{"search"?: "text", "tag"?: "…", "limit"?: 30}""", Writes: false);
    public static readonly PluginOperationSpec Read = new("post.read",
        "Einen Blogbeitrag vollständig lesen, mit Inhalt (HTML).", """{"id": 12} oder {"slug": "…"}""", Writes: false);
    public static readonly PluginOperationSpec Create = new("post.create",
        "Neuen Blogbeitrag schreiben. contentHtml: Absätze, Überschriften (h2/h3), Listen, Links, Bilder aus der Mediathek. titleImage: url aus der Mediathek.",
        """{"title": "…", "slug"?: "…", "excerpt"?: "kurzer Anriss", "contentHtml": "<p>…</p>", "tags"?: "A, B", "titleImage"?: "/uploads/…", "publish"?: true}""");
    public static readonly PluginOperationSpec Update = new("post.update",
        "Einen Blogbeitrag ändern; nicht genannte Felder bleiben.",
        """{"id": 12, "title"?, "slug"?, "excerpt"?, "contentHtml"?, "tags"?, "titleImage"?, "publish"?: true|false}""");
    public static readonly PluginOperationSpec Delete = new("post.delete", "Einen Blogbeitrag löschen.", """{"id": 12}""");
    public static readonly PluginOperationSpec Restore = new("post.restore", "Stellt einen Beitrag aus einer Rückgängig-Aufzeichnung wieder her.", Internal: true);

    private static JsonObject Answer(string status, string message, JsonNode? data = null, JsonNode? undo = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        if (undo is not null) o["undo"] = undo;
        return o;
    }

    private static JsonObject Undo(string name, JsonObject args) => new() { ["name"] = name, ["args"] = args };
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : null;
    private static bool? Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>A title picture is a library picture or an https address — never a script or data address.</summary>
    private static string? Image(string? url)
    {
        url = (url ?? "").Trim();
        return url.Length == 0 ? null : url.StartsWith("/uploads/", StringComparison.Ordinal) && !url.Contains("..") || url.StartsWith("https://", StringComparison.Ordinal) ? url : null;
    }

    private static JsonObject Short(BlogPost p) => new()
    {
        ["id"] = p.Id, ["title"] = p.Title, ["slug"] = p.Slug, ["url"] = "/blog/" + p.Slug, ["published"] = p.IsPublished,
        ["date"] = p.PublishedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["tags"] = p.Tags,
        ["excerpt"] = p.Excerpt.Length > 160 ? p.Excerpt[..160] + "…" : p.Excerpt,
    };

    /// <summary>Everything a post is — also what <c>post.restore</c> takes back.</summary>
    private static JsonObject Full(BlogPost p) => new()
    {
        ["id"] = p.Id, ["title"] = p.Title, ["slug"] = p.Slug, ["url"] = "/blog/" + p.Slug, ["published"] = p.IsPublished,
        ["publishedAt"] = p.PublishedAt.ToString("o", CultureInfo.InvariantCulture), ["tags"] = p.Tags, ["excerpt"] = p.Excerpt,
        ["contentHtml"] = p.ContentHtml, ["titleImage"] = p.TitleImage, ["locale"] = p.Locale,
    };

    private static async Task<BlogPost?> FindAsync(IDataTable posts, JsonObject o, CancellationToken ct)
    {
        if (o["id"] is JsonValue v && (v.TryGetValue<long>(out var id) || long.TryParse(v.ToString(), out id))) return await BlogStore.GetAsync(posts, id, ct);
        return Str(o, "slug") is { Length: > 0 } slug ? await BlogStore.BySlugAsync(posts, slug.Trim(), Localizer.DefaultCulture, ct) : null;
    }

    public static async Task<JsonNode?> ListAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? new JsonObject();
        var search = (Str(o, "search") ?? "").Trim();
        var tag = (Str(o, "tag") ?? "").Trim();
        var limit = o["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? Math.Clamp(l, 1, 200) : 30;
        var all = await BlogStore.AllAsync(ctx.Data.Table("posts"), ct);
        var hits = all.Where(p => search.Length == 0 || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Excerpt.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(p => tag.Length == 0 || MatCMS.Content.TagUtil.Split(p.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
        return Answer("applied", $"{hits.Count} Beiträge", new JsonArray(hits.Take(limit).Select(p => (JsonNode)Short(p)).ToArray()));
    }

    public static async Task<JsonNode?> ReadAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var p = await FindAsync(ctx.Data.Table("posts"), args as JsonObject ?? new JsonObject(), ct);
        return p is null ? Answer("failed", "Beitrag nicht gefunden.") : Answer("applied", p.Title, Full(p));
    }

    /// <summary>Add-only by address: a post whose address exists is skipped, never overwritten.</summary>
    public static async Task<JsonNode?> CreateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var title = (Str(o, "title") ?? "").Trim();
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(Str(o, "slug") ?? title);
        var publish = Flag(o, "publish") ?? Flag(o, "published") ?? true;
        if (string.IsNullOrWhiteSpace(title)) return Answer("failed", ctx.T("blog.noTitle"));
        if (string.IsNullOrWhiteSpace(slug)) return Answer("failed", ctx.T("blog.noSlug"));
        var posts = ctx.Data.Table("posts");
        if (await BlogStore.SlugTakenAsync(posts, slug, Localizer.DefaultCulture, 0, ct)) return Answer("skipped-exists", ctx.T("blog.exists", slug));
        var id = await BlogStore.SaveAsync(posts, new BlogPost
        {
            Title = title, Slug = slug,
            Excerpt = SafeHtml.Sanitize(Str(o, "excerpt")),
            ContentHtml = SafeHtml.Sanitize(Str(o, "contentHtml")),
            Tags = (Str(o, "tags") ?? "").Trim(),
            TitleImage = Image(Str(o, "titleImage")),
            Locale = Localizer.DefaultCulture, IsPublished = publish, PublishedAt = DateTime.UtcNow
        }, ct);
        return Answer("applied", ctx.T("blog.created", slug, ctx.T(publish ? "blog.createdPublished" : "blog.createdDraft")),
            new JsonObject { ["id"] = id, ["url"] = "/blog/" + slug }, Undo("post.delete", new JsonObject { ["id"] = id }));
    }

    public static async Task<JsonNode?> UpdateAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var posts = ctx.Data.Table("posts");
        if (await FindAsync(posts, o, ct) is not { } p) return Answer("failed", "Beitrag nicht gefunden.");
        var before = Full(p);
        var slug = p.Slug;
        if (Str(o, "slug") is { } s)
        {
            slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(s);
            if (slug.Length == 0) return Answer("failed", ctx.T("blog.noSlug"));
            if (await BlogStore.SlugTakenAsync(posts, slug, p.Locale, p.Id, ct)) return Answer("failed", ctx.T("blog.exists", slug));
        }
        var title = Str(o, "title")?.Trim();
        if (title is { Length: 0 }) return Answer("failed", ctx.T("blog.noTitle"));
        var image = p.TitleImage;
        if (o.ContainsKey("titleImage")) image = Image(Str(o, "titleImage"));
        var published = Flag(o, "publish") ?? Flag(o, "published") ?? p.IsPublished;
        await BlogStore.SaveAsync(posts, p with
        {
            Title = title ?? p.Title, Slug = slug,
            Excerpt = Str(o, "excerpt") is { } ex ? SafeHtml.Sanitize(ex) : p.Excerpt,
            ContentHtml = Str(o, "contentHtml") is { } ch ? SafeHtml.Sanitize(ch) : p.ContentHtml,
            Tags = Str(o, "tags")?.Trim() ?? p.Tags,
            TitleImage = image, IsPublished = published,
            PublishedAt = published && !p.IsPublished ? DateTime.UtcNow : p.PublishedAt, UpdatedAt = DateTime.UtcNow,
        }, ct);
        return Answer("applied", $"Beitrag „{title ?? p.Title}“ geändert.", new JsonObject { ["id"] = p.Id, ["url"] = "/blog/" + slug },
            Undo("post.restore", before));
    }

    public static async Task<JsonNode?> DeleteAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var posts = ctx.Data.Table("posts");
        if (await FindAsync(posts, args as JsonObject ?? new JsonObject(), ct) is not { } p) return Answer("failed", "Beitrag nicht gefunden.");
        await posts.DeleteAsync(p.Id, ct);
        return Answer("applied", $"Beitrag „{p.Title}“ gelöscht.", null, Undo("post.restore", Full(p)));
    }

    /// <summary>Puts a post back as it was (undo of a change or a delete). INTERNAL: it writes what the journal recorded, unsanitised — an
    /// admin's own post may carry markup the AI filter would strip, and an undo must not lose it.</summary>
    public static async Task<JsonNode?> RestoreAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var posts = ctx.Data.Table("posts");
        var id = o["id"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : 0;
        var existing = id > 0 ? await BlogStore.GetAsync(posts, id, ct) : null;
        var publishedAt = DateTime.TryParse(Str(o, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var pa) ? pa : DateTime.UtcNow;
        var post = new BlogPost
        {
            Id = existing?.Id ?? 0, Title = Str(o, "title") ?? "", Slug = Str(o, "slug") ?? "", Excerpt = Str(o, "excerpt") ?? "",
            ContentHtml = Str(o, "contentHtml") ?? "", Tags = Str(o, "tags") ?? "", TitleImage = Str(o, "titleImage"),
            Locale = Str(o, "locale") ?? Localizer.DefaultCulture, IsPublished = Flag(o, "published") ?? false, PublishedAt = publishedAt,
            AttachmentsJson = existing?.AttachmentsJson ?? "[]", GalleryJson = existing?.GalleryJson ?? "[]", GalleryLayout = existing?.GalleryLayout ?? "carousel",
            SourceUrl = existing?.SourceUrl, SourceName = existing?.SourceName, CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
        };
        await BlogStore.SaveAsync(posts, post, ct);
        return Answer("applied", $"Beitrag „{post.Title}“ wiederhergestellt.");
    }
}
