using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Updates: everything that swaps an image. The cloud's own self-update, the bulk "update all" of
/// the instances the cloud can act on (this host and nodes — a background job the page polls through
/// <see cref="OnGetStatus"/>), and the automatic-update rule. Who is TOLD about updates is the notification
/// matrix, not this page.
/// </summary>
public class UpdatesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly ReleaseWatcher _releases;
    private readonly InstanceService _instances;
    private readonly BulkUpdateService _bulk;
    private readonly CloudUpdaterService _updater;
    private readonly Localizer _t;

    public UpdatesModel(AppDbContext db, CloudContext cloud, ReleaseWatcher releases, InstanceService instances,
        BulkUpdateService bulk, CloudUpdaterService updater, Localizer t)
    {
        _db = db; _cloud = cloud; _releases = releases; _instances = instances; _bulk = bulk; _updater = updater; _t = t;
    }

    public CloudUpdateCard Card { get; private set; } = null!;
    public List<Instance> Candidates { get; private set; } = new();
    public string? LatestVersion => _releases.LatestVersion;
    public DateTime? LastChecked => _releases.LastCheckedUtc;
    public string? RunId { get; private set; }
    public bool AutoUpdate => _cloud.Flag(SettingKeys.AutoUpdateLocal);

    /// <summary>Approved instances the cloud can update itself — on its own host or through a node — that are behind
    /// the latest release.</summary>
    private async Task<List<Instance>> LoadCandidatesAsync()
    {
        var local = await _db.Instances.AsNoTracking()
            .Include(i => i.Node)
            .Where(i => i.Status == InstanceStatus.Approved
                        && (i.Hosting == InstanceHosting.Local || (i.Hosting == InstanceHosting.Node && i.NodeId != null)) && i.ContainerId != null)
            .OrderBy(i => i.Name)
            .ToListAsync();
        return local.Where(i => _instances.IsUpdateAvailable(i)).ToList();
    }

    public async Task OnGetAsync(bool check = false, string? run = null)
    {
        // Both registry checks are on demand: the page must still open when the registry is unreachable. The
        // instance-facing cache otherwise only refreshes every 30 min, which makes "is the new image there yet?"
        // untestable without a restart.
        if (check) await _releases.RefreshAsync(HttpContext.RequestAborted);
        Card = new CloudUpdateCard(await _updater.StatusAsync(check, HttpContext.RequestAborted), CanAct: true);
        RunId = run;
        Candidates = await LoadCandidatesAsync();
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        var ids = (await LoadCandidatesAsync()).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            TempData["Flash"] = _t["updates.none"];
            return RedirectToPage();
        }
        return RedirectToPage(new { run = _bulk.Start(ids) });
    }

    public async Task<IActionResult> OnPostSelfUpdateAsync()
    {
        var r = await _updater.StartAsync(HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = r.Message;
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAutoAsync(bool autoUpdate)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.AutoUpdateLocal] = autoUpdate ? "1" : "0" });
        TempData["Flash"] = _t["common.saved"];
        return RedirectToPage();
    }

    /// <summary>Live progress as JSON, polled by the page.</summary>
    public IActionResult OnGetStatus(string run)
    {
        var r = _bulk.Get(run);
        if (r is null) return new JsonResult(new { found = false });

        var completed = r.Items.Count(i => i.Status is "done" or "failed" or "skipped");
        return new JsonResult(new
        {
            found = true,
            done = r.Done,
            total = r.Items.Count,
            completed,
            items = r.Items.Select(i => new { i.Name, i.From, i.To, i.Status, i.Message })
        });
    }
}
