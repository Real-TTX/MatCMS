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
/// EVERY address the cloud routes — the automatic host addresses (name.server1…, at a host's proxy) and the
/// customer domains (at the edge or at the host's proxy), plus the routes created at provisioning that still wait
/// for their site to enroll. The one place to answer "what is routed where?" without opening each instance.
/// Publishing and removing stay on the instance's own Hosting tab (and the API); here routes can be CHECKED.
/// </summary>
public class DomainsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProxyService _proxy;

    public DomainsModel(AppDbContext db, ProxyService proxy) { _db = db; _proxy = proxy; }

    /// <param name="Kind">"host" = automatic host address, "custom" = customer domain.</param>
    /// <param name="Way">"edge" | "host" | "record" (customer domain only recorded, no proxy).</param>
    public sealed record Row(string Domain, string Kind, Instance Instance, string Way, string? Provider, string? RouteId,
        string? Error, DateTime? PublishedAt, string? Target);

    public List<Row> Rows { get; private set; } = new();
    public List<(string Key, PendingRoute Route)> Pending { get; private set; } = new();

    /// <summary>After "Alle prüfen": "kind:instanceId" → does the route still exist at the proxy (null = unknown).</summary>
    public Dictionary<string, bool?> Checked { get; private set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        var items = await _db.Instances.Include(i => i.Node)
            .Where(i => i.ProxyDomain != null || i.HostDomain != null).ToListAsync();
        foreach (var i in items)
        {
            if (i.HostDomain is { } hd)
                Rows.Add(new(hd, "host", i, "host", i.HostProvider, i.HostRouteId, i.HostRouteError, i.HostPublishedAt, null));
            if (i.ProxyDomain is { } d)
            {
                var way = i.ProxyVia == ProxyVia.Edge ? "edge" : i.ProxyRouteId is null ? "record" : "host";
                // Where the edge forwards to — the host address when there is one (Caddy), else host:port.
                var target = way != "edge" ? null
                    : i.HostDomain is not null && i.ProxyProvider == ProxyKinds.Caddy ? i.HostDomain
                    : i.Node?.Address is { Length: > 0 } a ? $"{a}:{i.LocalPort}" : i.LocalContainerName;
                Rows.Add(new(d, "custom", i, way, i.ProxyProvider, i.ProxyRouteId, i.ProxyError, i.ProxyPublishedAt, target));
            }
        }
        Rows = Rows.OrderBy(r => r.Domain, StringComparer.OrdinalIgnoreCase).ToList();

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
    /// One status call per instance covers both of its routes.</summary>
    public async Task OnPostCheckAsync()
    {
        await LoadAsync();
        foreach (var inst in Rows.Where(r => r.RouteId is not null).Select(r => r.Instance).Distinct())
        {
            var s = await _proxy.StatusAsync(inst, checkProvider: true, HttpContext.RequestAborted);
            if (inst.ProxyRouteId is not null) Checked["custom:" + inst.Id] = s.RouteExists;
            if (inst.HostRouteId is not null) Checked["host:" + inst.Id] = s.HostRouteExists;
        }
    }
}
