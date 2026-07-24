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
    [BindProperty] public bool OpenInNewTab { get; set; }

    public List<PageEntity> Pages { get; private set; } = new();
    public string? Error { get; private set; }

    public async Task OnGetAsync(string? menu)
    {
        if (menu is "header" or "footer") Menu = menu;
        await LoadPagesAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var label = (Label ?? "").Trim();
        var url = (Url ?? "").Trim();
        if (Menu is not ("header" or "footer")) Menu = "header";

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url))
        {
            Error = "Bitte Label und Ziel (URL) angeben.";
            await LoadPagesAsync();
            return Page();
        }

        var max = await _db.MenuItems.Where(m => m.Menu == Menu)
            .Select(m => (int?)m.SortOrder).MaxAsync() ?? -1;

        _db.MenuItems.Add(new MenuItem
        {
            Menu = Menu,
            Label = label,
            Url = url,
            OpenInNewTab = OpenInNewTab,
            SortOrder = max + 1
        });
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Menüpunkt hinzugefügt.";
        return RedirectToPage("Index");
    }

    private async Task LoadPagesAsync() =>
        Pages = await _db.Pages.OrderBy(p => p.Title).ToListAsync();
}
