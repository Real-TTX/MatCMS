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
    private readonly DockerHostService _docker;

    public IndexModel(VersionService version, ReleaseWatcher releases, DockerHostService docker)
    {
        _version = version;
        _releases = releases;
        _docker = docker;
    }

    public string Current => _version.Current;
    public string ImageRef => _version.ImageRef;
    public string UpdateCommand => _version.UpdateCommand;

    /// <summary>What the instance-facing watcher knows, shown here so both checks are visible in
    /// one place: the cloud's own image and the image its instances run.</summary>
    public string InstanceImageRef => ReleaseWatcher.ImageRef;
    public string? InstanceLatest => _releases.LatestVersion;
    public DateTime? InstanceChecked => _releases.LastCheckedUtc;

    public bool DockerConfigured => _docker.Configured;
    public bool DockerReachable { get; private set; }

    public VersionService.UpdateCheck? Check { get; private set; }

    public async Task OnGetAsync(bool check = false)
    {
        DockerReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
        if (check) Check = await _version.CheckAsync(HttpContext.RequestAborted);
    }
}
