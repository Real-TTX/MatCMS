namespace MatCMS.Cloud.Services.Proxy;

/// <summary>One proxy operation, self-contained: the settings of the host it runs on travel with it.</summary>
/// <param name="Op">test | publish | delete | exists</param>
/// <param name="Kind">delete/exists: the provider the route was created with (may differ from the current one).</param>
/// <param name="OldKind">publish: the provider of the route currently on record; a route of a DIFFERENT
/// provider is deleted first (the setting changed since).</param>
/// <param name="Upstream">publish: a fixed target (the edge forwarding to a host address or to node-address:port).
/// Null = derive it from the container, as a host proxy does.</param>
/// <param name="RewriteHost">publish: send the target's host name as the Host header (see IProxyProvider).</param>
public sealed record ProxyOp(
    string Op, ProxySettings Settings,
    string? ContainerId = null, int? LocalPort = null,
    string? Host = null, string? RouteKey = null, string? Name = null,
    string? Kind = null, string? RouteId = null, string? OldKind = null,
    string? Upstream = null, bool RewriteHost = false,
    string? DnsProvider = null, Dictionary<string, string>? Dns = null, List<string>? Trusted = null);

public sealed record ProxyOpResult(bool Ok, string Message, string? RouteId = null, bool? Exists = null);

/// <summary>
/// The part of <see cref="ProxyService"/> that must run WHERE the proxy and the container are: provider calls
/// and the upstream (joining the proxy's Docker network, or the published port). The cloud runs it in-process
/// for its own host; a node-agent runs the very same code for its host (the <c>proxy</c> job) — so the proxy of
/// a node never has to be reachable from the cloud, and the network join happens on the daemon that owns the
/// container. Everything that needs the database (uniqueness, the record on the instance) stays in ProxyService.
/// </summary>
public static class ProxyEngine
{
    public static IProxyProvider Provider(ProxySettings s, HttpClient http, string? kind = null) =>
        ProxyKinds.Normalise(kind ?? s.Kind) switch
        {
            ProxyKinds.Matcad => new MatcadProvider(http, s.MatcadUrl ?? "", s.MatcadToken),
            ProxyKinds.Caddy => new CaddyProvider(http, s.CaddyAdminUrl ?? "", s.CaddyServer),
            _ => new NoProxyProvider(),
        };

    public static async Task<ProxyOpResult> ExecuteAsync(ProxyOp op, HttpClient http, DockerHostService docker, CancellationToken ct = default)
    {
        try
        {
            return op.Op switch
            {
                "test" => await TestAsync(op.Settings, http, docker, ct),
                "publish" => await PublishAsync(op, http, docker, ct),
                "delete" => await DeleteAsync(op, http, ct),
                "exists" => new(true, "", Exists: await Provider(op.Settings, http, op.Kind).ExistsAsync(op.RouteId ?? "", ct)),
                // Wildcard certificate for *.{Host} (Host = the base domain), removing it, trusting the edge.
                "wildcard" => Result(await Provider(op.Settings, http).EnsureWildcardAsync(op.RouteId, op.Host ?? "", op.DnsProvider ?? "",
                    op.Dns ?? new(), op.Upstream ?? "https://" + op.Host, ct), "Wildcard-Zertifikat eingerichtet."),
                "unwildcard" => Result(await Provider(op.Settings, http, op.Kind).DeleteWildcardAsync(op.RouteId ?? "", op.Host ?? "", ct), "Wildcard-Zertifikat entfernt."),
                "trust" => Result(await Provider(op.Settings, http).SetTrustedProxiesAsync(op.Trusted ?? new(), ct), "Edge als vertrauenswürdiger Proxy eingetragen."),
                _ => new(false, $"Unbekannte Proxy-Operation „{op.Op}“."),
            };
        }
        catch (Exception ex) { return new(false, "Proxy-Fehler: " + ex.Message); }
    }

    private static ProxyOpResult Result(ProxyResult r, string okMessage) =>
        r.Ok ? new(true, r.Error ?? okMessage, r.RouteId) : new(false, r.Error ?? "Fehlgeschlagen.", r.RouteId);

