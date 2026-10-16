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
    public Dictionary<int, int> ParentDepths { get; private set; } = new();
    public int? MenuId { get; private set; }
    /// <summary>The edited item's content language — used to return to the right language tab.</summary>
    public string ItemLocale { get; private set; } = MatCMS.Services.Localizer.DefaultCulture;
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
        ItemLocale = item.Locale;
        await LoadListsAsync();
        MenuId = Menus.FirstOrDefault(m => m.Key == Menu)?.Id;
        await LoadParentOptionsAsync(item);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var item = await _db.MenuItems.FindAsync(id);
        if (item is null) return NotFound();
        ItemLocale = item.Locale;

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
        // "plugin:shop.cart" stands for a piece a plugin offers; it must be one that exists, else the item would silently never show.
        if (url.StartsWith("plugin:", StringComparison.Ordinal) && !HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginContributions>().HasMenuElement(url["plugin:".Length..]))
        {
            Error = "Dieses Plugin-Element gibt es nicht (ist das Plugin eingeschaltet?).";
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
        return MenuId is int mid ? RedirectToPage("EditMenu", new { id = mid, locale = item.Locale }) : RedirectToPage("Index");
    }

    private async Task LoadListsAsync()
    {
        Pages = await _db.Pages.OrderBy(p => p.Title).ToListAsync();
        Menus = await _db.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
    }

    // Offer the items of the same menu + locale as parents, at any level — except this item and what hangs below it (a loop), and only where it still fits into the most levels a menu may have.
    private async Task LoadParentOptionsAsync(MenuItem item)
    {
        var items = await _db.MenuItems.Where(m => m.Menu == item.Menu && m.Locale == item.Locale).OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
        var choices = MatCMS.Content.MenuTree.ParentChoices(items, item.Id);
        ParentOptions = choices.Select(c => c.Item).ToList();
        ParentDepths = choices.ToDictionary(c => c.Item.Id, c => c.Depth);
    }
}
