using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Einstellungen: the module switch, and how "Dieser Host" (the cloud's own Docker daemon) hosts sites —
/// port range and naming pattern. The reverse proxy, host addresses and edge live under Hosting → Proxy.
/// </summary>
public class SettingsModel : PageModel
{
    private readonly CloudContext _cloud;
    private readonly HostingService _hosting;
    private readonly DockerHostService _docker;

    public SettingsModel(CloudContext cloud, HostingService hosting, DockerHostService docker)
    {
        _cloud = cloud; _hosting = hosting; _docker = docker;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public bool AutoUpdate => _cloud.Flag(SettingKeys.AutoUpdateLocal);

    public bool DockerReachable { get; private set; }

    /// <summary>Der Port, den die nächste Instanz bekäme — die einzige Art, die Vergabe zu prüfen, ohne etwas
    /// anzulegen. Null heißt: Bereich voll oder Daemon nicht erreichbar.</summary>
    public int? NextPort { get; private set; }
    public int UsedPortCount { get; private set; }

    public async Task OnGetAsync()
    {
        DockerReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
        NextPort = await _hosting.NextFreePortAsync(HttpContext.RequestAborted);
        UsedPortCount = (await _hosting.UsedPortsAsync(HttpContext.RequestAborted))?.Count ?? 0;
    }

    /// <summary>The module switch. Off hides provisioning, Nodes and Domains; the container actions on local
    /// instances, Updates and Docker stay, because they predate the module.</summary>
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

    /// <summary>Port range and container naming of "Dieser Host". The proxy has its own tab (Hosting → Proxy).</summary>
    public async Task<IActionResult> OnPostSaveAsync(string? portFrom, string? portTo, string? namePattern)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            // Nur ein gültiger Bereich wird gespeichert. Ein verdrehter oder unsinniger würde beim Anlegen
            // entweder nie einen freien Port finden oder einen belegten vorschlagen.
            [SettingKeys.HostingPortFrom] = Port(portFrom),
            [SettingKeys.HostingPortTo] = Port(portTo),
            [SettingKeys.HostingNamePattern] = namePattern?.Trim(),
        });
        TempData["Flash"] = "Hosting-Einstellungen gespeichert.";
        return RedirectToPage();
    }

    /// <summary>Ein Port oder nichts — 1024 bis 65535, alles andere wird verworfen statt gespeichert.</summary>
    private static string Port(string? raw) =>
        int.TryParse(raw, out var p) && p >= 1024 && p <= 65535 ? p.ToString() : "";
}
