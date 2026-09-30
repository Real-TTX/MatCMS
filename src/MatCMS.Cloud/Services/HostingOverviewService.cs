using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Services;

/// <summary>
/// What the Hosting dashboard and its Instances tab show: every host (this one + nodes) with its size and load,
/// and every MatCMS container on them with what it uses — joined to the instance record where there is one.
/// <para>"Dieser Host" is read live from the daemon; a node from the inventory its agent last reported (usage is
/// sampled there once a minute). The live sample takes the daemon about a second, so it is cached for a few
/// seconds — both pages are opened in quick succession.</para>
/// </summary>
public class HostingOverviewService
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly InstanceService _instances;
    private readonly VersionService _version;
    private readonly IMemoryCache _cache;

    public HostingOverviewService(AppDbContext db, DockerHostService docker, InstanceService instances, VersionService version, IMemoryCache cache)
    {
        _db = db; _docker = docker; _instances = instances; _version = version; _cache = cache;
    }

    /// <param name="NodeId">Null = "Dieser Host".</param>
    public sealed record HostRow(int? NodeId, string Name, bool Online, bool Revoked, string? DockerVersion, string? AgentVersion,
        bool AgentOutdated, string? Error, int? Cpus, long? MemTotal, int Running, int Total, double Cpu, long Mem, DateTime? SampledAt);

    /// <param name="Instance">Null = a MatCMS container the cloud has no record for (started by hand, never joined).</param>
    public sealed record SiteRow(Instance? Instance, HostRow Host, string ContainerName, string State, string? Status,
        double? Cpu, long? Mem, long? MemLimit, int? Port, bool UpdateAvailable)
    {
        public bool Running => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record Overview(List<HostRow> Hosts, List<SiteRow> Sites);

    private sealed record LocalSample(List<DockerHostService.ContainerInfo>? Containers, Dictionary<string, DockerHostService.ContainerStats> Stats,
        (int Cpus, long MemTotal, string? Os)? Host, string? Version, bool Reachable, DateTime At);

    private async Task<LocalSample> LocalAsync(CancellationToken ct) =>
        (await _cache.GetOrCreateAsync("hosting.local-sample", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15);
            var reachable = await _docker.IsReachableAsync(ct);
            if (!reachable) return new LocalSample(null, new(), null, null, false, DateTime.UtcNow);
            var list = await _docker.ListMatCmsContainersAsync(ct);
            var stats = list is null ? new() : await _docker.StatsAsync(list.Where(c => c.State == "running").Select(c => c.Id), ct);
            var info = await _docker.DaemonInfoAsync(ct);
            return new LocalSample(list, stats, await _docker.HostResourcesAsync(ct), info.Version, true, DateTime.UtcNow);
        }))!;

    public async Task<Overview> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var instances = await _db.Instances.AsNoTracking()
            .Where(i => i.ContainerId != null && (i.Hosting == InstanceHosting.Local || i.Hosting == InstanceHosting.Node))
            .ToListAsync(ct);
        var hosts = new List<HostRow>();
        var sites = new List<SiteRow>();

        Instance? Match(IEnumerable<Instance> pool, string containerId) =>
            pool.FirstOrDefault(i => DockerHostService.IdMatches(containerId, i.ContainerId!.ToLowerInvariant()));

        // ---- this host (live) ----
        var local = await LocalAsync(ct);
        var localPool = instances.Where(i => i.Hosting == InstanceHosting.Local).ToList();
        var localRows = new List<(DockerHostService.ContainerInfo C, DockerHostService.ContainerStats? S)>();
        foreach (var c in local.Containers ?? new())
            localRows.Add((c, local.Stats.TryGetValue(c.Id, out var s) ? s : null));
        var thisHost = new HostRow(null, "", local.Reachable, false, local.Version, null, false,
            local.Reachable ? null : (_docker.Configured ? "unreachable" : "off"),
            local.Host?.Cpus, local.Host?.MemTotal,
            localRows.Count(r => r.C.State == "running"), localRows.Count,
            localRows.Sum(r => r.S?.CpuPercent ?? 0), localRows.Sum(r => r.S?.MemBytes ?? 0), local.Containers is null ? null : local.At);
        hosts.Add(thisHost);
        foreach (var (c, s) in localRows)
        {
            var inst = Match(localPool, c.Id);
            sites.Add(new SiteRow(inst, thisHost, c.Name, c.State, c.Status, s?.CpuPercent, s?.MemBytes, s?.MemLimit, c.PublishedPort,
                inst is not null && _instances.IsUpdateAvailable(inst)));
        }

        // ---- nodes (last reported inventory) ----
        foreach (var n in await _db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync(ct))
        {
            var inv = NodeJobExecutor.Deserialize<List<NodeContainer>>(n.InventoryJson) ?? new();
            var pool = instances.Where(i => i.NodeId == n.Id).ToList();
            var row = new HostRow(n.Id, n.Name, n.IsOnline(now), n.Revoked, n.DockerVersion, n.AgentVersion,
                NodeService.AgentOutdated(n, _version.Current), n.DockerError, n.Cpus, n.MemTotal,
                inv.Count(c => c.State == "running"), inv.Count, inv.Sum(c => c.CpuPercent ?? 0), inv.Sum(c => c.MemBytes ?? 0), n.InventoryAt);
            hosts.Add(row);
            foreach (var c in inv)
            {
                var inst = Match(pool, c.Id);
                sites.Add(new SiteRow(inst, row, c.Name, c.State, c.Status, c.CpuPercent, c.MemBytes, c.MemLimit, c.PublishedPort,
                    inst is not null && _instances.IsUpdateAvailable(inst)));
            }
        }

        return new Overview(hosts,
            sites.OrderBy(s => s.Instance is null).ThenBy(s => s.Instance?.Name ?? s.ContainerName, StringComparer.OrdinalIgnoreCase).ToList());
    }
}

