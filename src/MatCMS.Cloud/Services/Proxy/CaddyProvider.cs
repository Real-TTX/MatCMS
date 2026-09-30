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

    // ---- Wildcard certificate (DNS-01) and trusted proxies ---------------------------------------------------
    // Surgical like the routes: only objects with our own @id are created/removed, and the few server/app fields
    // touched (automatic_https.prefer_wildcard, trusted_proxies) are set by path, never by replacing the config.

    public static string WildcardIdFor(string baseDomain) => "matcms-wildcard-" + baseDomain.Replace('.', '-');

    /// <summary>
    /// One certificate for <c>*.{baseDomain}</c> via the DNS challenge: an automation policy with the DNS module and
    /// its credentials, the wildcard on the "automate" list (so Caddy obtains it without waiting for a request), and
    /// <c>automatic_https.prefer_wildcard</c> on the server so each host route under it uses THAT certificate instead
    /// of getting its own. The DNS module must be compiled into this Caddy (xcaddy --with github.com/caddy-dns/…);
    /// Caddy refuses the policy otherwise, which is reported as such.
    /// </summary>
    public async Task<ProxyResult> EnsureWildcardAsync(string? existingId, string baseDomain, string dnsProvider,
        IReadOnlyDictionary<string, string> credentials, string fallbackUrl, CancellationToken ct = default)
    {
        var id = WildcardIdFor(baseDomain);
        var subject = "*." + baseDomain;
        var provider = new JsonObject { ["name"] = dnsProvider.Trim().ToLowerInvariant() };
        foreach (var (k, v) in credentials) provider[k] = v;
        var policy = new JsonObject
        {
            ["@id"] = id,
            ["subjects"] = new JsonArray(subject),
            ["issuers"] = new JsonArray(new JsonObject
            {
                ["module"] = "acme",
                ["challenges"] = new JsonObject { ["dns"] = new JsonObject { ["provider"] = provider } },
            }),
        };
        try
        {
            // 1) the policy — replace ours in place, else append (creating the path level by level).
            var (gs, _) = await SendAsync(HttpMethod.Get, "/id/" + id, null, ct);
            ProxyResult r;
            if (gs == HttpStatusCode.OK)
            {
                var (ps, pb) = await SendAsync(HttpMethod.Patch, "/id/" + id, policy.ToJsonString(), ct);
                r = ps == HttpStatusCode.OK ? new(true) : new(false, DnsErr(ps, pb, dnsProvider));
            }
            else r = await AppendAsync("/config/apps/tls/automation/policies", policy,
                ("/config/apps/tls/automation", new JsonObject { ["policies"] = new JsonArray(policy.DeepClone()) }),
                ("/config/apps/tls", new JsonObject { ["automation"] = new JsonObject { ["policies"] = new JsonArray(policy.DeepClone()) } }), ct);
            if (!r.Ok) return r.Error?.Contains("DNS-Modul") == true ? r : new(false, DnsErr(HttpStatusCode.BadRequest, r.Error ?? "", dnsProvider));

            // 2) have Caddy obtain the wildcard right away.
            var (ls, lb) = await SendAsync(HttpMethod.Get, "/config/apps/tls/certificates/automate", null, ct);
            var list = ls == HttpStatusCode.OK && !IsNullBody(lb) ? JsonNode.Parse(lb)?.AsArray() : null;
            if (list is null || !list.Any(x => x?.GetValue<string>() == subject))
            {
                var a = await AppendAsync("/config/apps/tls/certificates/automate", JsonValue.Create(subject)!,
                    ("/config/apps/tls/certificates", new JsonObject { ["automate"] = new JsonArray(subject) }), ct);
                if (!a.Ok) return a;
            }

            // 3) host routes under the wildcard use it instead of fetching their own (Caddy ≥ 2.8).
            var (ws, wb) = await SendAsync(HttpMethod.Put, ServerPath + "/automatic_https/prefer_wildcard", "true", ct);
            if (ws != HttpStatusCode.OK)
            {
                var (w2, w2b) = await SendAsync(HttpMethod.Put, ServerPath + "/automatic_https", "{\"prefer_wildcard\":true}", ct);
                // Caddy 2.8/2.9 have the switch; newer versions dropped it and use a managed wildcard for the names
                // under it on their own — their "unknown field" is not a failure. Anything else is reported.
                if (w2 != HttpStatusCode.OK && !w2b.Contains("prefer_wildcard", StringComparison.OrdinalIgnoreCase))
                    return new(true, $"Wildcard-Richtlinie angelegt, aber prefer_wildcard nicht gesetzt ({Err(w2, w2b)}).", id);
            }
            return new(true, null, id);
        }
        catch (Exception ex) { return new(false, $"Caddy-Admin-API nicht erreichbar: {ex.Message}"); }
    }

    public async Task<ProxyResult> DeleteWildcardAsync(string id, string baseDomain, CancellationToken ct = default)
    {
        try
        {
            var (s, b) = await SendAsync(HttpMethod.Delete, "/id/" + id, null, ct);
            if (!(s == HttpStatusCode.OK || s == HttpStatusCode.NotFound || b.Contains("unknown object ID", StringComparison.OrdinalIgnoreCase)))
                return new(false, Err(s, b));
            var subject = "*." + baseDomain;
            var (ls, lb) = await SendAsync(HttpMethod.Get, "/config/apps/tls/certificates/automate", null, ct);
            if (ls == HttpStatusCode.OK && !IsNullBody(lb) && JsonNode.Parse(lb)?.AsArray() is { } list)
                for (var i = list.Count - 1; i >= 0; i--)
                    if (list[i]?.GetValue<string>() == subject)
                        await SendAsync(HttpMethod.Delete, $"/config/apps/tls/certificates/automate/{i}", null, ct);
            return new(true);
        }
        catch (Exception ex) { return new(false, $"Caddy-Admin-API nicht erreichbar: {ex.Message}"); }
    }

    /// <summary>
    /// Makes the server trust <paramref name="ranges"/> (the edge) as a proxy: Caddy then keeps the visitor's address
    /// the edge put in X-Forwarded-For instead of replacing it with the edge's own. Empty = remove the setting. Only
    /// the addresses named — trusting more would let anybody claim any address.
    /// </summary>
    public async Task<ProxyResult> SetTrustedProxiesAsync(IReadOnlyList<string> ranges, CancellationToken ct = default)
    {
        try
        {
            var (ss, sb) = await SendAsync(HttpMethod.Get, ServerPath, null, ct);
            if (ss != HttpStatusCode.OK || IsNullBody(sb)) return new(true, "Server noch nicht angelegt — wird beim nächsten Veröffentlichen gesetzt.");
            if (ranges.Count == 0)
            {
                await SendAsync(HttpMethod.Delete, ServerPath + "/trusted_proxies", null, ct);
                return new(true);
            }
            var body = new JsonObject { ["source"] = "static", ["ranges"] = new JsonArray(ranges.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) }.ToJsonString();
            var (ps, pb) = await SendAsync(HttpMethod.Put, ServerPath + "/trusted_proxies", body, ct);
            if (ps != HttpStatusCode.OK) (ps, pb) = await SendAsync(HttpMethod.Patch, ServerPath + "/trusted_proxies", body, ct);
            return ps == HttpStatusCode.OK ? new(true) : new(false, Err(ps, pb));
        }
        catch (Exception ex) { return new(false, $"Caddy-Admin-API nicht erreichbar: {ex.Message}"); }
    }

    /// <summary>POSTs <paramref name="item"/> onto the array at <paramref name="path"/>; when a level of the path does
    /// not exist yet, PUTs the given fallback object at the first level that works instead.</summary>
    private async Task<ProxyResult> AppendAsync(string path, JsonNode item, (string Path, JsonObject Body) level1, CancellationToken ct) =>
        await AppendAsync(path, item, level1, null, ct);

    private async Task<ProxyResult> AppendAsync(string path, JsonNode item, (string Path, JsonObject Body) level1,
        (string Path, JsonObject Body)? level2, CancellationToken ct)
    {
        var (s, b) = await SendAsync(HttpMethod.Post, path, item.ToJsonString(), ct);
        if (s == HttpStatusCode.OK) return new(true);
        // Only a MISSING path is a reason to create a level higher up; anything else (Caddy refusing the content,
        // e.g. an unknown field of a DNS module) is the answer and must not be buried under a follow-up error.
        var missing = s == HttpStatusCode.NotFound || b.Contains("invalid traversal path", StringComparison.OrdinalIgnoreCase)
                      || b.Contains("not found", StringComparison.OrdinalIgnoreCase) || IsNullBody(b);
        if (!missing) return new(false, Err(s, b));
        var (s1, b1) = await SendAsync(HttpMethod.Put, level1.Path, level1.Body.ToJsonString(), ct);
        if (s1 == HttpStatusCode.OK) return new(true);
        if (level2 is { } l2)
        {
            var (s2, b2) = await SendAsync(HttpMethod.Put, l2.Path, l2.Body.ToJsonString(), ct);
            if (s2 == HttpStatusCode.OK) return new(true);
            return new(false, Err(s2, b2));
        }
        return new(false, Err(s1, b1));
    }

    private static string DnsErr(HttpStatusCode s, string body, string provider)
    {
        if (body.Contains("unknown module", StringComparison.OrdinalIgnoreCase) || body.Contains("module not registered", StringComparison.OrdinalIgnoreCase))
            return $"Dieser Caddy kennt das DNS-Modul „{provider}“ nicht — Caddy mit dem Modul bauen (xcaddy build --with github.com/caddy-dns/{provider}, oder im offiziellen Image: caddy add-package github.com/caddy-dns/{provider}).";
        // The field names differ per module (hetzner: auth_api_token, cloudflare: api_token …) — say which one it refused.
        var m = System.Text.RegularExpressions.Regex.Match(body, @"unknown field (?:&quot;|"")([^&""]+)");
        if (m.Success)
            return $"Das DNS-Modul „{provider}“ kennt das Feld „{m.Groups[1].Value}“ nicht — die Feldnamen stehen in der Beschreibung des Moduls (github.com/caddy-dns/{provider}).";
        return body.StartsWith("Caddy") ? body : Err(s, body);
    }
}
