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
    [BindProperty] public string? Name { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        Current = m;
        Name = m.Name;
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
            return Page();
        }
        m.Name = name;
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü gespeichert.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        if (m.BuiltIn)
        {
            TempData["FlashError"] = "Dieses Menü kann nicht gelöscht werden.";
            return RedirectToPage("Index");
        }
        var items = await _db.MenuItems.Where(x => x.Menu == m.Key).ToListAsync();
        _db.MenuItems.RemoveRange(items);
        _db.Menus.Remove(m);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü gelöscht.";
        return RedirectToPage("Index");
    }
}
