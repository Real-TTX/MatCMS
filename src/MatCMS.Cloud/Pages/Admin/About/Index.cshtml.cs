using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.About;

/// <summary>
/// System page: which version of the cloud is running, and whether a newer image exists. The cloud
/// watches every instance's version — this is the one place it looks at its own.
/// <para>The check runs on demand, not on page load: it talks to a registry, and a system page must
/// still open when that registry is unreachable.</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly VersionService _version;
    private readonly ReleaseWatcher _releases;
    private readonly CloudUpdaterService _updater;

    public IndexModel(VersionService version, ReleaseWatcher releases, CloudUpdaterService updater)
    {
        _version = version;
        _releases = releases;
        _updater = updater;
    }

    /// <summary>Version card, read-only: the self-update is started from Hosting → Updates.</summary>
    public CloudUpdateCard Card { get; private set; } = null!;

    public string Current => _version.Current;
    public string ImageRef => _version.ImageRef;
    public string UpdateCommand => _version.UpdateCommand;

    /// <summary>What the instance-facing watcher knows, shown here so both checks are visible in
    /// one place: the cloud's own image and the image its instances run.</summary>
    public string InstanceImageRef => ReleaseWatcher.ImageRef;
    public string? InstanceLatest => _releases.LatestVersion;
    public DateTime? InstanceChecked => _releases.LastCheckedUtc;

    public VersionService.UpdateCheck? Check { get; private set; }

    public async Task OnGetAsync(bool check = false)
    {
        if (check)
        {
            // Force BOTH checks, not just the cloud's own image. The instance-facing release cache
            // (ReleaseWatcher) otherwise only refreshes on startup and every 30 min, which is exactly
            // what makes "did my new instance image show up yet?" untestable without a restart. One
            // click here re-polls the registry, and the next instance heartbeat (~60 s) sees the
            // result. Both talk to a registry, so — like the self-check — this is on demand, never on
            // plain page load.
            await _releases.RefreshAsync(HttpContext.RequestAborted);
        }
        // The card's own registry check replaces the former separate _version.CheckAsync — one call, one answer.
        Card = new CloudUpdateCard(await _updater.StatusAsync(check, HttpContext.RequestAborted), CanAct: false);
        Check = null;
    }
}
