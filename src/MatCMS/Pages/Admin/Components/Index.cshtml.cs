using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Components;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    /// <summary>User-defined components ("Eigene Komponenten").</summary>
    public List<Component> Items { get; private set; } = new();

    /// <summary>Built-in block types ("Systemkomponenten"), shown read-only.</summary>
    public IReadOnlyList<BlockDefinition> System { get; private set; } = BlockRegistry.Builtins;

    public async Task OnGetAsync() =>
        Items = await _db.Components.OrderBy(c => c.Name).ToListAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var c = await _db.Components.FindAsync(id);
        if (c is not null)
        {
            _db.Components.Remove(c);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Komponente gelöscht.";
        }
        return RedirectToPage();
    }
}
