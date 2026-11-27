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

    /// <summary>A plugin is listed for its API routes AND for the operations it describes (which the site's MCP server offers): both are reached
    /// with the same rights.</summary>
    private void Load() => Plugins = _registry.ApiRoutes.Select(r => (r.Owner, r.Info.Name, r.Options.Access))
        .Concat(_registry.Operations.Where(o => o.Spec is { Internal: false }).SelectMany(o => OperationRights(o).Select(a => (o.Owner, o.Info.Name, a))))
        .GroupBy(r => r.Owner)
        .Select(g => (g.Key, g.First().Name, (IReadOnlyList<ApiAccess>)g.Select(r => r.Item3).Distinct().OrderBy(a => a).ToList()))
        .OrderBy(p => p.Name).ToList();

    private static IEnumerable<ApiAccess> OperationRights(PluginRegistry.OperationEntry o)
    {
        if (o.Spec!.PersonalData) yield return ApiAccess.PersonalData;
        yield return o.Spec.Writes ? ApiAccess.Write : ApiAccess.Read;
    }

    /// <summary>The site's own areas for the MCP server (<see cref="SiteApiKeyService.CoreAreas"/>) and which kinds each offers.</summary>
    public static readonly (string Area, bool Read)[] CoreAreas =
        [("core.pages", true), ("core.media", true), ("core.forms", true), ("core.design", true), ("core.settings", false)];

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
