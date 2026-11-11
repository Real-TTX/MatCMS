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
    private readonly MatcmsStoreUpdates _updates;
    private readonly IServiceProvider _services;

    public MatcmsStoreClient(AppDbContext db, CloudService cloud, IWebHostEnvironment env, IMemoryCache cache, ILogger<MatcmsStoreClient> log,
        MatcmsStoreUpdates updates, IServiceProvider services)
    { _db = db; _cloud = cloud; _env = env; _cache = cache; _log = log; _updates = updates; _services = services; }

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
        bool PublisherVerified, IReadOnlyList<(long Id, string Caption)> Images, long Downloads = 0);

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
            if (plugins is not null) _updates.SetStore(plugins.OfType<JsonObject>().Select(p => (S(p["key"]), S(p["version"]))));
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
                p["publisherVerified"] is JsonValue pv && pv.TryGetValue<bool>(out var b) && b, images,
                p["downloads"] is JsonValue dv && dv.TryGetValue<long>(out var dl) ? dl : 0));
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

    /// <summary>
    /// Asks the store (and, with a cloud, the cloud's store) what is current, for the update hints — run by <see cref="MatcmsStoreUpdateService"/>
    /// in the background, never while a page waits. A site that may not use the store forgets what it knew.
    /// </summary>
    public async Task RefreshUpdatesAsync(CancellationToken ct = default)
    {
        if (!(await AccessAsync(ct)).Allowed) { _updates.Clear(); return; }
        await CatalogAsync(true, ct);
        // A key the cloud's store also offers is installed from there (see the store dialog) — so its updates come from there too.
        if ((await _cloud.GetSettingsAsync()).Configured
            && _services.GetService<CloudCatalogService>() is { } cloudCatalog
            && (await cloudCatalog.GetCatalogAsync(ct)).catalog is { } c)
            _updates.SetCloudKeys((c.Plugins ?? []).Select(p => p.Key));
        else _updates.SetCloudKeys([]);
    }

    /// <summary>The installed plugins (not the built-in ones) for which the MatCMS Store has a newer version: key → that version. From memory only.</summary>
    public async Task<Dictionary<string, string>> UpdatesAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_updates.IsEmpty || !(await AccessAsync(ct)).Allowed) return result;
        var installed = await _db.Plugins.AsNoTracking().Where(p => p.Kind != MatCMS.Models.PluginKind.BuiltIn)
            .Select(p => new { p.Key, p.Version }).ToListAsync(ct);
        foreach (var p in installed)
            if (_updates.Newer(p.Key, p.Version) is { } v) result[p.Key] = v;
        return result;
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
    /// The check for a package that reaches the site through its CLOUD (store dialog or profile rollout) under the MatCMS Store's name
    /// (<c>Origin = "matcms"</c>): it must carry a signature, the signature must fit the bytes and the package's own kind and version, and the
    /// package must be the plugin it was asked for. A package without that origin is the cloud's own — nothing to check. Null = fine; else the reason.
    /// </summary>
    public static string? CheckSignedPackage(byte[] bundle, string expectedKey, string? origin, string? signature, string? keyId,
        IReadOnlyDictionary<string, string>? keys = null)
    {
        if (!string.Equals(origin, "matcms", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(signature)) return null;
        if (string.IsNullOrEmpty(signature)) return $"„{expectedKey}“ kommt angeblich aus dem MatCMS Store, trägt aber keine Signatur – nichts installiert.";
        if (Manifest(bundle) is not { } m) return "Das Paket hat kein lesbares plugin.json.";
        if (!string.Equals(m.Key, expectedKey, StringComparison.OrdinalIgnoreCase))
            return $"Das Paket für „{expectedKey}“ ist das Plugin „{m.Key}“ – nichts installiert.";
        return StoreSignature.VerifyPackage(bundle, m.Kind, m.Key, m.Version, null, signature, keyId ?? "", keys ?? KeysOverride)
            ? null
            : $"Das Paket „{m.Key}“ {m.Version} trägt eine MatCMS-Store-Signatur, die nicht zu diesem Paket passt – nichts installiert.";
    }
}

/// <summary>
/// What the MatCMS Store offers right now (key → version), kept in memory for the update hints on Admin → Plugins and the dashboard. Filled
/// by every catalogue read and by <see cref="MatcmsStoreUpdateService"/>; keys the cloud's store offers are left out (they update from there).
/// </summary>
public sealed class MatcmsStoreUpdates
{
    private volatile IReadOnlyDictionary<string, string> _store = new Dictionary<string, string>();
    private volatile IReadOnlySet<string> _cloud = new HashSet<string>();

    public bool IsEmpty => _store.Count == 0;

    public void SetStore(IEnumerable<(string Key, string Version)> entries) =>
        _store = entries.Where(e => e.Key.Length > 0 && e.Version.Length > 0)
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.OrdinalIgnoreCase);

    public void SetCloudKeys(IEnumerable<string> keys) => _cloud = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Clear() { _store = new Dictionary<string, string>(); _cloud = new HashSet<string>(); }

    /// <summary>The store's version when it is newer than <paramref name="installed"/> (and the key is not the cloud's), else null.</summary>
    public string? Newer(string key, string? installed) =>
        !_cloud.Contains(key) && _store.TryGetValue(key, out var v) && PluginContext.CompareVersions(v, installed) > 0 ? v : null;
}

/// <summary>Asks the MatCMS Store for current versions a few minutes after start and then every six hours — only while the site may use it.</summary>
public sealed class MatcmsStoreUpdateService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MatcmsStoreUpdateService> _log;
    public MatcmsStoreUpdateService(IServiceScopeFactory scopes, ILogger<MatcmsStoreUpdateService> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MatcmsStoreClient>().RefreshUpdatesAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogInformation(ex, "MatCMS Store: update check failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
