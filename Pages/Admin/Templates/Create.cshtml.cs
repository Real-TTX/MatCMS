using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Templates;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    public CreateModel(AppDbContext db) => _db = db;

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? AccentColor { get; set; } = "#de7e11";
    [BindProperty] public string? HeadingFont { get; set; } = "Geologica";
    [BindProperty] public string? BodyFont { get; set; } = "Inter";
    [BindProperty] public string? ButtonStyle { get; set; } = "solid";
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Der Name ist erforderlich.";
            return Page();
        }

        var isFirst = !_db.Templates.Any();

        _db.Templates.Add(new Template
        {
            Name = name,
            IsActive = isFirst,
            AccentColor = TemplateFonts.NormalizeColor(AccentColor),
            HeadingFont = TemplateFonts.Coerce(HeadingFont, "Geologica"),
            BodyFont = TemplateFonts.Coerce(BodyFont, "Inter"),
            ButtonStyle = ButtonStyle == "outline" ? "outline" : "solid"
        });
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Template erstellt.";
        return RedirectToPage("Index");
    }
}
