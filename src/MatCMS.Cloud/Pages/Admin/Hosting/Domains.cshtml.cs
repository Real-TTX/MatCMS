using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Every domain the cloud has published — per instance, with the host it runs on, the provider and the route —
/// plus the routes created at provisioning that still wait for their site to enroll. The one place to answer
/// "what is routed where?" without opening each instance. Publishing and removing stay on the instance's own
/// Hosting tab (and /api/v1/instances/{id}/domain); here routes can be CHECKED against the proxy.
/// </summary>
public class DomainsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProxyService _proxy;

    public DomainsModel(AppDbContext db, ProxyService proxy) { _db = db; _proxy = proxy; }

    public List<Instance> Items { get; private set; } = new();
    public List<(string Key, PendingRoute Route)> Pending { get; private set; } = new();

    /// <summary>After "Alle prüfen": instance id → does the route still exist at the proxy (null = not asked / unknown).</summary>
    public Dictionary<int, bool?> Checked { get; private set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Items = await _db.Instances.Include(i => i.Node).Where(i => i.ProxyDomain != null)
            .OrderBy(i => i.ProxyDomain).ToListAsync();
        var rows = await _db.CloudSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(SettingKeys.HostingPendingRoutePrefix)).ToListAsync();
        foreach (var r in rows)
        {
            PendingRoute? p = null;
            try { p = string.IsNullOrWhiteSpace(r.Value) ? null : JsonSerializer.Deserialize<PendingRoute>(r.Value); } catch { }
            if (p is not null) Pending.Add((r.Key[SettingKeys.HostingPendingRoutePrefix.Length..], p));
        }
    }

    /// <summary>Asks each proxy whether the routes are still there (they can be removed by hand at the proxy).
    /// One call per routed domain, on the host it lives on.</summary>
    public async Task OnPostCheckAsync()
    {
        await LoadAsync();
        foreach (var i in Items.Where(i => i.ProxyRouteId is not null))
            Checked[i.Id] = (await _proxy.StatusAsync(i, checkProvider: true, HttpContext.RequestAborted)).RouteExists;
    }
}
