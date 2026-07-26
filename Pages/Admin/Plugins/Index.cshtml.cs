using MatCMS.Data;
using MatCMS.Models;
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
    public IndexModel(AppDbContext db, PluginRegistry registry, PluginRunner runner)
    {
        _db = db; _registry = registry; _runner = runner;
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
            _db.Plugins.Remove(p);
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
            TempData["Flash"] = "Plugin gelöscht.";
        }
        return RedirectToPage();
    }
}
