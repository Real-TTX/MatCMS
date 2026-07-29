using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Menus;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    public EditModel(AppDbContext db) => _db = db;

    [BindProperty] public string Menu { get; set; } = "header";
    [BindProperty] public string? Label { get; set; }
    [BindProperty] public string? Url { get; set; }
    [BindProperty] public string? Icon { get; set; }
    [BindProperty] public bool OpenInNewTab { get; set; }
    [BindProperty] public int? ParentId { get; set; }

    public List<PageEntity> Pages { get; private set; } = new();
    public List<Menu> Menus { get; private set; } = new();
    public List<MenuItem> ParentOptions { get; private set; } = new();
    public int? MenuId { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var item = await _db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        Menu = item.Menu;
        Label = item.Label;
        Url = item.Url;
        Icon = item.Icon;
        OpenInNewTab = item.OpenInNewTab;
        ParentId = item.ParentId;
        await LoadListsAsync();
        MenuId = Menus.FirstOrDefault(m => m.Key == Menu)?.Id;
        await LoadParentOptionsAsync(item);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var item = await _db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        await LoadListsAsync();
        var label = (Label ?? "").Trim();
        var url = (Url ?? "").Trim();
        if (!Menus.Any(m => m.Key == Menu)) Menu = Menus.FirstOrDefault()?.Key ?? "header";
        MenuId = Menus.FirstOrDefault(m => m.Key == Menu)?.Id;
        await LoadParentOptionsAsync(item);

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url))
        {
            Error = "Bitte Label und Ziel (URL) angeben.";
            return Page();
        }

        item.Menu = Menu;
        item.Label = label;
        item.Url = url;
        item.Icon = MatCMS.Content.MenuIcons.IsValid(Icon) ? Icon : null;
        item.OpenInNewTab = OpenInNewTab;
        // Accept only a valid offered parent (same menu+locale, top-level, not self) — else top-level.
        item.ParentId = ParentId is int pid && ParentOptions.Any(o => o.Id == pid) ? pid : null;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Menüpunkt gespeichert.";
        return MenuId is int mid ? RedirectToPage("EditMenu", new { id = mid }) : RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        Pages = await _db.Pages.OrderBy(p => p.Title).ToListAsync();
        Menus = await _db.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
    }

    // Offer top-level items of the same menu + locale (except this item) as parents — a clean 2-level
    // menu, cycle-free by construction. Deeper nesting can still arrive via import and renders fine.
    private async Task LoadParentOptionsAsync(MenuItem item) =>
        ParentOptions = await _db.MenuItems
            .Where(m => m.Menu == item.Menu && m.Locale == item.Locale && m.ParentId == null && m.Id != item.Id)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
}