/// <summary>Display helpers for the hosting pages.</summary>
public static class HostFmt
{
    public static string Bytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / 1048576.0:0} MB" : $"{b / 1024} KB";

    /// <summary>A share 0–100 for a bar's width, clamped (a container can briefly report more than its limit).</summary>
    public static int Pct(double part, double whole) => whole <= 0 ? 0 : (int)Math.Clamp(Math.Round(part * 100 / whole), 0, 100);
}

/// <summary>Model of <c>_UsageBar.cshtml</c>: a proportion as a bar with its text beside it. Whole ≤ 0 = unknown
/// (the text alone is shown).</summary>
public sealed record UsageBar(double Part, double Whole, string Text);

/// <summary>The one JSON shape of the hosting overview and the image list, shared by REST and MCP.</summary>
public static class HostingOverviewJson
{
    public static object Overview(HostingOverviewService.Overview o) => new
    {
        hosts = o.Hosts.Select(h => new
        {
            node = h.NodeId is null ? null : h.Name, thisHost = h.NodeId is null, online = h.Online, revoked = h.Revoked,
            dockerVersion = h.DockerVersion, agentVersion = h.AgentVersion, agentOutdated = h.AgentOutdated, error = h.Error,
            cpus = h.Cpus, memTotal = h.MemTotal, sitesRunning = h.Running, sitesTotal = h.Total,
            cpuPercent = Math.Round(h.Cpu, 1), memBytes = h.Mem, sampledAt = h.SampledAt,
        }),
        instances = o.Sites.Select(s => new
        {
            instanceId = s.Instance?.PublicId, name = s.Instance?.Name, container = s.ContainerName,
            node = s.Host.NodeId is null ? null : s.Host.Name, state = s.State, status = s.Status,
            cpuPercent = s.Cpu, memBytes = s.Mem, memLimit = s.MemLimit, port = s.Port, domain = s.Instance?.ProxyDomain,
            version = s.Instance?.Version, updateAvailable = s.UpdateAvailable,
        }),
    };

    public static object Images(List<DockerHostService.ImageInfo> list) => new
    {
        images = list.Select(i => new { id = i.Id, tag = i.Tag, size = i.Size, created = i.Created, inUse = i.InUse, dangling = i.Dangling }),
        prunable = list.Count(i => i.Dangling && i.InUse == 0),
        prunableBytes = list.Where(i => i.Dangling && i.InUse == 0).Sum(i => i.Size),
    };
}
