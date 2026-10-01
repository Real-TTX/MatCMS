using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Einstellungen: what applies to hosting as a whole — the module switch and automatic updates. What
/// belongs to one machine (proxy, ports, naming, Docker) is on that host's page under Hosting → Hosts.
/// </summary>
public class SettingsModel : PageModel
{
    private readonly CloudContext _cloud;
    public SettingsModel(CloudContext cloud) => _cloud = cloud;

    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public bool AutoUpdate => _cloud.Flag(SettingKeys.AutoUpdateLocal);

    public void OnGet() { }

    /// <summary>The module switch. Off hides provisioning, nodes, Domains and the edge proxy; the container actions,
    /// updates and "Dieser Host"'s page stay, because they predate the module.</summary>
    public async Task<IActionResult> OnPostModuleAsync(bool hostingEnabled)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.HostingEnabled] = hostingEnabled ? "1" : "0" });
        TempData["Flash"] = hostingEnabled ? "Hosting eingeschaltet." : "Hosting ausgeschaltet.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAutoAsync(bool autoUpdate)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.AutoUpdateLocal] = autoUpdate ? "1" : "0" });
        TempData["Flash"] = autoUpdate ? "Automatische Updates eingeschaltet." : "Automatische Updates ausgeschaltet.";
        return RedirectToPage();
    }
}
