using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MatCMS.Plugin.MatcmsStore;

/// <summary>
/// <c>/store/api/…</c> — what clouds and sites read. Public and read-only: the catalogue is no secret, the safety is the signature every package carries
/// (and that clouds and sites check against the keys in their own image). Who may LOAD from here is decided on the other side (a cloud always,
/// a site only when its profile allows it). These routes keep answering in maintenance mode (<see cref="PublicRouteOptions.DuringMaintenance"/>).
/// <para>The catalogue has the shape of a cloud's <c>/api/public/store</c> (format 1: plugins, templates, components), plus what installing needs:
/// hash, signature, key id and the package's address per version.</para>
/// </summary>
internal static class StoreApiRoutes
{
    private static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    private static JsonObject VersionJson(Item item, PackageVersion v) => new()
    {
        ["version"] = v.Version, ["kind"] = v.Kind, ["api"] = v.Api, ["minCms"] = v.MinCms, ["size"] = v.Size, ["sha256"] = v.Sha256,
        ["signature"] = v.Status == VersionStatus.Published ? v.Signature : "", ["keyId"] = v.Status == VersionStatus.Published ? v.KeyId : "",
        ["published"] = Iso(v.PublishedAt), ["notes"] = v.Notes, ["status"] = v.Status,
        ["reason"] = v.Status == VersionStatus.Withdrawn ? v.Reason : "",
        ["download"] = v.Status == VersionStatus.Published ? StoreUrls.Package(item.Key, v.Version, v.Kind) : ""
    };

    private static async Task<JsonObject> ItemJsonAsync(IPluginContext ctx, StoreDb db, Item item, Publisher? pub, bool allVersions, CancellationToken ct)
    {
        var versions = (await db.VersionsOfAsync(item.Id, ct)).Where(v => v.Status is VersionStatus.Published or VersionStatus.Withdrawn).ToList();
        var latest = versions.FirstOrDefault(v => v.Status == VersionStatus.Published);
        var images = new JsonArray();
        foreach (var p in await db.PicturesOfAsync(item.Id, ct))
            images.Add(new JsonObject { ["id"] = p.Id, ["caption"] = p.Caption, ["url"] = StoreUrls.Image(item.Key, p.Id) });
        var tags = new JsonArray();
        foreach (var t in StoreText.Tags(item.Tags)) tags.Add(t);
        var o = new JsonObject
        {
            ["key"] = item.Key, ["name"] = item.Name, ["version"] = latest?.Version ?? "", ["summary"] = item.Summary, ["description"] = item.Description,
            ["kind"] = latest?.Kind ?? item.LatestKind, ["api"] = latest?.Api ?? "", ["minCms"] = latest?.MinCms ?? "", ["updated"] = Iso(latest?.PublishedAt),
            ["publisher"] = pub?.Name ?? "", ["publisherUrl"] = pub is null ? "" : StoreText.SafeUrl(pub.Website),
            ["publisherPage"] = pub is null ? "" : ctx.Site.Origin.TrimEnd('/') + StoreUrls.Publisher(pub.Slug), ["publisherVerified"] = pub?.Verified ?? false,
            ["page"] = ctx.Site.Origin.TrimEnd('/') + StoreUrls.Item(item.Key), ["images"] = images, ["tags"] = tags, ["license"] = item.License,
            ["homepage"] = StoreText.SafeUrl(item.Homepage), ["downloads"] = item.Downloads,
            ["sha256"] = latest?.Sha256 ?? "", ["size"] = latest?.Size ?? 0, ["signature"] = latest?.Signature ?? "", ["keyId"] = latest?.KeyId ?? "",
            ["download"] = latest is null ? "" : StoreUrls.Package(item.Key, latest.Version, latest.Kind)
        };
        // A template's look (colours, fonts) and a component's icon, as the package carried them — a showcase draws them, a site never applies them.
        if (latest is not null && latest.Look.Length > 0 && JsonNode.Parse(latest.Look) is JsonObject look)
        {
            o["look"] = look;
            if (look["accent"] is JsonValue a) o["accentColor"] = a.DeepClone();
            if (look["icon"] is JsonValue ic) o["icon"] = ic.DeepClone();
        }
        if (allVersions)
        {
            var arr = new JsonArray();
            foreach (var v in versions) arr.Add(VersionJson(item, v));
            o["versions"] = arr;
        }
        return o;
    }

