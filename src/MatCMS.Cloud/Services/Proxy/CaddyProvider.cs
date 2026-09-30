using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MatCMS.Cloud.Services.Proxy;

/// <summary>
/// Routes straight through a Caddy's admin API — no Matcad in between. Surgical on purpose: the cloud never
/// replaces Caddy's configuration (<c>/load</c> would wipe every route somebody else put there, and could
/// even reset the admin listener it is talking to). Each route carries an <c>@id</c>
/// (<c>matcms-&lt;instance&gt;</c>) and is created, replaced and deleted through <c>/id/…</c>; new routes are
/// INSERTED at the top of the server's list, so an existing catch-all route cannot swallow them.
/// <para>TLS: Caddy obtains the certificate for a host-matched route by itself — provided the configured
/// server listens on :443. A server the cloud has to create gets exactly that.</para>
/// </summary>
public sealed class CaddyProvider : IProxyProvider
{
    private readonly HttpClient _http;
    private readonly string _admin;
    private readonly string _server;

    public CaddyProvider(HttpClient http, string adminUrl, string server)
    {
        _http = http;
        _admin = adminUrl.TrimEnd('/');
        _server = string.IsNullOrWhiteSpace(server) ? "srv0" : server.Trim();
    }

    public string Kind => ProxyKinds.Caddy;
    public bool ManagesRoutes => true;

    public static string RouteIdFor(string routeKey) => "matcms-" + routeKey;

