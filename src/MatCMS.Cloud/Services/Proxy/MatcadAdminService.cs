using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Cloud.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services.Proxy;

/// <summary>
/// Maintains the Matcad of "Dieser Host" from the cloud: its base domain and ACME e-mail, its DNS providers and every
/// domain (route) it serves — so a hosting server never needs Matcad's own UI after the first start. UI (Hosting →
/// Hosts → Dieser Host → Matcad), REST (<c>/api/v1/hosting/matcad/…</c>) and MCP all call this one class.
/// <para>Only this host: a node's Matcad is reached from the node, never from the cloud, and its proxy key travels
/// only inside node jobs. The edge, when it has its own Matcad, is not covered either.</para>
/// <para>Two rules keep the cloud's own bookkeeping intact: a route the cloud created for an instance (host address,
/// customer domain, pending route, wildcard) is shown but NOT editable here — it is changed on the instance, which
/// knows its route id; and provider secrets are never handed out — Matcad's API returns them in clear, the cloud
/// blanks them, and an empty secret on save keeps the stored one.</para>
/// </summary>
public sealed class MatcadAdminService
{
    private readonly ProxyService _proxy;
    private readonly IHttpClientFactory _http;
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;

    public MatcadAdminService(ProxyService proxy, IHttpClientFactory http, AppDbContext db, CloudContext cloud)
    {
        _proxy = proxy; _http = http; _db = db; _cloud = cloud;
    }

    public sealed record Field(string Key, string Label, bool Secret);
    public sealed record ProviderType(string Id, string DisplayName, List<Field> Fields);
    /// <param name="Values">Non-secret credential values.</param>
    /// <param name="SecretsSet">Secret fields that hold a value (the value itself is never returned).</param>
    public sealed record Provider(long Id, string Name, string Type, Dictionary<string, string> Values, List<string> SecretsSet);
    /// <param name="Target">"proxy" or "redirect" (a route without upstream that only redirects).</param>
    /// <param name="ManagedBy">The instance (or "Wildcard") the cloud created this route for; such routes are read-only here.</param>
    public sealed record Route(long Id, string Name, string Host, bool Wildcard, string Target, string? Upstream, string? FallbackUrl,
        long? ProviderId, bool Enabled, bool AllowEmbedding, string Source, bool Editable, string? CertKind,
        string? ManagedBy, int? ManagedInstanceId);
    /// <param name="PropagationDelay">Seconds Caddy waits after writing the DNS record before asking Let's Encrypt to check it.</param>
    /// <param name="PropagationTimeout">Seconds Caddy waits at most for the record to be visible (-1 = do not check).</param>
    public sealed record MatcadSettings(string BaseDomain, string AcmeEmail, int PropagationDelay, int PropagationTimeout);
    public sealed record Overview(MatcadSettings Settings, List<ProviderType> ProviderTypes, List<Provider> Providers, List<Route> Routes);
    public sealed record Result(bool Ok, string Message, long? Id = null);

    public sealed record RouteInput(long? Id, string? Host, string? Name, string? Target, string? Upstream, string? FallbackUrl,
        bool Wildcard, long? ProviderId, bool Enabled = true, bool AllowEmbedding = false);

    /// <summary>Null when this host's proxy is a usable Matcad, else why not.</summary>
    public string? Unavailable
    {
        get
        {
            var s = _proxy.Settings;
            if (s.Kind != ProxyKinds.Matcad) return "Der Proxy dieses Hosts ist nicht Matcad (Hosting → Hosts → Dieser Host → Proxy).";
            if (string.IsNullOrWhiteSpace(s.MatcadUrl)) return "Für Matcad ist keine Adresse eingetragen (Proxy-Tab).";
            return null;
        }
    }

    public bool Available => Unavailable is null;

