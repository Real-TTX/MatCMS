using MatCMS.Plugin;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.ApiKeys;

/// <summary>Makes an API key: a name, the rights per plugin (only plugins that offer an API are listed) and an optional end date.</summary>
public class CreateModel : PageModel
{
    private readonly SiteApiKeyService _keys;
    private readonly PluginRegistry _registry;
    public CreateModel(SiteApiKeyService keys, PluginRegistry registry) { _keys = keys; _registry = registry; }

    /// <summary>Per plugin with API routes: its name and which kinds of access its routes ask for.</summary>
    public List<(string Key, string Name, IReadOnlyList<ApiAccess> Kinds)> Plugins { get; private set; } = new();

    public string? Error { get; private set; }

    public void OnGet() => Load();

    private void Load() => Plugins = _registry.ApiRoutes.GroupBy(r => r.Owner)
        .Select(g => (g.Key, g.First().Info.Name, (IReadOnlyList<ApiAccess>)g.Select(r => r.Options.Access).Distinct().OrderBy(a => a).ToList()))
        .OrderBy(p => p.Name).ToList();

    public async Task<IActionResult> OnPostAsync(string? name, List<string>? rights, DateTime? expires)
    {
        Load();
        var clean = SiteApiKeyService.CleanRights(rights ?? new());
        if (string.IsNullOrWhiteSpace(name)) { Error = "Bitte einen Namen angeben (z. B. „ERP Lager“)."; return Page(); }
        if (clean.Length == 0) { Error = "Bitte mindestens ein Recht wählen – ein Schlüssel ohne Rechte kann nichts."; return Page(); }
        var until = expires is { } d ? DateTime.SpecifyKind(d.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        var (_, key) = await _keys.CreateAsync(name, clean.Split(','), until, HttpContext.RequestAborted);
        TempData["NewApiKey"] = key;
        TempData["Flash"] = "Schlüssel angelegt. Kopieren Sie ihn jetzt – er wird nicht wieder angezeigt.";
        return RedirectToPage("Index");
    }
}
