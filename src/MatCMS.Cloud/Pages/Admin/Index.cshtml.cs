using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Pages.Admin;

/// <summary>
/// The start page: what is running, what needs a look, and what just happened — across instances, hosts,
/// moves, domains, backups, syncs and the cloud itself. Everything here is a READ of state other pages own;
/// each line links to where it is dealt with.
/// <para>Operators see a dashboard of ONLY their instances: fleet facts (nodes, the cloud's own version and
/// Docker, notifications, backup totals) are an Admin's business and are neither loaded nor shown for them.</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly ReleaseWatcher _releases;
    private readonly DockerHostService _docker;
    private readonly OperatorScope _scope;
    private readonly VersionService _version;
    private readonly CloudContext _cloud;
    private readonly EmailService _mail;
    private readonly IMemoryCache _cache;
    private readonly Localizer _t;
    private readonly AttentionService _attention;

    public IndexModel(AppDbContext db, InstanceService instances, ReleaseWatcher releases, DockerHostService docker, OperatorScope scope,
        VersionService version, CloudContext cloud, EmailService mail, IMemoryCache cache, Localizer t, AttentionService attention)
    {
        _attention = attention;
        _db = db; _instances = instances; _releases = releases; _docker = docker; _scope = scope;
        _version = version; _cloud = cloud; _mail = mail; _cache = cache; _t = t;
    }

    public bool IsAdmin => _scope.IsAdmin;
    public bool HostingOn => _cloud.Flag(SettingKeys.HostingEnabled);

    public List<Instance> Instances { get; private set; } = new();
    public List<InstanceEvent> RecentEvents { get; private set; } = new();
    public List<InstanceSyncRun> RecentSyncs { get; private set; } = new();
    public List<Node> Nodes { get; private set; } = new();

    // ---- Tiles -------------------------------------------------------------------------------------
    public int OnlineCount => Instances.Count(InstanceService.IsOnline);
    public int OfflineCount => Instances.Count(IsOffline);
    public int StoppedCount => Instances.Count(IsStopped);
    public int UpdateCount => Instances.Count(HasUpdate);
    public int LocalCount => Instances.Count(i => i.Hosting == InstanceHosting.Local);
    public int NodeInstanceCount => Instances.Count(i => i.Hosting == InstanceHosting.Node);
    public int RemoteCount => Instances.Count(i => i.Hosting == InstanceHosting.Remote);
    public int OutOfSyncCount => Instances.Count(InstanceService.IsOutOfSync);
    public int SyncErrorCount => Instances.Count(HasSyncError);
    public int NodesOnline => Nodes.Count(n => n.IsOnline(DateTime.UtcNow));

    /// <summary>Errors per day for the last 7 days (oldest first): the cloud's own + the ones instances mirrored.</summary>
    public List<(DateTime Day, int Cloud, int Sites)> ErrorsPerDay { get; private set; } = new();
    public int ErrorTotal7d => ErrorsPerDay.Sum(d => d.Cloud + d.Sites);
    public Dictionary<int, int> InstanceErrorCounts { get; private set; } = new();
    public int ErrorCount(Instance i) => InstanceErrorCounts.GetValueOrDefault(i.Id);
    /// <summary>Rows a dashboard card shows; the rest is behind its "Alle anzeigen".</summary>
    public const int CardRows = 6;
    public List<Instance> InstancesWithErrorsAll => Instances.Where(i => ErrorCount(i) > 0).OrderByDescending(ErrorCount).ToList();
    public int SyncRunCount { get; private set; }

    // ---- System (admins) -----------------------------------------------------------------------------
    public string CloudVersion => _version.Current;
    public string? CloudLatest { get; private set; }
    public bool CloudUpdateAvailable { get; private set; }
    public string? LatestVersion => _releases.LatestVersion;
    public DateTime? LastReleaseCheck => _releases.LastCheckedUtc;
    public string? ReleaseError => _releases.LastError;
    public bool DockerConfigured => _docker.Configured;
    public bool DockerReachable { get; private set; }
    public bool MailConfigured { get; private set; }
    public int NotifyRowCount { get; private set; }
    public int DomainCount => Instances.Count(i => i.ProxyDomain is not null);
    public long BackupBytes { get; private set; }
    public int BackupCount { get; private set; }

    // ---- Attention -------------------------------------------------------------------------------------
    /// <summary>What needs a look — the same list as Admin/Attention (AttentionService).</summary>
    public List<AttentionService.Item> AttentionItems { get; private set; } = new();

    // A stopped container (by us or by hand) is not an outage — shown apart from "offline".
    public bool IsOffline(Instance i) => AttentionService.IsOffline(i);
    public bool IsStopped(Instance i) => AttentionService.IsStopped(i);
    public bool HasUpdate(Instance i) => _releases.IsUpdateAvailableFor(i.Version);
    public bool HasSyncError(Instance i) => !string.IsNullOrWhiteSpace(i.LastSyncError);

    public async Task OnGetAsync()
    {
        var ct = HttpContext.RequestAborted;
        var iq = _db.Instances.AsNoTracking().Include(i => i.Profile).Include(i => i.Node).AsQueryable();
        var eq = _db.InstanceEvents.AsNoTracking().Include(e => e.Instance).AsQueryable();
        var sq = _db.InstanceSyncRuns.AsNoTracking().Include(r => r.Instance).AsQueryable();
        if (!_scope.IsAdmin)
        {
            var allowed = await _scope.AllowedInstanceIdsAsync();
            iq = iq.Where(i => allowed.Contains(i.Id));
            eq = eq.Where(e => allowed.Contains(e.InstanceId));
            sq = sq.Where(r => allowed.Contains(r.InstanceId));
        }
        Instances = await iq.OrderBy(i => i.Name).ToListAsync(ct);
        RecentEvents = await eq.OrderByDescending(e => e.CreatedAt).Take(12).ToListAsync(ct);
        RecentSyncs = await sq.OrderByDescending(r => r.RanAt).Take(CardRows).ToListAsync(ct);
        SyncRunCount = await sq.CountAsync(ct);
        var ids = Instances.Select(i => i.Id).ToList();

        // Errors: per day, the cloud's own (admins only) and the ones the visible instances mirrored.
        var from = DateTime.UtcNow.Date.AddDays(-6);
        var cloudDays = _scope.IsAdmin
            ? (await _db.Logs.AsNoTracking().Where(l => l.Level == "Error" && l.CreatedAt >= from).Select(l => l.CreatedAt).ToListAsync(ct))
                .GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Count())
            : new Dictionary<DateTime, int>();
        var siteErrors = await _db.InstanceLogs.AsNoTracking()
            .Where(l => l.Level == "Error" && ids.Contains(l.InstanceId) && l.TimeUtc >= from)
            .Select(l => new { l.InstanceId, l.TimeUtc }).ToListAsync(ct);
        var siteDays = siteErrors.GroupBy(e => e.TimeUtc.Date).ToDictionary(g => g.Key, g => g.Count());
        for (var d = from; d <= DateTime.UtcNow.Date; d = d.AddDays(1))
            ErrorsPerDay.Add((d, cloudDays.GetValueOrDefault(d), siteDays.GetValueOrDefault(d)));
        InstanceErrorCounts = siteErrors.GroupBy(e => e.InstanceId).ToDictionary(g => g.Key, g => g.Count());

        if (_scope.IsAdmin)
        {
            DockerReachable = await _docker.IsReachableAsync(ct);
            Nodes = await _db.Nodes.AsNoTracking().Where(n => !n.Revoked).OrderBy(n => n.Name).ToListAsync(ct);
            MailConfigured = await _mail.IsConfiguredAsync();
            NotifyRowCount = HttpContext.RequestServices.GetRequiredService<NotificationService>().Load().Rows.Count(r => r.Events.Count > 0);
            BackupCount = await _db.CloudBackups.CountAsync(ct);
            BackupBytes = BackupCount == 0 ? 0 : await _db.CloudBackups.SumAsync(b => b.SizeBytes, ct);
            var check = await _attention.CloudUpdateAsync(ct);
            CloudLatest = check?.Latest;
            CloudUpdateAvailable = check is { Error: null, UpdateAvailable: true };
        }

        AttentionItems = await _attention.BuildAsync(_scope.IsAdmin ? null : await _scope.AllowedInstanceIdsAsync(), fleet: _scope.IsAdmin, ct);
    }
}
