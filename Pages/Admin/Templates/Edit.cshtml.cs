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
    [BindProperty] public string? SecondaryColor { get; set; }
    [BindProperty] public string? HeadingColor { get; set; }
    [BindProperty] public string? TextColor { get; set; }
    [BindProperty] public string? BackgroundColor { get; set; }
    [BindProperty] public string? AltBackground { get; set; }
    [BindProperty] public string? HeadingFont { get; set; }
    [BindProperty] public string? BodyFont { get; set; }
    [BindProperty] public string? ButtonStyle { get; set; }
    [BindProperty] public string? ContainerWidth { get; set; }
    [BindProperty] public string? ButtonRadius { get; set; }
    [BindProperty] public string? HeaderBackground { get; set; }
    [BindProperty] public string? HeaderTextColor { get; set; }
    [BindProperty] public string? HeaderPadding { get; set; }
    [BindProperty] public string? CustomCss { get; set; }
    [BindProperty] public string? CustomJs { get; set; }
    public bool IsActive { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        Id = t.Id;
        Name = t.Name;
        AccentColor = t.AccentColor;
        SecondaryColor = t.SecondaryColor;
        HeadingColor = t.HeadingColor;
        TextColor = t.TextColor;
        BackgroundColor = t.BackgroundColor;
        AltBackground = t.AltBackground;
        HeadingFont = t.HeadingFont;
        BodyFont = t.BodyFont;
        ButtonStyle = t.ButtonStyle;
        ContainerWidth = t.ContainerWidth;
        ButtonRadius = t.ButtonRadius;
        HeaderBackground = t.HeaderBackground;
        HeaderTextColor = t.HeaderTextColor;
        HeaderPadding = t.HeaderPadding;
        CustomCss = t.CustomCss;
        CustomJs = t.CustomJs;
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
        t.SecondaryColor = TemplateFonts.OptionalColor(SecondaryColor);
        t.HeadingColor = TemplateFonts.NormalizeColorOr(HeadingColor, "#010101");
        t.TextColor = TemplateFonts.NormalizeColorOr(TextColor, "#1a1a1a");
        t.BackgroundColor = TemplateFonts.NormalizeColorOr(BackgroundColor, "#ffffff");
        t.AltBackground = TemplateFonts.NormalizeColorOr(AltBackground, "#f6f7f9");
        t.HeadingFont = TemplateFonts.Coerce(HeadingFont, "Geologica");
        t.BodyFont = TemplateFonts.Coerce(BodyFont, "Inter");
        t.ButtonStyle = ButtonStyle == "outline" ? "outline" : "solid";
        t.ContainerWidth = TemplateFonts.Int(ContainerWidth, "1180", 600, 2000);
        t.ButtonRadius = TemplateFonts.Int(ButtonRadius, "0", 0, 60);
        t.HeaderBackground = TemplateFonts.OptionalColor(HeaderBackground);
        t.HeaderTextColor = TemplateFonts.OptionalColor(HeaderTextColor);
        t.HeaderPadding = TemplateFonts.Int(HeaderPadding, "16", 4, 60);
        t.CustomCss = TemplateFonts.Code(CustomCss);
        t.CustomJs = TemplateFonts.Code(CustomJs);
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Template gespeichert.";
        return RedirectToPage("Index");
    }
}
