using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Menus;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Menu> Menus { get; private set; } = new();
    public Dictionary<string, int> Counts { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Menus = await _db.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
        var counts = await _db.MenuItems.GroupBy(m => m.Menu)
            .Select(g => new { Menu = g.Key, Count = g.Count() })
            .ToListAsync();
        Counts = counts.ToDictionary(c => c.Menu, c => c.Count);
    }
}
