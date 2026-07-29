using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Menus;

public class EditMenuModel : PageModel
{
    private readonly AppDbContext _db;
    public EditMenuModel(AppDbContext db) => _db = db;

    public Menu Current { get; private set; } = default!;
    public List<MenuItem> Items { get; private set; } = new();

    /// <summary>Items in tree order (parent then its children), each with an indent depth — so the
    /// list shows the hierarchy. Children of nested items follow their parent.</summary>
    public List<(MenuItem Item, int Depth)> Rows { get; private set; } = new();
    [BindProperty] public string? Name { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        Current = m;
        Name = m.Name;
        await LoadItemsAsync(m.Key);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        Current = m;

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            await LoadItemsAsync(m.Key);
            return Page();
        }
        m.Name = name;
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü gespeichert.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        if (m.BuiltIn)
        {
            TempData["FlashError"] = "Dieses Menü kann nicht gelöscht werden.";
            return RedirectToPage(new { id });
        }
        var items = await _db.MenuItems.Where(x => x.Menu == m.Key).ToListAsync();
        _db.MenuItems.RemoveRange(items);
        _db.Menus.Remove(m);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü gelöscht.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteItemAsync(int id, int itemId)
    {
        var item = await _db.MenuItems.FindAsync(itemId);
        if (item is not null)
        {
            _db.MenuItems.Remove(item);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Menüpunkt gelöscht.";
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReorderAsync(int id, string menu, int[] order)
    {
        var items = await _db.MenuItems.Where(m => m.Menu == menu).ToListAsync();
        if (order is { Length: > 0 })
        {
            var pos = 0;
            foreach (var itemId in order)
            {
                var it = items.FirstOrDefault(x => x.Id == itemId);
                if (it is not null) it.SortOrder = pos++;
            }
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    private async Task LoadItemsAsync(string key)
    {
        Items = await _db.MenuItems.Where(m => m.Menu == key)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();

        // Flatten the tree (parent → children) with a depth for indentation.
        Rows = new List<(MenuItem, int)>();
        void Walk(IReadOnlyList<MatCMS.Content.MenuNode> nodes, int depth)
        {
            foreach (var n in nodes) { Rows.Add((n.Item, depth)); Walk(n.Children, depth + 1); }
        }
        Walk(MatCMS.Content.MenuTree.Build(Items), 0);
    }
}
