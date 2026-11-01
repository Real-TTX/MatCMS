using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.ApiKeys;

/// <summary>
/// Admin → API-Schlüssel: the keys other programs (an ERP) use for the plugins' API (<c>/api/v1/{plugin}/…</c>), and the endpoints the loaded plugins
/// offer. A new key is shown ONCE, right after it was made (TempData), then only its prefix.
/// </summary>
public class IndexModel : PageModel
{
    private readonly SiteApiKeyService _keys;
    private readonly PluginRegistry _registry;
    public IndexModel(SiteApiKeyService keys, PluginRegistry registry) { _keys = keys; _registry = registry; }

    public List<SiteApiKey> Items { get; private set; } = new();
    public IReadOnlyList<PluginRegistry.ApiRouteEntry> Routes => _registry.ApiRoutes;

    /// <summary>The key just made, in clear — only on the page that follows its creation.</summary>
    public string? NewKey { get; private set; }

    public async Task OnGetAsync()
    {
        Items = await _keys.ListAsync(HttpContext.RequestAborted);
        NewKey = TempData["NewApiKey"] as string;
    }

    public async Task<IActionResult> OnPostRevokeAsync(int id)
    {
        if (await _keys.RevokeAsync(id, HttpContext.RequestAborted)) TempData["Flash"] = "Schlüssel widerrufen – er gilt ab sofort nicht mehr.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        if (await _keys.DeleteAsync(id, HttpContext.RequestAborted)) TempData["Flash"] = "Schlüssel gelöscht.";
        return RedirectToPage();
    }

    /// <summary>The address a program calls, for the page to show (the configured canonical address when there is one).</summary>
    public string Origin => HttpContext.RequestServices.GetRequiredService<SiteContext>() is { } site && site.Get(SettingKeys.CanonicalUrl) is { Length: > 0 } c
        ? c.TrimEnd('/') : $"{Request.Scheme}://{Request.Host}";
}