    private static PluginResult Json(JsonObject body, int maxAge) =>
        PluginResult.Json(body).WithHeader("Cache-Control", "public, max-age=" + maxAge.ToString(CultureInfo.InvariantCulture)).WithHeader("Access-Control-Allow-Origin", "*");

    public static async Task<PluginResult> CatalogAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        var plugins = new JsonArray();
        var templates = new JsonArray();
        var components = new JsonArray();
        foreach (var (item, pub, _) in await StorePages.PublicItemsAsync(db, "", "", "", ct))
        {
            var json = await ItemJsonAsync(ctx, db, item, pub, allVersions: false, ct);
            (StorePackage.Group(item.LatestKind) switch { StorePackage.KindTemplate => templates, StorePackage.KindComponent => components, _ => plugins }).Add(json);
        }
        var body = new JsonObject
        {
            ["format"] = 1, ["generated"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["store"] = new JsonObject { ["name"] = StoreSettings.StoreTitle(ctx), ["url"] = ctx.Site.Origin.TrimEnd('/') + StoreUrls.Home, ["signatureScheme"] = StoreSig.Scheme },
            ["plugins"] = plugins, ["templates"] = templates, ["components"] = components
        };
        // A validator over the content, so a cloud asking every hour mostly gets a 304 from its own cache logic.
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plugins.ToJsonString() + templates.ToJsonString() + components.ToJsonString())))[..16] + "\"";
        return Json(body, 300).WithHeader("ETag", etag);
    }

    public static async Task<PluginResult> ItemAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await db.ItemByKeyAsync(ctx.Request.R("key").ToLowerInvariant(), ct) is not { Status: ItemStatus.Published } item) return NotFound();
        var pub = await db.PublisherAsync(item.PublisherId, ct);
        if (pub?.Blocked == true) return NotFound();
        return Json(await ItemJsonAsync(ctx, db, item, pub, allVersions: true, ct), 300);
    }

    private static PluginResult NotFound() => PluginResult.Json(new JsonObject { ["error"] = "not found" }, 404);

    /// <summary>A package: only a PUBLISHED version of a public entry. Counted anonymously, once per address, version and day.</summary>
    public static async Task<PluginResult> BundleAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await db.ItemByKeyAsync(ctx.Request.R("key").ToLowerInvariant(), ct) is not { Status: ItemStatus.Published } item) return NotFound();
        var version = (await db.VersionsOfAsync(item.Id, ct)).FirstOrDefault(v => v.Version == ctx.Request.R("version") && v.Status == VersionStatus.Published);
        if (version is null || await ctx.Files.OpenAsync(version.File, ct) is not { } stream) return NotFound();
        await db.CountDownloadAsync(version, ctx.Request.ClientIp, ct);
        var json = StorePackage.Group(version.Kind) != "plugin";
        return PluginResult.FromStream(stream, json ? "application/json" : "application/zip", item.Key + "-" + version.Version + (json ? ".json" : ".zip"))
            .WithHeader("X-Sha256", version.Sha256).WithHeader("Cache-Control", "public, max-age=86400").WithHeader("X-Content-Type-Options", "nosniff");
    }

    /// <summary>A picture of an entry. Before the entry is public only its publisher and the administrator see its pictures (the account page shows them).</summary>
    public static async Task<PluginResult> ImageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var db = new StoreDb(ctx.Data);
        if (await db.ItemByKeyAsync(ctx.Request.R("key").ToLowerInvariant(), ct) is not { } item || item.Status == ItemStatus.Withdrawn && !ctx.User.IsAdmin) return NotFound();
        if (!long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || await db.PictureAsync(id, ct) is not { } pic || pic.ItemId != item.Id)
            return NotFound();
        var isPublic = item.Status == ItemStatus.Published;
        if (!isPublic && !ctx.User.IsAdmin)
        {
            var owner = ctx.Member is { } m ? await db.PublisherOfMemberAsync(m.Id, ct) : null;
            if (owner is null || owner.Id != item.PublisherId) return NotFound();
        }
        if (await ctx.Files.OpenAsync(pic.File, ct) is not { } stream) return NotFound();
        return PluginResult.FromStream(stream, pic.Type)
            .WithHeader("Cache-Control", isPublic ? "public, max-age=86400" : "private, no-store")
            .WithHeader("X-Content-Type-Options", "nosniff").WithHeader("Content-Security-Policy", "default-src 'none'; sandbox");
    }
}
