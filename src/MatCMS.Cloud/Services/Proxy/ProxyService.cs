using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services.Proxy;

/// <summary>A route created at provisioning, waiting for its instance to enroll (see
/// <see cref="SettingKeys.HostingPendingRoutePrefix"/>; adopted in <c>InstanceService.ClassifyAsync</c>).</summary>
public sealed record PendingRoute(string Domain, string Provider, string? RouteId, bool PushCanonical);

/// <summary>
/// Publishing an instance under a domain on the configured reverse proxy (Hosting increment 3). One
/// implementation behind the Hosting tab, the operator API and the MCP tools. It owns the three things a
/// provider does not: checking the domain, making the container REACHABLE for the proxy (joining the proxy's
/// network, or pointing at the host port), and recording the result on the instance — whose
/// <see cref="Instance.Url"/> is then pinned to the domain.
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
    private readonly IHttpClientFactory _http;

    public ProxyService(AppDbContext db, CloudContext cloud, SecretProtector secrets, DockerHostService docker,
        InstanceService instances, IHttpClientFactory http)
    {
        _db = db; _cloud = cloud; _secrets = secrets; _docker = docker; _instances = instances; _http = http;
    }

    public ProxySettings Settings => new(
        ProxyKinds.Normalise(_cloud.Get(SettingKeys.HostingMode)),
        _cloud.Get(SettingKeys.HostingMatcadUrl),
        _secrets.Unprotect(_cloud.Get(SettingKeys.HostingMatcadToken)),
        _cloud.Get(SettingKeys.HostingCaddyAdminUrl),
        _cloud.Get(SettingKeys.HostingCaddyServer) is { Length: > 0 } srv ? srv : "srv0",
        UpstreamModes.Normalise(_cloud.Get(SettingKeys.HostingProxyUpstream)),
        _cloud.Get(SettingKeys.HostingProxyNetwork),
        _cloud.Get(SettingKeys.HostingProxyUpstreamHost));

    /// <summary>The provider for <paramref name="kind"/> (default: the configured one).</summary>
    public IProxyProvider Provider(string? kind = null)
    {
        var s = Settings;
        var http = _http.CreateClient("proxy");
        http.Timeout = TimeSpan.FromSeconds(15);
        return ProxyKinds.Normalise(kind ?? s.Kind) switch
        {
            ProxyKinds.Matcad => new MatcadProvider(http, s.MatcadUrl ?? "", s.MatcadToken),
            ProxyKinds.Caddy => new CaddyProvider(http, s.CaddyAdminUrl ?? "", s.CaddyServer),
            _ => new NoProxyProvider(),
        };
    }

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

    /// <summary>A partial update of the proxy configuration for the API/MCP (null = keep). The Matcad token is
    /// encrypted like on the settings page and never read back.</summary>
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
            provider = s.Kind, managesRoutes = Provider().ManagesRoutes,
            matcadUrl = s.MatcadUrl, matcadTokenSet = !string.IsNullOrEmpty(s.MatcadToken),
            caddyAdminUrl = s.CaddyAdminUrl, caddyServer = s.CaddyServer,
            upstream = s.UpstreamMode, network = s.Network, upstreamHost = s.UpstreamHost,
        };
    }

    public sealed record TestResult(bool Ok, string Message, string Kind);

    /// <summary>Provider reachable, plus — for the network mode — the proxy network actually exists.</summary>
    public async Task<TestResult> TestAsync(CancellationToken ct = default)
    {
        var s = Settings;
        var p = Provider();
        if (!p.ManagesRoutes) return new(true, "Kein Proxy: Instanzen sind über ihren Host-Port erreichbar; Domains werden nur vermerkt.", s.Kind);
        if (p is MatcadProvider && string.IsNullOrWhiteSpace(s.MatcadUrl)) return new(false, "Keine Matcad-Adresse eingetragen.", s.Kind);
        if (p is CaddyProvider && string.IsNullOrWhiteSpace(s.CaddyAdminUrl)) return new(false, "Keine Caddy-Admin-Adresse eingetragen.", s.Kind);

        var r = await p.TestAsync(ct);
        var msg = r.Error ?? "";
        if (s.UpstreamMode == UpstreamModes.Network)
        {
            if (string.IsNullOrWhiteSpace(s.Network)) return new(false, msg + " Kein Proxy-Netz eingetragen (Modus „Netzwerk“).", s.Kind);
            if (!await _docker.NetworkExistsAsync(s.Network!, ct)) return new(false, msg + $" Das Docker-Netz „{s.Network}“ existiert nicht.", s.Kind);
            msg += $" Netz „{s.Network}“ vorhanden.";
        }
        else if (string.IsNullOrWhiteSpace(s.UpstreamHost))
            return new(false, msg + " Kein Upstream-Host eingetragen (Modus „Host-Port“).", s.Kind);
        return new(r.Ok, msg.Trim(), s.Kind);
    }

    /// <summary>
    /// The address the proxy must forward to — and, in network mode, the step that makes it resolvable:
    /// the container is connected to the proxy's network (live). Container name and port come from the
    /// daemon, never from a display name.
    /// </summary>
    private async Task<(string? Upstream, string? Error)> UpstreamAsync(string containerId, int? localPort, CancellationToken ct)
    {
        var s = Settings;
        if (s.UpstreamMode == UpstreamModes.HostPort)
        {
            if (string.IsNullOrWhiteSpace(s.UpstreamHost)) return (null, "Kein Upstream-Host eingetragen (Modus „Host-Port“).");
            if (localPort is null) return (null, "Der Container veröffentlicht keinen Host-Port.");
            return ($"http://{s.UpstreamHost!.Trim()}:{localPort}", null);
        }
        if (string.IsNullOrWhiteSpace(s.Network)) return (null, "Kein Proxy-Netz eingetragen (Modus „Netzwerk“).");
        var (ok, msg, name) = await _docker.ConnectToNetworkAsync(containerId, s.Network!, ct);
        if (!ok || string.IsNullOrEmpty(name)) return (null, msg);
        // 8080 is the container's own port (the image EXPOSEs it; never changed) — not the host port.
        return ($"http://{name}:8080", null);
    }

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

        var s = Settings;
        var provider = Provider();
        string? routeId = null;

        if (provider.ManagesRoutes)
        {
            if (!HostingActionsService.IsLocal(inst))
                return Fail(inst, "Diese Instanz läuft nicht auf dem Docker-Host dieser Cloud — ihre Route kann hier nicht angelegt werden.");

            // A route left behind by a DIFFERENT provider (the setting changed since) goes first.
            if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null && inst.ProxyProvider != s.Kind)
                await Provider(inst.ProxyProvider).DeleteAsync(inst.ProxyRouteId, ct);

            var (upstream, err) = await UpstreamAsync(inst.ContainerId!, inst.LocalPort, ct);
            if (upstream is null) return Fail(inst, err ?? "Upstream nicht ermittelbar.");

            var existing = inst.ProxyProvider == s.Kind ? inst.ProxyRouteId : null;
            var r = await provider.UpsertAsync(existing, inst.PublicId, RouteName(inst.Name), domain, upstream, ct);
            if (!r.Ok)
            {
                if (r.RouteId is not null) { inst.ProxyRouteId = r.RouteId; inst.ProxyProvider = s.Kind; }
                return Fail(inst, r.Error ?? "Route konnte nicht angelegt werden.");
            }
            routeId = r.RouteId;
        }
        else if (inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
        {
            // Switched to "no proxy": the old provider's route must not linger pointing at the site.
            await Provider(inst.ProxyProvider).DeleteAsync(inst.ProxyRouteId, ct);
        }

        var moved = inst.ProxyDomain is not null && inst.ProxyDomain != domain;
        inst.ProxyDomain = domain;
        inst.ProxyProvider = s.Kind;
        inst.ProxyRouteId = routeId;
        inst.ProxyError = null;
        inst.ProxyPublishedAt = DateTime.UtcNow;
        inst.Url = "https://" + domain;
        inst.UrlPinned = true;
        _instances.Log(inst, InstanceEventKind.DomainPublished,
            (moved ? "Domain geändert auf " : "Domain veröffentlicht: ") + domain +
            (provider.ManagesRoutes ? $" (Route über {s.Kind})." : " (kein Proxy — nur vermerkt)."));
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
            var r = await Provider(inst.ProxyProvider).DeleteAsync(inst.ProxyRouteId, ct);
            if (!r.Ok) return Fail(inst, r.Error ?? "Route konnte nicht entfernt werden.");
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

    public sealed record DomainStatus(string? Domain, string? Provider, string? RouteId, string? Error, DateTime? PublishedAt, bool? RouteExists);

    public async Task<DomainStatus> StatusAsync(Instance inst, bool checkProvider, CancellationToken ct = default)
    {
        bool? exists = null;
        if (checkProvider && inst.ProxyRouteId is not null && inst.ProxyProvider is not null)
            exists = await Provider(inst.ProxyProvider).ExistsAsync(inst.ProxyRouteId, ct);
        return new(inst.ProxyDomain, inst.ProxyProvider, inst.ProxyRouteId, inst.ProxyError, inst.ProxyPublishedAt, exists);
    }

    /// <summary>
    /// Provisioning: the container exists but its instance row does not yet (it appears when the site enrolls
    /// with the join code). The route only needs the container, so it is created now and remembered under the
    /// container name; <c>InstanceService.ClassifyAsync</c> adopts it on the first beat.
    /// </summary>
    public async Task<PublishResult> PublishForNewContainerAsync(string containerId, string containerName, int? localPort,
        string? rawDomain, bool pushCanonical, CancellationToken ct = default)
    {
        var domain = NormaliseDomain(rawDomain);
        if (domain is null) return new(false, "Keine gültige Domain.");
        if (await _db.Instances.AnyAsync(i => i.ProxyDomain == domain, ct))
            return new(false, $"„{domain}“ ist bereits einer anderen Instanz zugeordnet.");

        var s = Settings;
        var provider = Provider();
        string? routeId = null;
        if (provider.ManagesRoutes)
        {
            var (upstream, err) = await UpstreamAsync(containerId, localPort, ct);
            if (upstream is null) return new(false, err ?? "Upstream nicht ermittelbar.");
            var r = await provider.UpsertAsync(null, containerName, RouteName(containerName), domain, upstream, ct);
            if (!r.Ok) return new(false, r.Error ?? "Route konnte nicht angelegt werden.");
            routeId = r.RouteId;
        }
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            [SettingKeys.HostingPendingRoutePrefix + containerName] =
                JsonSerializer.Serialize(new PendingRoute(domain, s.Kind, routeId, pushCanonical))
        });
        return new(true, provider.ManagesRoutes ? $"Route „{domain}“ angelegt." : $"Domain „{domain}“ vermerkt.", domain);
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
