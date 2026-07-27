using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Plugins;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly IWebHostEnvironment _env;
    public IndexModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, IWebHostEnvironment env)
    {
        _db = db; _registry = registry; _runner = runner; _env = env;
    }

    public List<MatCMS.Models.Plugin> Items { get; private set; } = new();
    public IReadOnlyDictionary<int, string> Errors => _registry.Errors;

    public async Task OnGetAsync() =>
        Items = await _db.Plugins.OrderBy(p => p.Name).ToListAsync();

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is not null)
        {
            p.Enabled = !p.Enabled;
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is not null)
        {
            var key = p.Key;
            _db.Plugins.Remove(p);
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
            // Remove the plugin's own asset folder.
            var dir = StoragePaths.PluginAssetDir(_env, key);
            if (!string.IsNullOrWhiteSpace(key) && Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            TempData["Flash"] = "Plugin gelöscht.";
        }
        return RedirectToPage();
    }
}
