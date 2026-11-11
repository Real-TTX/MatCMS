using System.Text.Json.Nodes;
using MatCMS.Cloud.Data;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The cloud's side of the MatCMS Store (the store server on matcms.de, <c>docs/official-store.md</c> S2): it reads the store's public catalogue and
/// TAKES OVER an entry into this cloud's own store — the package checked against the store keys in this image (<see cref="StoreKeys"/>), its pictures,
/// its publisher. There is no automatic mirror: the operator chooses, and the list shows when a newer version is waiting.
/// <para>A taken-over entry is an ordinary store plugin from then on (profiles select it, sites get it) with <c>Origin = "matcms"</c> and the
/// signature kept, which the sites' catalogue passes on. It is not edited here (repacking would break the signature); an own upload under the same
/// key turns it into an own entry again. An OWN entry with the same key is never overwritten by a take-over.</para>
/// </summary>
public sealed class MatcmsStoreService
{
    public const string Origin = "matcms";
    private const long MaxPackageBytes = 100L * 1024 * 1024;
    private const long MaxPictureBytes = 3 * 1024 * 1024;

    /// <summary>Test seam: the HTTP handler for the store server (null = the real network).</summary>
    public static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>Test seam: the keys a package must be signed with (null = the keys this version ships, <see cref="StoreKeys.Known"/>).</summary>
    public static IReadOnlyDictionary<string, string>? KeysOverride { get; set; }

    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static (DateTime At, string Url, JsonObject? Data, string? Error) _cache;

    private readonly AppDbContext _db;
    private readonly StoreService _store;
    private readonly ILogger<MatcmsStoreService> _log;

    public MatcmsStoreService(AppDbContext db, StoreService store, ILogger<MatcmsStoreService> log) { _db = db; _store = store; _log = log; }

    /// <summary>What the store offers — the catalogue's three arrays.</summary>
    public const string GroupPlugin = "plugin", GroupTemplate = StoreItemPackage.KindTemplate, GroupComponent = StoreItemPackage.KindComponent;

    /// <summary>One entry of the MatCMS Store and where this cloud stands with it.</summary>
    /// <param name="Group">plugin | template | component.</param>
    /// <param name="State">"new" (not here), "update" (here, older), "current" (here, same version), "own" (an own entry with that key — never overwritten).</param>
    public sealed record Entry(string Key, string Name, string Version, string Kind, string Summary, string Description, string Publisher, string PublisherUrl,
        bool PublisherVerified, string Page, int Pictures, string LocalVersion, string State, string Group = GroupPlugin);

    private static JsonArray Items(JsonObject data, string group) =>
        data[group switch { GroupTemplate => "templates", GroupComponent => "components", _ => "plugins" }] as JsonArray ?? new JsonArray();

