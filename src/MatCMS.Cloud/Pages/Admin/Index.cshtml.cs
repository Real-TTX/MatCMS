using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly InstanceService _instances;
    private readonly ReleaseWatcher _releases;
    private readonly DockerHostService _docker;
    private readonly OperatorScope _scope;
    private readonly VersionService _version;

    public IndexModel(AppDbContext db, InstanceService instances, ReleaseWatcher releases, DockerHostService docker, OperatorScope scope, VersionService version)
    {
        _db = db;
        _instances = instances;
        _releases = releases;
        _docker = docker;
        _scope = scope;
        _version = version;
    }

    public bool IsAdmin => _scope.IsAdmin;

    public List<Instance> Instances { get; private set; } = new();
    public List<InstanceEvent> RecentEvents { get; private set; } = new();

    public int OnlineCount => Instances.Count(InstanceService.IsOnline);
    public int OfflineCount => Instances.Count(i => i.HasConnected && !InstanceService.IsOnline(i));
    public int UpdateCount => Instances.Count(i => _releases.IsUpdateAvailableFor(i.Version));
    public int LocalCount => Instances.Count(i => i.Hosting == InstanceHosting.Local);

    public string? LatestVersion => _releases.LatestVersion;
    public DateTime? LastReleaseCheck => _releases.LastCheckedUtc;
    public string? ReleaseError => _releases.LastError;

    public bool DockerConfigured => _docker.Configured;
    public bool DockerReachable { get; private set; }

    public string CloudVersion => _version.Current;

    // Instances that need a look, each with the reasons. Drives the "Aufmerksamkeit" panel.
    public int SyncErrorCount => Instances.Count(i => !string.IsNullOrWhiteSpace(i.LastSyncError));
    public int OutdatedCount => Instances.Count(InstanceService.IsOutdatedProtocol);
    public bool IsOffline(Instance i) => i.HasConnected && !InstanceService.IsOnline(i);
    public bool IsOutdated(Instance i) => InstanceService.IsOutdatedProtocol(i);
    public bool HasSyncError(Instance i) => !string.IsNullOrWhiteSpace(i.LastSyncError);
    public List<Instance> AttentionList => Instances
        .Where(i => IsOffline(i) || HasSyncError(i) || HasUpdate(i) || IsOutdated(i))
        .OrderBy(i => i.Name).ToList();

    public async Task OnGetAsync()
    {
        var iq = _db.Instances.AsNoTracking().AsQueryable();
        var eq = _db.InstanceEvents.AsNoTracking().Include(e => e.Instance).AsQueryable();
        // Operators see a dashboard of ONLY their instances (and the events for them).
        if (!_scope.IsAdmin)
        {
            var allowed = await _scope.AllowedInstanceIdsAsync();
            iq = iq.Where(i => allowed.Contains(i.Id));
            eq = eq.Where(e => allowed.Contains(e.InstanceId));
        }
        Instances = await iq.OrderBy(i => i.Name).ToListAsync();
        RecentEvents = await eq.OrderByDescending(e => e.CreatedAt).Take(15).ToListAsync();
        DockerReachable = _scope.IsAdmin && await _docker.IsReachableAsync(HttpContext.RequestAborted);
    }

    public bool HasUpdate(Instance i) => _releases.IsUpdateAvailableFor(i.Version);
}