    private static async Task<ProxyOpResult> TestAsync(ProxySettings s, HttpClient http, DockerHostService docker, CancellationToken ct)
    {
        var p = Provider(s, http);
        if (!p.ManagesRoutes) return new(true, "Kein Proxy: Instanzen sind über ihren Host-Port erreichbar; Domains werden nur vermerkt.");
        if (p is MatcadProvider && string.IsNullOrWhiteSpace(s.MatcadUrl)) return new(false, "Keine Matcad-Adresse eingetragen.");
        if (p is CaddyProvider && string.IsNullOrWhiteSpace(s.CaddyAdminUrl)) return new(false, "Keine Caddy-Admin-Adresse eingetragen.");

        var r = await p.TestAsync(ct);
        var msg = r.Error ?? "";
        if (s.UpstreamMode == UpstreamModes.Network)
        {
            if (string.IsNullOrWhiteSpace(s.Network)) return new(false, msg + " Kein Proxy-Netz eingetragen (Modus „Netzwerk“).");
            if (!await docker.NetworkExistsAsync(s.Network!, ct)) return new(false, msg + $" Das Docker-Netz „{s.Network}“ existiert nicht.");
            msg += $" Netz „{s.Network}“ vorhanden.";
        }
        else if (string.IsNullOrWhiteSpace(s.UpstreamHost))
            return new(false, msg + " Kein Upstream-Host eingetragen (Modus „Host-Port“).");
        return new(r.Ok, msg.Trim());
    }

    /// <summary>
    /// The address the proxy must forward to — and, in network mode, the step that makes it resolvable: the
    /// container is connected to the proxy's network (live). Container name and port come from the daemon,
    /// never from a display name.
    /// </summary>
    private static async Task<(string? Upstream, string? Error)> UpstreamAsync(ProxySettings s, DockerHostService docker,
        string containerId, int? localPort, CancellationToken ct)
    {
        if (s.UpstreamMode == UpstreamModes.HostPort)
        {
            if (string.IsNullOrWhiteSpace(s.UpstreamHost)) return (null, "Kein Upstream-Host eingetragen (Modus „Host-Port“).");
            if (localPort is null) return (null, "Der Container veröffentlicht keinen Host-Port.");
            return ($"http://{s.UpstreamHost!.Trim()}:{localPort}", null);
        }
        if (string.IsNullOrWhiteSpace(s.Network)) return (null, "Kein Proxy-Netz eingetragen (Modus „Netzwerk“).");
        var (ok, msg, name) = await docker.ConnectToNetworkAsync(containerId, s.Network!, ct);
        if (!ok || string.IsNullOrEmpty(name)) return (null, msg);
        // 8080 is the container's own port (the image EXPOSEs it; never changed) — not the host port.
        return ($"http://{name}:8080", null);
    }

    private static async Task<ProxyOpResult> PublishAsync(ProxyOp op, HttpClient http, DockerHostService docker, CancellationToken ct)
    {
        var s = op.Settings;
        var provider = Provider(s, http);

        // A route left behind by a DIFFERENT provider (the setting changed since) goes first — also when the
        // new setting is "no proxy": the old route must not linger pointing at the site.
        if (op.RouteId is not null && op.OldKind is not null && op.OldKind != s.Kind)
            await Provider(s, http, op.OldKind).DeleteAsync(op.RouteId, ct);
        if (!provider.ManagesRoutes) return new(true, "Kein Proxy — Domain nur vermerkt.");

        var upstream = op.Upstream;
        if (upstream is null)
        {
            if (string.IsNullOrEmpty(op.ContainerId)) return new(false, "Kein Container angegeben.");
            var (derived, err) = await UpstreamAsync(s, docker, op.ContainerId, op.LocalPort, ct);
            if (derived is null) return new(false, err ?? "Upstream nicht ermittelbar.");
            upstream = derived;
        }

        var existing = op.OldKind == s.Kind ? op.RouteId : null;
        var r = await provider.UpsertAsync(existing, op.RouteKey ?? op.ContainerId ?? "", op.Name ?? "MatCMS", op.Host ?? "", upstream, op.RewriteHost, ct);
        return r.Ok ? new(true, $"Route über {s.Kind} angelegt.", r.RouteId)
                    : new(false, r.Error ?? "Route konnte nicht angelegt werden.", r.RouteId);
    }

    private static async Task<ProxyOpResult> DeleteAsync(ProxyOp op, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(op.RouteId)) return new(true, "Keine Route.");
        var r = await Provider(op.Settings, http, op.Kind).DeleteAsync(op.RouteId, ct);
        return r.Ok ? new(true, "Route entfernt.") : new(false, r.Error ?? "Route konnte nicht entfernt werden.");
    }
}
