using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Services;

/// <summary>
/// "Braucht Aufmerksamkeit": every line that needs a look — offline or stopped sites, sync and proxy errors, outdated
/// protocols, stale backups, available updates, failed moves, and (fleet scope) nodes, the cloud's own update, SMTP,
/// Docker and the release check. ONE list for the dashboard card, its full page (Admin/Attention), /api/v1/attention
/// and MCP get_attention, so they cannot disagree about what needs attention.
/// <para>Scoping is the caller's: it passes the instance ids it may see (null = all) and whether fleet lines belong
/// in (Admins / all-instances keys). Each line carries the UI page where it is dealt with.</para>
/// </summary>
public sealed class AttentionService
{
    private readonly AppDbContext _db;
    private readonly ReleaseWatcher _releases;
    private readonly DockerHostService _docker;
    private readonly VersionService _version;
    private readonly EmailService _mail;
    private readonly IMemoryCache _cache;
    private readonly Localizer _t;
    private readonly LinkGenerator _links;

    public AttentionService(AppDbContext db, ReleaseWatcher releases, DockerHostService docker, VersionService version, EmailService mail,
        IMemoryCache cache, Localizer t, LinkGenerator links)
    {
        _db = db; _releases = releases; _docker = docker; _version = version; _mail = mail; _cache = cache; _t = t; _links = links;
    }

    /// <summary>One line. <paramref name="Level"/>: err | warn | info. <paramref name="Kind"/>: what it is about (offline,
    /// syncError, update, …) — the filter of the full page. <paramref name="InstanceId"/>: the site's public id, if any.
    /// <paramref name="Detail"/>: what it comes down to — last contact, last backup, versions, the failed step …
    /// <paramref name="LastError"/>: for a line about one site, the newest error that site reported (14 days), because
    /// "offline" or "sync error" alone rarely says why.</summary>
    public sealed record Item(string Level, string Kind, string Icon, string Title, string Text, string Url, string? InstanceId,
        string? Detail = null, string? LastError = null);

    /// <summary>A site that uploads to the cloud but has not for this long is worth a line — one that never used cloud
    /// backups is not (it may back up elsewhere).</summary>
    public static readonly TimeSpan StaleBackup = TimeSpan.FromDays(7);

    public static bool IsStopped(Instance i) => i.Hosting is InstanceHosting.Local or InstanceHosting.Node
        && !string.IsNullOrEmpty(i.ContainerState) && !string.Equals(i.ContainerState, "running", StringComparison.OrdinalIgnoreCase);
    public static bool IsOffline(Instance i) => i.HasConnected && !InstanceService.IsOnline(i) && !IsStopped(i);

