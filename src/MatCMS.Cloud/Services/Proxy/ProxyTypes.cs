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

/// <summary>What <c>_ProxyFields.cshtml</c> renders — the same fields for "Dieser Host" (Hosting → Einstellungen)
/// and for every node, so the two forms cannot drift. The Matcad key is never rendered, only whether one is set.</summary>
public sealed record ProxyFieldsView(string Kind, string? MatcadUrl, bool MatcadTokenSet, string? CaddyAdminUrl, string? CaddyServer,
    string Upstream, string? Network, string? UpstreamHost);

/// <summary>Outcome of a provider call. <paramref name="RouteId"/> is the provider's own id for the route.</summary>
public sealed record ProxyResult(bool Ok, string? Error = null, string? RouteId = null);

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
    /// provider that cannot do it must refuse rather than route to the wrong site.</summary>
    Task<ProxyResult> UpsertAsync(string? existingId, string routeKey, string name, string host, string upstream, bool rewriteHost = false, CancellationToken ct = default);

    /// <summary>Removes the route. A route that is already gone counts as success.</summary>
    Task<ProxyResult> DeleteAsync(string routeId, CancellationToken ct = default);

    /// <summary>Whether the route still exists at the provider; null when that cannot be told.</summary>
    Task<bool?> ExistsAsync(string routeId, CancellationToken ct = default);
}
