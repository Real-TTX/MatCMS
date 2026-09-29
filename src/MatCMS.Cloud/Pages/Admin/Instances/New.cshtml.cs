using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// Eine neue Instanz anlegen — die Seite, die den Erzeuger auslöst.
///
/// <para>Erreichbar nur, wenn Hosting eingeschaltet ist. Wer die Adresse direkt aufruft, landet
/// wieder in der Liste: eine Seite, deren einzige Schaltfläche sicher scheitert, ist keine.</para>
/// </summary>
public class NewModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly HostingService _hosting;
    private readonly ReleaseWatcher _releases;
    private readonly MatCMS.Cloud.Services.Proxy.ProxyService _proxy;

    public NewModel(AppDbContext db, HostingService hosting, ReleaseWatcher releases, MatCMS.Cloud.Services.Proxy.ProxyService proxy)
    {
        _db = db;
        _hosting = hosting;
        _releases = releases;
        _proxy = proxy;
    }

    public List<Profile> Profiles { get; private set; } = [];
    public List<Node> Nodes { get; private set; } = [];
    /// <summary>True when a real proxy (Matcad/Caddy) will route the domain on this host or a node; false = it is only recorded.</summary>
    public bool ProxyRoutes => _proxy.ManagesRoutes(null) || Nodes.Any(n => _proxy.ManagesRoutes(n));
    /// <summary>Somewhere to create it: this host has a free port, or a node is connected.</summary>
    public bool CanCreate => NextPort is not null || Nodes.Any(n => n.IsOnline(DateTime.UtcNow));
    public int? NextPort { get; private set; }

    /// <summary>Die neueste bekannte Version als Vorschlag. "latest" bleibt möglich, aber ein
    /// festgenagelter Stand ist die ehrlichere Vorgabe: er sagt, was gerade entsteht.</summary>
    public string? LatestVersion => _releases.LatestVersion;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!_hosting.Enabled) return RedirectToPage("/Admin/Hosting/Settings");
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Profiles = await _db.Profiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        Nodes = await _db.Nodes.AsNoTracking().Where(n => !n.Revoked).OrderBy(n => n.Name).ToListAsync();
        NextPort = await _hosting.NextFreePortAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostAsync(string? name, int profileId, string? domain, string? imageTag, int? nodeId)
    {
        if (!_hosting.Enabled) return RedirectToPage("/Admin/Hosting/Settings");

        // One path for UI, API and MCP (HostingService.ProvisionAsync). The instance appears in the list when
        // IT enrolls — not now; the route set up here is adopted on its first beat.
        var r = await _hosting.ProvisionAsync(name, profileId, domain, imageTag, nodeId, pushCanonical: true, HttpContext.RequestAborted);
        if (!r.Ok)
        {
            TempData["FlashError"] = r.Message;
            return RedirectToPage();
        }
        TempData[r.DomainFailed ? "FlashError" : "Flash"] = r.Message;
        return RedirectToPage("Index");
    }
}