    private string ServerPath => $"/config/apps/http/servers/{Uri.EscapeDataString(_server)}";

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod m, string path, string? json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, _admin + path);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static bool IsNullBody(string body) => string.IsNullOrWhiteSpace(body) || body.Trim() == "null";

    private static string Err(HttpStatusCode s, string body)
    {
        try { var e = JsonNode.Parse(body)?["error"]?.GetValue<string>(); if (!string.IsNullOrEmpty(e)) return $"Caddy ({(int)s}): {e}"; } catch { }
        return $"Caddy antwortete {(int)s}: {(body.Length > 300 ? body[..300] + "…" : body)}";
    }

    public async Task<ProxyResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var (s, body) = await SendAsync(HttpMethod.Get, ServerPath, null, ct);
            if (s == HttpStatusCode.OK && !IsNullBody(body))
            {
                var listen = JsonNode.Parse(body)?["listen"]?.AsArray().Select(x => x?.GetValue<string>()).ToList() ?? new();
                var tls = listen.Any(l => l is not null && l.EndsWith(":443", StringComparison.Ordinal));
                return new(true, tls
                    ? $"Caddy erreichbar, Server „{_server}“ lauscht auf {string.Join(", ", listen)}."
                    : $"Caddy erreichbar, aber Server „{_server}“ lauscht nicht auf :443 ({string.Join(", ", listen)}) — ohne :443 kein automatisches HTTPS.");
            }
            if (s is HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return new(true, $"Caddy erreichbar; Server „{_server}“ existiert noch nicht und wird beim ersten Veröffentlichen angelegt (auf :443).");
            return new(false, Err(s, body));
        }
        catch (Exception ex) { return new(false, $"Caddy-Admin-API nicht erreichbar: {ex.Message}"); }
    }

    public async Task<ProxyResult> UpsertAsync(string? existingId, string routeKey, string name, string host, string upstream, bool rewriteHost = false, CancellationToken ct = default)
    {
        // Keep an id we already own: a route created at provisioning was keyed by the container name, and
        // re-deriving it from the instance id later would add a SECOND route for the same host.
        var id = !string.IsNullOrWhiteSpace(existingId) && existingId.StartsWith("matcms-", StringComparison.Ordinal)
            ? existingId : RouteIdFor(routeKey);
        // Caddy's reverse_proxy wants "host:port", not a URL. An https:// target (another proxy — the edge forwarding
        // to a host's automatic address) is dialled on 443 with TLS; the certificate is checked against that name.
        var https = upstream.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var target = upstream.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var targetHost = target.Contains(':') ? target[..target.LastIndexOf(':')] : target;
        var dial = https && !target.Contains(':') ? target + ":443" : target;
        var proxy = new JsonObject
        {
            ["handler"] = "reverse_proxy",
            ["upstreams"] = new JsonArray(new JsonObject { ["dial"] = dial }),
        };
        if (https)
            proxy["transport"] = new JsonObject { ["protocol"] = "http", ["tls"] = new JsonObject { ["server_name"] = targetHost } };
        // The host proxy matches on its own name for the site, not on the customer's; the customer's name still
        // travels as X-Forwarded-Host, which Caddy sets by itself.
        if (rewriteHost)
            proxy["headers"] = new JsonObject { ["request"] = new JsonObject { ["set"] = new JsonObject { ["Host"] = new JsonArray(targetHost) } } };
        var route = new JsonObject
        {
            ["@id"] = id,
            ["match"] = new JsonArray(new JsonObject { ["host"] = new JsonArray(host) }),
            ["handle"] = new JsonArray(proxy),
            ["terminal"] = true,
        }.ToJsonString();

        try
        {
            // Replace in place when our route already exists.
            var (gs, _) = await SendAsync(HttpMethod.Get, "/id/" + id, null, ct);
            if (gs == HttpStatusCode.OK)
            {
                var (ps, pb) = await SendAsync(HttpMethod.Patch, "/id/" + id, route, ct);
                return ps == HttpStatusCode.OK ? new(true, null, id) : new(false, Err(ps, pb));
            }

            // Otherwise make sure the server (and its route list) exists, then insert at the top.
            var (ss, sb) = await SendAsync(HttpMethod.Get, ServerPath, null, ct);
            if (ss != HttpStatusCode.OK || IsNullBody(sb))
            {
                var server = new JsonObject { ["listen"] = new JsonArray(":443"), ["routes"] = new JsonArray(JsonNode.Parse(route)) }.ToJsonString();
                var (cs, cb) = await SendAsync(HttpMethod.Put, ServerPath, server, ct);
                return cs == HttpStatusCode.OK ? new(true, null, id)
                    : new(false, Err(cs, cb) + " — Server ließ sich nicht anlegen; bitte in Caddy einrichten oder den Servernamen anpassen.");
            }
            if (JsonNode.Parse(sb)?["routes"] is null)
            {
                var (rs, rb) = await SendAsync(HttpMethod.Put, ServerPath + "/routes", "[" + route + "]", ct);
                return rs == HttpStatusCode.OK ? new(true, null, id) : new(false, Err(rs, rb));
            }
            var (is_, ib) = await SendAsync(HttpMethod.Put, ServerPath + "/routes/0", route, ct);
            return is_ == HttpStatusCode.OK ? new(true, null, id) : new(false, Err(is_, ib));
        }
        catch (Exception ex) { return new(false, $"Caddy-Admin-API nicht erreichbar: {ex.Message}"); }
    }

    public async Task<ProxyResult> DeleteAsync(string routeId, CancellationToken ct = default)
    {
        try
        {
            var (s, b) = await SendAsync(HttpMethod.Delete, "/id/" + routeId, null, ct);
            // Caddy answers an unknown @id with 404 (older versions: 400/500 "unknown object ID") — gone is gone.
            if (s == HttpStatusCode.OK || s == HttpStatusCode.NotFound || b.Contains("unknown object ID", StringComparison.OrdinalIgnoreCase))
                return new(true);
            return new(false, Err(s, b));
        }
        catch (Exception ex) { return new(false, $"Caddy-Admin-API nicht erreichbar: {ex.Message}"); }
    }

    public async Task<bool?> ExistsAsync(string routeId, CancellationToken ct = default)
    {
        try
        {
            var (s, b) = await SendAsync(HttpMethod.Get, "/id/" + routeId, null, ct);
            if (s == HttpStatusCode.OK && !IsNullBody(b)) return true;
            if (s == HttpStatusCode.NotFound || b.Contains("unknown object ID", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }
        catch { return null; }
    }
}
