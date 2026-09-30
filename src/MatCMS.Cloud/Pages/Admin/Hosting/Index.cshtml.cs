using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Übersicht, the module's dashboard: how many sites run where, how loaded the hosts are, and what needs an
/// update — the cloud itself (starting its self-update opens the full-screen update page), the instances behind the
/// release and node agents behind the cloud. Running instance updates is its own page (Hosting → Updates).
/// Admin-only (folder lock in Program.cs): it spans the whole fleet.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly HostingOverviewService _overview;
    private readonly InstanceUpdatesService _updates;
    private readonly ReleaseWatcher _releases;
    private readonly CloudUpdaterService _updater;
    private readonly VersionService _version;
    private readonly IMemoryCache _cache;
    private readonly Localizer _t;

    public IndexModel(AppDbContext db, CloudContext cloud, HostingOverviewService overview, InstanceUpdatesService updates,
        ReleaseWatcher releases, CloudUpdaterService updater, VersionService version, IMemoryCache cache, Localizer t)
    {
        _db = db; _cloud = cloud; _overview = overview; _updates = updates; _releases = releases;
        _updater = updater; _version = version; _cache = cache; _t = t;
    }

    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public bool AutoUpdate => _cloud.Flag(SettingKeys.AutoUpdateLocal);
    public HostingOverviewService.Overview Data { get; private set; } = null!;
    public int DomainCount { get; private set; }

    // ---- updates ----
    public string CloudCurrent => _version.Current;
    public string? CloudLatest { get; private set; }
    public bool CloudUpdateAvailable { get; private set; }
    public string? CloudCheckError { get; private set; }
    public CloudUpdaterService.Status CloudStatus { get; private set; } = null!;
    public string? Busy { get; private set; }
    public List<Instance> Candidates { get; private set; } = new();
    public string? LatestInstanceVersion => _releases.LatestVersion;
    /// <summary>Nodes whose agent runs another version than the cloud — updated from the node's own page.</summary>
    public List<HostingOverviewService.HostRow> OutdatedAgents => Data.Hosts.Where(h => h.AgentOutdated && !h.Revoked).ToList();

    public async Task OnGetAsync(bool check = false)
    {
        var ct = HttpContext.RequestAborted;
        // Registry checks are cached (the cloud dashboard shares the entry) and only forced on request.
        if (check)
        {
            _cache.Remove("dashboard.cloudUpdate");
            await _releases.RefreshAsync(ct);
        }
        var c = await _cache.GetOrCreateAsync("dashboard.cloudUpdate", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            try { return await _version.CheckAsync(cts.Token); } catch { return null; }
        });
        CloudLatest = c?.Latest;
        CloudCheckError = c?.Error;
        CloudUpdateAvailable = c is { Error: null, UpdateAvailable: true };
        CloudStatus = await _updater.StatusAsync(checkRegistry: false, ct);
        Busy = await _updater.BusyWithAsync(ct);

        Data = await _overview.BuildAsync(ct);
        DomainCount = await _db.Instances.CountAsync(i => i.ProxyDomain != null, ct);
        Candidates = await _updates.CandidatesAsync(ct: ct);
    }

    /// <summary>Starts the cloud's self-update and hands over to the full-screen update page.</summary>
    public async Task<IActionResult> OnPostSelfUpdateAsync()
    {
        var r = await _updater.StartAsync(HttpContext.RequestAborted);
        if (!r.Ok)
        {
            TempData["FlashError"] = r.Message;
            return RedirectToPage();
        }
        return Redirect("/admin/cloudupdate");
    }

}
