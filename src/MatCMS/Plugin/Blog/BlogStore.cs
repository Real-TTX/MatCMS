using MatCMS.Data;
using MatCMS.Services.Plugins;
using MatCMS.Plugin;
using MatCMS.Services.Plugins;

namespace MatCMS.Plugin.Blog;

/// <summary>A blog post as the plugin works with it: a typed view of one row of the <c>Posts</c> table.</summary>
public sealed record BlogPost
{
    /// <summary>The row's key; 0 for a post that is not saved yet.</summary>
    public long Id { get; init; }
    /// <summary>The heading.</summary>
    public string Title { get; init; } = "";
    /// <summary>The URL-safe name: the post is served at <c>/blog/{slug}</c>.</summary>
    public string Slug { get; init; } = "";
    /// <summary>The title image, or null.</summary>
    public string? TitleImage { get; init; }
    /// <summary>The teaser shown on cards.</summary>
    public string Excerpt { get; init; } = "";
    /// <summary>The body as HTML.</summary>
    public string ContentHtml { get; init; } = "";
    /// <summary>Comma-separated tags.</summary>
    public string Tags { get; init; } = "";
    /// <summary>The download list as a JSON array of <c>{url,name}</c>.</summary>
    public string AttachmentsJson { get; init; } = "[]";
    /// <summary>The gallery as a JSON array of <c>{url,alt}</c>.</summary>
    public string GalleryJson { get; init; } = "[]";
    /// <summary>How the gallery is shown: <c>carousel</c>, <c>single</c> or <c>grid</c>.</summary>
    public string GalleryLayout { get; init; } = "carousel";
    /// <summary>Where the post was taken from, or null.</summary>
    public string? SourceUrl { get; init; }
    /// <summary>The label for <see cref="SourceUrl"/>, or null.</summary>
    public string? SourceName { get; init; }
    /// <summary>The content language.</summary>
    public string Locale { get; init; } = "de";
    /// <summary>Published (a draft is not).</summary>
    public bool IsPublished { get; init; }
    /// <summary>The publication date (UTC); a future date schedules the post.</summary>
    public DateTime PublishedAt { get; init; } = DateTime.UtcNow;
    /// <summary>When it was created (UTC).</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>When it was last saved (UTC).</summary>
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Everything that reads and writes the blog's table. The blog <i>adopts</i> the CMS's own <c>Posts</c> table — no data was moved
/// when the blog became a plugin — and this class is the only place that knows its columns.
/// <para>It can also be used when the plugin is switched off (<see cref="Table"/>): the backup, which must carry the posts
/// whether or not the blog is on, goes through here.</para>
/// </summary>
public static class BlogStore
{
    /// <summary>The columns of <c>Posts</c>, as <c>(name, type, required)</c>. One list: the plugin declares its table from it and the host
    /// code that works without the plugin builds the same <see cref="TableSpec"/>.</summary>
    internal static readonly (string Name, ColumnType Type, bool Required)[] Columns =
    {
        ("Title", ColumnType.Text, true), ("Slug", ColumnType.Text, true), ("TitleImage", ColumnType.Text, false), ("Excerpt", ColumnType.Text, true),
        ("ContentHtml", ColumnType.Text, true), ("Tags", ColumnType.Text, true), ("AttachmentsJson", ColumnType.Text, true), ("GalleryJson", ColumnType.Text, true),
        ("GalleryLayout", ColumnType.Text, true), ("SourceUrl", ColumnType.Text, false), ("SourceName", ColumnType.Text, false), ("Locale", ColumnType.Text, true),
        ("IsPublished", ColumnType.Bool, true), ("PublishedAt", ColumnType.DateTime, true), ("CreatedAt", ColumnType.DateTime, true), ("UpdatedAt", ColumnType.DateTime, true)
    };

    /// <summary>The declaration of the table, identical to what the plugin registers.</summary>
    public static TableSpec Spec { get; } = new("posts",
        Columns.Select(c => new ColumnSpec(c.Name, c.Type) { Required = c.Required }).ToList(),
        new[] { new IndexSpec(new[] { "Slug", "Locale" }, Unique: true) })
    { Adopts = "Posts", KeyColumn = "Id" };

    /// <summary>Makes sure the table exists (a database that never ran the migration that creates it, or a test database built from the model
    /// alone, has none) and returns it. Backup and restore call this: they work whether or not the blog plugin is loaded.</summary>
    public static async Task<IDataTable> EnsureTableAsync(PluginDataService data, AppDbContext db, CancellationToken ct = default)
    {
        await data.ReconcileAsync(db, "blog", new[] { Spec }, ct);
        return Table(db);
    }

    /// <summary>The posts table over a database context — usable whether or not the blog plugin is loaded.</summary>
    public static IDataTable Table(AppDbContext db) => new PluginDataAccess(db, "blog", new[] { Spec }).Table("posts");

