using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Menus;

public class EditMenuModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MatCMS.Services.SiteContext _site;
    public EditMenuModel(AppDbContext db, MatCMS.Services.SiteContext site) { _db = db; _site = site; }

    public Menu Current { get; private set; } = default!;
    public List<MenuItem> Items { get; private set; } = new();

    /// <summary>Items in tree order (parent then its children), each with an indent depth — so the
    /// list shows the hierarchy. Children of nested items follow their parent.</summary>
    public List<(MenuItem Item, int Depth)> Rows { get; private set; } = new();
    [BindProperty] public string? Name { get; set; }
    public string? Error { get; private set; }

    /// <summary>The language whose items are currently shown/edited (first-level selector).</summary>
    public string SelectedLocale { get; private set; } = MatCMS.Services.Localizer.DefaultCulture;
    /// <summary>Languages offered in the dropdown: the site's active languages plus any language that
    /// already has items in this menu (so imported translations are never hidden). Default first.</summary>
    public IReadOnlyList<string> AvailableLocales { get; private set; } = new List<string>();

    public async Task<IActionResult> OnGetAsync(int id, string? locale)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        Current = m;
        Name = m.Name;
        await BuildLocalesAsync(m.Key, locale);
        await LoadItemsAsync(m.Key, SelectedLocale);
        return Page();
    }

    /// <summary>Resolve the language dropdown options + the currently selected language.</summary>
    private async Task BuildLocalesAsync(string key, string? requested)
    {
        var present = await _db.MenuItems.Where(x => x.Menu == key)
            .Select(x => x.Locale).Distinct().ToListAsync();
        var set = new HashSet<string>(_site.ActiveLocales, StringComparer.OrdinalIgnoreCase)
        {
            MatCMS.Services.Localizer.DefaultCulture
        };
        foreach (var p in present) if (!string.IsNullOrWhiteSpace(p)) set.Add(p);
        AvailableLocales = MatCMS.Services.Localizer.SupportedCultures.Where(set.Contains).ToList();
        SelectedLocale = !string.IsNullOrWhiteSpace(requested) && set.Contains(requested)
            ? AvailableLocales.First(c => string.Equals(c, requested, StringComparison.OrdinalIgnoreCase))
            : MatCMS.Services.Localizer.DefaultCulture;
    }

    public async Task<IActionResult> OnPostAsync(int id, string? locale)
    {
        var m = await _db.Menus.FindAsync(id);
        if (m is null) return RedirectToPage("Index");
        Current = m;

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            await BuildLocalesAsync(m.Key, locale);
            await LoadItemsAsync(m.Key, SelectedLocale);
            return Page();
        }
        m.Name = name;
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Menü gespeichert.";
        return RedirectToPage(new { id, locale });
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

    public async Task<IActionResult> OnPostDeleteItemAsync(int id, int itemId, string? locale)
    {
        var item = await _db.MenuItems.FindAsync(itemId);
        if (item is not null)
        {
            _db.MenuItems.Remove(item);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Menüpunkt gelöscht.";
        }
        return RedirectToPage(new { id, locale });
    }

    public async Task<IActionResult> OnPostReorderAsync(int id, string menu, int[] order, string? locale)
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
        return RedirectToPage(new { id, locale });
    }

    private async Task LoadItemsAsync(string key, string locale)
    {
        Items = await _db.MenuItems.Where(m => m.Menu == key && m.Locale == locale)
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
