using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// Component editor as its own page — mirroring MatCMS, where a component is edited on
/// Components/Edit/{id} with the field designer and live preview, not inside a tab.
/// </summary>
public class ComponentModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public ComponentModel(AppDbContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public Profile Owner { get; private set; } = new();
    public ProfileComponent Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    /// <summary>Theme the preview renders against — the template this profile activates, so a block
    /// is judged in the design it will actually live in.</summary>
    public ProfileTemplate? PreviewTheme { get; private set; }

    public async Task<IActionResult> OnGetAsync(int profileId, int? id)
    {
        if (!await LoadAsync(profileId, id)) return RedirectToPage("Index");
        return Page();
    }

    private async Task<bool> LoadAsync(int profileId, int? id)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return false;
        Owner = owner;

        if (!string.IsNullOrWhiteSpace(owner.ActivateTemplateName))
            PreviewTheme = await _db.ProfileTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.ProfileId == profileId && t.Name == owner.ActivateTemplateName);

        if (id is null) return true;

        var item = await _db.ProfileComponents.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.ProfileId == profileId);
        if (item is null) return false;
        Item = item;
        return true;
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, int? id, string? type, string? name, string? description,
        string? icon, string? fieldsJson, string? templateHtml)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Typ und Name sind erforderlich.";
            return RedirectToPage(new { profileId, id });
        }

        // Reject broken field JSON here rather than letting every instance fail on apply.
        var fields = string.IsNullOrWhiteSpace(fieldsJson) ? "[]" : fieldsJson.Trim();
        try { using var _ = JsonDocument.Parse(fields); }
        catch
        {
            TempData["FlashError"] = "Die Feld-Definition ist kein gültiges JSON.";
            return RedirectToPage(new { profileId, id });
        }

        var slug = type.Trim().ToLowerInvariant();
        var row = id is null
            ? await _db.ProfileComponents.FirstOrDefaultAsync(c => c.ProfileId == profileId && c.Type == slug)
            : await _db.ProfileComponents.FirstOrDefaultAsync(c => c.Id == id && c.ProfileId == profileId);

        if (row is null)
        {
            row = new ProfileComponent { ProfileId = profileId };
            _db.ProfileComponents.Add(row);
        }
        // Renaming onto an identity another row already holds violates the unique index, which
        // surfaces as an unhandled DbUpdateException — a 500 instead of "that type is taken".
        else if (row.Type != slug
                 && await _db.ProfileComponents.AnyAsync(c => c.ProfileId == profileId && c.Type == slug && c.Id != row.Id))
        {
            TempData["FlashError"] = $"Der Typ \"{slug}\" wird in diesem Profil bereits verwendet.";
            return RedirectToPage(new { profileId, id });
        }

        row.Type = slug;
        row.Name = name.Trim();
        row.Description = description?.Trim() ?? "";
        row.Icon = icon?.Trim() ?? "";
        row.FieldsJson = fields;
        row.TemplateHtml = templateHtml ?? "";

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"Komponente \"{row.Name}\" gespeichert.";
        return RedirectToPage(new { profileId, id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int profileId, int id)
    {
        var row = await _db.ProfileComponents.FirstOrDefaultAsync(c => c.Id == id && c.ProfileId == profileId);
        if (row is not null)
        {
            _db.ProfileComponents.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Komponente aus dem Profil entfernt. Auf den Instanzen bleibt sie bestehen.";
        }
        return RedirectToPage("Edit", new { id = profileId, tab = "components" });
    }

    /// <summary>
    /// Exports the component as plain JSON — the same shape every importer on both sides reads, so a
    /// block designed here can be pasted onto a site or into another profile without the sync.
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(int profileId, int id)
    {
        var c = await _db.ProfileComponents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ProfileId == profileId);
        if (c is null) return RedirectToPage("Edit", new { id = profileId, tab = "components" });

        return ExportFile(c.Type, new { c.Type, c.Name, c.Description, c.Icon, c.FieldsJson, c.TemplateHtml });
    }

    /// <summary>One JSON download, named after the identity the importers match on.</summary>
    private IActionResult ExportFile(string type, object payload)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var slug = new string(type.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", $"component-{slug}.json");
    }

}
