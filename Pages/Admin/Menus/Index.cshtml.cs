using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Menus;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<Menu> Menus { get; private set; } = new();
    public Dictionary<string, List<MenuItem>> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Menus = await _db.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
        var all = await _db.MenuItems.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
        Items = Menus.ToDictionary(m => m.Key, m => all.Where(i => i.Menu == m.Key).ToList());
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var item = await _db.MenuItems.FindAsync(id);
        if (item is not null)
        {
            _db.MenuItems.Remove(item);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Menüpunkt gelöscht.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReorderAsync(string menu, int[] order)
    {
        var items = await _db.MenuItems.Where(m => m.Menu == menu).ToListAsync();
        if (order is { Length: > 0 })
        {
            var pos = 0;
            foreach (var id in order)
            {
                var it = items.FirstOrDefault(x => x.Id == id);
                if (it is not null) it.SortOrder = pos++;
            }
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

}
