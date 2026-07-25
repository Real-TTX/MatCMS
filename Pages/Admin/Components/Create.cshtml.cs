using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Components;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateModel(AppDbContext db) => _db = db;

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

        var baseType = Slugify(name);
        if (string.IsNullOrEmpty(baseType)) baseType = "komponente";
        var type = baseType;
        var n = 2;
        while (BlockRegistry.BuiltinTypes.Contains(type) || await _db.Components.AnyAsync(c => c.Type == type))
            type = $"{baseType}-{n++}";

        var c = new Component { Name = name, Type = type, FieldsJson = "[]", TemplateHtml = "" };
        _db.Components.Add(c);
        await _db.SaveChangesAsync();
        return RedirectToPage("Edit", new { id = c.Id });
    }

    private static string Slugify(string s)
    {
        s = s.Trim().ToLowerInvariant()
            .Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        s = Regex.Replace(s, "[^a-z0-9]+", "-").Trim('-');
        return s;
    }
}
