using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// Bulk "update all local instances". Lists the instances that can be updated (from → to), starts a
/// background job, and shows its live progress — the client polls <see cref="OnGetStatusAsync"/> while
/// the job runs one update after another.
/// </summary>
public class UpdateAllModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ReleaseWatcher _releases;
    private readonly InstanceService _instances;
    private readonly BulkUpdateService _bulk;

    public UpdateAllModel(AppDbContext db, ReleaseWatcher releases, InstanceService instances, BulkUpdateService bulk)
    {
        _db = db; _releases = releases; _instances = instances; _bulk = bulk;
    }

    public List<Instance> Candidates { get; private set; } = new();
    public string? LatestVersion => _releases.LatestVersion;
    public string? RunId { get; private set; }

    /// <summary>Approved, LOCAL (updatable by the cloud) instances that are behind the latest release.</summary>
    private async Task<List<Instance>> LoadCandidatesAsync()
    {
        var local = await _db.Instances.AsNoTracking()
            .Where(i => i.Status == InstanceStatus.Approved
                        && i.Hosting == InstanceHosting.Local && i.ContainerId != null)
            .OrderBy(i => i.Name)
            .ToListAsync();
        return local.Where(i => _instances.IsUpdateAvailable(i)).ToList();
    }

    public async Task OnGetAsync(string? run = null)
    {
        RunId = run;
        Candidates = await LoadCandidatesAsync();
    }

    public async Task<IActionResult> OnPostStartAsync()
    {
        var ids = (await LoadCandidatesAsync()).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            TempData["Flash"] = "Es gibt keine lokalen Instanzen mit verfügbarem Update.";
            return RedirectToPage();
        }
        var runId = _bulk.Start(ids);
        return RedirectToPage(new { run = runId });
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