    private HttpClient Http()
    {
        var http = _http.CreateClient("proxy");
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var s = _proxy.Settings;
        var r = new HttpRequestMessage(m, (s.MatcadUrl ?? "").TrimEnd('/') + "/api/v1" + path);
        if (!string.IsNullOrEmpty(s.MatcadToken)) r.Headers.Add("X-Api-Key", s.MatcadToken);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    /// <summary>One call to Matcad. Throws <see cref="MatcadException"/> with a message fit for the operator.</summary>
    private async Task<JsonNode?> CallAsync(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        if (Unavailable is { } why) throw new MatcadException(why);
        HttpResponseMessage resp;
        try { resp = await Http().SendAsync(Req(m, path, body), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { throw new MatcadException($"Matcad nicht erreichbar: {ex.Message}"); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string? err = null;
                try { err = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? JsonNode.Parse(text)?["detail"]?.GetValue<string>(); } catch { }
                throw new MatcadException(resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Matcad lehnt den API-Schlüssel ab.",
                    HttpStatusCode.ServiceUnavailable => "Matcad hat kein API aktiviert (Matcad__ApiKey nicht gesetzt).",
                    HttpStatusCode.Conflict => err ?? "Matcad meldet einen Konflikt.",
                    _ => $"Matcad antwortete {(int)resp.StatusCode}: {err ?? (text.Length > 300 ? text[..300] + "…" : text)}",
                });
            }
            if (resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                throw new MatcadException("Unter der Matcad-Adresse antwortet kein Matcad-API (HTML statt JSON).");
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    /// <summary>"applied.ok = false" — Matcad stored the change but Caddy refused it.</summary>
    private static string? ApplyError(JsonNode? n) =>
        n?["applied"]?["ok"]?.GetValue<bool>() == false ? n["applied"]?["error"]?.GetValue<string>() ?? "unbekannter Fehler" : null;

    private static string Str(JsonNode? n) => n?.GetValue<string>() ?? "";
    private static string? StrOrNull(JsonNode? n) => n is null ? null : n.GetValue<string>() is { Length: > 0 } s ? s : null;
    private static long? LongOrNull(JsonNode? n) => n is null ? null : n.GetValue<long>();
    private static bool Bool(JsonNode? n) => n?.GetValue<bool>() ?? false;

    // ---- read ----------------------------------------------------------------------------------------------------

    public async Task<Overview> OverviewAsync(CancellationToken ct = default)
    {
        var settings = CallAsync(HttpMethod.Get, "/settings", null, ct);
        var types = ProviderTypesAsync(ct);
        var raw = RawProvidersAsync(ct);
        var routes = RoutesAsync(ct);
        await Task.WhenAll(settings, types, raw, routes);
        var s = settings.Result;
        var t = types.Result;
        return new(new(Str(s?["baseDomain"]), Str(s?["acmeEmail"]), s?["acmePropagationDelaySeconds"]?.GetValue<int>() ?? 0,
            s?["acmePropagationTimeoutSeconds"]?.GetValue<int>() ?? 0), t, raw.Result.Select(p => Mask(p, t)).ToList(), routes.Result);
    }

    public async Task<List<ProviderType>> ProviderTypesAsync(CancellationToken ct = default) =>
        ((await CallAsync(HttpMethod.Get, "/provider-types", null, ct))?.AsArray() ?? new JsonArray())
        .Select(t => new ProviderType(Str(t?["id"]), Str(t?["displayName"]),
            (t?["fields"]?.AsArray() ?? new JsonArray()).Select(f => new Field(Str(f?["key"]), Str(f?["label"]), Bool(f?["secret"]))).ToList()))
        .ToList();

    private sealed record RawProvider(long Id, string Name, string Type, Dictionary<string, string> Credentials);

    private async Task<List<RawProvider>> RawProvidersAsync(CancellationToken ct) =>
        ((await CallAsync(HttpMethod.Get, "/providers", null, ct))?.AsArray() ?? new JsonArray())
        .Select(p => new RawProvider(p!["id"]!.GetValue<long>(), Str(p["name"]), Str(p["type"]),
            (p["credentials"]?.AsObject() ?? new JsonObject()).ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "")))
        .ToList();

    /// <summary>A field counts as secret when its provider type says so; for a type the cloud does not know (Matcad's
    /// "custom") every field is treated as secret — better hidden than leaked.</summary>
    private static bool IsSecret(List<ProviderType> types, string type, string key)
    {
        var t = types.FirstOrDefault(x => string.Equals(x.Id, type, StringComparison.OrdinalIgnoreCase));
        var f = t?.Fields.FirstOrDefault(x => x.Key == key);
        return f?.Secret ?? true;
    }

    private static Provider Mask(RawProvider p, List<ProviderType> types) => new(p.Id, p.Name, p.Type,
        p.Credentials.Where(kv => !IsSecret(types, p.Type, kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
        p.Credentials.Where(kv => IsSecret(types, p.Type, kv.Key) && kv.Value.Length > 0).Select(kv => kv.Key).ToList());

    public async Task<List<Provider>> ProvidersAsync(CancellationToken ct = default)
    {
        var types = await ProviderTypesAsync(ct);
        return (await RawProvidersAsync(ct)).Select(p => Mask(p, types)).ToList();
    }

    /// <summary>Every route Matcad serves (manual, system, docker), each marked with the instance the cloud created it
    /// for, if any.</summary>
    public async Task<List<Route>> RoutesAsync(CancellationToken ct = default)
    {
        var managed = await ManagedRouteIdsAsync(ct);
        return ((await CallAsync(HttpMethod.Get, "/routes", null, ct))?.AsArray() ?? new JsonArray())
            .Select(r =>
            {
                var id = r!["id"]!.GetValue<long>();
                var upstream = StrOrNull(r["upstream"]);
                var isManaged = managed.TryGetValue(id.ToString(), out var m);
                var editable = Bool(r["editable"]);
                return new Route(id, Str(r["name"]), Str(r["host"]), Bool(r["wildcard"]), upstream is null ? "redirect" : "proxy", upstream,
                    StrOrNull(r["fallbackUrl"]), LongOrNull(r["providerId"]), Bool(r["enabled"]), Bool(r["allowEmbedding"]),
                    StrOrNull(r["source"]) ?? "manual", editable && !isManaged && r["listenPort"] is null, StrOrNull(r["certKind"]),
                    isManaged ? m.Label : null, isManaged ? m.InstanceId : null);
            })
            .ToList();
    }

    /// <summary>Route ids at THIS host's Matcad that the cloud keeps track of. Ids are per Matcad, so only routes known to
    /// live here count: on this host (no node) and, while the edge is this host's proxy, the edge routes.</summary>
    private async Task<Dictionary<string, (string? Label, int? InstanceId)>> ManagedRouteIdsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string? Label, int? InstanceId)>();
        var edgeHere = _proxy.EdgeUsesHostProxy;
        var rows = await _db.Instances.AsNoTracking()
            .Where(i => i.HostRouteId != null || i.ProxyRouteId != null)
            .Select(i => new { i.Id, i.Name, i.NodeId, i.HostRouteId, i.HostProvider, i.ProxyRouteId, i.ProxyProvider, i.ProxyVia })
            .ToListAsync(ct);
        foreach (var i in rows)
        {
            if (i.NodeId is null && i.HostRouteId is { } h && i.HostProvider == ProxyKinds.Matcad) map[h] = (i.Name, i.Id);
            var onEdge = i.ProxyVia == ProxyVia.Edge;
            if (i.ProxyRouteId is { } p && i.ProxyProvider == ProxyKinds.Matcad && (onEdge ? edgeHere : i.NodeId is null)) map[p] = (i.Name, i.Id);
        }
        var pending = await _db.CloudSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(SettingKeys.HostingPendingRoutePrefix)).ToListAsync(ct);
        foreach (var row in pending)
        {
            PendingRoute? pr = null;
            try { pr = string.IsNullOrWhiteSpace(row.Value) ? null : JsonSerializer.Deserialize<PendingRoute>(row.Value); } catch { }
            if (pr is null) continue;
            var label = row.Key[SettingKeys.HostingPendingRoutePrefix.Length..];
            if (pr.HostRouteId is { } h && pr.HostProvider == ProxyKinds.Matcad) map[h] = (label, null);
            if (pr.RouteId is { } r && pr.Provider == ProxyKinds.Matcad && (pr.Via != ProxyVia.Edge || edgeHere)) map[r] = (label, null);
        }
        if (_proxy.WildcardFor(null).RouteId is { } w) map[w] = ("Wildcard", null);
        return map;
    }

    // ---- settings ------------------------------------------------------------------------------------------------

    /// <summary>Base domain and ACME e-mail. Null = leave unchanged, "" = clear.</summary>
    public async Task<Result> SaveSettingsAsync(string? baseDomain, string? acmeEmail, int? propagationDelay = null, int? propagationTimeout = null,
        CancellationToken ct = default)
    {
        string? bd = null;
        if (baseDomain is not null)
        {
            bd = baseDomain.Trim().Length == 0 ? "" : ProxyService.NormaliseDomain(baseDomain);
            if (bd is null) return new(false, "Keine gültige Basis-Domain (nur ein Name wie example.de).");
        }
        try
        {
            if (propagationDelay is < 0 or > 3600 || propagationTimeout is < -1 or > 7200)
                return new(false, "Wartezeit 0–3600 s, Zeitlimit -1–7200 s.");
            var r = await CallAsync(HttpMethod.Put, "/settings", new
            {
                baseDomain = bd, acmeEmail = acmeEmail?.Trim(),
                acmePropagationDelaySeconds = propagationDelay, acmePropagationTimeoutSeconds = propagationTimeout,
            }, ct);
            return r?["ok"]?.GetValue<bool>() == false
                ? new(false, "Gespeichert, aber Caddy übernahm es nicht: " + (StrOrNull(r["error"]) ?? "unbekannter Fehler"))
                : new(true, "Matcad-Einstellungen gespeichert.");
        }
        catch (MatcadException ex) { return new(false, ex.Message); }
    }

    // ---- DNS providers -------------------------------------------------------------------------------------------

    /// <summary>Creates (no id) or updates a provider. An empty secret field keeps the value Matcad has.</summary>
    public async Task<Result> SaveProviderAsync(long? id, string? name, string? type, IReadOnlyDictionary<string, string?>? credentials,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(false, "Bitte einen Namen angeben.");
        if (string.IsNullOrWhiteSpace(type)) return new(false, "Bitte einen Anbieter-Typ wählen.");
        try
        {
            var creds = await MergeCredentialsAsync(id, type.Trim(), credentials, ct);
            if (creds is null) return new(false, "DNS-Anbieter nicht gefunden.");
            var r = await CallAsync(HttpMethod.Post, "/providers", new { id, name = name.Trim(), type = type.Trim(), credentials = creds }, ct);
            return new(true, id is null ? "DNS-Anbieter angelegt." : "DNS-Anbieter gespeichert.", LongOrNull(r?["id"]));
        }
        catch (MatcadException ex) { return new(false, ex.Message); }
    }

    /// <summary>The credentials to send: what was entered, and for an existing provider of the same type its stored value
    /// for every field left empty. Null = the id does not exist.</summary>
    private async Task<Dictionary<string, string>?> MergeCredentialsAsync(long? id, string type, IReadOnlyDictionary<string, string?>? input,
        CancellationToken ct)
    {
        var result = (input ?? new Dictionary<string, string?>())
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value?.Trim() ?? "");
        if (id is null) return result.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        var existing = (await RawProvidersAsync(ct)).FirstOrDefault(p => p.Id == id);
        if (existing is null) return null;
        // A changed type has other fields — keeping the old type's secrets would only send nonsense.
        if (!string.Equals(existing.Type, type, StringComparison.OrdinalIgnoreCase))
            return result.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, v) in existing.Credentials)
            if (!result.TryGetValue(k, out var now) || now.Length == 0) result[k] = v;
        return result.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async Task<Result> DeleteProviderAsync(long id, CancellationToken ct = default)
    {
        try
        {
            await CallAsync(HttpMethod.Delete, "/providers/" + id, null, ct);
            return new(true, "DNS-Anbieter gelöscht.");
        }
        catch (MatcadException ex)
        {
            return new(false, ex.Message.Contains("used by", StringComparison.OrdinalIgnoreCase)
                ? "Der DNS-Anbieter wird noch von einer Domain benutzt." : ex.Message);
        }
    }

