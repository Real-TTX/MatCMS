using System.Globalization;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>What a store entry is right now.</summary>
internal static class ItemStatus
{
    /// <summary>Submitted, no version approved yet — not public.</summary>
    public const string Draft = "draft";
    /// <summary>At least one approved version — public.</summary>
    public const string Published = "published";
    /// <summary>Taken out of the store by the operator — not public, its versions are not offered.</summary>
    public const string Withdrawn = "withdrawn";
}

/// <summary>What one version is right now. Versions are never changed after approval; a bad one is withdrawn, not deleted.</summary>
internal static class VersionStatus
{
    public const string Review = "review", Published = "published", Rejected = "rejected", Withdrawn = "withdrawn";
}

internal sealed record Publisher(long Id, long MemberId, string Name, string Slug, string Website, string About, bool Verified, bool Blocked, DateTime? Created);

internal sealed record Item(long Id, string Key, string Name, string Summary, string Description, long PublisherId, string Tags, string License, string Homepage,
    string Status, string LatestVersion, string LatestKind, long Downloads, DateTime? Created, DateTime? Updated);

internal sealed record PackageVersion(long Id, long ItemId, string Version, string Kind, string Api, string MinCms, long Size, string Sha256, string File,
    string Signature, string KeyId, string Notes, string Status, string Reason, long Downloads, DateTime? Created, DateTime? PublishedAt, string Files);

internal sealed record Picture(long Id, long ItemId, string File, string Type, string Caption, long Sort);

/// <summary>The plugin's tables and the queries on them. Everything goes through the plugin data API (parameters only, names from the declaration).</summary>
internal sealed class StoreDb
{
    private readonly IPluginData _data;
    public StoreDb(IPluginData data) => _data = data;

    public IDataTable Publishers => _data.Table("publishers");
    public IDataTable Items => _data.Table("items");
    public IDataTable Versions => _data.Table("versions");
    public IDataTable Pictures => _data.Table("pictures");

    public static void Declare(IDataBuilder d)
    {
        d.Table("publishers", t => t
            .Int("member_id", required: true).Text("name", required: true).Text("slug", required: true, unique: true)
            .Text("website", def: "").Text("about", def: "").Bool("verified", def: "0").Bool("blocked", def: "0").Timestamp("created")
            .UniqueIndex("member_id"));
        d.Table("items", t => t
            .Text("key", required: true, unique: true).Text("name", required: true).Text("summary", def: "").Text("description", def: "")
            .Int("publisher_id", required: true).Text("tags", def: "").Text("license", def: "").Text("homepage", def: "")
            .Text("status", def: ItemStatus.Draft).Text("latest_version", def: "").Text("latest_kind", def: "").Int("downloads", def: "0")
            .Timestamp("created").Timestamp("updated")
            .Index("status").Index("publisher_id"));
        d.Table("versions", t => t
            .Int("item_id", required: true).Text("version", required: true).Text("kind", def: "script").Text("api", def: "").Text("min_cms", def: "")
            .Int("size", def: "0").Text("sha256", def: "").Text("file", def: "").Text("signature", def: "").Text("key_id", def: "")
            .Text("notes", def: "").Text("status", def: VersionStatus.Review).Text("reason", def: "").Int("downloads", def: "0")
            .Timestamp("created").Timestamp("published_at").Text("files", def: "")
            .UniqueIndex("item_id", "version").Index("status"));
        d.Table("pictures", t => t
            .Int("item_id", required: true).Text("file", required: true).Text("type", def: "image/png").Text("caption", def: "").Int("sort", def: "0")
            .Index("item_id"));
    }

    // ---- reading rows -------------------------------------------------------------------------------------------------

    private static Publisher P(DataRow r) => new(r.Id, r.Int("member_id"), r.Text("name"), r.Text("slug"), r.Text("website"), r.Text("about"),
        r.Bool("verified"), r.Bool("blocked"), r.Timestamp("created"));

    private static Item I(DataRow r) => new(r.Id, r.Text("key"), r.Text("name"), r.Text("summary"), r.Text("description"), r.Int("publisher_id"),
        r.Text("tags"), r.Text("license"), r.Text("homepage"), r.Text("status"), r.Text("latest_version"), r.Text("latest_kind"), r.Int("downloads"),
        r.Timestamp("created"), r.Timestamp("updated"));

    private static PackageVersion V(DataRow r) => new(r.Id, r.Int("item_id"), r.Text("version"), r.Text("kind"), r.Text("api"), r.Text("min_cms"),
        r.Int("size"), r.Text("sha256"), r.Text("file"), r.Text("signature"), r.Text("key_id"), r.Text("notes"), r.Text("status"), r.Text("reason"),
        r.Int("downloads"), r.Timestamp("created"), r.Timestamp("published_at"), r.Text("files"));

    private static Picture Pic(DataRow r) => new(r.Id, r.Int("item_id"), r.Text("file"), r.Text("type"), r.Text("caption"), r.Int("sort"));

    // ---- publishers ---------------------------------------------------------------------------------------------------

