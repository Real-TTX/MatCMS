using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Nodes;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting.Nodes;

/// <summary>
/// ONE page per Docker host — "Dieser Host" (no id: the cloud's own daemon) and every node — with the same tabs:
/// Übersicht, Container, Docker (images + cleanup), Proxy (reverse proxy, automatic addresses, wildcard),
/// Einstellungen, and for a node its jobs. Everything that belongs to a machine is here; Hosting's own tabs keep only
/// what spans hosts (dashboard, all instances, domains, the edge proxy, the module switch).
/// <para>"Dieser Host" stores its configuration as cloud settings, a node on its row — the forms are the same
/// partials, the handlers branch on <c>id</c>. Every write goes through <see cref="NodeService"/> /
/// <see cref="ProxyService"/> / <see cref="HostImagesService"/>, the same code as /api/v1 and MCP.</para>
/// </summary>
public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly NodeService _nodes;
    private readonly ProxyService _proxy;
    private readonly VersionService _version;
    private readonly HostingOverviewService _overview;
    private readonly HostImagesService _images;
    private readonly HostingService _hosting;
    private readonly DockerHostService _docker;
    private readonly CloudContext _cloud;
    private readonly SecretProtector _secrets;
    private readonly Localizer _t;

    public DetailsModel(AppDbContext db, NodeService nodes, ProxyService proxy, VersionService version, HostingOverviewService overview,
        HostImagesService images, HostingService hosting, DockerHostService docker, CloudContext cloud, SecretProtector secrets, Localizer t)
    {
        _db = db; _nodes = nodes; _proxy = proxy; _version = version; _overview = overview; _images = images;
        _hosting = hosting; _docker = docker; _cloud = cloud; _secrets = secrets; _t = t;
    }

    /// <summary>The node, or null for "Dieser Host".</summary>
    public Node? Item { get; private set; }
    public bool IsLocal => Item is null;
    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);

    public string CloudVersion => _version.Current;
    public bool AgentOutdated => Item is not null && NodeService.AgentOutdated(Item, CloudVersion);
    public string? Command { get; private set; }

    /// <summary>This host's line of the hosting overview (state, size, load) and its containers.</summary>
    public HostingOverviewService.HostRow? Host { get; private set; }
    public List<HostingOverviewService.SiteRow> Sites { get; private set; } = new();
    public List<NodeJob> Jobs { get; private set; } = new();

    // Proxy tab.
    public ProxyFieldsView ProxyFields => Item is null ? _proxy.FieldsView() : ProxyService.FieldsView(Item);
    public ProxyService.WildcardConfig Wildcard => _proxy.WildcardFor(Item);
    public bool AutoDomainEnabled => Item?.AutoDomainEnabled ?? _cloud.Flag(SettingKeys.HostingAutoDomainEnabled);
    public string? AutoDomainBase => Item is null ? _cloud.Get(SettingKeys.HostingAutoDomainBase) : Item.AutoDomainBase;
    public int MissingHostAddresses { get; private set; }

    // Einstellungen tab of "Dieser Host" (a node keeps its ports on its row).
    public string Get(string key) => _cloud.Get(key) ?? "";
    public int? NextPort { get; private set; }
    public int UsedPortCount { get; private set; }
    public string? Endpoint => IsLocal && _docker.Configured ? _docker.Endpoint : null;

    // Docker tab — loaded on demand (?handler=Images): for a node it is a round trip to its agent.
    public HostImagesService.Result? Images { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (!await LoadAsync(id)) return RedirectToPage("Index");
        if (TempData["NodeCommand"] is string cmd) Command = cmd;
        var data = await _overview.BuildAsync(HttpContext.RequestAborted);
        Host = data.Hosts.FirstOrDefault(h => h.NodeId == id);
        Sites = data.Sites.Where(s => s.Host.NodeId == id).ToList();
        if (Item is not null) Jobs = await _nodes.JobsAsync(Item.Id, 50, HttpContext.RequestAborted);
        if (AutoDomainEnabled) MissingHostAddresses = await _proxy.MissingHostAddressCountAsync(Item, HttpContext.RequestAborted);
        if (IsLocal && ModuleEnabled)
        {
            NextPort = await _hosting.NextFreePortAsync(HttpContext.RequestAborted);
            UsedPortCount = (await _hosting.UsedPortsAsync(HttpContext.RequestAborted))?.Count ?? 0;
        }
        return Page();
    }

    /// <summary>The Docker tab's content, fetched when the tab is opened.</summary>
    public async Task<IActionResult> OnGetImagesAsync(int? id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Images = await _images.ListAsync(Item, HttpContext.RequestAborted);
        return Partial("_HostImages", this);
    }

    private async Task<bool> LoadAsync(int? id)
    {
        if (id is null) return true;
        Item = await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return Item is not null;
    }

    // An explicit id = null in the route values makes link generation fail for the optional {id:int?} ("No page named
    // … matches"), so "Dieser Host" gets no id at all — here and in every URL the view builds (see Route).
    private IActionResult Back(int? id, string tab) => RedirectToPage("/Admin/Hosting/Nodes/Details", Route(id, tab: tab));

    /// <summary>Route values for this page: the id only when there is one.</summary>
    public static Microsoft.AspNetCore.Routing.RouteValueDictionary Route(int? id, string? handler = null, string? tab = null)
    {
        var r = new Microsoft.AspNetCore.Routing.RouteValueDictionary();
        if (id is int i) r["id"] = i;
        if (handler is not null) r["handler"] = handler;
        if (tab is not null) r["tab"] = tab;
        return r;
    }

    // ---- Docker -------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostPruneImagesAsync(int? id)
    {
        Node? n = id is null ? null : await _db.Nodes.FindAsync(id);
        if (id is not null && n is null) return RedirectToPage("Index");
        var (ok, message, r) = await _images.PruneAsync(n, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = !ok ? message
            : r is null || r.Removed == 0 ? _t["docker.pruneNone"]
            : _t["docker.pruneDone", r.Removed, (r.BytesReclaimed / (1024.0 * 1024.0)).ToString("0.#")];
        return Back(id, "docker");
    }

    // ---- Proxy --------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostProxyAsync(int? id, string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        var err = await SaveProxyAsync(id, hostingMode, matcadUrl, matcadToken, clearMatcadToken, caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost);
        TempData[err is null ? "Flash" : "FlashError"] = err ?? "Proxy gespeichert.";
        return Back(id, "proxy");
    }

    /// <summary>"Speichern und Verbindung testen" — saves first, so what is tested is what is stored. For a node the
    /// test runs ON the node (its proxy is reached from there, never from the cloud).</summary>
    public async Task<IActionResult> OnPostProxyTestAsync(int? id, string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        var err = await SaveProxyAsync(id, hostingMode, matcadUrl, matcadToken, clearMatcadToken, caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost);
        if (err is not null) { TempData["FlashError"] = err; return Back(id, "proxy"); }
        Node? n = id is null ? null : await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        var r = await _proxy.TestAsync(n, HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = "Gespeichert. " + r.Message;
        return Back(id, "proxy");
    }

    private async Task<string?> SaveProxyAsync(int? id, string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        if (id is not null)
        {
            var n = await _db.Nodes.FindAsync(id);
            if (n is null) return "Node nicht gefunden.";
            // Hidden fields still post (empty) — which is exactly "no value" for a field of another provider, so
            // everything is written as posted; only the Matcad key keeps its "empty = keep" rule.
            return await _nodes.UpdateAsync(n, new NodeService.NodeInput(Provider: hostingMode, MatcadUrl: matcadUrl ?? "", MatcadToken: matcadToken,
                ClearMatcadToken: clearMatcadToken, CaddyAdminUrl: caddyAdminUrl ?? "", CaddyServer: caddyServer ?? "", Upstream: proxyUpstream,
                Network: proxyNetwork ?? "", UpstreamHost: proxyUpstreamHost ?? ""), HttpContext.RequestAborted);
        }
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            // Only a known provider name is stored; anything else — also the historical "docker" — means "no proxy".
            [SettingKeys.HostingMode] = ProxyKinds.Normalise(hostingMode),
            [SettingKeys.HostingCaddyAdminUrl] = caddyAdminUrl?.Trim().TrimEnd('/'),
            [SettingKeys.HostingCaddyServer] = caddyServer?.Trim(),
            [SettingKeys.HostingProxyUpstream] = UpstreamModes.Normalise(proxyUpstream),
            [SettingKeys.HostingProxyNetwork] = proxyNetwork?.Trim(),
            [SettingKeys.HostingProxyUpstreamHost] = proxyUpstreamHost?.Trim(),
            [SettingKeys.HostingMatcadUrl] = matcadUrl?.Trim().TrimEnd('/'),
            // Wie beim SMTP-Passwort: leer BEHÄLT, nur der ausdrückliche Haken löscht — das Feld wird leer gerendert.
            [SettingKeys.HostingMatcadToken] = clearMatcadToken ? ""
                : string.IsNullOrEmpty(matcadToken) ? Get(SettingKeys.HostingMatcadToken)
                : _secrets.Protect(matcadToken),
        });
        return null;
    }

    /// <summary>Automatic addresses (name.&lt;base&gt;) with the wildcard certificate, and for a node the address the edge
    /// reaches it at.</summary>
    public async Task<IActionResult> OnPostAddressesAsync(int? id, bool autoDomainEnabled, string? autoDomainBase, string? address,
        bool wildcard, string? dnsProvider, string? dnsCredentials)
    {
        Node? n = null;
        if (id is not null)
        {
            n = await _db.Nodes.FindAsync(id);
            if (n is null) return RedirectToPage("Index");
            var err = await _nodes.UpdateAsync(n, new NodeService.NodeInput(AutoDomainBase: autoDomainBase ?? "", AutoDomainEnabled: autoDomainEnabled,
                Address: address ?? ""), HttpContext.RequestAborted);
            if (err is not null) { TempData["FlashError"] = err; return Back(id, "proxy"); }
        }
        else
        {
            if (autoDomainEnabled && ProxyService.NormaliseDomain(autoDomainBase) is null)
            {
                TempData["FlashError"] = "Bitte eine gültige Basis-Domain angeben, z. B. cloud.example.de.";
                return Back(id, "proxy");
            }
            await _proxy.SetAutoDomainAsync(autoDomainEnabled, autoDomainBase);
        }
        var msg = "Adressen gespeichert.";
        // The wildcard follows the base domain; switching it (or the domain) applies it at the host's proxy right away
        // (for a node: ON the node).
        var before = _proxy.WildcardFor(n);
        if (wildcard || before.Enabled || before.RouteId is not null)
        {
            var w = await _proxy.SetWildcardAsync(n, wildcard && autoDomainEnabled, dnsProvider, dnsCredentials, HttpContext.RequestAborted);
            if (!w.Ok) { TempData["FlashError"] = msg + " Wildcard-Zertifikat: " + w.Message; return Back(id, "proxy"); }
            msg += " " + w.Message;
        }
        if (n is not null)
        {
            var t = await _proxy.ApplyTrustAsync(n, HttpContext.RequestAborted);
            if (!t.Ok && _proxy.EdgeEnabled) msg += " Hinweis: " + t.Message;
        }
        TempData["Flash"] = msg;
        return Back(id, "proxy");
    }

    public async Task<IActionResult> OnPostAddressBackfillAsync(int? id)
    {
        Node? n = id is null ? null : await _db.Nodes.FindAsync(id);
        if (id is not null && n is null) return RedirectToPage("Index");
        var r = await _proxy.PublishMissingHostAddressesAsync(n, HttpContext.RequestAborted);
        TempData[r.Failed == 0 ? "Flash" : "FlashError"] = $"{r.Created} Adresse(n) angelegt." + (r.Failed > 0 ? $" {r.Failed} fehlgeschlagen: " + string.Join(" · ", r.Errors) : "");
        return Back(id, "proxy");
    }

    // ---- Einstellungen ------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostSettingsAsync(int? id, string? name, string? portFrom, string? portTo, string? namePattern)
    {
        if (id is not null)
        {
            var n = await _db.Nodes.FindAsync(id);
            if (n is null) return RedirectToPage("Index");
            var err = await _nodes.UpdateAsync(n, new NodeService.NodeInput(Name: name ?? "",
                PortFrom: int.TryParse(portFrom, out var f) ? f : null, PortTo: int.TryParse(portTo, out var t) ? t : null), HttpContext.RequestAborted);
            TempData[err is null ? "Flash" : "FlashError"] = err ?? "Node gespeichert.";
            return Back(id, "settings");
        }
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            // Nur ein gültiger Bereich wird gespeichert. Ein verdrehter oder unsinniger würde beim Anlegen entweder nie
            // einen freien Port finden oder einen belegten vorschlagen.
            [SettingKeys.HostingPortFrom] = Port(portFrom),
            [SettingKeys.HostingPortTo] = Port(portTo),
            [SettingKeys.HostingNamePattern] = namePattern?.Trim(),
        });
        TempData["Flash"] = "Einstellungen gespeichert.";
        return Back(id, "settings");
    }

    /// <summary>Ein Port oder nichts — 1024 bis 65535, alles andere wird verworfen statt gespeichert.</summary>
    private static string Port(string? raw) =>
        int.TryParse(raw, out var p) && p >= 1024 && p <= 65535 ? p.ToString() : "";

    // ---- Node only ----------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostUpdateAgentAsync(int id)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        var r = await _nodes.UpdateAgentAsync(n, HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = r.Message;
        return Back(id, "overview");
    }

    public async Task<IActionResult> OnPostRotateAsync(int id)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        var token = await _nodes.RotateTokenAsync(n, HttpContext.RequestAborted);
        TempData["NodeCommand"] = _nodes.AgentCommand(n, token);
        TempData["Flash"] = "Neuer Token erzeugt — der Agent muss mit dem neuen Befehl neu gestartet werden.";
        return Back(id, "settings");
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, bool revoked)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        await _nodes.SetRevokedAsync(n, revoked, HttpContext.RequestAborted);
        TempData["Flash"] = revoked ? "Node gesperrt — er bekommt keine Aufträge mehr." : "Node wieder freigegeben.";
        return Back(id, "settings");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        await _nodes.DeleteAsync(n, HttpContext.RequestAborted);
        TempData["Flash"] = $"Node „{n.Name}“ gelöscht. Seine Websites laufen weiter, die Cloud steuert sie nur nicht mehr.";
        return RedirectToPage("Index");
    }
}
