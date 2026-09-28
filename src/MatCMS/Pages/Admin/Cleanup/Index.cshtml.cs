using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Cleanup;

/// <summary>
/// "Aufräumen" — finds media/components/plugins nothing references and removes them, but only after the
/// operator SEES the exact list and confirms it TWICE: step 1 picks items and goes to a confirmation
/// view that spells out every file/type/plugin; step 2 is a separate, red "endgültig löschen" submit
/// (with a JS confirm as the second gate). Detection is conservative (<see cref="CleanupService"/>) and
/// re-run at every step, so nothing that became referenced in between is ever deleted.
/// </summary>
public class IndexModel : PageModel
{
    private readonly CleanupService _cleanup;
    public IndexModel(CleanupService cleanup) => _cleanup = cleanup;

    public CleanupService.Report Report { get; private set; } = new([], 0, [], []);

    /// <summary>True on the confirmation step: the page shows only the chosen items + the final delete.</summary>
    public bool Confirming { get; private set; }
    public List<CleanupService.MediaItem> SelMedia { get; private set; } = new();
    public List<CleanupService.ComponentItem> SelComponents { get; private set; } = new();
    public List<CleanupService.PluginItem> SelPlugins { get; private set; } = new();
    public long SelMediaBytes { get; private set; }

    public async Task OnGetAsync()
    {
        Report = await _cleanup.AnalyzeAsync(HttpContext.RequestAborted);
    }

    // Step 1 → 2: the operator picked items; re-analyse and show exactly what they picked for a final look.
    public async Task<IActionResult> OnPostReviewAsync(int[]? mediaIds, string[]? componentTypes, string[]? pluginKeys)
    {
        var report = await _cleanup.AnalyzeAsync(HttpContext.RequestAborted);
        var mIds = (mediaIds ?? []).ToHashSet();
        var cTypes = (componentTypes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pKeys = (pluginKeys ?? []).ToHashSet();

        SelMedia = report.UnusedMedia.Where(m => mIds.Contains(m.Id)).ToList();
        SelComponents = report.UnusedComponents.Where(c => cTypes.Contains(c.Type)).ToList();
        SelPlugins = report.Plugins.Where(p => pKeys.Contains(p.Key)).ToList();
        SelMediaBytes = SelMedia.Sum(m => m.SizeBytes);

        if (SelMedia.Count == 0 && SelComponents.Count == 0 && SelPlugins.Count == 0)
        {
            TempData["FlashError"] = "Nichts ausgewählt.";
            return RedirectToPage();
        }
        Report = report;
        Confirming = true;
        return Page();
    }

    // Step 2: actually delete (CleanupService re-checks "still unused" one more time).
    public async Task<IActionResult> OnPostDeleteAsync(int[]? mediaIds, string[]? componentTypes, string[]? pluginKeys)
    {
        var (media, comps, plugins) = await _cleanup.DeleteAsync(
            mediaIds ?? [], componentTypes ?? [], pluginKeys ?? [], HttpContext.RequestAborted);
        TempData["Flash"] = $"Aufgeräumt: {media} Medien, {comps} Komponenten, {plugins} Plugins gelöscht.";
        return RedirectToPage();
    }
}
