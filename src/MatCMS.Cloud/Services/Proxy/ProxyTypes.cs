namespace MatCMS.Cloud.Services.Proxy;

/// <summary>The provider names, as stored in <c>hosting.mode</c> and on <c>Instance.ProxyProvider</c>.</summary>
public static class ProxyKinds
{
    public const string None = "none";
    public const string Matcad = "matcad";
    public const string Caddy = "caddy";

    /// <summary>Reads the stored mode. The historical value "docker" (and anything unknown) means "none":
    /// the setting that needs nothing installed is the only safe fallback.</summary>
    public static string Normalise(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        Matcad => Matcad,
        Caddy => Caddy,
        _ => None,
    };
}

/// <summary>How the proxy reaches an instance container.</summary>
public static class UpstreamModes
{
    /// <summary>Shared Docker network: container name + port 8080. The cloud connects the container.</summary>
    public const string Network = "network";
    /// <summary>The container's published host port on a configured host.</summary>
    public const string HostPort = "hostport";

    public static string Normalise(string? mode) =>
        string.Equals(mode?.Trim(), HostPort, StringComparison.OrdinalIgnoreCase) ? HostPort : Network;
}

/// <summary>The cloud-wide proxy configuration — today the one implicit node's; in increment 4 each node
/// carries its own copy of exactly this (docs/hosting-platform.md §3.2/§3.4).</summary>
public sealed record ProxySettings(
    string Kind,
    string? MatcadUrl, string? MatcadToken,
    string? CaddyAdminUrl, string CaddyServer,
    string UpstreamMode, string? Network, string? UpstreamHost);

/// <summary>What <c>_ProxyFields.cshtml</c> renders — the same fields for "Dieser Host" (its host page, tab Proxy)
/// and for every node, so the two forms cannot drift. The Matcad key is never rendered, only whether one is set.</summary>
public sealed record ProxyFieldsView(string Kind, string? MatcadUrl, bool MatcadTokenSet, string? CaddyAdminUrl, string? CaddyServer,
    string Upstream, string? Network, string? UpstreamHost);

/// <summary>Outcome of a provider call. <paramref name="RouteId"/> is the provider's own id for the route.</summary>
public sealed record ProxyResult(bool Ok, string? Error = null, string? RouteId = null);

/// <summary>A route that only answers requests carrying <paramref name="HeaderName"/> = <paramref name="HeaderValue"/>; anything
/// else gets 403 before it reaches the site. This is what lets a node's proxy accept ONLY the edge: the edge sends the header on
/// its way to the node's automatic address (<see cref="ProxyRouteOptions.UpstreamHeaders"/>), nobody else knows the value.</summary>
public sealed record RouteGuard(string HeaderName, string HeaderValue)
{
    /// <summary>Why this guard would be worthless or dangerous, or null. Caddy's header matcher reads <c>*</c> as a wildcard and
    /// fills in <c>{…}</c> placeholders, and a short secret can be guessed — none of that may reach a proxy.</summary>
    public string? Problem() => ProxyRouteOptions.HeaderProblem(HeaderName, HeaderValue, minValueLength: 16, visibleAsciiOnly: true);
}

/// <summary>What a route needs beyond host → upstream. All of it is optional, and a provider that cannot do what is asked must
/// REFUSE — a guard that is silently dropped would leave the site open, a missing Host rewrite would land on the wrong site.</summary>
/// <param name="RewriteHost">Send the upstream's host name as the Host header (and as the TLS server name).</param>
/// <param name="UpstreamHeaders">Headers set on the request to the upstream (name → value).</param>
/// <param name="Guard">Only requests with this header get through; the rest is answered 403.</param>
/// <param name="StripHeaders">Headers removed from the request before it reaches the upstream, guard or not — a node route that does not
/// ask for the secret YET (or any more) must still keep it from the site while the edge sends it. Caddy only; a Matcad strips the
/// header of its own guard and has no separate switch.</param>
public sealed record ProxyRouteOptions(bool RewriteHost = false, Dictionary<string, string>? UpstreamHeaders = null, RouteGuard? Guard = null,
    IReadOnlyList<string>? StripHeaders = null)
{
    /// <summary>Anything beyond a plain route — what a provider has to be able to do before it may take the route.</summary>
    public bool NeedsGuardCapability => RewriteHost || UpstreamHeaders is { Count: > 0 } || Guard is not null;

    private static readonly System.Text.RegularExpressions.Regex TokenRx = new(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase) { "host", "content-length", "transfer-encoding", "connection" };

    /// <summary>The problem with a header the cloud is about to put into a proxy, or null. Shared by every provider, so a bad value
    /// is refused the same way wherever it would have ended up.</summary>
    /// <param name="visibleAsciiOnly">For a secret that is COMPARED: only characters 0x21–0x7E. HTTP trims blanks and tabs at the ends of a
    /// header value, so a secret with such a character would never match what the edge sends and would lock everybody out.</param>
    public static string? HeaderProblem(string name, string value, int minValueLength = 1, bool visibleAsciiOnly = false)
    {
        if (!TokenRx.IsMatch(name ?? "")) return $"„{name}“ ist kein gültiger Header-Name.";
        if (Forbidden.Contains(name!)) return $"Der Header „{name}“ darf nicht gesetzt werden.";
        if (string.IsNullOrEmpty(value) || value.Length < minValueLength) return $"Der Wert von „{name}“ ist zu kurz (mindestens {minValueLength} Zeichen).";
        if (value.IndexOfAny(new[] { '*', '{', '}' }) >= 0) return $"Der Wert von „{name}“ darf kein *, {{ oder }} enthalten.";
        if (visibleAsciiOnly ? value.Any(c => c < 0x21 || c > 0x7E) : value.Any(c => c < 0x20 || c == 0x7F) || value != value.Trim())
            return visibleAsciiOnly ? $"Der Wert von „{name}“ darf nur sichtbare ASCII-Zeichen enthalten (kein Leerzeichen, kein Tab, keine Umlaute)."
                                    : $"Der Wert von „{name}“ darf keine Steuerzeichen und kein Leerzeichen am Rand enthalten.";
        return null;
    }

    /// <summary>The first problem of these options, or null — checked by the providers before they touch a proxy.</summary>
    public string? Problem()
    {
        if (Guard?.Problem() is { } g) return g;
        foreach (var (k, v) in UpstreamHeaders ?? new()) if (HeaderProblem(k, v) is { } p) return p;
        foreach (var s in StripHeaders ?? Array.Empty<string>()) if (!TokenRx.IsMatch(s ?? "") || Forbidden.Contains(s!)) return $"„{s}“ kann nicht als zu entfernender Header gesetzt werden.";
        // The guard's header is stripped before the request goes on to the upstream, and a header set towards the upstream under the
        // same name would be stripped with it — a collision means one of the two does nothing.
        if (Guard is { } gd && (UpstreamHeaders ?? new()).Keys.Any(k => string.Equals(k, gd.HeaderName, StringComparison.OrdinalIgnoreCase)))
            return $"Der Header „{gd.HeaderName}“ kann nicht zugleich Pflicht-Header dieser Route und Header zum Upstream sein.";
        return null;
    }
}

