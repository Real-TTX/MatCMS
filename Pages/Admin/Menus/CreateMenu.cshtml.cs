using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Menus;

public class CreateMenuModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateMenuModel(AppDbContext db) => _db = db;

    [BindProperty] public string? Name { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            return Page();
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
        return RedirectToPage("Index");
    }

    private static string Slugify(string s)
    {
        s = s.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        return Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
    }
}
