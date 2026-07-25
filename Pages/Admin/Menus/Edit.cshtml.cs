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

    public List<PageEntity> Pages { get; private set; } = new();
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
        await LoadPagesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var item = await _db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();

        var label = (Label ?? "").Trim();
        var url = (Url ?? "").Trim();
        if (Menu is not ("header" or "footer" or "toolbar")) Menu = "header";

        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url))
        {
            Error = "Bitte Label und Ziel (URL) angeben.";
            await LoadPagesAsync();
            return Page();
        }

        item.Menu = Menu;
        item.Label = label;
        item.Url = url;
        item.Icon = MatCMS.Content.MenuIcons.IsValid(Icon) ? Icon : null;
        item.OpenInNewTab = OpenInNewTab;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Menüpunkt gespeichert.";
        return RedirectToPage("Index");
    }

    private async Task LoadPagesAsync() =>
        Pages = await _db.Pages.OrderBy(p => p.Title).ToListAsync();
}