    public async Task<Publisher?> PublisherOfMemberAsync(long memberId, CancellationToken ct) =>
        await Publishers.FindByAsync("member_id", memberId, ct) is { } r ? P(r) : null;

    public async Task<Publisher?> PublisherAsync(long id, CancellationToken ct) => await Publishers.GetAsync(id, ct) is { } r ? P(r) : null;

    public async Task<Publisher?> PublisherBySlugAsync(string slug, CancellationToken ct) => await Publishers.FindByAsync("slug", slug, ct) is { } r ? P(r) : null;

    public async Task<IReadOnlyList<Publisher>> PublishersAsync(CancellationToken ct) =>
        (await Publishers.QueryAsync(DataQuery.Everything.OrderBy("name"), ct)).Select(P).ToList();

    /// <summary>Creates the publisher profile of a member; the slug is made unique by a number.</summary>
    public async Task<long> CreatePublisherAsync(long memberId, string name, string website, CancellationToken ct)
    {
        var baseSlug = StoreText.Slug(name);
        var slug = baseSlug;
        for (var n = 2; await Publishers.FindByAsync("slug", slug, ct) is not null; n++) slug = baseSlug + "-" + n.ToString(CultureInfo.InvariantCulture);
        return await Publishers.InsertAsync(new Dictionary<string, object?>
        {
            ["member_id"] = memberId, ["name"] = name, ["slug"] = slug, ["website"] = website, ["created"] = DateTime.UtcNow
        }, ct);
    }

    // ---- items --------------------------------------------------------------------------------------------------------

    public async Task<Item?> ItemAsync(long id, CancellationToken ct) => await Items.GetAsync(id, ct) is { } r ? I(r) : null;

    public async Task<Item?> ItemByKeyAsync(string key, CancellationToken ct) => await Items.FindByAsync("key", key, ct) is { } r ? I(r) : null;

    public async Task<IReadOnlyList<Item>> ItemsAsync(string? status, CancellationToken ct)
    {
        var q = DataQuery.Everything.OrderBy("name");
        if (status is not null) q = q.Eq("status", status);
        return (await Items.QueryAsync(q, ct)).Select(I).ToList();
    }

    public async Task<IReadOnlyList<Item>> ItemsOfPublisherAsync(long publisherId, CancellationToken ct) =>
        (await Items.QueryAsync(DataQuery.Everything.Eq("publisher_id", publisherId).OrderBy("name"), ct)).Select(I).ToList();

    public Task UpdateItemAsync(long id, IReadOnlyDictionary<string, object?> values, CancellationToken ct)
    {
        var v = new Dictionary<string, object?>(values) { ["updated"] = DateTime.UtcNow };
        return Items.UpdateAsync(id, v, ct);
    }

    // ---- versions -----------------------------------------------------------------------------------------------------

    public async Task<PackageVersion?> VersionAsync(long id, CancellationToken ct) => await Versions.GetAsync(id, ct) is { } r ? V(r) : null;

    public async Task<IReadOnlyList<PackageVersion>> VersionsOfAsync(long itemId, CancellationToken ct) =>
        (await Versions.QueryAsync(DataQuery.Everything.Eq("item_id", itemId).OrderBy("created", descending: true), ct)).Select(V)
            .OrderByDescending(v => StoreText.VersionKey(v.Version)).ToList();

    public async Task<IReadOnlyList<PackageVersion>> VersionsInStatusAsync(string status, CancellationToken ct) =>
        (await Versions.QueryAsync(DataQuery.Everything.Eq("status", status).OrderBy("created"), ct)).Select(V).ToList();

    public Task<long> ReviewCountAsync(CancellationToken ct) => Versions.CountAsync(DataQuery.Everything.Eq("status", VersionStatus.Review), ct);

    /// <summary>The newest published version of an item, or null.</summary>
    public async Task<PackageVersion?> LatestPublishedAsync(long itemId, CancellationToken ct) =>
        (await VersionsOfAsync(itemId, ct)).FirstOrDefault(v => v.Status == VersionStatus.Published);

    /// <summary>After a version changed state: the item's latest version, kind and public state follow its published versions.</summary>
    public async Task RefreshItemAsync(long itemId, CancellationToken ct)
    {
        if (await ItemAsync(itemId, ct) is not { } item) return;
        var latest = await LatestPublishedAsync(itemId, ct);
        var status = item.Status == ItemStatus.Withdrawn ? ItemStatus.Withdrawn : latest is null ? ItemStatus.Draft : ItemStatus.Published;
        await UpdateItemAsync(itemId, new Dictionary<string, object?>
        {
            ["latest_version"] = latest?.Version ?? "", ["latest_kind"] = latest?.Kind ?? "", ["status"] = status
        }, ct);
    }

    // ---- pictures -----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Picture>> PicturesOfAsync(long itemId, CancellationToken ct) =>
        (await Pictures.QueryAsync(DataQuery.Everything.Eq("item_id", itemId).OrderBy("sort").OrderBy("id"), ct)).Select(Pic).ToList();

    public async Task<Picture?> PictureAsync(long id, CancellationToken ct) => await Pictures.GetAsync(id, ct) is { } r ? Pic(r) : null;
}
