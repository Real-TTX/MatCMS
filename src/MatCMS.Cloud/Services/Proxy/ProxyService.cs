using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Nodes;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services.Proxy;

/// <summary>A route created at provisioning, waiting for its instance to enroll (see
/// <see cref="InstanceService.PendingRouteKey"/>; adopted in <c>InstanceService.ClassifyAsync</c>).</summary>
public sealed record PendingRoute(string Domain, string Provider, string? RouteId, bool PushCanonical);

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

    /// <summary>Publishes (or moves) <paramref name="inst"/> to <paramref name="rawDomain"/>. <paramref name="inst"/>
    /// must be tracked. <paramref name="pushCanonical"/> additionally tells the instance its public address.</summary>
    public async Task<PublishResult> PublishAsync(Instance inst, string? rawDomain, bool pushCanonical, CancellationToken ct = default)
    {
        var domain = NormaliseDomain(rawDomain);
        if (domain is null) return new(false, "Keine gültige Domain (nur ein Hostname, z. B. shop.example.de — ohne Pfad, Port oder *).");
        if (await _db.Instances.AnyAsync(i => i.Id != inst.Id && i.ProxyDomain == domain, ct))
            return new(false, $"„{domain}“ ist bereits einer anderen Instanz zugeordnet.");

        var node = await NodeOfAsync(inst, ct);
        var kind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        var manages = kind != ProxyKinds.None;
        if (manages && !HostingActionsService.CanAct(inst))
            return Fail(inst, "Diese Instanz läuft weder auf dem Docker-Host dieser Cloud noch auf einem verbundenen Node — ihre Route kann nicht angelegt werden.");

        string? routeId = null;
        if (manages || inst.ProxyRouteId is not null)
        {
            var r = await RunAsync(node, s => new ProxyOp("publish", s, inst.ContainerId, inst.LocalPort, domain,
                inst.PublicId, RouteName(inst.Name), RouteId: inst.ProxyRouteId, OldKind: inst.ProxyProvider), inst.Id, ct);
            if (!r.Ok)
            {
                if (r.RouteId is not null) { inst.ProxyRouteId = r.RouteId; inst.ProxyProvider = kind; }
                return Fail(inst, r.Message);
            }
            routeId = manages ? r.RouteId : null;
        }

        var moved = inst.ProxyDomain is not null && inst.ProxyDomain != domain;
        inst.ProxyDomain = domain;
        inst.ProxyProvider = kind;
        inst.ProxyRouteId = routeId;
        inst.ProxyError = null;
        inst.ProxyPublishedAt = DateTime.UtcNow;
        inst.Url = "https://" + domain;
        inst.UrlPinned = true;
        _instances.Log(inst, InstanceEventKind.DomainPublished,
            (moved ? "Domain geändert auf " : "Domain veröffentlicht: ") + domain +
            (manages ? $" (Route über {kind}{(node is null ? "" : $", Node „{node.Name}“")})." : " (kein Proxy — nur vermerkt)."));
        await _db.SaveChangesAsync(ct);

        var note = await PushCanonicalAsync(inst, pushCanonical ? "https://" + domain : null, pushCanonical, ct);
        return new(true, $"„{domain}“ veröffentlicht.{note}", domain);
    }

    /// <summary>Removes the domain again. The route goes first; only when that succeeded is the record cleared,
    /// so a proxy that is down never leaves a route behind that the cloud has forgotten about.</summary>
    public async Task<PublishResult> UnpublishAsync(Instance inst, bool pushCanonical, CancellationToken ct = default)
    {
        if (inst.ProxyDomain is null && inst.ProxyRouteId is null) return new(true, "Keine Domain veröffentlicht.");
        if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
        {
            var node = await NodeOfAsync(inst, ct);
            var r = await RunAsync(node, s => new ProxyOp("delete", s, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), inst.Id, ct);
            if (!r.Ok) return Fail(inst, r.Message);
        }
        var old = inst.ProxyDomain;
        inst.ProxyDomain = null; inst.ProxyProvider = null; inst.ProxyRouteId = null; inst.ProxyError = null; inst.ProxyPublishedAt = null;
        // Hand the address back to the heartbeat — the instance reports its own again from the next beat.
        inst.UrlPinned = false;
        inst.Url = null;
        _instances.Log(inst, InstanceEventKind.DomainUnpublished, $"Domain entfernt: {old}.");
        await _db.SaveChangesAsync(ct);
        var note = await PushCanonicalAsync(inst, "", pushCanonical, ct);
        return new(true, $"Domain „{old}“ entfernt.{note}");
    }

    /// <summary>
    /// After a move: the route leaves with the site. The old route is deleted on the OLD host first, then the
    /// domain is published on the host the instance now runs on (<paramref name="inst"/> is already classified
    /// there). That order is deliberate: when both hosts share one proxy, publishing first would update the
    /// very route (same id) the delete would then remove.
    /// </summary>
    public async Task<PublishResult> MoveRouteAsync(Instance inst, Node? from, CancellationToken ct = default)
    {
        if (inst.ProxyDomain is not { } domain) return new(true, "Keine Domain.");
        if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
        {
            var r = await RunAsync(from, s => new ProxyOp("delete", s, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), inst.Id, ct);
            if (!r.Ok) return new(false, "Alte Route nicht entfernt: " + r.Message);
        }
        inst.ProxyRouteId = null;
        inst.ProxyProvider = null;
        return await PublishAsync(inst, domain, pushCanonical: false, ct);
    }

    public sealed record DomainStatus(string? Domain, string? Provider, string? RouteId, string? Error, DateTime? PublishedAt, bool? RouteExists);

    public async Task<DomainStatus> StatusAsync(Instance inst, bool checkProvider, CancellationToken ct = default)
    {
        bool? exists = null;
        if (checkProvider && inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
        {
            var node = await NodeOfAsync(inst, ct);
            var r = await RunAsync(node, s => new ProxyOp("exists", s, Kind: inst.ProxyProvider, RouteId: inst.ProxyRouteId), inst.Id, ct);
            exists = r.Ok ? r.Exists : null;
        }
        return new(inst.ProxyDomain, inst.ProxyProvider, inst.ProxyRouteId, inst.ProxyError, inst.ProxyPublishedAt, exists);
    }

    /// <summary>
    /// Provisioning: the container exists but its instance row does not yet (it appears when the site enrolls
    /// with the join code). The route only needs the container, so it is created now — on the host that runs
    /// it — and remembered under the container name; <c>InstanceService.ClassifyAsync</c> adopts it on the
    /// first beat.
    /// </summary>
    public async Task<PublishResult> PublishForNewContainerAsync(Node? node, string containerId, string containerName, int? localPort,
        string? rawDomain, bool pushCanonical, CancellationToken ct = default)
    {
        var domain = NormaliseDomain(rawDomain);
        if (domain is null) return new(false, "Keine gültige Domain.");
        if (await _db.Instances.AnyAsync(i => i.ProxyDomain == domain, ct))
            return new(false, $"„{domain}“ ist bereits einer anderen Instanz zugeordnet.");

        var kind = node is null ? Settings.Kind : ProxyKinds.Normalise(node.ProxyKind);
        var manages = kind != ProxyKinds.None;
        string? routeId = null;
        if (manages)
        {
            var r = await RunAsync(node, s => new ProxyOp("publish", s, containerId, localPort, domain, containerName, RouteName(containerName)), null, ct);
            if (!r.Ok) return new(false, r.Message);
            routeId = r.RouteId;
        }
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [InstanceService.PendingRouteKey(node?.Id, containerName)] =
                JsonSerializer.Serialize(new PendingRoute(domain, kind, routeId, pushCanonical))
        });
        return new(true, manages ? $"Route „{domain}“ angelegt." : $"Domain „{domain}“ vermerkt.", domain);
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
