using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Plugins;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    public EditModel(AppDbContext db, PluginRegistry registry, PluginRunner runner)
    {
        _db = db; _registry = registry; _runner = runner;
    }

    public MatCMS.Models.Plugin Current { get; private set; } = default!;
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public bool Enabled { get; set; }
    public string? Error { get; private set; }
    public string? RunError { get; private set; }
    public IReadOnlyList<string> Log => _registry.Log;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        Current = p;
        Name = p.Name; Description = p.Description; Code = p.Code; Enabled = p.Enabled;
        RunError = _registry.Errors.TryGetValue(id, out var e) ? e : null;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        Current = p;

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            return Page();
        }

        p.Name = name;
        p.Description = (Description ?? "").Trim();
        p.Code = Code ?? "";
        p.Enabled = Enabled;
        await _db.SaveChangesAsync();

        // Re-run all plugins so this one takes effect (or surfaces its error).
        await _runner.RunAllAsync();
        RunError = _registry.Errors.TryGetValue(id, out var e) ? e : null;

        if (RunError is not null)
        {
            // Stay on the page and show the compile/run error.
            Name = p.Name; Description = p.Description; Code = p.Code; Enabled = p.Enabled;
            return Page();
        }

        TempData["Flash"] = "Plugin gespeichert.";
        return RedirectToPage("Index");
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
        return RedirectToPage("Index");
    }
}
