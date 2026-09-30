using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Einstellungen: the module switch, and how "Dieser Host" (the cloud's own Docker daemon) hosts sites —
/// reverse proxy, port range, naming pattern. Nodes carry their own copy of the same fields (same partial).
/// </summary>
public class SettingsModel : PageModel
{
    private readonly CloudContext _cloud;
    private readonly SecretProtector _secrets;
    private readonly HostingService _hosting;
    private readonly ProxyService _proxy;
    private readonly DockerHostService _docker;

    public SettingsModel(CloudContext cloud, SecretProtector secrets, HostingService hosting, ProxyService proxy, DockerHostService docker)
    {
        _cloud = cloud; _secrets = secrets; _hosting = hosting; _proxy = proxy; _docker = docker;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public bool AutoUpdate => _cloud.Flag(SettingKeys.AutoUpdateLocal);

    // Automatic host addresses of "Dieser Host" + the edge proxy.
    public bool AutoDomainEnabled => _cloud.Flag(SettingKeys.HostingAutoDomainEnabled);
    public string AutoDomainBase => Get(SettingKeys.HostingAutoDomainBase);
    public int MissingHostAddresses { get; private set; }
    public int DomainsOnOtherWay { get; private set; }
    public bool EdgeEnabled => _proxy.EdgeEnabled;
    public bool EdgeUsesHostProxy => _proxy.EdgeUsesHostProxy;
    public string EdgeKind => _proxy.EdgeSettings.Kind;
    public ProxyFieldsView EdgeFields => _proxy.EdgeFieldsView();
    public ProxyFieldsView ProxyFields => _proxy.FieldsView();
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
        if (AutoDomainEnabled) MissingHostAddresses = await _proxy.MissingHostAddressCountAsync(null, HttpContext.RequestAborted);
        DomainsOnOtherWay = await _proxy.CustomerDomainsOnOtherWayCountAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostAutoDomainAsync(bool enabled, string? baseDomain)
    {
        if (enabled && ProxyService.NormaliseDomain(baseDomain) is null)
        {
            TempData["FlashError"] = "Bitte eine gültige Basis-Domain angeben, z. B. cloud.example.de.";
            return RedirectToPage();
        }
        await _proxy.SetAutoDomainAsync(enabled, baseDomain);
        TempData["Flash"] = enabled ? "Automatische Adressen eingeschaltet — neue Instanzen bekommen ihre Adresse beim Anlegen." : "Automatische Adressen ausgeschaltet.";
        return RedirectToPage();
    }

    /// <summary>Host addresses for the sites this host already runs.</summary>
    public async Task<IActionResult> OnPostAutoDomainBackfillAsync()
    {
        var r = await _proxy.PublishMissingHostAddressesAsync(null, HttpContext.RequestAborted);
        TempData[r.Failed == 0 ? "Flash" : "FlashError"] = $"{r.Created} Adresse(n) angelegt." + (r.Failed > 0 ? $" {r.Failed} fehlgeschlagen: " + string.Join(" · ", r.Errors) : "");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEdgeAsync(bool enabled, bool useHostProxy, string? provider, string? matcadUrl, string? matcadToken,
        bool clearMatcadToken, string? caddyAdminUrl, string? caddyServer)
    {
        // Only the switches when the edge is the host's proxy — its own fields keep what they had.
        await _proxy.UpdateEdgeAsync(useHostProxy
            ? new ProxyService.EdgeConfigInput(enabled, true, null, null, null, false, null, null)
            : new ProxyService.EdgeConfigInput(enabled, false, provider, matcadUrl ?? "", matcadToken, clearMatcadToken, caddyAdminUrl ?? "", caddyServer ?? ""));
        TempData["Flash"] = enabled
            ? "Edge-Proxy eingeschaltet — neu veröffentlichte Kundendomains laufen über ihn."
            : "Edge-Proxy ausgeschaltet.";
        return RedirectToPage();
    }

    /// <summary>Moves the customer domains published before the switch onto the way it now says (edge or host).</summary>
    public async Task<IActionResult> OnPostEdgeMoveAsync()
    {
        var r = await _proxy.MoveCustomerDomainsToCurrentWayAsync(HttpContext.RequestAborted);
        TempData[r.Failed == 0 ? "Flash" : "FlashError"] = $"{r.Created} Domain(s) umgestellt." + (r.Failed > 0 ? $" {r.Failed} fehlgeschlagen: " + string.Join(" · ", r.Errors) : "");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEdgeTestAsync()
    {
        var r = await _proxy.TestEdgeAsync(HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = r.Message;
        return RedirectToPage();
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

    public async Task<IActionResult> OnPostSaveAsync(
        string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? portFrom, string? portTo, string? namePattern,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        await SaveAsync(hostingMode, matcadUrl, matcadToken, clearMatcadToken, portFrom, portTo, namePattern,
            caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost);
        TempData["Flash"] = "Hosting-Einstellungen gespeichert.";
        return RedirectToPage();
    }

    /// <summary>"Speichern und Verbindung testen": the same form, posted here — saves first, so what is tested is
    /// exactly what is stored (a test of unsaved values would pass and then not be in effect).</summary>
    public async Task<IActionResult> OnPostProxyTestAsync(
        string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? portFrom, string? portTo, string? namePattern,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        await SaveAsync(hostingMode, matcadUrl, matcadToken, clearMatcadToken, portFrom, portTo, namePattern,
            caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost);
        var r = await _proxy.TestAsync(null, HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = "Gespeichert. " + r.Message;
        return RedirectToPage();
    }

    private async Task SaveAsync(
        string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? portFrom, string? portTo, string? namePattern,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?>
        {
            // Only a known provider name is stored; anything else — also the historical "docker" — means
            // "no proxy", the setting that needs nothing installed.
            [SettingKeys.HostingMode] = ProxyKinds.Normalise(hostingMode),
            [SettingKeys.HostingCaddyAdminUrl] = caddyAdminUrl?.Trim().TrimEnd('/'),
            [SettingKeys.HostingCaddyServer] = caddyServer?.Trim(),
            [SettingKeys.HostingProxyUpstream] = UpstreamModes.Normalise(proxyUpstream),
            [SettingKeys.HostingProxyNetwork] = proxyNetwork?.Trim(),
            [SettingKeys.HostingProxyUpstreamHost] = proxyUpstreamHost?.Trim(),
            [SettingKeys.HostingMatcadUrl] = matcadUrl?.Trim().TrimEnd('/'),
            // Wie beim SMTP-Passwort: leer BEHÄLT, nur der ausdrückliche Haken löscht. Sonst würde ein Speichern
            // der Portfelder den Schlüssel wegwerfen, weil das Feld leer gerendert wird.
            [SettingKeys.HostingMatcadToken] = clearMatcadToken ? ""
                : string.IsNullOrEmpty(matcadToken) ? Get(SettingKeys.HostingMatcadToken)
                : _secrets.Protect(matcadToken),
            // Nur ein gültiger Bereich wird gespeichert. Ein verdrehter oder unsinniger würde beim Anlegen
            // entweder nie einen freien Port finden oder einen belegten vorschlagen.
            [SettingKeys.HostingPortFrom] = Port(portFrom),
            [SettingKeys.HostingPortTo] = Port(portTo),
            [SettingKeys.HostingNamePattern] = namePattern?.Trim(),
        });
    }

    /// <summary>Ein Port oder nichts — 1024 bis 65535, alles andere wird verworfen statt gespeichert.</summary>
    private static string Port(string? raw) =>
        int.TryParse(raw, out var p) && p >= 1024 && p <= 65535 ? p.ToString() : "";
}
