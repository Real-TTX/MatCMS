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

    public async Task<ProxyResult> UpsertAsync(string? existingId, string routeKey, string name, string host, string upstream, bool rewriteHost = false, CancellationToken ct = default)
    {
        // Matcad forwards the visitor's Host header unchanged and has no setting to replace it — a route to another
        // proxy's host-matched route would land on the wrong (or no) site. The cloud then forwards to address:port.
        if (rewriteHost) return new(false, "Matcad kann den Host-Header nicht umschreiben — als Edge leitet Matcad nur auf Node-Adresse:Port weiter.");
        long? id = long.TryParse(existingId, out var n) ? n : null;
        var r = await PostRouteAsync(id, name, host, upstream, ct);
        // The route may have been deleted in Matcad in the meantime — then create it anew instead of failing.
        if (!r.Ok && id is not null && r.Error?.Contains("404") == true)
            r = await PostRouteAsync(null, name, host, upstream, ct);
        return r;
    }

    private async Task<ProxyResult> PostRouteAsync(long? id, string name, string host, string upstream, CancellationToken ct)
    {
        var body = new
        {
            id,
            host,
            wildcard = false,
            target = "proxy",
            upstream,
            insecureSkipVerify = false,
            fallbackUrl = (string?)null,
            redirectPermanent = false,
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
            return new(true, null, routeId);
        }
        catch (Exception ex) { return new(false, $"Matcad nicht erreichbar: {ex.Message}"); }
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
}
