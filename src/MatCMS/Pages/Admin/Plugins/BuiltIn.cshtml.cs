using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Plugins;

/// <summary>
/// The page of a plugin that is part of the CMS (the blog). It can be switched on and off and has its settings, like any plugin —
/// but there is no source to edit, nothing to export and nothing to delete: it comes with the image and is updated with it.
/// Switching it off keeps its data; the page says so.
/// </summary>
public class BuiltInModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly PluginDataService _data;

    public BuiltInModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, PluginDataService data)
    {
        _db = db; _registry = registry; _runner = runner; _data = data;
    }

    public MatCMS.Models.Plugin Current { get; private set; } = default!;
    public bool Loaded { get; private set; }
    public string? RunError { get; private set; }
    public bool HasSettings { get; private set; }
    public IReadOnlyList<string> Log { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<PluginDataService.TableInfo> Tables { get; private set; } = Array.Empty<PluginDataService.TableInfo>();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var p = await _db.Plugins.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return RedirectToPage("Index");
        if (PluginKind.Normalize(p.Kind) != PluginKind.BuiltIn) return RedirectToPage(PluginKind.Normalize(p.Kind) == PluginKind.Native ? "Native" : "Edit", new { id });
        await FillAsync(p);
        return Page();
    }

    /// <summary>Switches the plugin on or off. Off removes its pages, blocks and menu entries at once; its data stays.</summary>
    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null || PluginKind.Normalize(p.Kind) != PluginKind.BuiltIn) return RedirectToPage("Index");
        p.Enabled = !p.Enabled;
        await _db.SaveChangesAsync();
        await _runner.ReloadAsync(id);
        return RedirectToPage(new { id });
    }

    private async Task FillAsync(MatCMS.Models.Plugin p)
    {
        Current = p;
        Loaded = _registry.IsLoaded(p.Key);
        RunError = _registry.Errors.TryGetValue(p.Id, out var e) ? e : null;
        HasSettings = _registry.SliceOf(p.Key) is { Settings.Count: > 0 };
        Log = _registry.LogOf(p.Key);
        Tables = await _data.DescribeAsync(_db, p.Key);
    }
}
