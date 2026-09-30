using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin;

/// <summary>
/// The full-screen page of a running cloud self-update: a loading screen with the helper's log, nothing else to
/// click. Every other /admin request is redirected here while the run is in flight (<c>Program.cs</c>). The page
/// polls <see cref="OnGetStateAsync"/>; while the cloud is restarting those polls simply fail, which the page
/// shows as "startet neu". When the run has finished it shows the outcome and reloads into the admin.
/// </summary>
public class CloudUpdateModel : PageModel
{
    private readonly CloudUpdaterService _updater;
    private readonly VersionService _version;

    public CloudUpdateModel(CloudUpdaterService updater, VersionService version)
    {
        _updater = updater; _version = version;
    }

    public SelfUpdateState? Run { get; private set; }

    public IActionResult OnGet()
    {
        Run = _updater.LastRun();
        // Nothing running and nothing just finished: there is no update to watch.
        if (Run is null || (!Run.InFlight && Run.FinishedAt < DateTime.UtcNow.AddMinutes(-2)))
            return Redirect("/admin/hosting");
        return Page();
    }

    public async Task<IActionResult> OnGetStateAsync()
    {
        // StatusAsync, not just the file: it is what notices a helper that died without writing its result.
        var s = await _updater.StatusAsync(checkRegistry: false, HttpContext.RequestAborted);
        var run = s.LastRun;
        return new JsonResult(new
        {
            state = run?.State ?? "none",
            inFlight = run?.InFlight ?? false,
            message = run?.Message,
            log = run?.Log ?? new List<string>(),
            version = _version.Current,
        });
    }
}
