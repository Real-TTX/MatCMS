using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin;

/// <summary>
/// A bare page that renders one STORED component with sample data, so a component list can show what
/// the block actually looks like instead of an icon name. The counterpart to
/// <see cref="TemplatePreviewModel"/>, and built the same way: it feeds the stored values to
/// <c>component-editor.js</c> rather than to a second renderer, so the tile and the editor can never
/// disagree about what a component looks like.
/// </summary>
public class ComponentPreviewModel : PageModel
{
    private readonly AppDbContext _db;
    public ComponentPreviewModel(AppDbContext db) => _db = db;

    public string Name { get; private set; } = "";
    public string FieldsJson { get; private set; } = "[]";
    public string TemplateHtml { get; private set; } = "";

    /// <summary>The design the block is judged in — the template its profile activates. A store
    /// component has no profile and therefore no theme; it renders against the defaults.</summary>
    public ProfileTemplate? PreviewTheme { get; private set; }

    public async Task<IActionResult> OnGetAsync(string kind, int id)
    {
        if (string.Equals(kind, "profile", StringComparison.OrdinalIgnoreCase))
        {
            var c = await _db.ProfileComponents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return NotFound();
            Fill(c.Name, c.FieldsJson, c.TemplateHtml);

            var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == c.ProfileId);
            if (owner is not null && !string.IsNullOrWhiteSpace(owner.ActivateTemplateName))
                PreviewTheme = await _db.ProfileTemplates.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.ProfileId == c.ProfileId && t.Name == owner.ActivateTemplateName);
            return Page();
        }

        var s = await _db.StoreComponents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        Fill(s.Name, s.FieldsJson, s.TemplateHtml);
        return Page();
    }

    private void Fill(string name, string? fields, string? template)
    {
        Name = name;
        FieldsJson = string.IsNullOrWhiteSpace(fields) ? "[]" : fields;
        TemplateHtml = template ?? "";
    }
}
