using MatCMS.Content;
using MatCMS.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Templates;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    public EditModel(AppDbContext db) => _db = db;

    [BindProperty] public int Id { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? AccentColor { get; set; }
    [BindProperty] public string? HeadingFont { get; set; }
    [BindProperty] public string? BodyFont { get; set; }
    [BindProperty] public string? ButtonStyle { get; set; }
    public bool IsActive { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        Id = t.Id;
        Name = t.Name;
        AccentColor = t.AccentColor;
        HeadingFont = t.HeadingFont;
        BodyFont = t.BodyFont;
        ButtonStyle = t.ButtonStyle;
        IsActive = t.IsActive;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var t = await _db.Templates.FindAsync(Id);
        if (t is null) return RedirectToPage("Index");

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            IsActive = t.IsActive;
            Error = "Der Name ist erforderlich.";
            return Page();
        }

        t.Name = name;
        t.AccentColor = TemplateFonts.NormalizeColor(AccentColor);
        t.HeadingFont = TemplateFonts.Coerce(HeadingFont, "Geologica");
        t.BodyFont = TemplateFonts.Coerce(BodyFont, "Inter");
        t.ButtonStyle = ButtonStyle == "outline" ? "outline" : "solid";
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Template gespeichert.";
        return RedirectToPage("Index");
    }
}
