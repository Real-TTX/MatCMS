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
/// EVERY address the cloud routes, grouped by instance: its customer domain (at the edge or at the host's proxy), its
/// further domains (www.…, old domains) and its automatic host address — plus the routes created at provisioning
/// that still wait for their site to enroll. The one place to answer "what is routed where?" without opening each
/// instance. Publishing and removing stay on the instance's own Hosting tab (and the API); here routes can be CHECKED.
/// <para>The grouping is markup only (admin-list.js: a header row data-list-group, its rows data-list-in) — search and
/// the kind filter work on the domain rows, and a group heading disappears when none of its rows is left.</para>
/// </summary>
public class DomainsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProxyService _proxy;

    public DomainsModel(AppDbContext db, ProxyService proxy) { _db = db; _proxy = proxy; }

    /// <param name="Kind">"custom" = main customer domain, "extra" = further domain, "host" = automatic host address.</param>
    /// <param name="Way">"edge" | "host" | "record" (only recorded, no proxy route).</param>
    /// <param name="CheckKey">Key into <see cref="Checked"/>.</param>
    public sealed record Row(string Domain, string Kind, Instance Instance, string Way, string? Provider, string? RouteId,
        string? Error, DateTime? PublishedAt, string? Target, string CheckKey);

    /// <summary>One instance and its addresses, main domain first, then further domains, then the host address.</summary>
    public sealed record Group(Instance Instance, List<Row> Rows);

    public List<Group> Groups { get; private set; } = new();
    public int RowCount => Groups.Sum(g => g.Rows.Count);
    public List<(string Key, PendingRoute Route)> Pending { get; private set; } = new();

    /// <summary>After "Alle prüfen": check key → does the route still exist at the proxy (null = unknown).</summary>
    public Dictionary<string, bool?> Checked { get; private set; } = new();

    private List<InstanceDomain> _extras = new();

    public async Task OnGetAsync() => await LoadAsync();

    private static int Order(string kind) => kind switch { "custom" => 0, "extra" => 1, _ => 2 };

    private async Task LoadAsync()
    {
        var items = await _db.Instances.Include(i => i.Node)
            .Where(i => i.ProxyDomain != null || i.HostDomain != null).ToListAsync();
        _extras = await _db.InstanceDomains.Include(x => x.Instance!).ThenInclude(i => i.Node).ToListAsync();
        var rows = new List<Row>();
        foreach (var i in items)
        {
            if (i.HostDomain is { } hd)
                rows.Add(new(hd, "host", i, "host", i.HostProvider, i.HostRouteId, i.HostRouteError, i.HostPublishedAt, null, "host:" + i.Id));
            if (i.ProxyDomain is { } d)
            {
                var way = i.ProxyVia == ProxyVia.Edge ? "edge" : i.ProxyRouteId is null ? "record" : "host";
                // Where the edge forwards to — the host address when there is one (Caddy), else host:port.
                var target = way != "edge" ? null
                    : i.HostDomain is not null && i.ProxyProvider == ProxyKinds.Caddy ? i.HostDomain
                    : i.Node?.Address is { Length: > 0 } a ? $"{a}:{i.LocalPort}" : i.LocalContainerName;
                rows.Add(new(d, "custom", i, way, i.ProxyProvider, i.ProxyRouteId, i.ProxyError, i.ProxyPublishedAt, target, "custom:" + i.Id));
            }
        }
        foreach (var x in _extras)
            rows.Add(new(x.Domain, "extra", x.Instance!, x.Via == ProxyVia.Edge ? "edge" : x.RouteId is null ? "record" : "host", x.Provider, x.RouteId,
                x.Error, x.PublishedAt, x.Mode == InstanceDomainModes.Alias ? null : "https://" + x.Instance!.ProxyDomain, "extra:" + x.Id));

        Groups = rows.GroupBy(r => r.Instance.Id)
            .Select(g => new Group(g.First().Instance, g.OrderBy(r => Order(r.Kind)).ThenBy(r => r.Domain, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.Instance.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pending = await _db.CloudSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(SettingKeys.HostingPendingRoutePrefix)).ToListAsync();
        foreach (var r in pending)
        {
            PendingRoute? p = null;
            try { p = string.IsNullOrWhiteSpace(r.Value) ? null : JsonSerializer.Deserialize<PendingRoute>(r.Value); } catch { }
            if (p is not null) Pending.Add((r.Key[SettingKeys.HostingPendingRoutePrefix.Length..], p));
        }
    }

    /// <summary>Asks each proxy whether the routes are still there (they can be removed by hand at the proxy). One status
    /// call per instance covers its main domain and host address; further domains are asked one by one.</summary>
    public async Task OnPostCheckAsync()
    {
        await LoadAsync();
        var ct = HttpContext.RequestAborted;
        foreach (var inst in Groups.Select(g => g.Instance).Where(i => i.ProxyRouteId is not null || i.HostRouteId is not null))
        {
            var s = await _proxy.StatusAsync(inst, checkProvider: true, ct);
            if (inst.ProxyRouteId is not null) Checked["custom:" + inst.Id] = s.RouteExists;
            if (inst.HostRouteId is not null) Checked["host:" + inst.Id] = s.HostRouteExists;
        }
        foreach (var x in _extras.Where(x => x.RouteId is not null))
            Checked["extra:" + x.Id] = await _proxy.ExtraRouteExistsAsync(x.Instance!, x, ct);
    }
}
