using System.Text.RegularExpressions;
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

    public async Task<IActionResult> OnPostCreateMenuAsync(string name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Bitte einen Menü-Namen angeben.";
            return RedirectToPage();
        }

        var baseKey = Slugify(name);
        if (string.IsNullOrEmpty(baseKey)) baseKey = "menu";
        var key = baseKey;
        var n = 2;
        while (await _db.Menus.AnyAsync(m => m.Key == key)) key = $"{baseKey}-{n++}";

        var max = await _db.Menus.Select(m => (int?)m.SortOrder).MaxAsync() ?? -1;
        _db.Menus.Add(new Menu { Key = key, Name = name, SortOrder = max + 1, BuiltIn = false });
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü angelegt.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteMenuAsync(int id)
    {
        var menu = await _db.Menus.FindAsync(id);
        if (menu is null || menu.BuiltIn)
        {
            TempData["FlashError"] = "Dieses Menü kann nicht gelöscht werden.";
            return RedirectToPage();
        }
        var items = await _db.MenuItems.Where(m => m.Menu == menu.Key).ToListAsync();
        _db.MenuItems.RemoveRange(items);
        _db.Menus.Remove(menu);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü gelöscht.";
        return RedirectToPage();
    }

    private static string Slugify(string s)
    {
        s = s.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        return Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
    }
}