    /// <summary>The one place that knows the valid gallery layouts — editor, page and restore all go through it, so a layout added
    /// here cannot be silently turned back into "carousel" by one of them.</summary>
    public static string NormaliseGalleryLayout(string? layout) => layout is "grid" or "single" ? layout : "carousel";

    /// <summary>A row as a post.</summary>
    public static BlogPost From(DataRow r) => new()
    {
        Id = r.Id, Title = r.Text("Title"), Slug = r.Text("Slug"), TitleImage = r.IsNull("TitleImage") ? null : r.Text("TitleImage"),
        Excerpt = r.Text("Excerpt"), ContentHtml = r.Text("ContentHtml"), Tags = r.Text("Tags"),
        AttachmentsJson = r.Text("AttachmentsJson") is { Length: > 0 } a ? a : "[]",
        GalleryJson = r.Text("GalleryJson") is { Length: > 0 } g ? g : "[]",
        GalleryLayout = NormaliseGalleryLayout(r.Text("GalleryLayout")),
        SourceUrl = r.IsNull("SourceUrl") ? null : r.Text("SourceUrl"), SourceName = r.IsNull("SourceName") ? null : r.Text("SourceName"),
        Locale = r.Text("Locale") is { Length: > 0 } l ? l : "de", IsPublished = r.Bool("IsPublished"),
        PublishedAt = r.Timestamp("PublishedAt") ?? DateTime.UtcNow, CreatedAt = r.Timestamp("CreatedAt") ?? DateTime.UtcNow, UpdatedAt = r.Timestamp("UpdatedAt") ?? DateTime.UtcNow
    };

    /// <summary>A post as the column values of a write (the key is not among them).</summary>
    public static Dictionary<string, object?> ToValues(BlogPost p) => new()
    {
        ["Title"] = p.Title, ["Slug"] = p.Slug, ["TitleImage"] = p.TitleImage, ["Excerpt"] = p.Excerpt, ["ContentHtml"] = p.ContentHtml,
        ["Tags"] = p.Tags, ["AttachmentsJson"] = p.AttachmentsJson, ["GalleryJson"] = p.GalleryJson, ["GalleryLayout"] = p.GalleryLayout,
        ["SourceUrl"] = p.SourceUrl, ["SourceName"] = p.SourceName, ["Locale"] = p.Locale, ["IsPublished"] = p.IsPublished,
        ["PublishedAt"] = p.PublishedAt, ["CreatedAt"] = p.CreatedAt, ["UpdatedAt"] = p.UpdatedAt
    };

    /// <summary>The post with this key, or null.</summary>
    public static async Task<BlogPost?> GetAsync(IDataTable posts, long id, CancellationToken ct = default) =>
        await posts.GetAsync(id, ct) is { } r ? From(r) : null;

    /// <summary>The post with this slug in this language, or null.</summary>
    public static async Task<BlogPost?> BySlugAsync(IDataTable posts, string slug, string locale, CancellationToken ct = default) =>
        await posts.FindAsync(DataQuery.Everything.Eq("Slug", slug).Eq("Locale", locale), ct) is { } r ? From(r) : null;

    /// <summary>True when another post of the language already uses the slug.</summary>
    public static async Task<bool> SlugTakenAsync(IDataTable posts, string slug, string locale, long exceptId, CancellationToken ct = default)
    {
        var existing = await posts.FindAsync(DataQuery.Everything.Eq("Slug", slug).Eq("Locale", locale), ct);
        return existing is not null && existing.Id != exceptId;
    }

    /// <summary>Every post, newest first.</summary>
    public static async Task<IReadOnlyList<BlogPost>> AllAsync(IDataTable posts, CancellationToken ct = default) =>
        (await posts.QueryAsync(DataQuery.Everything.OrderBy("PublishedAt", descending: true).OrderBy("Id", descending: true), ct)).Select(From).ToList();

    /// <summary>Inserts or updates a post and returns its key.</summary>
    public static async Task<long> SaveAsync(IDataTable posts, BlogPost p, CancellationToken ct = default)
    {
        var values = ToValues(p);
        if (p.Id == 0) return await posts.InsertAsync(values, ct);
        values.Remove("CreatedAt");   // an edit keeps the day the post was written
        await posts.UpdateAsync(p.Id, values, ct);
        return p.Id;
    }

    /// <summary>The distinct tags across all posts, sorted — the picker's suggestions.</summary>
    public static async Task<IReadOnlyList<string>> AllTagsAsync(IDataTable posts, CancellationToken ct = default) =>
        (await posts.QueryAsync(DataQuery.Everything, ct)).Select(r => r.Text("Tags"))
            .SelectMany(MatCMS.Content.TagUtil.Split).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
}
