using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Updates: running instance updates. Every instance the cloud can update is listed with a checkbox (all
/// ticked by default); the ticked ones are updated one after another, each with rollback. The run's progress replaces
/// the list on this page (<c>?run=</c>, polled via <see cref="OnGetStatus"/>). The dashboard only shows what needs an
/// update and links here — one place runs updates, so it never appears twice.
/// </summary>
public class UpdatesModel : PageModel
{
    private readonly InstanceUpdatesService _updates;
    private readonly ReleaseWatcher _releases;
    private readonly Localizer _t;

    public UpdatesModel(InstanceUpdatesService updates, ReleaseWatcher releases, Localizer t)
    {
        _updates = updates; _releases = releases; _t = t;
    }

    public List<Instance> Candidates { get; private set; } = new();
    public string? LatestVersion => _releases.LatestVersion;
    public string? RunId { get; private set; }

    public async Task OnGetAsync(string? run = null)
    {
        RunId = run;
        if (run is null) Candidates = await _updates.CandidatesAsync(ct: HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostStartAsync(string[]? ids)
    {
        if (ids is null || ids.Length == 0)
        {
            TempData["FlashError"] = _t["updates.noneSelected"];
            return RedirectToPage();
        }
        var r = await _updates.StartAsync(ids, ct: HttpContext.RequestAborted);
        if (!r.Ok)
        {
            TempData["FlashError"] = r.Error;
            return RedirectToPage();
        }
        return RedirectToPage(new { run = r.RunId });
    }

    /// <summary>Live progress as JSON, polled by the page.</summary>
    public IActionResult OnGetStatus(string run) => new JsonResult(_updates.Progress(run) ?? new { found = false });
}
