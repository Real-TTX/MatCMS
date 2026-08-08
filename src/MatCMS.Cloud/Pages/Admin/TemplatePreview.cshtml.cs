using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin;

/// <summary>
/// A bare page that renders one STORED template as a sample site, so a template list can show a real
/// thumbnail instead of a colour swatch — the same idea as MatCMS's <c>?previewTemplate=id</c>, which
/// renders the actual site with a chosen theme. The cloud has no site, so it renders the same sample
/// page the template editor previews.
/// <para>No admin layout: this is meant to be framed, and the frame should contain the preview and
/// nothing else. It reuses <c>template-preview.js</c> rather than a second renderer, so the tile and
/// the editor can never disagree about what a template looks like.</para>
/// </summary>
public class TemplatePreviewModel : PageModel
{
    private readonly AppDbContext _db;
    public TemplatePreviewModel(AppDbContext db) => _db = db;

    /// <summary>The values the preview script reads, whichever table the template came from.</summary>
    public StoreTemplate Item { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string kind, int id)
    {
        // Profile-local and store templates carry the same fields; copying into one shape keeps the
        // view free of "which kind is this?" branching.
        if (string.Equals(kind, "profile", StringComparison.OrdinalIgnoreCase))
        {
            var t = await _db.ProfileTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return NotFound();
            Item = new StoreTemplate
            {
                Name = t.Name,
                AccentColor = t.AccentColor, SecondaryColor = t.SecondaryColor,
                HeadingFont = t.HeadingFont, BodyFont = t.BodyFont, ButtonStyle = t.ButtonStyle,
                HeadingColor = t.HeadingColor, TextColor = t.TextColor,
                BackgroundColor = t.BackgroundColor, AltBackground = t.AltBackground,
                ContainerWidth = t.ContainerWidth, ButtonRadius = t.ButtonRadius,
                HeaderBackground = t.HeaderBackground, HeaderTextColor = t.HeaderTextColor,
                HeaderPadding = t.HeaderPadding, CustomCss = t.CustomCss, LayoutHtml = t.LayoutHtml
            };
            return Page();
        }

        var s = await _db.StoreTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        Item = s;
        return Page();
    }
}
