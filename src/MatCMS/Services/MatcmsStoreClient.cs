using System.IO.Compression;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Services;

/// <summary>
/// The site's own way into the MatCMS Store (the store server on matcms.de, <c>docs/official-store.md</c> S3): browse its catalogue and install a
/// plugin from it — ONLY with a valid store signature of exactly the downloaded bytes, checked against the keys in this image
/// (<see cref="StoreKeys.Known"/>). The plugin arrives switched off, through the same importer as an upload; a native one still runs only with
/// <c>plugins.allowNative</c>.
/// <para>Who may: a site WITHOUT a cloud by its own switch (<see cref="SettingKeys.MatcmsStore"/>, on unless switched off); a site WITH a cloud
/// only when the cloud's operator allowed it (<see cref="SettingKeys.MatcmsStoreCloud"/>, written only by the sync from
/// <c>InstanceConfig.MatcmsStore</c>; never written = not allowed). Both keys are protected against every generic writer.</para>
/// </summary>
public sealed class MatcmsStoreClient
{
    public const string DefaultBaseUrl = "https://matcms.de";
    private const long MaxPackageBytes = 100L * 1024 * 1024;
    private const long MaxPictureBytes = 4 * 1024 * 1024;

    /// <summary>Test seams: the store server's address and the HTTP handler (null = the real network), the keys a package must be signed with.</summary>
    public static string BaseUrl { get; set; } = DefaultBaseUrl;
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }
    public static IReadOnlyDictionary<string, string>? KeysOverride { get; set; }

    private readonly AppDbContext _db;
    private readonly CloudService _cloud;
    private readonly IWebHostEnvironment _env;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MatcmsStoreClient> _log;

    public MatcmsStoreClient(AppDbContext db, CloudService cloud, IWebHostEnvironment env, IMemoryCache cache, ILogger<MatcmsStoreClient> log)
    { _db = db; _cloud = cloud; _env = env; _cache = cache; _log = log; }

    /// <summary>Where this site stands: may it use the MatCMS Store, and who decides (its own switch, or its cloud).</summary>
    public sealed record Access(bool Allowed, bool CloudDecides, bool OwnSwitch);

    public async Task<Access> AccessAsync(CancellationToken ct = default)
    {
        var rows = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.MatcmsStore || s.Key == SettingKeys.MatcmsStoreCloud)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var own = !(rows.TryGetValue(SettingKeys.MatcmsStore, out var o) && o is "0" or "false");
        if ((await _cloud.GetSettingsAsync()).Configured)
            return new(rows.TryGetValue(SettingKeys.MatcmsStoreCloud, out var c) && c is "1" or "true", true, own);
        return new(own, false, own);
    }

    /// <summary>One plugin of the MatCMS Store, as the store dialog shows it. Picture addresses are the STORE's (relative); the site passes them through.</summary>
    public sealed record Entry(string Key, string Name, string Version, string Kind, string Summary, string Description, string Publisher, string PublisherUrl,
        bool PublisherVerified, IReadOnlyList<(long Id, string Caption)> Images);

    private static HttpClient Client(TimeSpan timeout)
    {
        var c = HandlerFactory is { } f ? new HttpClient(f(), disposeHandler: true) : new HttpClient();
        c.Timeout = timeout;
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS/" + (typeof(MatcmsStoreClient).Assembly.GetName().Version?.ToString() ?? "1"));
        return c;
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The catalogue as JSON, five minutes in memory (a failure one). Never throws — an unreachable store is a message in the dialog.</summary>
    private async Task<(JsonArray? Plugins, string? Error)> CatalogAsync(bool fresh, CancellationToken ct)
    {
        var cacheKey = "matcms-store-catalog|" + BaseUrl;
        if (!fresh && _cache.TryGetValue(cacheKey, out (JsonArray? Plugins, string? Error) hit)) return hit;
        (JsonArray?, string?) result;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(8));
            var body = await http.GetStringAsync(BaseUrl + "/store/api/catalog", ct);
            var plugins = body.Length < 5_000_000 && JsonNode.Parse(body) is JsonObject o ? o["plugins"] as JsonArray : null;
            result = plugins is null ? (null, "Der MatCMS Store hat keinen gültigen Katalog geliefert.") : (plugins, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogInformation("MatCMS Store not reachable: {Message}", ex.Message);
            result = (null, "Der MatCMS Store (matcms.de) ist gerade nicht erreichbar.");
        }
        _cache.Set(cacheKey, result, result.Item1 is null ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5));
        return result;
    }

    /// <summary>The store's plugins (only when this site may use the store).</summary>
    public async Task<(List<Entry> Entries, string? Error)> ListAsync(CancellationToken ct = default)
    {
        if (!(await AccessAsync(ct)).Allowed) return (new(), "Diese Website darf nicht aus dem MatCMS Store laden.");
        var (plugins, error) = await CatalogAsync(false, ct);
        if (plugins is null) return (new(), error);
        var list = new List<Entry>();
        foreach (var p in plugins.OfType<JsonObject>())
        {
            var key = S(p["key"]);
            if (key.Length == 0 || S(p["download"]).Length == 0) continue;
            var images = (p["images"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(i => (Id: i["id"] is JsonValue iv && iv.TryGetValue<long>(out var id) ? id : 0, Caption: S(i["caption"])))
                .Where(i => i.Id > 0).Take(8).ToList();
            list.Add(new Entry(key, S(p["name"]), S(p["version"]), S(p["kind"]), S(p["summary"]), S(p["description"]), S(p["publisher"]), S(p["publisherUrl"]),
                p["publisherVerified"] is JsonValue pv && pv.TryGetValue<bool>(out var b) && b, images));
        }
        return (list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
    }

    /// <summary>
    /// A store picture for the site's own admin, fetched by the SITE and served by it (the admin's browser never talks to matcms.de; the
    /// admin's content policy would refuse it anyway). Only an id the catalogue names for that plugin, only PNG/JPEG/WebP, an hour in memory.
    /// </summary>
    public async Task<(byte[] Data, string Type)?> ImageAsync(string key, long id, CancellationToken ct = default)
    {
        if (!(await AccessAsync(ct)).Allowed) return null;
        var cacheKey = $"matcms-store-image|{BaseUrl}|{key}|{id}";
        if (_cache.TryGetValue(cacheKey, out (byte[] Data, string Type) hit)) return hit;
        var (plugins, _) = await CatalogAsync(false, ct);
        var entry = plugins?.OfType<JsonObject>().FirstOrDefault(p => S(p["key"]) == key);
        var url = (entry?["images"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(i => i["id"] is JsonValue iv && iv.TryGetValue<long>(out var x) && x == id) is { } img ? S(img["url"]) : "";
        if (!url.StartsWith('/') || url.StartsWith("//")) return null;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(15));
            using var res = await http.GetAsync(BaseUrl + url, ct);
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!res.IsSuccessStatusCode || type is not ("image/png" or "image/jpeg" or "image/webp") || res.Content.Headers.ContentLength > MaxPictureBytes) return null;
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            if (data.Length > MaxPictureBytes) return null;
            _cache.Set(cacheKey, (data, type), TimeSpan.FromHours(1));
            return (data, type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Installs (or updates) one plugin from the MatCMS Store: the package only from the store server itself, only with a valid signature of
    /// exactly these bytes for this key, kind and version, and only when the package's own manifest names the same key and version. Arrives
    /// switched off, like every import.
    /// </summary>
    public async Task<(bool Ok, string Message, int? PluginId)> InstallAsync(string key, CancellationToken ct = default)
    {
        if (!(await AccessAsync(ct)).Allowed) return (false, "Diese Website darf nicht aus dem MatCMS Store laden.", null);
        var (plugins, error) = await CatalogAsync(true, ct);
        if (plugins is null) return (false, error ?? "Der MatCMS Store ist nicht erreichbar.", null);
        var entry = plugins.OfType<JsonObject>().FirstOrDefault(p => S(p["key"]) == key);
        if (entry is null) return (false, $"„{key}“ gibt es im MatCMS Store nicht.", null);
        var download = S(entry["download"]);
        if (!download.StartsWith('/') || download.StartsWith("//")) return (false, "Der Katalog nennt kein Paket für diesen Eintrag.", null);

        byte[] bytes;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(90));
            using var res = await http.GetAsync(BaseUrl + download, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return (false, $"Das Paket kam nicht an (HTTP {(int)res.StatusCode}).", null);
            if (res.Content.Headers.ContentLength > MaxPackageBytes) return (false, "Das Paket ist zu groß.", null);
            bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.LongLength > MaxPackageBytes) return (false, "Das Paket ist zu groß.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "Das Paket konnte nicht geladen werden: " + ex.Message, null);
        }

        var version = S(entry["version"]);
        var kind = S(entry["kind"]) is { Length: > 0 } k ? k : PluginBundle.KindScript;
        if (!StoreSignature.VerifyPackage(bytes, kind, key, version, S(entry["sha256"]), S(entry["signature"]), S(entry["keyId"]), KeysOverride))
            return (false, $"Die Signatur von „{key}“ {version} passt nicht zu diesem Paket oder zu den Store-Schlüsseln dieser MatCMS-Version – nichts installiert.", null);
        if (Manifest(bytes) is not { } m || !string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase) || m.Version != version)
            return (false, "Das Paket nennt einen anderen Plugin-Schlüssel oder eine andere Version als der Store – nichts installiert.", null);

        using var stream = new MemoryStream(bytes, writable: false);
        var (plugin, updated, importError) = await PluginPackager.ImportAsync(stream, _env, _db);
        if (plugin is null) return (false, importError ?? "Import fehlgeschlagen.", null);
        return (true, updated
            ? $"Plugin „{plugin.Name}“ aus dem MatCMS Store auf {plugin.Version} aktualisiert (Signatur geprüft). Es ist deaktiviert, bis du es freischaltest."
            : $"Plugin „{plugin.Name}“ {plugin.Version} aus dem MatCMS Store installiert (Signatur geprüft). Es ist deaktiviert, bis du es freischaltest.", plugin.Id);
    }

    /// <summary>A bundle's own key, version and kind (from <c>plugin.json</c>), or null when it has no readable manifest.</summary>
    public static (string Key, string Version, string Kind)? Manifest(byte[] bundle)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bundle, writable: false), ZipArchiveMode.Read);
            var e = zip.GetEntry(PluginBundle.ManifestEntry);
            if (e is null || e.Length > PluginBundle.MaxManifestBytes) return null;
            using var r = new StreamReader(e.Open());
            if (JsonNode.Parse(r.ReadToEnd().TrimStart('﻿')) is not JsonObject o) return null;
            static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            var kind = Str(o["Kind"]).Trim().ToLowerInvariant() == PluginBundle.KindNative ? PluginBundle.KindNative : PluginBundle.KindScript;
            return (Str(o["Key"]).Trim(), Str(o["Version"]).Trim(), kind);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The check for a package from the CLOUD's store that came from the MatCMS Store (<c>CatalogPlugin.Origin = "matcms"</c> with a signature):
    /// the signature must fit the bytes and the package's own key, kind and version. Null = fine (also: nothing to check); else the reason.
    /// </summary>
    public static string? CheckCloudPackage(byte[] bundle, CatalogPlugin entry, IReadOnlyDictionary<string, string>? keys = null)
    {
        if (entry.Signature.Length == 0) return null;
        if (Manifest(bundle) is not { } m) return "Das Paket hat kein lesbares plugin.json.";
        return StoreSignature.VerifyPackage(bundle, m.Kind, m.Key, m.Version, null, entry.Signature, entry.SignatureKeyId, keys ?? KeysOverride)
            ? null
            : $"Das Paket „{m.Key}“ {m.Version} trägt eine MatCMS-Store-Signatur, die nicht zu diesem Paket passt – nichts installiert.";
    }
}
