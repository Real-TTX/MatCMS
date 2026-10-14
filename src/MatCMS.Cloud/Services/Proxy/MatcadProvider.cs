using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MatCMS.Cloud.Services.Proxy;

/// <summary>
/// Routes through Matcad's REST API (<c>/api/v1/routes</c>, header <c>X-Api-Key</c>) — Matcad writes the
/// route into its Caddy and gets the certificate. NOT through <c>matcad.*</c> container labels: Matcad's
/// label discovery is off by default, labels are fixed at container creation (a domain change would mean
/// recreating the site), and the route it builds from them points at a container name the proxy can only
/// resolve on a shared network nobody set up. The API route is explicit, editable and removable.
/// </summary>
public sealed class MatcadProvider : IProxyProvider
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string? _key;

    // No ACME e-mail here on purpose: Matcad keeps a route's own e-mail only for wildcard routes and
    // otherwise uses its global setting — a field for it in the cloud would do nothing.
    public MatcadProvider(HttpClient http, string baseUrl, string? apiKey)
    {
        _http = http;
        _base = baseUrl.TrimEnd('/');
        _key = apiKey;
    }

    public string Kind => ProxyKinds.Matcad;
    public bool ManagesRoutes => true;

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, _base + path);
        if (!string.IsNullOrEmpty(_key)) r.Headers.Add("X-Api-Key", _key);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private static string Explain(HttpStatusCode s, string body) => s switch
    {
        HttpStatusCode.Unauthorized => "Matcad lehnt den API-Schlüssel ab.",
        HttpStatusCode.ServiceUnavailable => "Matcad hat kein API aktiviert (Matcad__ApiKey nicht gesetzt).",
        _ => $"Matcad antwortete {(int)s}: {Trim(body)}",
    };

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // A Matcad from before its REST API (08/2026) — or any other web app at that address — answers
    // /api/v1/* with a redirect to its login page, which HttpClient follows to a 200 HTML page. Without
    // this check that surfaced as a JSON parser error ("'<' is an invalid start of a value").
    private string? NotJson(HttpResponseMessage resp) =>
        resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? null
            : $"Unter {_base} antwortet kein Matcad-API (HTML statt JSON) — falsche Adresse oder eine Matcad-Version ohne REST-API?";

    public async Task<ProxyResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/v1/status"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new(false, $"Unter {_base} gibt es kein Matcad-API (/api/v1/status: 404) — falsche Adresse?");
            if (!resp.IsSuccessStatusCode) return new(false, Explain(resp.StatusCode, text));
            if (NotJson(resp) is { } nj) return new(false, nj);
            using var doc = JsonDocument.Parse(text);
            var bd = doc.RootElement.TryGetProperty("baseDomain", out var b) ? b.GetString() : null;
            return new(true, string.IsNullOrEmpty(bd) ? "Matcad erreichbar." : $"Matcad erreichbar (Basis-Domain {bd}).");
        }
        catch (Exception ex) { return new(false, $"Matcad nicht erreichbar: {ex.Message}"); }
    }

    // What a Matcad can do is asked of /api/v1/status ("capabilities": ["routeGuards"], Matcad 0.9). The answer is kept for a short
    // while — the cloud asks on every publish and the answer only changes when somebody updates Matcad.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Can, DateTime Until)> GuardCache = new();

    /// <summary>The last known answer of <see cref="CanGuardAsync"/> for this address — false when never asked or stale. For
    /// places that cannot await (a status page); the decision to publish always asks.</summary>
    public static bool CachedCanGuard(string? baseUrl) =>
        !string.IsNullOrWhiteSpace(baseUrl) && GuardCache.TryGetValue(baseUrl.TrimEnd('/'), out var c) && c.Until > DateTime.UtcNow && c.Can;

    /// <summary>Whether this Matcad can rewrite the Host header and carry guards. An old Matcad ignores the unknown JSON fields
    /// without a word — which would publish a route that is SUPPOSED to be guarded as an open one — so "no capability" is asked
    /// for explicitly and means no.</summary>
    public async Task<bool> CanGuardAsync(CancellationToken ct = default) => (await GuardCapabilityAsync(ct)).Can == true;

    /// <summary>The answer with its third state: <c>null</c> = could not be asked (unreachable, wrong key, not a Matcad) together with the
    /// reason — an operator told "Matcad too old" when the real problem is a dead port goes and updates the wrong thing. Only definite
    /// answers are cached.</summary>
    private async Task<(bool? Can, string? Error)> GuardCapabilityAsync(CancellationToken ct)
    {
        if (GuardCache.TryGetValue(_base, out var c) && c.Until > DateTime.UtcNow) return (c.Can, null);
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/v1/status"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (null, resp.StatusCode == HttpStatusCode.NotFound ? $"Unter {_base} gibt es kein Matcad-API (/api/v1/status: 404) — falsche Adresse?" : Explain(resp.StatusCode, text));
            if (NotJson(resp) is { } nj) return (null, nj);
            using var doc = JsonDocument.Parse(text);
            var can = doc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                      && caps.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == "routeGuards");
            GuardCache[_base] = (can, DateTime.UtcNow.AddSeconds(can ? 120 : 20));
            return (can, null);
        }
        catch (Exception ex) { return (null, $"Matcad nicht erreichbar: {ex.Message}"); }
    }

    public async Task<ProxyResult> UpsertAsync(string? existingId, string routeKey, string name, string host, string upstream, bool rewriteHost = false,
        ProxyRouteOptions? options = null, CancellationToken ct = default)
    {
        if (options?.Problem() is { } problem) return new(false, problem);
        rewriteHost |= options?.RewriteHost == true;
        // Matcad before 0.9 forwards the visitor's Host header unchanged and cannot guard a route — a route to another proxy's
        // host-matched route would land on the wrong (or no) site, and a guard would be dropped silently. Refuse instead.
        if (rewriteHost || options?.NeedsGuardCapability == true)
        {
            var (can, why) = await GuardCapabilityAsync(ct);
            if (can is null) return new(false, why);   // "not reachable" is not "too old"
            if (can == false)
                return new(false, "Dieses Matcad kann den Host-Header nicht umschreiben und keine Zugriffsregeln (Pflicht-Header) setzen — es braucht die Fähigkeit „routeGuards“ (Matcad ab 0.9). Bis dahin leitet Matcad als Edge nur auf Node-Adresse:Port weiter.");
        }
        long? id = long.TryParse(existingId, out var n) ? n : await ManualIdForHostAsync(host, ct);
        var r = await PostRouteAsync(id, name, host, upstream, ct, null, rewriteHost, options);
        // The route may have been deleted in Matcad in the meantime — then create it anew instead of failing.
        if (!r.Ok && id is not null && r.Error?.Contains("404") == true)
            r = await PostRouteAsync(null, name, host, upstream, ct, null, rewriteHost, options);
        return r;
    }

    /// <summary>
    /// The id of a manual route Matcad already has for <paramref name="host"/>. A POST without an id does NOT take such a
    /// route over: Matcad stores a second one for the same host and only shows one of them — the other resurfaces the
    /// moment the cloud's route is deleted (seen with www routes set up by hand before the cloud managed them). So the
    /// cloud adopts it by id instead.
    /// </summary>
    private async Task<long?> ManualIdForHostAsync(string host, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/v1/routes/manual"), ct);
            if (!resp.IsSuccessStatusCode || NotJson(resp) is not null) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.TryGetProperty("host", out var h) && string.Equals(h.GetString(), host, StringComparison.OrdinalIgnoreCase)
                    && !(e.TryGetProperty("wildcard", out var w) && w.ValueKind == JsonValueKind.True)
                    && e.TryGetProperty("id", out var id) && id.TryGetInt64(out var v))
                    return v;
        }
        catch { }
        return null;
    }

    /// <summary>A redirect route (Matcad target "redirect", permanent). Without an id Matcad updates a route that already
    /// has the host — so a www route someone set up by hand is taken over, not duplicated.</summary>
    public async Task<ProxyResult> UpsertRedirectAsync(string? existingId, string routeKey, string name, string host, string targetUrl, CancellationToken ct = default)
    {
        long? id = long.TryParse(existingId, out var n) ? n : await ManualIdForHostAsync(host, ct);
        // Matcad writes the URL verbatim into Caddy's Location header, where Caddy fills in placeholders — so the visitor's
        // path and query survive (www.example.de/impressum → example.de/impressum instead of the home page).
        var location = targetUrl.TrimEnd('/') + "{http.request.uri}";
        var r = await PostRouteAsync(id, name, host, null, ct, location);
        if (!r.Ok && id is not null && r.Error?.Contains("404") == true) r = await PostRouteAsync(null, name, host, null, ct, location);
        // Matcad 0.6 validates the target as a plain URL and refuses the placeholder ("must be an http:// or https://
        // URL"). Then the redirect goes to the bare target — it works, the path is just not kept — instead of none at all.
        if (!r.Ok && r.Error?.Contains("redirect target", StringComparison.OrdinalIgnoreCase) == true)
            r = await PostRouteAsync(id, name, host, null, ct, targetUrl);
        return r;
    }

    private async Task<ProxyResult> PostRouteAsync(long? id, string name, string host, string? upstream, CancellationToken ct, string? redirectTo = null,
        bool rewriteHost = false, ProxyRouteOptions? options = null)
    {
        // The four route properties are ALWAYS sent, null when unused: Matcad keeps a property that is missing from the body and
        // clears one that is null — so dropping a guard (or the edge's header) means saying so. A Matcad without them ignores them.
        string? upstreamHost = null;
        // IdnHost: a name with an umlaut goes over the wire as punycode, which is what Matcad accepts as a plain host name.
        if (rewriteHost && upstream is not null && Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri)) upstreamHost = upstreamUri.IdnHost;
        var upstreamHeaders = options?.UpstreamHeaders is { Count: > 0 } uh ? uh.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray() : null;
        var requireHeader = options?.Guard is { } g ? new { name = g.HeaderName, value = g.HeaderValue } : null;
        var body = new
        {
            upstreamHost,
            upstreamHeaders,
            requireHeader,
            allowedIps = (string[]?)null,
            id,
            host,
            wildcard = false,
            target = redirectTo is null ? "proxy" : "redirect",
            upstream,
            insecureSkipVerify = false,
            fallbackUrl = redirectTo,
            redirectPermanent = redirectTo is not null,
            authenticationId = (long?)null,
            providerId = (long?)null,
            acmeEmail = (string?)null,
            enabled = true,
            name,
            allowEmbedding = false,
            listenPort = (int?)null,
        };
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Post, "/api/v1/routes", body), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return new(false, Explain(resp.StatusCode, text));
            if (NotJson(resp) is { } nj) return new(false, nj);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var routeId = root.TryGetProperty("route", out var route) && route.TryGetProperty("id", out var rid)
                ? rid.ToString() : null;
            // Matcad stores the route even when writing it into Caddy failed; hand the id back so the
            // route can still be removed, but report the failure — the site is not reachable yet.
            if (root.TryGetProperty("applied", out var applied) && applied.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                var err = applied.TryGetProperty("error", out var e) ? e.GetString() : null;
                return new(false, $"Route gespeichert, aber Caddy übernahm sie nicht: {err}", routeId);
            }

            // Trust, but check: a route that was meant to be guarded must come back WITH its guard. If it did not (a Matcad that
            // answered "routeGuards" but stored something else), the route is removed again rather than left open.
            if (rewriteHost || options?.NeedsGuardCapability == true)
                if (Unverified(route, rewriteHost ? upstreamHost : null, options) is { } missing)
                {
                    // The id Matcad answered with, else the one we posted to — and when neither exists, the message must not claim a removal.
                    var removable = routeId ?? id?.ToString();
                    var removed = removable is not null && (await DeleteAsync(removable, ct)).Ok;
                    return new(false, "Matcad hat die Zugriffsregeln der Route nicht übernommen (" + missing + ") — "
                        + (removed ? "die Route wurde wieder entfernt, damit sie nicht offen bleibt." : "die Route ließ sich NICHT entfernen, bitte in Matcad prüfen und löschen."));
                }
            return new(true, null, routeId);
        }
        catch (Exception ex) { return new(false, $"Matcad nicht erreichbar: {ex.Message}"); }
    }

    /// <summary>What of the requested host rewrite / headers / guard is missing from the route Matcad answered with (null = all
    /// there). Matcad never returns the secret values, only that they are set.</summary>
    private static string? Unverified(JsonElement route, string? upstreamHost, ProxyRouteOptions? options)
    {
        if (route.ValueKind != JsonValueKind.Object) return "keine Route in der Antwort";
        if (upstreamHost is not null && !(route.TryGetProperty("upstreamHost", out var h) && string.Equals(h.GetString(), upstreamHost, StringComparison.OrdinalIgnoreCase)))
            return "Host-Header";
        if (options?.Guard is { } g && !(route.TryGetProperty("requireHeader", out var rh) && rh.ValueKind == JsonValueKind.Object
                && string.Equals(rh.TryGetProperty("name", out var rn) ? rn.GetString() : null, g.HeaderName, StringComparison.OrdinalIgnoreCase)
                && rh.TryGetProperty("valueSet", out var vs) && vs.ValueKind == JsonValueKind.True))
            return "Pflicht-Header";
        if (options?.UpstreamHeaders is { Count: > 0 } uh)
        {
            var have = route.TryGetProperty("upstreamHeaders", out var uhe) && uhe.ValueKind == JsonValueKind.Array
                ? uhe.EnumerateArray().Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null).ToHashSet(StringComparer.OrdinalIgnoreCase) : new();
            if (!uh.Keys.All(k => have.Contains(k))) return "Upstream-Header";
        }
        return null;
    }

    public async Task<ProxyResult> DeleteAsync(string routeId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Delete, "/api/v1/routes/" + Uri.EscapeDataString(routeId)), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new(true);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode ? new(true) : new(false, Explain(resp.StatusCode, text));
        }
        catch (Exception ex) { return new(false, $"Matcad nicht erreichbar: {ex.Message}"); }
    }

    public async Task<bool?> ExistsAsync(string routeId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.SendAsync(Req(HttpMethod.Get, "/api/v1/routes/manual"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("routes", out var r) ? r : default;
            if (list.ValueKind != JsonValueKind.Array) return null;
            return list.EnumerateArray().Any(e => e.TryGetProperty("id", out var id) && id.ToString() == routeId);
        }
        catch { return null; }
    }

    /// <summary>
    /// Matcad does wildcards itself: a wildcard route <c>*.{baseDomain}</c> with one of ITS DNS providers gets one
    /// certificate, and Matcad serves every concrete host route under it from that certificate. The provider and its
    /// credentials are set up in Matcad (they stay there); <paramref name="dnsProvider"/> names it. Unknown names under
    /// the wildcard are redirected to <paramref name="fallbackUrl"/>.
    /// </summary>
    public async Task<ProxyResult> EnsureWildcardAsync(string? existingId, string baseDomain, string dnsProvider,
        IReadOnlyDictionary<string, string> credentials, string fallbackUrl, CancellationToken ct = default)
    {
        try
        {
            using var req = Req(HttpMethod.Get, "/api/v1/providers");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return new(false, $"Matcad antwortete {(int)res.StatusCode} auf /providers.");
            var list = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsArray();
            var wanted = dnsProvider.Trim();
            var match = list?.FirstOrDefault(p => string.Equals(p?["name"]?.GetValue<string>(), wanted, StringComparison.OrdinalIgnoreCase))
                        ?? list?.FirstOrDefault(p => string.Equals(p?["type"]?.GetValue<string>(), wanted, StringComparison.OrdinalIgnoreCase));
            if (match?["id"] is not { } pid) return new(false, $"In Matcad gibt es keinen DNS-Anbieter „{wanted}“ — dort unter Provider anlegen und hier seinen Namen eintragen.");

            long? id = long.TryParse(existingId, out var n) ? n : null;
            var body = new
            {
                id, host = "*." + baseDomain, wildcard = true, target = "redirect", upstream = (string?)null, insecureSkipVerify = false,
                fallbackUrl, redirectPermanent = false, authenticationId = (long?)null, providerId = pid.GetValue<long>(),
                acmeEmail = (string?)null, enabled = true, name = "MatCMS Wildcard " + baseDomain,
            };
            using var post = Req(HttpMethod.Post, "/api/v1/routes", body);
            using var pr = await _http.SendAsync(post, ct);
            var text = await pr.Content.ReadAsStringAsync(ct);
            if (!pr.IsSuccessStatusCode)
            {
                string? err = null;
                try { err = System.Text.Json.Nodes.JsonNode.Parse(text)?["error"]?.GetValue<string>(); } catch { }
                return new(false, err is not null ? $"Matcad lehnt die Wildcard ab: {err}" : Explain(pr.StatusCode, text));
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            var rid = node?["route"]?["id"]?.ToString();
            // Stored, but Caddy refused the config — typically the DNS module missing from Matcad's Caddy.
            if (node?["applied"]?["ok"]?.GetValue<bool>() == false)
                return new(false, $"Wildcard in Matcad gespeichert, aber Caddy übernahm sie nicht: {node["applied"]?["error"]?.GetValue<string>()}", rid);
            return new(true, null, rid);
        }
        catch (Exception ex) { return new(false, $"Matcad nicht erreichbar: {ex.Message}"); }
    }

    public Task<ProxyResult> DeleteWildcardAsync(string id, string baseDomain, CancellationToken ct = default) => DeleteAsync(id, ct);

    public Task<ProxyResult> SetTrustedProxiesAsync(IReadOnlyList<string> ranges, CancellationToken ct = default) =>
        Task.FromResult(new ProxyResult(false, "Matcad lässt sich die vertrauenswürdigen Proxys nicht von außen setzen — in Matcad selbst einstellen."));
}
