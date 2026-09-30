using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Übersicht, the module's dashboard: how many sites run where, how loaded the hosts are, and
/// everything update-related in one card — the cloud's own version (starting its self-update opens the
/// full-screen update page) and the instances with a newer release (the bulk run shows its progress here).
/// Admin-only (folder lock in Program.cs): it spans the whole fleet.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly HostingOverviewService _overview;
    private readonly InstanceService _instances;
    private readonly ReleaseWatcher _releases;
    private readonly BulkUpdateService _bulk;
    private readonly CloudUpdaterService _updater;
    private readonly VersionService _version;
    private readonly IMemoryCache _cache;
    private readonly Localizer _t;

    public IndexModel(AppDbContext db, CloudContext cloud, HostingOverviewService overview, InstanceService instances,
        ReleaseWatcher releases, BulkUpdateService bulk, CloudUpdaterService updater, VersionService version, IMemoryCache cache, Localizer t)
    {
        _db = db; _cloud = cloud; _overview = overview; _instances = instances; _releases = releases;
        _bulk = bulk; _updater = updater; _version = version; _cache = cache; _t = t;
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
    public string? RunId { get; private set; }

    /// <summary>Approved instances the cloud can update itself — on its own host or through a node — that are behind
    /// the latest release.</summary>
    private async Task<List<Instance>> LoadCandidatesAsync()
    {
        var list = await _db.Instances.AsNoTracking().Include(i => i.Node)
            .Where(i => i.Status == InstanceStatus.Approved
                        && (i.Hosting == InstanceHosting.Local || (i.Hosting == InstanceHosting.Node && i.NodeId != null)) && i.ContainerId != null)
            .OrderBy(i => i.Name).ToListAsync();
        return list.Where(i => _instances.IsUpdateAvailable(i)).ToList();
    }

    public async Task OnGetAsync(bool check = false, string? run = null)
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
        Candidates = await LoadCandidatesAsync();
        RunId = run;
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        if (_updater.LastRun() is { InFlight: true })
        {
            TempData["FlashError"] = _t["updates.cloudBusy"];
            return RedirectToPage();
        }
        var ids = (await LoadCandidatesAsync()).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            TempData["Flash"] = _t["updates.none"];
            return RedirectToPage();
        }
        return RedirectToPage(new { run = _bulk.Start(ids) });
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

    /// <summary>Live progress of a bulk run as JSON, polled by the page.</summary>
    public IActionResult OnGetStatus(string run)
    {
        var r = _bulk.Get(run);
        if (r is null) return new JsonResult(new { found = false });
        var completed = r.Items.Count(i => i.Status is "done" or "failed" or "skipped");
        return new JsonResult(new
        {
            found = true, done = r.Done, total = r.Items.Count, completed,
            items = r.Items.Select(i => new { i.Name, i.From, i.To, i.Status, i.Message })
        });
    }
}
