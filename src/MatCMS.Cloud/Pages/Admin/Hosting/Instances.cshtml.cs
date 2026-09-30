using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Instanzen: every MatCMS container on a host the cloud can reach — where it runs, its state and what it
/// uses. Containers without an instance record (started by hand, never joined) are listed too, marked, because
/// they take the same memory.
/// </summary>
public class InstancesModel : PageModel
{
    private readonly HostingOverviewService _overview;
    private readonly CloudContext _cloud;

    public InstancesModel(HostingOverviewService overview, CloudContext cloud)
    {
        _overview = overview; _cloud = cloud;
    }

    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public HostingOverviewService.Overview Data { get; private set; } = null!;

    public async Task OnGetAsync() => Data = await _overview.BuildAsync(HttpContext.RequestAborted);
}