    /// <summary>The cloud's own update, from the registry — cached and time-boxed, a page must open fast when GHCR is slow.</summary>
    public async Task<VersionService.UpdateCheck?> CloudUpdateAsync(CancellationToken ct) =>
        await _cache.GetOrCreateAsync("dashboard.cloudUpdate", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            try { return await _version.CheckAsync(cts.Token); } catch { return null; }
        });

    /// <param name="allowed">Instance ids the caller may see; null = all.</param>
    /// <param name="fleet">Include the lines about nodes and the cloud itself.</param>
    public async Task<List<Item>> BuildAsync(IReadOnlySet<int>? allowed, bool fleet, CancellationToken ct = default)
    {
        var iq = _db.Instances.AsNoTracking().AsQueryable();
        if (allowed is not null) iq = iq.Where(i => allowed.Contains(i.Id));
        var instances = await iq.OrderBy(i => i.Name).ToListAsync(ct);
        var ids = instances.Select(i => i.Id).ToList();
        var lastBackup = await _db.CloudBackups.AsNoTracking().Where(b => ids.Contains(b.InstanceId))
            .GroupBy(b => b.InstanceId).Select(g => new { g.Key, Last = g.Max(b => b.CreatedAt) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        var recentCut = DateTime.UtcNow.AddDays(-1);
        var moves = await _db.InstanceMigrations.AsNoTracking()
            .Where(m => ids.Contains(m.InstanceId) && (m.State == "running" || (m.State != "succeeded" && m.StartedAt > recentCut)))
            .OrderByDescending(m => m.Id).ToListAsync(ct);

        // The newest error each site reported — read once for all of them.
        var errCut = DateTime.UtcNow.AddDays(-14);
        var newestErr = (await _db.InstanceLogs.AsNoTracking()
                .Where(l => ids.Contains(l.InstanceId) && l.Level == "Error" && l.TimeUtc >= errCut)
                .GroupBy(l => l.InstanceId).Select(g => g.Max(l => l.Id)).ToListAsync(ct)) is var maxIds && maxIds.Count > 0
            ? await _db.InstanceLogs.AsNoTracking().Where(l => maxIds.Contains(l.Id)).ToDictionaryAsync(l => l.InstanceId, ct)
            : new Dictionary<int, InstanceLogEntry>();
        string? LastErr(Instance i) => newestErr.TryGetValue(i.Id, out var e)
            ? _t["attention.detail.lastError", Local(e.TimeUtc), (e.StatusCode is int sc ? sc + " " : "") + (string.IsNullOrEmpty(e.Path) ? "" : e.Path + " · ") + e.Message]
            : null;
        string D(string key, params object[] args) => _t["attention.detail." + key, args];

        var a = new List<Item>();
        var now = DateTime.UtcNow;
        string Page(string page, object? values = null, string? fragment = null) =>
            (_links.GetPathByPage(page, values: values) ?? page) + (fragment is null ? "" : "#" + fragment);
        string Inst(Instance i, string tab = "overview") => Page("/Admin/Instances/Details", new { id = i.Id, tab });
        string L(string key, params object[] args) => args.Length == 0 ? _t["dashboard.attn." + key] : _t["dashboard.attn." + key, args];
        void Add(string level, string kind, string icon, string title, string url, string? instanceId, params object[] args) =>
            a.Add(new(level, kind, icon, title, L(kind, args), url, instanceId));
        // The same, with what it comes down to — and for a site its newest reported error.
        void AddI(Instance i, string level, string kind, string icon, string url, string? detail, params object[] args) =>
            a.Add(new(level, kind, icon, i.Name, L(kind, args), url, i.PublicId, detail, LastErr(i)));
        void AddD(string level, string kind, string icon, string title, string url, string? detail, params object[] args) =>
            a.Add(new(level, kind, icon, title, L(kind, args), url, null, detail));

        foreach (var m in moves)
        {
            var i = instances.FirstOrDefault(x => x.Id == m.InstanceId);
            if (i is null) continue;
            if (m.State == "running") AddI(i, "info", "moveRunning", "🚚", Inst(i, "hosting"), D("since", Local(m.StartedAt)), m.FromName, m.ToName, m.Step);
            else AddI(i, "err", m.State == "rolled-back" ? "moveRolledBack" : "moveFailed", "🚚", Inst(i, "hosting"), LastLogLine(m.Log), m.FromName, m.ToName);
        }
        foreach (var i in instances)
        {
            if (IsOffline(i)) AddI(i, "err", "offline", "🔴", Inst(i), D("lastSeen", i.LastHeartbeatUtc is DateTime hb ? Local(hb) : "—", i.Url ?? "—"), $"{i.LastHeartbeatUtc:dd.MM. HH:mm} UTC");
            else if (IsStopped(i)) AddI(i, "warn", "stopped", "⏸️", Inst(i, "hosting"), D("container", i.ContainerState ?? "?"), i.ContainerState ?? "");
            if (!string.IsNullOrWhiteSpace(i.LastSyncError)) AddI(i, "err", "syncError", "⚠️", Inst(i, "config"), D("sync", i.LastSyncRunAt is DateTime sr ? Local(sr) : "—", i.AppliedRevision), i.LastSyncError!);
            if (!string.IsNullOrEmpty(i.ProxyError)) AddI(i, "err", "proxyError", "🌐", Inst(i, "hosting"), D("domain", i.ProxyDomain ?? "—"), i.ProxyError);
            if (InstanceService.IsOutdatedProtocol(i)) AddI(i, "warn", "outdated", "🧓", Inst(i), D("protocol", i.ProtocolVersion, InstanceService.CurrentProtocolVersion, i.Version ?? "?"));
            if (lastBackup.TryGetValue(i.Id, out var lb) && now - lb > StaleBackup)
                AddI(i, "warn", "backupStale", "💾", Inst(i, "backup"), D("lastBackup", Local(lb)), (int)(now - lb).TotalDays);
            if (_releases.IsUpdateAvailableFor(i.Version))
                AddI(i, "info", "update", "⬆️", Inst(i, HostingActionsService.CanAct(i) ? "hosting" : "overview"), HostingActionsService.CanAct(i) ? D("updateHere") : D("updateRemote"), _releases.LatestVersion ?? "", i.Version ?? "?");
        }
        if (fleet)
        {
            var cloudVersion = _version.Current;
            foreach (var n in await _db.Nodes.AsNoTracking().Where(n => !n.Revoked).OrderBy(n => n.Name).ToListAsync(ct))
            {
                var url = Page("/Admin/Hosting/Nodes/Details", new { id = n.Id });
                var title = L("node", n.Name);
                if (n.LastSeenAt is not null && !n.IsOnline(now)) AddD("err", "nodeOffline", "🖧", title, url, D("node", n.HostName ?? "—", n.Address ?? "—"), $"{n.LastSeenAt:dd.MM. HH:mm} UTC");
                else if (!string.IsNullOrEmpty(n.DockerError)) AddD("err", "nodeDocker", "🖧", title, url, D("node", n.HostName ?? "—", n.Address ?? "—"), n.DockerError);
                if (Nodes.NodeService.AgentOutdated(n, cloudVersion)) AddD("info", "agentOutdated", "🖧", title, url, D("agent"), n.AgentVersion ?? "", cloudVersion);
            }
            var check = await CloudUpdateAsync(ct);
            if (check is { Error: null, UpdateAvailable: true }) AddD("info", "cloudUpdate", "☁️", L("cloud.title"), Page("/Admin/Hosting/Index", fragment: "updates"), D("running", check.Current), check.Latest ?? "");
            if (!await _mail.IsConfiguredAsync()) Add("warn", "smtp", "✉️", L("smtp.title"), Page("/Admin/Settings/Index", new { tab = "smtp" }), null);
            if (_docker.Configured && !await _docker.IsReachableAsync(ct)) AddD("err", "docker", "🐳", "Docker", Page("/Admin/Hosting/Nodes/Details", new { tab = "docker" }), D("endpoint", _docker.Endpoint ?? "—"));
            if (!string.IsNullOrWhiteSpace(_releases.LastError)) a.Add(new("warn", "release", "📦", L("release.title"), _releases.LastError!, Page("/Admin/Index"), null));
        }
        // Errors first, then warnings, then information; within a level, by name.
        static string Local(DateTime utc) => utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        static string? LastLogLine(string? log) => string.IsNullOrWhiteSpace(log) ? null
            : log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return a.OrderBy(x => x.Level switch { "err" => 0, "warn" => 1, _ => 2 }).ThenBy(x => x.Title).ToList();
    }
}