/// <summary>
/// One reverse proxy. Every place that makes an instance reachable under a domain goes through this — so
/// "with or without proxy" is a configuration, never a code path that assumes one (guardrail 3).
/// Routes are keyed by the caller's <c>routeKey</c> (stable per instance) and the provider's own id.
/// </summary>
public interface IProxyProvider
{
    string Kind { get; }

    /// <summary>False for <c>none</c>: nothing to create, the domain is only recorded.</summary>
    bool ManagesRoutes { get; }

    /// <summary>Can the provider be reached with the configured address/credentials?</summary>
    Task<ProxyResult> TestAsync(CancellationToken ct = default);

    /// <summary>Creates or updates the route <paramref name="host"/> → <paramref name="upstream"/>
    /// (<c>http://host:port</c>, or <c>https://host</c> when the target is another proxy). <paramref name="existingId"/>
    /// = the id from a previous publish, if any. <paramref name="rewriteHost"/> = send the upstream's host name as the
    /// Host header (the edge forwarding to a host's automatic address — the host proxy matches on THAT name); a
    /// provider that cannot do it must refuse rather than route to the wrong site. <paramref name="options"/> = a guard on the route
    /// and/or headers towards the upstream (the authenticated edge → node hop); its <see cref="ProxyRouteOptions.RewriteHost"/>
    /// counts like <paramref name="rewriteHost"/>.</summary>
    Task<ProxyResult> UpsertAsync(string? existingId, string routeKey, string name, string host, string upstream, bool rewriteHost = false,
        ProxyRouteOptions? options = null, CancellationToken ct = default);

    /// <summary>Whether this provider can rewrite the Host header and carry guards (Caddy always; Matcad from the version that
    /// reports the capability <c>routeGuards</c>). Decides whether the edge may forward to a host's automatic address.</summary>
    Task<bool> CanGuardAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Creates or updates a route that only REDIRECTS <paramref name="host"/> to <paramref name="targetUrl"/>
    /// (permanently, path kept) — a further domain such as www.example.de pointing at the main domain.</summary>
    Task<ProxyResult> UpsertRedirectAsync(string? existingId, string routeKey, string name, string host, string targetUrl, CancellationToken ct = default) =>
        Task.FromResult(new ProxyResult(false, "Dieser Proxy kann keine Umleitungen anlegen."));

    /// <summary>Removes the route. A route that is already gone counts as success.</summary>
    Task<ProxyResult> DeleteAsync(string routeId, CancellationToken ct = default);

    /// <summary>Whether the route still exists at the provider; null when that cannot be told.</summary>
    Task<bool?> ExistsAsync(string routeId, CancellationToken ct = default);

    /// <summary>One certificate for <c>*.{baseDomain}</c> via the DNS challenge. <paramref name="fallbackUrl"/> = where
    /// a request for an unknown name under the wildcard is sent (Matcad needs a target for its wildcard route).</summary>
    Task<ProxyResult> EnsureWildcardAsync(string? existingId, string baseDomain, string dnsProvider,
        IReadOnlyDictionary<string, string> credentials, string fallbackUrl, CancellationToken ct = default) =>
        Task.FromResult(new ProxyResult(false, "Ohne Proxy gibt es keine Zertifikate — auf diesem Host erst einen Proxy einrichten."));

    Task<ProxyResult> DeleteWildcardAsync(string id, string baseDomain, CancellationToken ct = default) => Task.FromResult(new ProxyResult(true));

    /// <summary>Trust <paramref name="ranges"/> (the edge) as a proxy, so the visitor's address survives. Empty = remove.</summary>
    Task<ProxyResult> SetTrustedProxiesAsync(IReadOnlyList<string> ranges, CancellationToken ct = default) =>
        Task.FromResult(new ProxyResult(false, "Dieser Proxy lässt sich von der Cloud aus keine vertrauenswürdigen Proxys setzen."));
}
