using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Nodes;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services.Proxy;

/// <summary>A route created at provisioning, waiting for its instance to enroll (see
/// <see cref="InstanceService.PendingRouteKey"/>; adopted in <c>InstanceService.ClassifyAsync</c>).</summary>
/// <param name="Domain">The customer domain, or null when only a host address was created.</param>
/// <param name="Via">"host" | "edge" — where <paramref name="RouteId"/> lives. Null (older rows) = host.</param>
public sealed record PendingRoute(string? Domain, string Provider, string? RouteId, bool PushCanonical,
    string? HostDomain = null, string? HostProvider = null, string? HostRouteId = null, string? Via = null);

/// <summary>The two ways a customer domain is routed (<see cref="Instance.ProxyVia"/>).</summary>
public static class ProxyVia
{
    public const string Host = "host";
    public const string Edge = "edge";
}

/// <summary>
/// Publishing an instance under a domain on a reverse proxy (Hosting increment 3). One implementation behind
/// the Hosting tab, the operator API and the MCP tools. It owns what needs the database: checking the domain,
/// the record on the instance — whose <see cref="Instance.Url"/> is then pinned to the domain — and the
/// canonical-URL push.
/// <para>The proxy work itself (provider calls, joining the proxy's network) is <see cref="ProxyEngine"/> and
/// runs WHERE the site runs: in-process for the cloud's own host with the cloud-wide settings, as a
/// <c>proxy</c> job on a node with that node's settings (increment 4). The proxy of a node therefore never has
/// to be reachable from the cloud.</para>
/// <para>MatCMS itself is untouched by any of this (it keeps running without a cloud); the optional push of
/// <c>site.canonicalUrl</c>/<c>site.behindHttpsProxy</c> goes through the ordinary content-op channel.</para>
/// </summary>
public class ProxyService
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly SecretProtector _secrets;
    private readonly DockerHostService _docker;
    private readonly InstanceService _instances;
    private readonly NodeService _nodes;
    private readonly IHttpClientFactory _http;

    public ProxyService(AppDbContext db, CloudContext cloud, SecretProtector secrets, DockerHostService docker,
        InstanceService instances, NodeService nodes, IHttpClientFactory http)
    {
        _db = db; _cloud = cloud; _secrets = secrets; _docker = docker; _instances = instances; _nodes = nodes; _http = http;
    }

    /// <summary>The settings of the cloud's OWN host ("Dieser Host") — cloud-wide hosting.* keys.</summary>
    public ProxySettings Settings => new(
        ProxyKinds.Normalise(_cloud.Get(SettingKeys.HostingMode)),
        _cloud.Get(SettingKeys.HostingMatcadUrl),
        _secrets.Unprotect(_cloud.Get(SettingKeys.HostingMatcadToken)),
        _cloud.Get(SettingKeys.HostingCaddyAdminUrl),
        _cloud.Get(SettingKeys.HostingCaddyServer) is { Length: > 0 } srv ? srv : "srv0",
        UpstreamModes.Normalise(_cloud.Get(SettingKeys.HostingProxyUpstream)),
        _cloud.Get(SettingKeys.HostingProxyNetwork),
        _cloud.Get(SettingKeys.HostingProxyUpstreamHost));

    private HttpClient Http()
    {
        var http = _http.CreateClient("proxy");
        http.Timeout = TimeSpan.FromSeconds(15);
        return http;
    }

    /// <summary>The provider of the cloud's own host (default: the configured one).</summary>
    public IProxyProvider Provider(string? kind = null) => ProxyEngine.Provider(Settings, Http(), kind);

    /// <summary>Whether publishing on this host/node creates routes (false = "no proxy", domains are recorded).</summary>
    public bool ManagesRoutes(Node? node) => (node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind)) != ProxyKinds.None;

    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.Compiled);

    /// <summary>Normalises what an operator or an agent typed into a bare host ("https://Shop.Example.de/" →
    /// "shop.example.de"). Null = not a valid single hostname (no wildcards, no ports, no paths).</summary>
    public static string? NormaliseDomain(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.StartsWith("https://")) s = s[8..];
        else if (s.StartsWith("http://")) s = s[7..];
        s = s.TrimEnd('/');
        return HostRx.IsMatch(s) ? s : null;
    }

    /// <summary>A partial update of the proxy configuration of the cloud's own host for the API/MCP (null =
    /// keep). The Matcad token is encrypted like on the settings page and never read back.</summary>
    public sealed record ProxyConfigInput(string? Provider, string? MatcadUrl, string? MatcadToken, bool ClearMatcadToken,
        string? CaddyAdminUrl, string? CaddyServer, string? Upstream, string? Network, string? UpstreamHost);

    public async Task UpdateSettingsAsync(ProxyConfigInput b)
    {
        var d = new Dictionary<string, string?>();
        if (b.Provider is not null) d[SettingKeys.HostingMode] = ProxyKinds.Normalise(b.Provider);
        if (b.MatcadUrl is not null) d[SettingKeys.HostingMatcadUrl] = b.MatcadUrl.Trim().TrimEnd('/');
        if (b.ClearMatcadToken) d[SettingKeys.HostingMatcadToken] = "";
        else if (!string.IsNullOrEmpty(b.MatcadToken)) d[SettingKeys.HostingMatcadToken] = _secrets.Protect(b.MatcadToken);
        if (b.CaddyAdminUrl is not null) d[SettingKeys.HostingCaddyAdminUrl] = b.CaddyAdminUrl.Trim().TrimEnd('/');
        if (b.CaddyServer is not null) d[SettingKeys.HostingCaddyServer] = b.CaddyServer.Trim();
        if (b.Upstream is not null) d[SettingKeys.HostingProxyUpstream] = UpstreamModes.Normalise(b.Upstream);
        if (b.Network is not null) d[SettingKeys.HostingProxyNetwork] = b.Network.Trim();
        if (b.UpstreamHost is not null) d[SettingKeys.HostingProxyUpstreamHost] = b.UpstreamHost.Trim();
        if (d.Count > 0) await _cloud.SaveAsync(d);
    }

    /// <summary>The configuration as the API shows it — without the secret, only whether one is set.</summary>
    public object PublicConfig()
    {
        var s = Settings;
        return new
        {
            provider = s.Kind, managesRoutes = s.Kind != ProxyKinds.None,
            matcadUrl = s.MatcadUrl, matcadTokenSet = !string.IsNullOrEmpty(s.MatcadToken),
            caddyAdminUrl = s.CaddyAdminUrl, caddyServer = s.CaddyServer,
            upstream = s.UpstreamMode, network = s.Network, upstreamHost = s.UpstreamHost,
        };
    }

    /// <summary>The cloud's own host for <c>_ProxyFields.cshtml</c>.</summary>
    public ProxyFieldsView FieldsView()
    {
        var s = Settings;
        return new(s.Kind, s.MatcadUrl, !string.IsNullOrEmpty(s.MatcadToken), s.CaddyAdminUrl, s.CaddyServer, s.UpstreamMode, s.Network, s.UpstreamHost);
    }

    /// <summary>A node for <c>_ProxyFields.cshtml</c> — same shape as the cloud's own host.</summary>
    public static ProxyFieldsView FieldsView(Node n) => new(ProxyKinds.Normalise(n.ProxyKind), n.MatcadUrl, !string.IsNullOrEmpty(n.MatcadTokenEnc),
        n.CaddyAdminUrl, string.IsNullOrWhiteSpace(n.CaddyServer) ? "srv0" : n.CaddyServer, UpstreamModes.Normalise(n.ProxyUpstream), n.ProxyNetwork, n.ProxyUpstreamHost);

    // ---- Edge proxy (cloud-wide, optional) -------------------------------------------------------------------

    /// <summary>Customer domains go through the central edge proxy on the cloud's host.</summary>
    public bool EdgeEnabled => _cloud.Flag(SettingKeys.HostingEdgeEnabled);

    /// <summary>The edge IS the proxy "Dieser Host" uses (default — one Caddy on the cloud server).</summary>
    public bool EdgeUsesHostProxy => _cloud.Get(SettingKeys.HostingEdgeUseHostProxy) != "0";

    /// <summary>The edge's provider settings. The upstream fields are always this host's: the edge sits on the
    /// cloud's host, so a site running HERE is reached the way this host's proxy reaches it.</summary>
    public ProxySettings EdgeSettings
    {
        get
        {
            var h = Settings;
            if (EdgeUsesHostProxy) return h;
            return new(ProxyKinds.Normalise(_cloud.Get(SettingKeys.HostingEdgeMode)),
                _cloud.Get(SettingKeys.HostingEdgeMatcadUrl), _secrets.Unprotect(_cloud.Get(SettingKeys.HostingEdgeMatcadToken)),
                _cloud.Get(SettingKeys.HostingEdgeCaddyAdminUrl),
                _cloud.Get(SettingKeys.HostingEdgeCaddyServer) is { Length: > 0 } srv ? srv : "srv0",
                h.UpstreamMode, h.Network, h.UpstreamHost);
        }
    }

    /// <summary>The edge's own provider for <c>_ProxyFields.cshtml</c> (only shown when it is not the host's).</summary>
    public ProxyFieldsView EdgeFieldsView()
    {
        var kind = ProxyKinds.Normalise(_cloud.Get(SettingKeys.HostingEdgeMode));
        return new(kind, _cloud.Get(SettingKeys.HostingEdgeMatcadUrl), !string.IsNullOrEmpty(_cloud.Get(SettingKeys.HostingEdgeMatcadToken)),
            _cloud.Get(SettingKeys.HostingEdgeCaddyAdminUrl), _cloud.Get(SettingKeys.HostingEdgeCaddyServer) is { Length: > 0 } srv ? srv : "srv0",
            UpstreamModes.Network, null, null);
    }

    /// <summary>Partial update of the edge settings for UI/API/MCP (null = keep).</summary>
    public sealed record EdgeConfigInput(bool? Enabled, bool? UseHostProxy, string? Provider, string? MatcadUrl, string? MatcadToken,
        bool ClearMatcadToken, string? CaddyAdminUrl, string? CaddyServer, string? TrustedIps = null);

    public async Task UpdateEdgeAsync(EdgeConfigInput b)
    {
        var d = new Dictionary<string, string?>();
        if (b.Enabled is bool e) d[SettingKeys.HostingEdgeEnabled] = e ? "1" : "0";
        if (b.UseHostProxy is bool u) d[SettingKeys.HostingEdgeUseHostProxy] = u ? "1" : "0";
        if (b.Provider is not null) d[SettingKeys.HostingEdgeMode] = ProxyKinds.Normalise(b.Provider);
        if (b.MatcadUrl is not null) d[SettingKeys.HostingEdgeMatcadUrl] = b.MatcadUrl.Trim().TrimEnd('/');
        if (b.ClearMatcadToken) d[SettingKeys.HostingEdgeMatcadToken] = "";
        else if (!string.IsNullOrEmpty(b.MatcadToken)) d[SettingKeys.HostingEdgeMatcadToken] = _secrets.Protect(b.MatcadToken);
        if (b.CaddyAdminUrl is not null) d[SettingKeys.HostingEdgeCaddyAdminUrl] = b.CaddyAdminUrl.Trim().TrimEnd('/');
        if (b.CaddyServer is not null) d[SettingKeys.HostingEdgeCaddyServer] = b.CaddyServer.Trim();
        if (b.TrustedIps is not null) d[SettingKeys.HostingEdgeTrustedIps] = b.TrustedIps.Trim();
        if (d.Count > 0) await _cloud.SaveAsync(d);
    }

    public object PublicEdgeConfig()
    {
        var s = EdgeSettings;
        return new
        {
            enabled = EdgeEnabled, useHostProxy = EdgeUsesHostProxy, provider = s.Kind,
            matcadUrl = s.MatcadUrl, matcadTokenSet = !string.IsNullOrEmpty(s.MatcadToken),
            caddyAdminUrl = s.CaddyAdminUrl, caddyServer = s.CaddyServer,
            // A host's automatic address as the edge's target needs a Host header rewrite — only Caddy does that.
            forwardsToHostAddress = s.Kind == ProxyKinds.Caddy,
            trustedIps = EdgeTrustedIps,
        };
    }

    public async Task<TestResult> TestEdgeAsync(CancellationToken ct = default)
    {
        var s = EdgeSettings;
        if (s.Kind == ProxyKinds.None) return new(false, "Für den Edge ist kein Proxy eingerichtet.", s.Kind);
        var r = await ProxyEngine.ExecuteAsync(new ProxyOp("test", s), Http(), _docker, ct);
        return new(r.Ok, r.Message, s.Kind);
    }

    // ---- Automatic host addresses (per host, optional) ---------------------------------------------------------

    /// <summary>The base domain of a host's automatic addresses, or null when switched off there.</summary>
    public string? AutoBase(Node? node) => node is null
        ? (_cloud.Flag(SettingKeys.HostingAutoDomainEnabled) ? NormaliseDomain(_cloud.Get(SettingKeys.HostingAutoDomainBase)) : null)
        : (node.AutoDomainEnabled ? NormaliseDomain(node.AutoDomainBase) : null);

    public async Task SetAutoDomainAsync(bool enabled, string? baseDomain)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.HostingAutoDomainEnabled] = enabled ? "1" : "0",
            [SettingKeys.HostingAutoDomainBase] = NormaliseDomain(baseDomain) ?? "",
        });
    }

    /// <summary>A free host address for <paramref name="name"/> under <paramref name="baseDomain"/>: the Docker-style
    /// slug of the name, numbered when taken — by another instance's host address OR customer domain.</summary>
    public async Task<string> FreeHostAddressAsync(string name, string baseDomain, int? exceptInstanceId, CancellationToken ct)
    {
        var slug = HostingService.Normalise(name);
        if (slug.Length == 0) slug = "site";
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        for (var i = 1; ; i++)
        {
            var candidate = (i == 1 ? slug : $"{slug}-{i}") + "." + baseDomain;
            if (!await _db.Instances.AnyAsync(x => x.Id != exceptInstanceId && (x.HostDomain == candidate || x.ProxyDomain == candidate), ct))
                return candidate;
        }
    }

    // ---- Wildcard certificate per host + trusting the edge ------------------------------------------------------

    public sealed record WildcardConfig(bool Enabled, string? DnsProvider, bool CredentialsSet, string? RouteId, string? Error);

    public WildcardConfig WildcardFor(Node? node) => node is null
        ? new(_cloud.Flag(SettingKeys.HostingWildcardEnabled), _cloud.Get(SettingKeys.HostingWildcardDnsProvider),
            !string.IsNullOrEmpty(_cloud.Get(SettingKeys.HostingWildcardDnsCredentials)), _cloud.Get(SettingKeys.HostingWildcardRouteId) is { Length: > 0 } r ? r : null,
            _cloud.Get(SettingKeys.HostingWildcardError) is { Length: > 0 } e ? e : null)
        : new(node.WildcardEnabled, node.DnsProvider, !string.IsNullOrEmpty(node.DnsCredentialsEnc), node.WildcardRouteId, node.WildcardError);

    /// <summary>Parses "key=value" lines (the credentials field) into a map; blank lines and lines without "=" are skipped.</summary>
    public static Dictionary<string, string> ParseCredentials(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')].Trim(), Value: l[(l.IndexOf('=') + 1)..].Trim()))
            .Where(p => p.Key.Length > 0).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    private Dictionary<string, string> CredentialsOf(Node? node)
    {
        var enc = node is null ? _cloud.Get(SettingKeys.HostingWildcardDnsCredentials) : node.DnsCredentialsEnc;
        try { return string.IsNullOrEmpty(enc) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(_secrets.Unprotect(enc) ?? "") ?? new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Saves the wildcard setting of a host and applies it there: on = one certificate for *.{base} via the DNS
    /// challenge (needs automatic addresses with a base domain and a proxy on the host), off = removes what the cloud
    /// set up. Empty <paramref name="credentialsText"/> keeps the stored credentials (they are never shown again).
    /// </summary>
    public async Task<PublishResult> SetWildcardAsync(Node? node, bool enabled, string? dnsProvider, string? credentialsText, CancellationToken ct = default)
    {
        var creds = ParseCredentials(credentialsText);
        var keepCreds = creds.Count == 0;
        string? encCreds = keepCreds ? null : _secrets.Protect(JsonSerializer.Serialize(creds));
        if (node is null)
        {
            var d = new Dictionary<string, string?>
            {
                [SettingKeys.HostingWildcardEnabled] = enabled ? "1" : "0",
                [SettingKeys.HostingWildcardDnsProvider] = dnsProvider?.Trim(),
            };
            if (!keepCreds) d[SettingKeys.HostingWildcardDnsCredentials] = encCreds;
            await _cloud.SaveAsync(d);
        }
        else
        {
            node.WildcardEnabled = enabled;
            node.DnsProvider = string.IsNullOrWhiteSpace(dnsProvider) ? null : dnsProvider.Trim();
            if (!keepCreds) node.DnsCredentialsEnc = encCreds;
            await _db.SaveChangesAsync(ct);
        }
        return await ApplyWildcardAsync(node, ct);
    }

    /// <summary>Brings the host's proxy in line with its wildcard setting (also used after the base domain changed).</summary>
    public async Task<PublishResult> ApplyWildcardAsync(Node? node, CancellationToken ct = default)
    {
        var cfg = WildcardFor(node);
        var baseDomain = AutoBase(node);
        var kind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        ProxyOpResult r;
        if (cfg.Enabled)
        {
            if (baseDomain is null) return await RecordWildcardAsync(node, cfg.RouteId, "Erst automatische Adressen mit einer Basis-Domain einschalten.");
            if (kind == ProxyKinds.None) return await RecordWildcardAsync(node, cfg.RouteId, "Auf diesem Host ist kein Proxy eingerichtet.");
            if (string.IsNullOrWhiteSpace(cfg.DnsProvider)) return await RecordWildcardAsync(node, cfg.RouteId, "Bitte den DNS-Anbieter angeben.");
            var fallback = _cloud.Get(SettingKeys.CanonicalUrl) is { Length: > 0 } u ? u : "https://" + baseDomain;
            var creds = CredentialsOf(node);
            r = await RunAsync(node, x => new ProxyOp("wildcard", x, Host: baseDomain, RouteId: cfg.RouteId, Upstream: fallback,
                DnsProvider: cfg.DnsProvider, Dns: creds), null, ct);
            return await RecordWildcardAsync(node, r.Ok ? r.RouteId ?? cfg.RouteId : cfg.RouteId, r.Ok ? null : r.Message, r.Ok ? r.Message : null);
        }
        if (cfg.RouteId is null) return await RecordWildcardAsync(node, null, null, "Kein Wildcard-Zertifikat eingerichtet.");
        r = await RunAsync(node, x => new ProxyOp("unwildcard", x, Kind: kind, Host: baseDomain ?? "", RouteId: cfg.RouteId), null, ct);
        return await RecordWildcardAsync(node, r.Ok ? null : cfg.RouteId, r.Ok ? null : r.Message, r.Ok ? r.Message : null);
    }

    private async Task<PublishResult> RecordWildcardAsync(Node? node, string? routeId, string? error, string? okMessage = null)
    {
        if (node is null)
            await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.HostingWildcardRouteId] = routeId ?? "", [SettingKeys.HostingWildcardError] = error ?? "" });
        else { node.WildcardRouteId = routeId; node.WildcardError = error; await _db.SaveChangesAsync(); }
        return error is null ? new(true, okMessage ?? "Gespeichert.") : new(false, error);
    }

    /// <summary>The edge's source addresses as the nodes see them (from the edge settings).</summary>
    public List<string> EdgeTrustedIps => (_cloud.Get(SettingKeys.HostingEdgeTrustedIps) ?? "")
        .Split(new[] { ',', ' ', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(ip => System.Net.IPAddress.TryParse(ip.Split('/')[0], out _)).Distinct().ToList();

    /// <summary>Tells the proxy of one host to trust the edge (Caddy: trusted_proxies), or to stop trusting it when the
    /// edge is off. Only for hosts with a proxy; Matcad reports that it cannot be set from here.</summary>
    public async Task<ProxyOpResult> ApplyTrustAsync(Node? node, CancellationToken ct = default)
    {
        var kind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        if (kind == ProxyKinds.None) return new(true, "Kein Proxy.");
        var ranges = EdgeEnabled ? EdgeTrustedIps : new List<string>();
        return await RunAsync(node, x => new ProxyOp("trust", x, Trusted: ranges), null, ct);
    }

    /// <summary><see cref="ApplyTrustAsync"/> on this host and every node; returns the failures by host name.</summary>
    public async Task<List<string>> ApplyTrustEverywhereAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        var r = await ApplyTrustAsync(null, ct);
        if (!r.Ok) errors.Add("Dieser Host: " + r.Message);
        foreach (var n in await _db.Nodes.Where(n => !n.Revoked).ToListAsync(ct))
        {
            var nr = await ApplyTrustAsync(n, ct);
            if (!nr.Ok) errors.Add($"{n.Name}: {nr.Message}");
        }
        return errors;
    }

    public sealed record TestResult(bool Ok, string Message, string Kind);

    /// <summary>Runs one proxy operation where it belongs: in-process for the cloud's own host, as a job on the
    /// node otherwise (with THAT node's settings).</summary>
    private async Task<ProxyOpResult> RunAsync(Node? node, Func<ProxySettings, ProxyOp> build, int? instanceId, CancellationToken ct)
    {
        if (node is null) return await ProxyEngine.ExecuteAsync(build(Settings), Http(), _docker, ct);
        var r = await _nodes.RunAsync(node, NodeJobKinds.Proxy, build(_nodes.ProxySettingsFor(node)), instanceId, TimeSpan.FromSeconds(45), ct);
        if (!r.Finished) return new(false, r.Message);
        return NodeJobExecutor.Deserialize<ProxyOpResult>(r.ResultJson) ?? new(false, r.Message);
    }

    /// <summary>Provider reachable, plus — for the network mode — the proxy network actually exists. For
    /// <paramref name="node"/> null: the cloud's own host.</summary>
    public async Task<TestResult> TestAsync(Node? node = null, CancellationToken ct = default)
    {
        var kind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        var r = await RunAsync(node, s => new ProxyOp("test", s), null, ct);
        return new(r.Ok, r.Message, kind);
    }

    /// <summary>The node an instance runs on, or null for the cloud's own host. The route lives where the site does.</summary>
    private async Task<Node?> NodeOfAsync(Instance inst, CancellationToken ct) =>
        HostingActionsService.IsOnNode(inst) ? await _db.Nodes.FindAsync(new object[] { inst.NodeId! }, ct) : null;

    // The label in the proxy's own UI. Instances are often simply called "MatCMS…" — no "MatCMS MatCMS".
    private static string RouteName(string name) =>
        name.StartsWith("MatCMS", StringComparison.OrdinalIgnoreCase) ? name : "MatCMS " + name;

    public sealed record PublishResult(bool Ok, string Message, string? Domain = null);

    /// <summary>
    /// Where the EDGE forwards a site to, in this order: the site's automatic host address over https (the host
    /// proxy matches on that name, so the edge must rewrite the Host header — only Caddy can); a site on THIS host
    /// the way this host's proxy reaches it (derived from the container — the edge runs here); a site on a node
    /// at <c>node-address:port</c>. Null upstream with no error = derive from the container.
    /// </summary>
    private static (string? Upstream, bool RewriteHost, string? Error) EdgeTarget(Instance inst, Node? node, ProxySettings edge)
    {
        if (inst.HostDomain is { } hd && edge.Kind == ProxyKinds.Caddy) return ("https://" + hd, true, null);
        if (node is null) return (null, false, null);
        if (string.IsNullOrWhiteSpace(node.Address))
            return (null, false, $"Node „{node.Name}“ hat keine Adresse für den Edge-Proxy (Node → Proxy & Ports)"
                + (edge.Kind == ProxyKinds.Caddy ? " — oder dort automatische Adressen einschalten." : "."));
        if (inst.LocalPort is not int port) return (null, false, "Der Container veröffentlicht keinen Host-Port.");
        return ($"http://{node.Address!.Trim()}:{port}", false, null);
    }

    /// <summary>Publishes (or moves) the CUSTOMER domain of <paramref name="inst"/> (tracked). With the edge on it is
    /// routed at the edge, otherwise at the proxy of the site's host. A route left on the other way is removed
    /// first, so switching the edge on or off never leaves two routes for one domain.
    /// <paramref name="pushCanonical"/> additionally tells the instance its public address.</summary>
    public async Task<PublishResult> PublishAsync(Instance inst, string? rawDomain, bool pushCanonical, CancellationToken ct = default)
    {
        var domain = NormaliseDomain(rawDomain);
        if (domain is null) return new(false, "Keine gültige Domain (nur ein Hostname, z. B. shop.example.de — ohne Pfad, Port oder *).");
        if (await _db.Instances.AnyAsync(i => i.Id != inst.Id && (i.ProxyDomain == domain || i.HostDomain == domain), ct))
            return new(false, $"„{domain}“ ist bereits einer anderen Instanz zugeordnet.");

        var node = await NodeOfAsync(inst, ct);
        var viaEdge = EdgeEnabled;
        var wasEdge = inst.ProxyVia == ProxyVia.Edge;
        var edge = EdgeSettings;
        var kind = viaEdge ? edge.Kind : node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        if (viaEdge && kind == ProxyKinds.None) return Fail(inst, "Der Edge-Proxy ist eingeschaltet, aber für ihn ist kein Proxy eingerichtet (Hosting → Einstellungen).");
        var manages = kind != ProxyKinds.None;
        if (manages && !HostingActionsService.CanAct(inst))
            return Fail(inst, "Diese Instanz läuft weder auf dem Docker-Host dieser Cloud noch auf einem verbundenen Node — ihre Route kann nicht angelegt werden.");

        // A route on the OTHER way goes first.
        if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null && wasEdge != viaEdge)
        {
            var del = await DeleteCustomerRouteAsync(inst, node, ct);
            if (!del.Ok) return Fail(inst, "Alte Route nicht entfernt: " + del.Message);
            inst.ProxyRouteId = null; inst.ProxyProvider = null;
        }

        string? routeId = null;
        if (viaEdge)
        {
            var (up, rewrite, err) = EdgeTarget(inst, node, edge);
            if (err is not null) return Fail(inst, err);
            var r = await ProxyEngine.ExecuteAsync(new ProxyOp("publish", edge, inst.ContainerId, inst.LocalPort, domain,
                "edge-" + inst.PublicId, RouteName(inst.Name), RouteId: inst.ProxyRouteId, OldKind: inst.ProxyProvider,
                Upstream: up, RewriteHost: rewrite), Http(), _docker, ct);
            if (!r.Ok)
            {
                if (r.RouteId is not null) { inst.ProxyRouteId = r.RouteId; inst.ProxyProvider = kind; inst.ProxyVia = ProxyVia.Edge; }
                return Fail(inst, r.Message);
            }
            routeId = r.RouteId;
        }
        else if (manages || inst.ProxyRouteId is not null)
        {
            var r = await RunAsync(node, x => new ProxyOp("publish", x, inst.ContainerId, inst.LocalPort, domain,
                inst.PublicId, RouteName(inst.Name), RouteId: inst.ProxyRouteId, OldKind: inst.ProxyProvider), inst.Id, ct);
            if (!r.Ok)
            {
                if (r.RouteId is not null) { inst.ProxyRouteId = r.RouteId; inst.ProxyProvider = kind; inst.ProxyVia = ProxyVia.Host; }
                return Fail(inst, r.Message);
            }
            routeId = manages ? r.RouteId : null;
        }

        var moved = inst.ProxyDomain is not null && inst.ProxyDomain != domain;
        inst.ProxyDomain = domain;
        inst.ProxyProvider = kind;
        inst.ProxyRouteId = routeId;
        inst.ProxyVia = viaEdge ? ProxyVia.Edge : ProxyVia.Host;
        inst.ProxyError = null;
        inst.ProxyPublishedAt = DateTime.UtcNow;
        inst.Url = "https://" + domain;
        inst.UrlPinned = true;
        _instances.Log(inst, InstanceEventKind.DomainPublished,
            (moved ? "Domain geändert auf " : "Domain veröffentlicht: ") + domain +
            (viaEdge ? $" (über den Edge-Proxy, {kind})." :
             manages ? $" (Route über {kind}{(node is null ? "" : $", Node „{node.Name}“")})." : " (kein Proxy — nur vermerkt)."));
        await _db.SaveChangesAsync(ct);

        var note = await PushCanonicalAsync(inst, pushCanonical ? "https://" + domain : null, pushCanonical, ct);
        return new(true, $"„{domain}“ veröffentlicht{(viaEdge ? " (über den Edge)" : "")}.{note}", domain);
    }

    /// <summary>Removes the customer domain again. The route goes first; only when that succeeded is the record
    /// cleared, so a proxy that is down never leaves a route behind that the cloud has forgotten about.</summary>
    public async Task<PublishResult> UnpublishAsync(Instance inst, bool pushCanonical, CancellationToken ct = default)
    {
        if (inst.ProxyDomain is null && inst.ProxyRouteId is null) return new(true, "Keine Domain veröffentlicht.");
        if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
        {
            var r = await DeleteCustomerRouteAsync(inst, await NodeOfAsync(inst, ct), ct);
            if (!r.Ok) return Fail(inst, r.Message);
        }
        var old = inst.ProxyDomain;
        inst.ProxyDomain = null; inst.ProxyProvider = null; inst.ProxyRouteId = null; inst.ProxyError = null; inst.ProxyPublishedAt = null;
        inst.ProxyVia = null;
        // The host address (if any) becomes the address again; else hand it back to the heartbeat.
        if (inst.HostDomain is { } hd) { inst.Url = "https://" + hd; inst.UrlPinned = true; }
        else { inst.UrlPinned = false; inst.Url = null; }
        _instances.Log(inst, InstanceEventKind.DomainUnpublished, $"Domain entfernt: {old}.");
        await _db.SaveChangesAsync(ct);
        var note = await PushCanonicalAsync(inst, inst.HostDomain is { } h2 ? "https://" + h2 : "", pushCanonical, ct);
        return new(true, $"Domain „{old}“ entfernt.{note}");
    }

    private Task<ProxyOpResult> DeleteCustomerRouteAsync(Instance inst, Node? node, CancellationToken ct) =>
        inst.ProxyVia == ProxyVia.Edge
            ? ProxyEngine.ExecuteAsync(new ProxyOp("delete", EdgeSettings, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), Http(), _docker, ct)
            : RunAsync(node, x => new ProxyOp("delete", x, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), inst.Id, ct);

    /// <summary>
    /// Creates (or renews) the automatic host address of <paramref name="inst"/> (tracked) at the proxy of its host:
    /// <c>name.{base}</c>. Without a customer domain it becomes the site's address. A customer domain at the edge is
    /// re-pointed at it when <paramref name="repointEdge"/> (the edge then reaches the site by name, not by port).
    /// </summary>
    public async Task<PublishResult> PublishHostAddressAsync(Instance inst, bool pushCanonical, bool repointEdge = true, CancellationToken ct = default)
    {
        var node = await NodeOfAsync(inst, ct);
        var baseDomain = AutoBase(node);
        if (baseDomain is null) return new(false, "Für diesen Host sind automatische Adressen nicht eingeschaltet.");
        var kind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        if (kind == ProxyKinds.None) return new(false, "Automatische Adressen brauchen einen Proxy auf diesem Host (Proxy & Ports).");
        if (!HostingActionsService.CanAct(inst)) return new(false, "Diese Instanz läuft weder auf diesem Host noch auf einem verbundenen Node.");

        // Named after the CONTAINER (the name it was created with), not the site name the instance reports — that
        // is often just "MatCMS" and can change any time.
        var label = HostingService.SlugFromStack(inst.LocalContainerName, _cloud.Get(SettingKeys.HostingNamePattern)) ?? inst.Name;
        var address = inst.HostDomain is { } cur && cur.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase)
            ? cur : await FreeHostAddressAsync(label, baseDomain, inst.Id, ct);
        var r = await RunAsync(node, x => new ProxyOp("publish", x, inst.ContainerId, inst.LocalPort, address,
            "host-" + inst.PublicId, RouteName(inst.Name), RouteId: inst.HostRouteId, OldKind: inst.HostProvider), inst.Id, ct);
        if (!r.Ok)
        {
            inst.HostRouteError = r.Message;
            _instances.Log(inst, InstanceEventKind.DomainFailed, "Host-Adresse: " + r.Message);
            await _db.SaveChangesAsync(ct);
            return new(false, r.Message);
        }
        inst.HostDomain = address; inst.HostProvider = kind; inst.HostRouteId = r.RouteId;
        inst.HostRouteError = null; inst.HostPublishedAt = DateTime.UtcNow;
        if (inst.ProxyDomain is null) { inst.Url = "https://" + address; inst.UrlPinned = true; }
        _instances.Log(inst, InstanceEventKind.DomainPublished, $"Host-Adresse angelegt: {address}{(node is null ? "" : $" (Node „{node.Name}“)")}.");
        await _db.SaveChangesAsync(ct);
        // The first route may just have created the proxy's server — trust the edge there too (best effort).
        if (EdgeEnabled && EdgeTrustedIps.Count > 0) await ApplyTrustAsync(node, ct);

        var note = "";
        if (repointEdge && inst.ProxyDomain is { } d && inst.ProxyVia == ProxyVia.Edge && EdgeSettings.Kind == ProxyKinds.Caddy)
        {
            var e = await PublishAsync(inst, d, pushCanonical: false, ct);
            note = e.Ok ? " Edge leitet jetzt über die Host-Adresse weiter." : " Edge NICHT umgestellt: " + e.Message;
        }
        if (inst.ProxyDomain is null) note += await PushCanonicalAsync(inst, "https://" + address, pushCanonical, ct);
        return new(true, $"Host-Adresse „{address}“ angelegt.{note}", address);
    }

    /// <summary>Removes the automatic host address (route first, record second).</summary>
    public async Task<PublishResult> RemoveHostAddressAsync(Instance inst, bool pushCanonical, CancellationToken ct = default)
    {
        if (inst.HostDomain is null && inst.HostRouteId is null) return new(true, "Keine Host-Adresse.");
        var node = await NodeOfAsync(inst, ct);
        if (inst.HostRouteId is not null && inst.HostProvider is not null)
        {
            var r = await RunAsync(node, x => new ProxyOp("delete", x, Kind: inst.HostProvider, RouteId: inst.HostRouteId), inst.Id, ct);
            if (!r.Ok) { inst.HostRouteError = r.Message; await _db.SaveChangesAsync(ct); return new(false, r.Message); }
        }
        var old = inst.HostDomain;
        inst.HostDomain = null; inst.HostProvider = null; inst.HostRouteId = null; inst.HostRouteError = null; inst.HostPublishedAt = null;
        if (inst.ProxyDomain is null) { inst.UrlPinned = false; inst.Url = null; }
        _instances.Log(inst, InstanceEventKind.DomainUnpublished, $"Host-Adresse entfernt: {old}.");
        await _db.SaveChangesAsync(ct);
        var note = "";
        // An edge route that forwarded to the host address has lost its target — re-point it (address:port).
        if (inst.ProxyDomain is { } d && inst.ProxyVia == ProxyVia.Edge)
        {
            var e = await PublishAsync(inst, d, pushCanonical: false, ct);
            note = e.Ok ? " Edge leitet jetzt direkt auf den Port weiter." : " ACHTUNG, Edge-Route ohne Ziel: " + e.Message;
        }
        else if (inst.ProxyDomain is null) note = await PushCanonicalAsync(inst, "", pushCanonical, ct);
        return new(true, $"Host-Adresse „{old}“ entfernt.{note}");
    }

    public sealed record BulkResult(int Created, int Failed, List<string> Errors);

    private IQueryable<Instance> OnHost(Node? node) => _db.Instances.Where(i => i.ContainerId != null && i.HostDomain == null &&
        (node == null ? i.Hosting == InstanceHosting.Local : i.Hosting == InstanceHosting.Node && i.NodeId == node.Id));

    /// <summary>Creates host addresses for every instance on the host that has none yet — after switching the
    /// feature on for a host that already runs sites.</summary>
    public async Task<BulkResult> PublishMissingHostAddressesAsync(Node? node, CancellationToken ct = default)
    {
        var list = await OnHost(node).ToListAsync(ct);
        int ok = 0, fail = 0;
        var errors = new List<string>();
        foreach (var i in list)
        {
            var r = await PublishHostAddressAsync(i, pushCanonical: true, ct: ct);
            if (r.Ok) ok++; else { fail++; errors.Add($"{i.Name}: {r.Message}"); }
        }
        return new(ok, fail, errors);
    }

    /// <summary>Customer domains still routed the OTHER way than the edge switch says (published before it was
    /// flipped). Re-publishing moves each one — the old route is removed first (see <see cref="PublishAsync"/>).</summary>
    private IQueryable<Instance> OnOtherWay()
    {
        var edge = EdgeEnabled;
        return _db.Instances.Where(i => i.ProxyDomain != null &&
            (edge ? i.ProxyVia != ProxyVia.Edge || i.ProxyVia == null : i.ProxyVia == ProxyVia.Edge));
    }

    public Task<int> CustomerDomainsOnOtherWayCountAsync(CancellationToken ct = default) => OnOtherWay().CountAsync(ct);

    public async Task<BulkResult> MoveCustomerDomainsToCurrentWayAsync(CancellationToken ct = default)
    {
        int ok = 0, fail = 0;
        var errors = new List<string>();
        foreach (var i in await OnOtherWay().ToListAsync(ct))
        {
            var r = await PublishAsync(i, i.ProxyDomain, pushCanonical: false, ct);
            if (r.Ok) ok++; else { fail++; errors.Add($"{i.ProxyDomain}: {r.Message}"); }
        }
        return new(ok, fail, errors);
    }

    /// <summary>Instances on the host without a host address — what <see cref="PublishMissingHostAddressesAsync"/>
    /// would create.</summary>
    public Task<int> MissingHostAddressCountAsync(Node? node, CancellationToken ct = default) => OnHost(node).CountAsync(ct);

    /// <summary>
    /// After a move: the routes follow the site. The host address is deleted on the OLD host and — if the new host
    /// has automatic addresses — created there under ITS base domain. A customer domain at the edge is simply
    /// re-pointed (no DNS change, the site answers again at once); one at a host proxy is deleted on the old host
    /// first, then published where the site now runs. <paramref name="inst"/> is already classified on the target.
    /// </summary>
    public async Task<PublishResult> MoveRouteAsync(Instance inst, Node? from, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var ok = true;
        if (inst.HostRouteId is not null && inst.HostProvider is not null)
        {
            var r = await RunAsync(from, x => new ProxyOp("delete", x, Kind: inst.HostProvider, RouteId: inst.HostRouteId), inst.Id, ct);
            if (!r.Ok) { ok = false; notes.Add("Alte Host-Adresse nicht entfernt: " + r.Message); }
        }
        if (inst.HostDomain is not null || inst.HostRouteId is not null)
        {
            inst.HostDomain = null; inst.HostProvider = null; inst.HostRouteId = null; inst.HostRouteError = null; inst.HostPublishedAt = null;
            await _db.SaveChangesAsync(ct);
        }
        if (AutoBase(await NodeOfAsync(inst, ct)) is not null)
        {
            var h = await PublishHostAddressAsync(inst, pushCanonical: inst.ProxyDomain is null, repointEdge: false, ct);
            if (!h.Ok) ok = false;
            notes.Add(h.Message);
        }

        if (inst.ProxyDomain is { } domain)
        {
            if (inst.ProxyVia != ProxyVia.Edge && inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
            {
                var r = await RunAsync(from, x => new ProxyOp("delete", x, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), inst.Id, ct);
                if (!r.Ok) return new(false, string.Join(" ", notes.Append("Alte Route nicht entfernt: " + r.Message)));
                inst.ProxyRouteId = null;
                inst.ProxyProvider = null;
            }
            var p = await PublishAsync(inst, domain, pushCanonical: false, ct);
            if (!p.Ok) ok = false;
            notes.Add(p.Message + (inst.ProxyVia == ProxyVia.Edge ? " Keine DNS-Änderung nötig." : " DNS muss auf den neuen Host zeigen."));
        }
        return new(ok, notes.Count == 0 ? "Keine Domain." : string.Join(" ", notes));
    }

    /// <summary>Best effort when a site is torn down: every route the cloud made for it goes. A proxy that is down
    /// must not stop the removal — its failure is returned for the log.</summary>
    public async Task<string?> RemoveAllRoutesAsync(Instance inst, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var node = await NodeOfAsync(inst, ct);
        if (inst.HostRouteId is not null && inst.HostProvider is not null)
        {
            var r = await RunAsync(node, x => new ProxyOp("delete", x, Kind: inst.HostProvider, RouteId: inst.HostRouteId), inst.Id, ct);
            if (!r.Ok) errors.Add($"Host-Adresse {inst.HostDomain}: {r.Message}");
        }
        if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
        {
            var r = await DeleteCustomerRouteAsync(inst, node, ct);
            if (!r.Ok) errors.Add($"Domain {inst.ProxyDomain}: {r.Message}");
        }
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    public sealed record DomainStatus(string? Domain, string? Provider, string? RouteId, string? Error, DateTime? PublishedAt, bool? RouteExists,
        string? Via = null, string? HostDomain = null, string? HostRouteId = null, string? HostError = null, bool? HostRouteExists = null);

    /// <summary>The one JSON shape of a domain status for REST and MCP.</summary>
    public static object StatusJson(DomainStatus s) => new
    {
        domain = s.Domain, via = s.Via, provider = s.Provider, routeId = s.RouteId, error = s.Error, publishedAt = s.PublishedAt, routeExists = s.RouteExists,
        hostAddress = s.HostDomain, hostRouteId = s.HostRouteId, hostError = s.HostError, hostRouteExists = s.HostRouteExists,
    };

    public async Task<DomainStatus> StatusAsync(Instance inst, bool checkProvider, CancellationToken ct = default)
    {
        bool? exists = null, hostExists = null;
        if (checkProvider)
        {
            var node = await NodeOfAsync(inst, ct);
            if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
            {
                var r = inst.ProxyVia == ProxyVia.Edge
                    ? await ProxyEngine.ExecuteAsync(new ProxyOp("exists", EdgeSettings, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), Http(), _docker, ct)
                    : await RunAsync(node, x => new ProxyOp("exists", x, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), inst.Id, ct);
                exists = r.Ok ? r.Exists : null;
            }
            if (inst.HostRouteId is not null && inst.HostProvider is not null)
            {
                var r = await RunAsync(node, x => new ProxyOp("exists", x, Kind: inst.HostProvider, RouteId: inst.HostRouteId), inst.Id, ct);
                hostExists = r.Ok ? r.Exists : null;
            }
        }
        return new(inst.ProxyDomain, inst.ProxyProvider, inst.ProxyRouteId, inst.ProxyError, inst.ProxyPublishedAt, exists,
            inst.ProxyDomain is null ? null : inst.ProxyVia ?? ProxyVia.Host, inst.HostDomain, inst.HostRouteId, inst.HostRouteError, hostExists);
    }

    /// <summary>
    /// Provisioning: the container exists but its instance row does not yet (it appears when the site enrolls
    /// with the join code). Routes only need the container, so they are created now — the automatic host address
    /// when the host has the feature on, and the customer domain when one was given (at the edge or the host) —
    /// and remembered under the container name; <c>InstanceService.ClassifyAsync</c> adopts them on the first beat.
    /// </summary>
    public async Task<PublishResult> ProvisionRoutesAsync(Node? node, string containerId, string containerName, int? localPort,
        string instanceName, string? rawDomain, bool pushCanonical, CancellationToken ct = default)
    {
        string? domain = null;
        if (!string.IsNullOrWhiteSpace(rawDomain))
        {
            domain = NormaliseDomain(rawDomain);
            if (domain is null) return new(false, "Keine gültige Domain.");
            if (await _db.Instances.AnyAsync(i => i.ProxyDomain == domain || i.HostDomain == domain, ct))
                return new(false, $"„{domain}“ ist bereits einer anderen Instanz zugeordnet.");
        }

        var hostKind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        var notes = new List<string>();
        string? hostDomain = null, hostRouteId = null;
        if (AutoBase(node) is { } baseDomain && hostKind != ProxyKinds.None)
        {
            var address = await FreeHostAddressAsync(instanceName, baseDomain, null, ct);
            var r = await RunAsync(node, x => new ProxyOp("publish", x, containerId, localPort, address, "host-" + containerName, RouteName(instanceName)), null, ct);
            if (r.Ok)
            {
                hostDomain = address; hostRouteId = r.RouteId; notes.Add($"Host-Adresse „{address}“ angelegt.");
                if (EdgeEnabled && EdgeTrustedIps.Count > 0) await ApplyTrustAsync(node, ct);
            }
            else notes.Add($"Host-Adresse NICHT angelegt: {r.Message}");
        }

        var kind = hostKind;
        string? routeId = null, via = null;
        if (domain is not null)
        {
            if (EdgeEnabled)
            {
                var edge = EdgeSettings;
                kind = edge.Kind;
                if (kind == ProxyKinds.None) return new(false, "Der Edge-Proxy ist eingeschaltet, aber für ihn ist kein Proxy eingerichtet.");
                string? up = null;
                var rewrite = false;
                if (hostDomain is not null && kind == ProxyKinds.Caddy) { up = "https://" + hostDomain; rewrite = true; }
                else if (node is not null)
                {
                    if (string.IsNullOrWhiteSpace(node.Address)) return new(false, $"Node „{node.Name}“ hat keine Adresse für den Edge-Proxy.");
                    up = $"http://{node.Address!.Trim()}:{localPort}";
                }
                var r = await ProxyEngine.ExecuteAsync(new ProxyOp("publish", edge, containerId, localPort, domain, "edge-" + containerName,
                    RouteName(instanceName), Upstream: up, RewriteHost: rewrite), Http(), _docker, ct);
                if (!r.Ok) return new(false, r.Message);
                routeId = r.RouteId;
                via = ProxyVia.Edge;
                notes.Add($"„{domain}“ über den Edge angelegt.");
            }
            else
            {
                via = ProxyVia.Host;
                if (hostKind != ProxyKinds.None)
                {
                    var r = await RunAsync(node, x => new ProxyOp("publish", x, containerId, localPort, domain, containerName, RouteName(instanceName)), null, ct);
                    if (!r.Ok) return new(false, r.Message);
                    routeId = r.RouteId;
                    notes.Add($"Route „{domain}“ angelegt.");
                }
                else notes.Add($"Domain „{domain}“ vermerkt.");
            }
        }

        if (domain is null && hostDomain is null) return new(true, string.Join(" ", notes));
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [InstanceService.PendingRouteKey(node?.Id, containerName)] =
                JsonSerializer.Serialize(new PendingRoute(domain, kind, routeId, pushCanonical, hostDomain, hostDomain is null ? null : hostKind, hostRouteId, via))
        });
        return new(true, string.Join(" ", notes), domain ?? hostDomain);
    }

    private PublishResult Fail(Instance inst, string error)
    {
        inst.ProxyError = error;
        _instances.Log(inst, InstanceEventKind.DomainFailed, "Proxy: " + error);
        _db.SaveChanges();
        return new(false, error);
    }

    /// <summary>Tells the instance its public address via content ops (async, applied on its next beat).
    /// Only an APPROVED instance receives content ops — anything else is reported, not silently skipped.</summary>
    private async Task<string> PushCanonicalAsync(Instance inst, string? url, bool wanted, CancellationToken ct)
    {
        if (!wanted || url is null) return "";
        if (inst.Status != InstanceStatus.Approved) return " Instanz ist nicht freigegeben — ihre Adresse bitte dort selbst eintragen.";
        await _instances.EnqueueContentOpAsync(inst, "setting.set",
            JsonSerializer.Serialize(new { key = "site.canonicalUrl", value = url }), overwrite: true, reason: "Hosting: öffentliche Adresse", ct);
        await _instances.EnqueueContentOpAsync(inst, "setting.set",
            JsonSerializer.Serialize(new { key = "site.behindHttpsProxy", value = url.Length > 0 ? "1" : "" }), overwrite: true, reason: "Hosting: hinter HTTPS-Proxy", ct);
        return url.Length > 0 ? " Die Instanz übernimmt die Adresse beim nächsten Kontakt." : " Die Instanz setzt ihre Adresse beim nächsten Kontakt zurück.";
    }
}
