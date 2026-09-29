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

    public IndexModel(AppDbContext db, InstanceService instances, ReleaseWatcher releases, DockerHostService docker, OperatorScope scope,
        VersionService version, CloudContext cloud, EmailService mail, IMemoryCache cache, Localizer t)
    {
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
    public List<Instance> InstancesWithErrors => Instances.Where(i => ErrorCount(i) > 0).OrderByDescending(ErrorCount).Take(6).ToList();

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
    /// <summary>One line that needs a look. <paramref name="Level"/>: err | warn | info.</summary>
    public sealed record Attention(string Level, string Icon, string Title, string Text, string Url);
    public List<Attention> AttentionItems { get; private set; } = new();

    public bool IsOffline(Instance i) => i.HasConnected && !InstanceService.IsOnline(i) && !IsStopped(i);
    // A stopped container (by us or by hand) is not an outage — shown apart from "offline".
    public bool IsStopped(Instance i) => i.Hosting is InstanceHosting.Local or InstanceHosting.Node
        && !string.IsNullOrEmpty(i.ContainerState) && !string.Equals(i.ContainerState, "running", StringComparison.OrdinalIgnoreCase);
    public bool HasUpdate(Instance i) => _releases.IsUpdateAvailableFor(i.Version);
    public bool HasSyncError(Instance i) => !string.IsNullOrWhiteSpace(i.LastSyncError);

    /// <summary>A site that uploads to the cloud but has not for this long is worth a line — one that never used
    /// cloud backups is not (it may back up elsewhere).</summary>
    public static readonly TimeSpan StaleBackup = TimeSpan.FromDays(7);

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
        RecentSyncs = await sq.OrderByDescending(r => r.RanAt).Take(8).ToListAsync(ct);
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

        // Last cloud backup per visible instance (for the "no recent backup" line).
        var lastBackup = await _db.CloudBackups.AsNoTracking().Where(b => ids.Contains(b.InstanceId))
            .GroupBy(b => b.InstanceId).Select(g => new { g.Key, Last = g.Max(b => b.CreatedAt) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);

        // Moves: one running or recently failed is worth a line.
        var recentCut = DateTime.UtcNow.AddDays(-1);
        var moves = await _db.InstanceMigrations.AsNoTracking()
            .Where(m => ids.Contains(m.InstanceId) && (m.State == "running" || (m.State != "succeeded" && m.StartedAt > recentCut)))
            .OrderByDescending(m => m.Id).ToListAsync(ct);

        if (_scope.IsAdmin)
        {
            DockerReachable = await _docker.IsReachableAsync(ct);
            Nodes = await _db.Nodes.AsNoTracking().Where(n => !n.Revoked).OrderBy(n => n.Name).ToListAsync(ct);
            MailConfigured = await _mail.IsConfiguredAsync();
            NotifyRowCount = HttpContext.RequestServices.GetRequiredService<NotificationService>().Load().Rows.Count(r => r.Events.Count > 0);
            BackupCount = await _db.CloudBackups.CountAsync(ct);
            BackupBytes = BackupCount == 0 ? 0 : await _db.CloudBackups.SumAsync(b => b.SizeBytes, ct);
            // The cloud's own update: a registry call, so cached and time-boxed — the start page must open fast
            // even when GHCR is slow or unreachable.
            var check = await _cache.GetOrCreateAsync("dashboard.cloudUpdate", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                try { return await _version.CheckAsync(cts.Token); } catch { return null; }
            });
            CloudLatest = check?.Latest;
            CloudUpdateAvailable = check is { Error: null, UpdateAvailable: true };
        }

        BuildAttention(lastBackup, moves);
    }

    // Built here (they need the data), WORDED through the localizer — the admin also runs in English.
    private void BuildAttention(Dictionary<int, DateTime> lastBackup, List<InstanceMigration> moves)
    {
        string Inst(Instance i, string tab = "overview") => Url.Page("/Admin/Instances/Details", new { id = i.Id, tab })!;
        var a = AttentionItems;
        var now = DateTime.UtcNow;
        string L(string key, params object[] args) => args.Length == 0 ? _t["dashboard.attn." + key] : _t["dashboard.attn." + key, args];
        void Add(string level, string icon, string title, string key, string url, params object[] args) => a.Add(new(level, icon, title, L(key, args), url));

        foreach (var m in moves)
        {
            var i = Instances.FirstOrDefault(x => x.Id == m.InstanceId);
            if (i is null) continue;
            if (m.State == "running") Add("info", "🚚", i.Name, "moveRunning", Inst(i, "hosting"), m.FromName, m.ToName, m.Step);
            else Add("err", "🚚", i.Name, m.State == "rolled-back" ? "moveRolledBack" : "moveFailed", Inst(i, "hosting"), m.FromName, m.ToName);
        }
        foreach (var i in Instances)
        {
            if (IsOffline(i)) Add("err", "🔴", i.Name, "offline", Inst(i), $"{i.LastHeartbeatUtc:dd.MM. HH:mm} UTC");
            else if (IsStopped(i)) Add("warn", "⏸️", i.Name, "stopped", Inst(i, "hosting"), i.ContainerState ?? "");
            if (HasSyncError(i)) Add("err", "⚠️", i.Name, "syncError", Inst(i, "config"), i.LastSyncError!);
            if (!string.IsNullOrEmpty(i.ProxyError)) Add("err", "🌐", i.Name, "proxyError", Inst(i, "hosting"), i.ProxyError);
            if (InstanceService.IsOutdatedProtocol(i)) Add("warn", "🧓", i.Name, "outdated", Inst(i));
            if (lastBackup.TryGetValue(i.Id, out var lb) && now - lb > StaleBackup)
                Add("warn", "💾", i.Name, "backupStale", Inst(i, "backup"), (int)(now - lb).TotalDays);
            if (HasUpdate(i)) Add("info", "⬆️", i.Name, "update", Inst(i, HostingActionsService.CanAct(i) ? "hosting" : "overview"), LatestVersion ?? "", i.Version ?? "?");
        }
        if (_scope.IsAdmin)
        {
            foreach (var n in Nodes)
            {
                var url = Url.Page("/Admin/Hosting/Nodes/Details", new { id = n.Id })!;
                var title = L("node", n.Name);
                if (n.LastSeenAt is not null && !n.IsOnline(now)) Add("err", "🖧", title, "nodeOffline", url, $"{n.LastSeenAt:dd.MM. HH:mm} UTC");
                else if (!string.IsNullOrEmpty(n.DockerError)) Add("err", "🖧", title, "nodeDocker", url, n.DockerError);
                if (Services.Nodes.NodeService.AgentOutdated(n, CloudVersion)) Add("info", "🖧", title, "agentOutdated", url, n.AgentVersion ?? "", CloudVersion);
            }
            if (CloudUpdateAvailable) Add("info", "☁️", L("cloud.title"), "cloudUpdate", Url.Page("/Admin/Hosting/Index")!, CloudLatest ?? "");
            if (!MailConfigured) Add("warn", "✉️", L("smtp.title"), "smtp", Url.Page("/Admin/Settings/Index", new { tab = "smtp" })!);
            if (DockerConfigured && !DockerReachable) Add("err", "🐳", "Docker", "docker", Url.Page("/Admin/Settings/Index", new { tab = "docker" })!);
            if (!string.IsNullOrWhiteSpace(ReleaseError)) a.Add(new("warn", "📦", L("release.title"), ReleaseError!, Url.Page("/Admin/Index")!));
        }
        // Errors first, then warnings, then information; within a level, by name.
        AttentionItems = a.OrderBy(x => x.Level switch { "err" => 0, "warn" => 1, _ => 2 }).ThenBy(x => x.Title).ToList();
    }
}