    public async Task<string> BaseUrlAsync(CancellationToken ct = default)
    {
        var raw = (await _db.CloudSettings.AsNoTracking().Where(s => s.Key == SettingKeys.MatcmsStoreUrl).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.GetLeftPart(UriPartial.Authority) : "https://matcms.de";
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var c = HandlerFactory is { } f ? new HttpClient(f(), disposeHandler: true) : new HttpClient();
        c.Timeout = timeout;
        c.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Cloud/" + (typeof(MatcmsStoreService).Assembly.GetName().Version?.ToString() ?? "1"));
        return c;
    }

    /// <summary>The store's catalogue: from memory for five minutes (a failure for one), so the store page never waits for matcms.de twice.</summary>
    private async Task<(JsonObject? Data, string? Error)> CatalogJsonAsync(bool fresh, CancellationToken ct)
    {
        var url = await BaseUrlAsync(ct);
        await CacheGate.WaitAsync(ct);
        try
        {
            var age = DateTime.UtcNow - _cache.At;
            if (!fresh && _cache.Url == url && (_cache.Data is not null ? age < TimeSpan.FromMinutes(5) : age < TimeSpan.FromMinutes(1))) return (_cache.Data, _cache.Error);
            try
            {
                using var http = Client(TimeSpan.FromSeconds(6));
                var body = await http.GetStringAsync(url + "/store/api/catalog", ct);
                var data = body.Length < 5_000_000 ? JsonNode.Parse(body) as JsonObject : null;
                _cache = (DateTime.UtcNow, url, data, data is null ? "Der MatCMS Store hat keinen gültigen Katalog geliefert." : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _log.LogInformation("MatCMS Store not reachable at {Url}: {Message}", url, ex.Message);
                _cache = (DateTime.UtcNow, url, null, "Der MatCMS Store ist gerade nicht erreichbar (" + url + ").");
            }
            return (_cache.Data, _cache.Error);
        }
        finally { CacheGate.Release(); }
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The catalogue with this cloud's state per entry — plugins, templates and components (<see cref="Entry.Group"/>).</summary>
    public async Task<(List<Entry> Entries, string? Error)> ListAsync(bool fresh = false, CancellationToken ct = default)
    {
        var (data, error) = await CatalogJsonAsync(fresh, ct);
        if (data is null) return (new List<Entry>(), error);
        // Here = (version, origin). A template and a component are found by the store key their name / type gives (StoreItemPackage.Key).
        var here = new Dictionary<(string, string), (string Version, string Origin)>();
        foreach (var p in await _db.StorePlugins.AsNoTracking().Select(p => new { p.Key, p.Version, p.Origin }).ToListAsync(ct)) here[(GroupPlugin, p.Key)] = (p.Version, p.Origin);
        foreach (var t in await _db.StoreTemplates.AsNoTracking().Select(t => new { t.Name, t.OriginVersion, t.Origin }).ToListAsync(ct))
            here[(GroupTemplate, StoreItemPackage.Key(t.Name))] = (t.OriginVersion, t.Origin);
        foreach (var c in await _db.StoreComponents.AsNoTracking().Select(c => new { c.Type, c.OriginVersion, c.Origin }).ToListAsync(ct))
            here[(GroupComponent, StoreItemPackage.Key(c.Type))] = (c.OriginVersion, c.Origin);
        var list = new List<Entry>();
        foreach (var group in new[] { GroupPlugin, GroupTemplate, GroupComponent })
            foreach (var p in Items(data, group).OfType<JsonObject>())
            {
                var key = S(p["key"]);
                if (key.Length == 0) continue;
                var version = S(p["version"]);
                var found = here.TryGetValue((group, key), out var mine);
                var state = !found ? "new" : mine.Origin != Origin ? "own" : mine.Version == version ? "current" : "update";
                list.Add(new Entry(key, S(p["name"]), version, group == GroupPlugin ? S(p["kind"]) : group, S(p["summary"]), S(p["description"]), S(p["publisher"]),
                    S(p["publisherUrl"]), p["publisherVerified"] is JsonValue pv && pv.TryGetValue<bool>(out var b) && b, S(p["page"]),
                    (p["images"] as JsonArray)?.Count ?? 0, found ? mine.Version : "", state, group));
            }
        return (list.OrderBy(e => e.Group == GroupPlugin ? 0 : e.Group == GroupTemplate ? 1 : 2).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
    }

    /// <summary>The outcome of a take-over, in words for the operator.</summary>
    public sealed record ImportResult(bool Ok, string Message, string? Key = null, string? Version = null);

    /// <summary>
    /// Takes one entry over into this cloud's store: the package (only with a valid signature of exactly these bytes, key, kind and version), its
    /// publisher and its pictures. Profiles that selected the entry roll the new version out, as after any store upload.
    /// <para><paramref name="replaceOwn"/>: an OWN entry with the same key is replaced only when the operator says so in that very request (the UI
    /// asks first) — the same key may be somebody else's plugin. The row stays (upsert by key), so profiles that selected it keep it.</para>
    /// </summary>
    public async Task<ImportResult> ImportAsync(string key, CancellationToken ct = default, bool replaceOwn = false, string group = GroupPlugin)
    {
        if (group is GroupTemplate or GroupComponent) return await ImportDesignAsync(group, key, replaceOwn, ct);
        var (data, error) = await CatalogJsonAsync(fresh: true, ct);
        if (data is null) return new(false, error ?? "Der MatCMS Store ist nicht erreichbar.");
        var entry = (data["plugins"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(p => S(p["key"]) == key);
        if (entry is null) return new(false, $"„{key}“ gibt es im MatCMS Store nicht.");
        var mine = await _db.StorePlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key, ct);
        if (mine is not null && mine.Origin != Origin && !replaceOwn)
            return new(false, $"Im Store dieser Cloud gibt es schon ein eigenes Plugin „{key}“ – es wird nur auf ausdrücklichen Wunsch ersetzt („Ersetzen“).");

        var baseUrl = await BaseUrlAsync(ct);
        var download = S(entry["download"]);
        // Addresses come from the catalogue, so only paths on the store server itself are followed — never somewhere else.
        if (!download.StartsWith('/') || download.StartsWith("//")) return new(false, "Der Katalog nennt kein Paket für diesen Eintrag.");
        byte[] bytes;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(60));
            using var res = await http.GetAsync(baseUrl + download, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return new(false, $"Das Paket kam nicht an (HTTP {(int)res.StatusCode}).");
            if (res.Content.Headers.ContentLength > MaxPackageBytes) return new(false, "Das Paket ist zu groß.");
            bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.LongLength > MaxPackageBytes) return new(false, "Das Paket ist zu groß.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, "Das Paket konnte nicht geladen werden: " + ex.Message);
        }

        var version = S(entry["version"]);
        var kind = S(entry["kind"]) is { Length: > 0 } k ? k : "script";
        var signature = S(entry["signature"]);
        var keyId = S(entry["keyId"]);
        if (!StoreSignature.VerifyPackage(bytes, kind, key, version, S(entry["sha256"]), signature, keyId, KeysOverride))
            return new(false, $"Die Signatur von „{key}“ {version} passt nicht zu diesem Paket oder zu den Store-Schlüsseln dieser Version – nichts übernommen.");

        var stored = await _store.UpsertPluginAsync(bytes, ct);
        if (stored is null || stored.Key != key) return new(false, "Das Paket ist kein gültiges Plugin-Paket.");
        var row = await _db.StorePlugins.FirstAsync(p => p.Key == key, ct);
        row.Origin = Origin;
        row.Signature = signature;
        row.SignatureKeyId = keyId;
        await _db.SaveChangesAsync(ct);
        await _store.SetPluginPublisherAsync(key, S(entry["publisher"]), S(entry["publisherUrl"]), ct);

        // The pictures follow the store's: the old ones go, the store's come (in its order, with its captions).
        foreach (var img in await _store.ListPluginImagesAsync(key, ct) ?? new()) await _store.DeletePluginImageAsync(key, img.Id, ct);
        var pictures = 0;
        using (var http = Client(TimeSpan.FromSeconds(20)))
        {
            foreach (var img in (entry["images"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Take(StoreService.MaxPluginImages))
            {
                var url = S(img["url"]);
                if (!url.StartsWith('/') || url.StartsWith("//")) continue;
                try
                {
                    var pic = await http.GetByteArrayAsync(baseUrl + url, ct);
                    if (pic.Length <= MaxPictureBytes && (await _store.AddPluginImageAsync(key, pic, S(img["caption"]), ct)).id is not null) pictures++;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _log.LogInformation("MatCMS Store picture {Url} not taken: {Message}", url, ex.Message);
                }
            }
        }
        return new(true, $"„{stored.Name}“ {version} aus dem MatCMS Store übernommen (Signatur geprüft, {pictures} {(pictures == 1 ? "Bild" : "Bilder")}).", key, version);
    }

    /// <summary>
    /// Takes a template or a component over: the JSON file only from the store server, only with a valid signature of exactly these bytes for this
    /// kind, key and version, and only when its name (template) or type (component) gives the key the store named — so it lands on the entry of
    /// that name and on no other. Then through the store's own upsert (profiles that selected it roll it out), with the origin kept. An OWN entry of
    /// that name is replaced only with <paramref name="replaceOwn"/>. A site checks no signature for these (it gets them from its cloud like any
    /// template of the cloud's own); the check is here, where they enter.
    /// </summary>
    private async Task<ImportResult> ImportDesignAsync(string group, string key, bool replaceOwn, CancellationToken ct)
    {
        var (data, error) = await CatalogJsonAsync(fresh: true, ct);
        if (data is null) return new(false, error ?? "Der MatCMS Store ist nicht erreichbar.");
        var entry = Items(data, group).OfType<JsonObject>().FirstOrDefault(p => S(p["key"]) == key);
        if (entry is null) return new(false, $"„{key}“ gibt es im MatCMS Store nicht.");
        var download = S(entry["download"]);
        if (!download.StartsWith('/') || download.StartsWith("//")) return new(false, "Der Katalog nennt keine Datei für diesen Eintrag.");
        byte[] bytes;
        try
        {
            using var http = Client(TimeSpan.FromSeconds(30));
            using var res = await http.GetAsync(await BaseUrlAsync(ct) + download, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return new(false, $"Die Datei kam nicht an (HTTP {(int)res.StatusCode}).");
            if (res.Content.Headers.ContentLength > StoreItemPackage.MaxBytes) return new(false, "Die Datei ist zu groß.");
            bytes = await res.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(false, "Die Datei konnte nicht geladen werden: " + ex.Message);
        }

        var version = S(entry["version"]);
        if (!StoreSignature.VerifyPackage(bytes, group, key, version, S(entry["sha256"]), S(entry["signature"]), S(entry["keyId"]), KeysOverride))
            return new(false, $"Die Signatur von „{key}“ {version} passt nicht zu dieser Datei oder zu den Store-Schlüsseln dieser Version – nichts übernommen.");
        using var doc = StoreItemPackage.Parse(bytes);
        if (doc is null || StoreItemPackage.KeyOf(group, doc.RootElement) != key)
            return new(false, "Die Datei ist nicht der Eintrag, den der Store nennt – nichts übernommen.");
        var root = doc.RootElement;
        string? Opt(string prop) => JsonImport.Has(root, prop) ? JsonImport.Text(root, prop) : null;
        string? Blob(string prop, string fallback) => JsonImport.Has(root, prop) ? JsonImport.Raw(root, prop, fallback) : null;
        var description = S(entry["summary"]) is { Length: > 0 } sum ? sum : S(entry["description"]);

        if (group == GroupTemplate)
        {
            var name = JsonImport.Text(root, "Name").Trim();
            var mine = await _db.StoreTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Name == name, ct);
            if (mine is not null && mine.Origin != Origin && !replaceOwn)
                return new(false, $"Im Store dieser Cloud gibt es schon ein eigenes Template „{name}“ – es wird nur auf ausdrücklichen Wunsch ersetzt („Ersetzen“).");
            await _store.UpsertTemplateAsync(new StoreTemplateInput(name, description, Opt("AccentColor"), Opt("SecondaryColor"), Opt("HeadingFont"), Opt("BodyFont"),
                Opt("ButtonStyle"), Opt("HeadingColor"), Opt("TextColor"), Opt("BackgroundColor"), Opt("AltBackground"), Opt("ContainerWidth"), Opt("ButtonRadius"),
                Opt("HeaderBackground"), Opt("HeaderTextColor"), Opt("HeaderPadding"), Opt("CustomCss"), Opt("CustomJs"), Opt("LayoutHtml"),
                Blob("MenuMapJson", "{}"), Blob("ParametersJson", "[]"), Blob("ParamValuesJson", "{}"), Blob("PartsJson", "{}")), ct);
            var row = await _db.StoreTemplates.FirstAsync(t => t.Name == name, ct);
            if (JsonImport.Has(root, "SchemaVersion")) row.SchemaVersion = JsonImport.Int(root, "SchemaVersion", 1);
            row.Origin = Origin;
            row.OriginVersion = version;
            await _db.SaveChangesAsync(ct);
            return new(true, $"Template „{name}“ {version} aus dem MatCMS Store übernommen (Signatur geprüft).", key, version);
        }

        var type = JsonImport.Text(root, "Type").Trim().ToLowerInvariant();
        var compName = JsonImport.Text(root, "Name").Trim();
        if (type.Length == 0 || compName.Length == 0) return new(false, "Der Komponente fehlen Typ oder Name.");
        var own = await _db.StoreComponents.AsNoTracking().FirstOrDefaultAsync(c => c.Type == type, ct);
        if (own is not null && own.Origin != Origin && !replaceOwn)
            return new(false, $"Im Store dieser Cloud gibt es schon eine eigene Komponente „{type}“ – sie wird nur auf ausdrücklichen Wunsch ersetzt („Ersetzen“).");
        await _store.UpsertComponentAsync(new StoreComponentInput(type, compName, Opt("Description") ?? description, Opt("Icon"), Blob("FieldsJson", "[]"),
            Opt("TemplateHtml")), ct);
        var comp = await _db.StoreComponents.FirstAsync(c => c.Type == type, ct);
        comp.Origin = Origin;
        comp.OriginVersion = version;
        await _db.SaveChangesAsync(ct);
        return new(true, $"Komponente „{compName}“ {version} aus dem MatCMS Store übernommen (Signatur geprüft).", key, version);
    }
}
