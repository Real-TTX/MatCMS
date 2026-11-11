using MatCMS.Data;
using MatCMS.Plugin;
using MatCMS.Services.Plugins;

namespace MatCMS.Services;

/// <summary>
/// The CMS's own <c>Posts</c> table, as the CORE knows it. The blog left the image (it is a native plugin from the MatCMS Store now,
/// <c>plugins/blog</c>) but its posts did not: the table stays a core table (<see cref="Data.DbSeeder"/> creates it) and the backup carries
/// it whether or not the blog is installed — a site that never installs the plugin again must not lose its posts with the next restore.
/// <para>The blog plugin <i>adopts</i> this very table with the same declaration (<c>plugins/blog/BlogStore.cs</c>); both reconcile under
/// the owner key <c>blog</c>, so the bookkeeping in <c>__plugin_tables</c> sees one owner. Reconciling only ever ADDS: a column one side
/// declares and the other does not is simply there for both. Keep the two column lists the same.</para>
/// </summary>
public static class PostsTable
{
    /// <summary>The key the table is booked under — the blog's, which adopts it.</summary>
    public const string OwnerKey = "blog";

    /// <summary>The columns of <c>Posts</c>, as <c>(name, type, required)</c>. <c>TranslationGroup</c> came with the multilingual blog
    /// (2026-11): posts that are translations of each other share it; empty = a post on its own.</summary>
    public static readonly (string Name, ColumnType Type, bool Required)[] Columns =
    {
        ("Title", ColumnType.Text, true), ("Slug", ColumnType.Text, true), ("TitleImage", ColumnType.Text, false), ("Excerpt", ColumnType.Text, true),
        ("ContentHtml", ColumnType.Text, true), ("Tags", ColumnType.Text, true), ("AttachmentsJson", ColumnType.Text, true), ("GalleryJson", ColumnType.Text, true),
        ("GalleryLayout", ColumnType.Text, true), ("SourceUrl", ColumnType.Text, false), ("SourceName", ColumnType.Text, false), ("Locale", ColumnType.Text, true),
        ("IsPublished", ColumnType.Bool, true), ("PublishedAt", ColumnType.DateTime, true), ("CreatedAt", ColumnType.DateTime, true), ("UpdatedAt", ColumnType.DateTime, true),
        ("TranslationGroup", ColumnType.Text, false)
    };

    /// <summary>The declaration of the table, identical to what the blog plugin registers.</summary>
    public static TableSpec Spec { get; } = new("posts",
        Columns.Select(c => new ColumnSpec(c.Name, c.Type) { Required = c.Required }).ToList(),
        new[] { new IndexSpec(new[] { "Slug", "Locale" }, Unique: true) })
    { Adopts = "Posts", KeyColumn = "Id" };

    /// <summary>Makes sure the table and its columns exist (a database that never ran the step that creates it, or a test database built
    /// from the model alone, has none) and returns it.</summary>
    public static async Task<IDataTable> EnsureAsync(PluginDataService data, AppDbContext db, CancellationToken ct = default)
    {
        await data.ReconcileAsync(db, OwnerKey, new[] { Spec }, ct);
        return Table(db);
    }

    /// <summary>The posts table over a database context — with or without the blog plugin.</summary>
    public static IDataTable Table(AppDbContext db) => new PluginDataAccess(db, OwnerKey, new[] { Spec }).Table("posts");

    /// <summary>The valid gallery layouts (the plugin has the same rule): anything else is read as "carousel".</summary>
    public static string NormaliseGalleryLayout(string? layout) => layout is "grid" or "single" ? layout : "carousel";
}
