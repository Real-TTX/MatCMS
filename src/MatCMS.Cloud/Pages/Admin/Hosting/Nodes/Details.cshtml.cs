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
/// One node: status, the start command (after creating or rotating the token — shown once), proxy &amp; ports,
/// the containers its agent reports and its job history. Every handler goes through <see cref="NodeService"/> /
/// <see cref="ProxyService"/>, the same code as /api/v1/nodes and the MCP node tools.
/// </summary>
public class DetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly NodeService _nodes;
    private readonly ProxyService _proxy;
    private readonly VersionService _version;

    public DetailsModel(AppDbContext db, NodeService nodes, ProxyService proxy, VersionService version)
    { _db = db; _nodes = nodes; _proxy = proxy; _version = version; }

    public string CloudVersion => _version.Current;
    public bool AgentOutdated => NodeService.AgentOutdated(Item, CloudVersion);

    public Node Item { get; private set; } = null!;
    public string? Command { get; private set; }
    public List<NodeContainer> Containers { get; private set; } = new();
    public Dictionary<string, Instance> InstancesByContainer { get; private set; } = new();
    public List<NodeJob> Jobs { get; private set; } = new();
    public ProxyFieldsView ProxyFields => ProxyService.FieldsView(Item);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var n = await _db.Nodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (n is null) return RedirectToPage("Index");
        Item = n;
        if (TempData["NodeCommand"] is string cmd) Command = cmd;
        Containers = NodeService.Inventory(n);
        var mine = await _db.Instances.AsNoTracking().Where(i => i.NodeId == n.Id && i.ContainerId != null).ToListAsync();
        foreach (var c in Containers)
        {
            var hit = mine.FirstOrDefault(i => DockerHostService.IdMatches(c.Id, i.ContainerId!.ToLowerInvariant()));
            if (hit is not null) InstancesByContainer[c.Id] = hit;
        }
        Jobs = await _nodes.JobsAsync(n.Id, 50, HttpContext.RequestAborted);
        if (n.AutoDomainEnabled) MissingHostAddresses = await _proxy.MissingHostAddressCountAsync(n, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>Instances on this node without an automatic address yet.</summary>
    public int MissingHostAddresses { get; private set; }

    public async Task<IActionResult> OnPostAddressesAsync(int id, bool autoDomainEnabled, string? autoDomainBase, string? address)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        var err = await _nodes.UpdateAsync(n, new NodeService.NodeInput(AutoDomainBase: autoDomainBase ?? "", AutoDomainEnabled: autoDomainEnabled,
            Address: address ?? ""), HttpContext.RequestAborted);
        TempData[err is null ? "Flash" : "FlashError"] = err ?? "Adressen gespeichert.";
        return Back(id, "proxy");
    }

    public async Task<IActionResult> OnPostAddressBackfillAsync(int id)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        var r = await _proxy.PublishMissingHostAddressesAsync(n, HttpContext.RequestAborted);
        TempData[r.Failed == 0 ? "Flash" : "FlashError"] = $"{r.Created} Adresse(n) angelegt." + (r.Failed > 0 ? $" {r.Failed} fehlgeschlagen: " + string.Join(" · ", r.Errors) : "");
        return Back(id, "proxy");
    }

    private IActionResult Back(int id, string tab) => RedirectToPage(new { id, tab });

    private NodeService.NodeInput Input(string? name, int? portFrom, int? portTo, string? hostingMode, string? matcadUrl, string? matcadToken,
        bool clearMatcadToken, string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost) =>
        // Hidden fields still post (empty) — which is exactly "no value" for a field of another provider, so
        // everything is written as posted; only the Matcad key keeps its "empty = keep" rule.
        new(name, portFrom, portTo, hostingMode, matcadUrl ?? "", matcadToken, clearMatcadToken, caddyAdminUrl ?? "", caddyServer ?? "",
            proxyUpstream, proxyNetwork ?? "", proxyUpstreamHost ?? "");

    public async Task<IActionResult> OnPostSaveAsync(int id, string? name, int? portFrom, int? portTo, string? hostingMode, string? matcadUrl,
        string? matcadToken, bool clearMatcadToken, string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork,
        string? proxyUpstreamHost)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        var err = await _nodes.UpdateAsync(n, Input(name, portFrom, portTo, hostingMode, matcadUrl, matcadToken, clearMatcadToken,
            caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost), HttpContext.RequestAborted);
        TempData[err is null ? "Flash" : "FlashError"] = err ?? "Node gespeichert.";
        return Back(id, "proxy");
    }

    /// <summary>"Speichern und Verbindung testen" — saves first, so what is tested is what is stored. The test
    /// runs ON the node (its proxy is reached from there, never from the cloud).</summary>
    public async Task<IActionResult> OnPostProxyTestAsync(int id, string? name, int? portFrom, int? portTo, string? hostingMode, string? matcadUrl,
        string? matcadToken, bool clearMatcadToken, string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork,
        string? proxyUpstreamHost)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        var err = await _nodes.UpdateAsync(n, Input(name, portFrom, portTo, hostingMode, matcadUrl, matcadToken, clearMatcadToken,
            caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost), HttpContext.RequestAborted);
        if (err is not null) { TempData["FlashError"] = err; return Back(id, "proxy"); }
        var r = await _proxy.TestAsync(n, HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = "Gespeichert. " + r.Message;
        return Back(id, "proxy");
    }

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
        return Back(id, "overview");
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id, bool revoked)
    {
        var n = await _db.Nodes.FindAsync(id);
        if (n is null) return RedirectToPage("Index");
        await _nodes.SetRevokedAsync(n, revoked, HttpContext.RequestAborted);
        TempData["Flash"] = revoked ? "Node gesperrt — er bekommt keine Aufträge mehr." : "Node wieder freigegeben.";
        return Back(id, "overview");
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
