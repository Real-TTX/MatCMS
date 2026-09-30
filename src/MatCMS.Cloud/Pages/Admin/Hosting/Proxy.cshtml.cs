using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Proxy: everything that routes a name to a site, in one place — the reverse proxy of "Dieser Host", its
/// automatic host addresses (with the wildcard certificate) and the cloud-wide edge proxy for customer domains. A
/// node's own proxy stays on the node's page, because it belongs to that machine. Part of the optional module: with
/// the module off, nothing here is used, so the page sends the operator back to Einstellungen.
/// </summary>
public class ProxyModel : PageModel
{
    private readonly CloudContext _cloud;
    private readonly SecretProtector _secrets;
    private readonly ProxyService _proxy;

    public ProxyModel(CloudContext cloud, SecretProtector secrets, ProxyService proxy)
    {
        _cloud = cloud; _secrets = secrets; _proxy = proxy;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);

    public bool AutoDomainEnabled => _cloud.Flag(SettingKeys.HostingAutoDomainEnabled);
    public string AutoDomainBase => Get(SettingKeys.HostingAutoDomainBase);
    public int MissingHostAddresses { get; private set; }
    public int DomainsOnOtherWay { get; private set; }
    public bool EdgeEnabled => _proxy.EdgeEnabled;
    public bool EdgeUsesHostProxy => _proxy.EdgeUsesHostProxy;
    public string EdgeKind => _proxy.EdgeSettings.Kind;
    public ProxyFieldsView EdgeFields => _proxy.EdgeFieldsView();
    public ProxyService.WildcardConfig Wildcard => _proxy.WildcardFor(null);
    public string EdgeTrustedIps => Get(SettingKeys.HostingEdgeTrustedIps);
    public ProxyFieldsView ProxyFields => _proxy.FieldsView();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!ModuleEnabled) return RedirectToPage("Settings");
        if (AutoDomainEnabled) MissingHostAddresses = await _proxy.MissingHostAddressCountAsync(null, HttpContext.RequestAborted);
        DomainsOnOtherWay = await _proxy.CustomerDomainsOnOtherWayCountAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        await SaveAsync(hostingMode, matcadUrl, matcadToken, clearMatcadToken, caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost);
        TempData["Flash"] = "Proxy-Einstellungen gespeichert.";
        return RedirectToPage();
    }

    /// <summary>"Speichern und Verbindung testen": the same form, posted here — saves first, so what is tested is
    /// exactly what is stored (a test of unsaved values would pass and then not be in effect).</summary>
    public async Task<IActionResult> OnPostProxyTestAsync(
        string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
        string? caddyAdminUrl, string? caddyServer, string? proxyUpstream, string? proxyNetwork, string? proxyUpstreamHost)
    {
        await SaveAsync(hostingMode, matcadUrl, matcadToken, clearMatcadToken, caddyAdminUrl, caddyServer, proxyUpstream, proxyNetwork, proxyUpstreamHost);
        var r = await _proxy.TestAsync(null, HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = "Gespeichert. " + r.Message;
        return RedirectToPage();
    }

    private async Task SaveAsync(
        string? hostingMode, string? matcadUrl, string? matcadToken, bool clearMatcadToken,
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
            // Wie beim SMTP-Passwort: leer BEHÄLT, nur der ausdrückliche Haken löscht — das Feld wird leer gerendert.
            [SettingKeys.HostingMatcadToken] = clearMatcadToken ? ""
                : string.IsNullOrEmpty(matcadToken) ? Get(SettingKeys.HostingMatcadToken)
                : _secrets.Protect(matcadToken),
        });
    }

    public async Task<IActionResult> OnPostAutoDomainAsync(bool enabled, string? baseDomain, bool wildcard, string? dnsProvider, string? dnsCredentials)
    {
        if (enabled && ProxyService.NormaliseDomain(baseDomain) is null)
        {
            TempData["FlashError"] = "Bitte eine gültige Basis-Domain angeben, z. B. cloud.example.de.";
            return RedirectToPage();
        }
        await _proxy.SetAutoDomainAsync(enabled, baseDomain);
        var msg = enabled ? "Automatische Adressen eingeschaltet — neue Instanzen bekommen ihre Adresse beim Anlegen." : "Automatische Adressen ausgeschaltet.";
        // The wildcard follows the base domain; switching it (or the domain) applies it at the proxy right away.
        var before = _proxy.WildcardFor(null);
        if (wildcard || before.Enabled || before.RouteId is not null)
        {
            var w = await _proxy.SetWildcardAsync(null, wildcard && enabled, dnsProvider, dnsCredentials, HttpContext.RequestAborted);
            if (!w.Ok) { TempData["FlashError"] = msg + " Wildcard-Zertifikat: " + w.Message; return RedirectToPage(); }
            msg += " " + w.Message;
        }
        TempData["Flash"] = msg;
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
        bool clearMatcadToken, string? caddyAdminUrl, string? caddyServer, string? trustedIps)
    {
        await _cloud.SaveAsync(new Dictionary<string, string?> { [SettingKeys.HostingEdgeTrustedIps] = trustedIps?.Trim() });
        // Only the switches when the edge is the host's proxy — its own fields keep what they had.
        await _proxy.UpdateEdgeAsync(useHostProxy
            ? new ProxyService.EdgeConfigInput(enabled, true, null, null, null, false, null, null)
            : new ProxyService.EdgeConfigInput(enabled, false, provider, matcadUrl ?? "", matcadToken, clearMatcadToken, caddyAdminUrl ?? "", caddyServer ?? ""));
        // Every Caddy host trusts the edge while it is on (the visitor's IP survives the second proxy), and stops when off.
        var trustErrors = await _proxy.ApplyTrustEverywhereAsync(HttpContext.RequestAborted);
        var msg = enabled ? "Edge-Proxy eingeschaltet — neu veröffentlichte Kundendomains laufen über ihn." : "Edge-Proxy ausgeschaltet.";
        if (trustErrors.Count > 0) { TempData["FlashError"] = msg + " Vertrauenswürdiger Proxy nicht überall gesetzt: " + string.Join(" · ", trustErrors); return RedirectToPage(); }
        TempData["Flash"] = msg;
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
}
