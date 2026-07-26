using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Menus;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateModel(AppDbContext db) => _db = db;

    [BindProperty] public string Menu { get; set; } = "header";
    [BindProperty] public string? Label { get; set; }
    [BindProperty] public string? Url { get; set; }
    [BindProperty] public string? Icon { get; set; }
    [BindProperty] public bool OpenInNewTab { get; set; }

    public List<PageEntity> Pages { get; private set; } = new();
    public List<Menu> Menus { get; private set; } = new();
    public int? MenuId { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(string? menu)
    {
        await LoadListsAsync();
        if (!string.IsNullOrEmpty(menu) && Menus.Any(m => m.Key == menu)) Menu = menu;
        MenuId = Menus.FirstOrDefault(m => m.Key == Menu)?.Id;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadListsAsync();
        var label = (Label ?? "").Trim();
        var url = (Url ?? "").Trim();
        if (!Menus.Any(m => m.Key == Menu)) Menu = Menus.FirstOrDefault()?.Key ?? "header";
        MenuId = Menus.FirstOrDefault(m => m.Key == Menu)?.Id;

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url))
        {
            Error = "Bitte Label und Ziel (URL) angeben.";
            return Page();
        }

        var max = await _db.MenuItems.Where(m => m.Menu == Menu)
            .Select(m => (int?)m.SortOrder).MaxAsync() ?? -1;

        _db.MenuItems.Add(new MenuItem
        {
            Menu = Menu,
            Label = label,
            Url = url,
            Icon = MatCMS.Content.MenuIcons.IsValid(Icon) ? Icon : null,
            OpenInNewTab = OpenInNewTab,
            SortOrder = max + 1,
            Locale = MatCMS.Services.Localizer.DefaultCulture
        });
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Menüpunkt hinzugefügt.";
        return MenuId is int mid ? RedirectToPage("EditMenu", new { id = mid }) : RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        Pages = await _db.Pages.OrderBy(p => p.Title).ToListAsync();
        Menus = await _db.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
    }
}
