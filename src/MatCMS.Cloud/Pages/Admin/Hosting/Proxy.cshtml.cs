using MatCMS.Cloud.Services;
using MatCMS.Cloud.Services.Proxy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Edge-Proxy: the cloud-wide proxy for customer domains — the one proxy concern that spans hosts. Each
/// host's own reverse proxy, automatic addresses and wildcard certificate are on its page (Hosting → Hosts). Part of
/// the optional module: with the module off nothing here is used, so the page sends the operator to Einstellungen.
/// </summary>
public class ProxyModel : PageModel
{
    private readonly CloudContext _cloud;
    private readonly ProxyService _proxy;

    public ProxyModel(CloudContext cloud, ProxyService proxy)
    {
        _cloud = cloud; _proxy = proxy;
    }

    public string Get(string key) => _cloud.Get(key) ?? "";
    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);

    public int DomainsOnOtherWay { get; private set; }
    public bool EdgeEnabled => _proxy.EdgeEnabled;
    public bool EdgeUsesHostProxy => _proxy.EdgeUsesHostProxy;
    public string EdgeKind => _proxy.EdgeSettings.Kind;
    public ProxyFieldsView EdgeFields => _proxy.EdgeFieldsView();
    public string EdgeTrustedIps => Get(SettingKeys.HostingEdgeTrustedIps);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!ModuleEnabled) return RedirectToPage("Settings");
        DomainsOnOtherWay = await _proxy.CustomerDomainsOnOtherWayCountAsync(HttpContext.RequestAborted);
        return Page();
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