    /// <summary>Asks Matcad whether the credentials work against the DNS provider. For an existing provider, empty secret
    /// fields are filled from what Matcad has stored, so a saved provider can be tested without re-entering it.</summary>
    public async Task<Result> TestProviderAsync(long? id, string? type, IReadOnlyDictionary<string, string?>? credentials, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(type)) return new(false, "Bitte einen Anbieter-Typ wählen.");
        try
        {
            var creds = await MergeCredentialsAsync(id, type.Trim(), credentials, ct);
            if (creds is null) return new(false, "DNS-Anbieter nicht gefunden.");
            var r = await CallAsync(HttpMethod.Post, "/providers/test", new { type = type.Trim(), credentials = creds }, ct);
            var ok = r?["ok"]?.GetValue<bool>() == true;
            var msg = StrOrNull(r?["message"]);
            // Matcad can only test some providers; for the rest it says so with ok = false — that is not a failure.
            if (!ok && msg?.Contains("No credential test available", StringComparison.OrdinalIgnoreCase) == true)
                return new(true, "Für diesen Anbieter kann Matcad die Zugangsdaten nicht vorab prüfen – das zeigt sich erst beim Ausstellen eines Zertifikats.");
            return new(ok, ok ? "Zugangsdaten funktionieren." + (msg is null ? "" : " " + msg) : "Test fehlgeschlagen: " + (msg ?? "keine Antwort"));
        }
        catch (MatcadException ex) { return new(false, ex.Message); }
    }

    // ---- routes / domains ----------------------------------------------------------------------------------------

    /// <summary>Creates (no id) or updates a manual route. Fields the cloud's form does not carry (basic auth, own ACME
    /// e-mail, listen port, …) keep what Matcad has, so a route set up in Matcad's UI survives an edit here.</summary>
    public async Task<Result> SaveRouteAsync(RouteInput inp, CancellationToken ct = default)
    {
        var target = string.Equals(inp.Target, "redirect", StringComparison.OrdinalIgnoreCase) ? "redirect" : "proxy";
        var host = (inp.Host ?? "").Trim().ToLowerInvariant();
        if (host.StartsWith("*.")) host = host[2..];
        if (ProxyService.NormaliseDomain(host) is not { } h) return new(false, "Keine gültige Domain (nur ein Name wie shop.example.de).");
        if (target == "proxy" && string.IsNullOrWhiteSpace(inp.Upstream)) return new(false, "Bitte ein Ziel angeben, z. B. http://container:8080.");
        if (target == "redirect" && string.IsNullOrWhiteSpace(inp.FallbackUrl)) return new(false, "Bitte die Adresse angeben, auf die umgeleitet wird.");
        if (inp.Wildcard && inp.ProviderId is null) return new(false, "Eine Wildcard-Domain braucht einen DNS-Anbieter (für das Zertifikat).");
        try
        {
            JsonNode? old = null;
            if (inp.Id is long id)
            {
                if (await GuardAsync(id, ct) is { } denied) return denied;
                old = ((await CallAsync(HttpMethod.Get, "/routes/manual", null, ct))?.AsArray() ?? new JsonArray())
                    .FirstOrDefault(r => r?["id"]?.GetValue<long>() == id);
                if (old is null) return new(false, "Diese Domain gibt es in Matcad nicht (mehr) — oder sie stammt nicht aus der manuellen Liste.");
                // A port binding has no host at all; the cloud's form cannot express it, so it would turn it into a host route.
                if (old["listenPort"] is not null) return new(false, "Routen auf einem eigenen Port bitte in Matcad selbst bearbeiten.");
            }
            var body = new
            {
                id = inp.Id,
                host = h,
                wildcard = inp.Wildcard,
                target,
                upstream = target == "proxy" ? inp.Upstream!.Trim() : null,
                insecureSkipVerify = Bool(old?["insecureSkipVerify"]),
                fallbackUrl = string.IsNullOrWhiteSpace(inp.FallbackUrl) ? null : inp.FallbackUrl.Trim(),
                redirectPermanent = Bool(old?["redirectPermanent"]),
                authenticationId = LongOrNull(old?["authenticationId"]),
                providerId = inp.ProviderId,
                acmeEmail = StrOrNull(old?["acmeEmail"]),
                enabled = inp.Enabled,
                name = string.IsNullOrWhiteSpace(inp.Name) ? h : inp.Name.Trim(),
                allowEmbedding = inp.AllowEmbedding,
                listenPort = (int?)null,
            };
            var r = await CallAsync(HttpMethod.Post, "/routes", body, ct);
            var newId = LongOrNull(r?["route"]?["id"]);
            if (ApplyError(r) is { } err) return new(false, $"Gespeichert, aber Caddy übernahm die Domain nicht: {err}", newId);
            return new(true, inp.Id is null ? $"Domain „{h}“ angelegt." : $"Domain „{h}“ gespeichert.", newId);
        }
        catch (MatcadException ex) { return new(false, ex.Message); }
    }

    public async Task<Result> DeleteRouteAsync(long id, CancellationToken ct = default)
    {
        try
        {
            if (await GuardAsync(id, ct) is { } denied) return denied;
            var r = await CallAsync(HttpMethod.Delete, "/routes/" + id, null, ct);
            return r?["ok"]?.GetValue<bool>() == false
                ? new(false, "Gelöscht, aber Caddy übernahm es nicht: " + (StrOrNull(r["error"]) ?? "unbekannter Fehler"))
                : new(true, "Domain gelöscht.");
        }
        catch (MatcadException ex) { return new(false, ex.Message); }
    }

    /// <summary>A route the cloud created for an instance is changed through that instance — otherwise the instance keeps
    /// a route id that points at something else, or at nothing.</summary>
    private async Task<Result?> GuardAsync(long id, CancellationToken ct)
    {
        var managed = await ManagedRouteIdsAsync(ct);
        return managed.TryGetValue(id.ToString(), out var m)
            ? new(false, $"Diese Domain hat die Cloud für „{m.Label}“ angelegt — bitte dort im Hosting-Tab ändern oder entfernen.")
            : null;
    }
}

public sealed class MatcadException : Exception
{
    public MatcadException(string message) : base(message) { }
}

/// <summary>What the wildcard fields of a host render: its setting and — for a Matcad the cloud reaches — Matcad's DNS
/// providers to choose from (null = free-text name + Caddy credentials).</summary>
public sealed record WildcardFieldsView(ProxyService.WildcardConfig Config, List<MatcadAdminService.Provider>? Providers,
    List<MatcadAdminService.ProviderType>? Types, string? MatcadError);
