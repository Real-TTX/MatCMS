using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// Template editor as its OWN page, mirroring MatCMS (list under Templates, editor at
/// Templates/Edit/{id}) — a theme has colours, fonts, layout HTML, CSS, JS and three JSON blocks,
/// which is far too much to hang off a tab in the profile page.
/// </summary>
public class TemplateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public TemplateModel(AppDbContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public Profile Owner { get; private set; } = new();

    /// <summary>The template being edited; a fresh one (not yet saved) when creating.</summary>
    public ProfileTemplate Item { get; private set; } = new();

    public bool IsNew => Item.Id == 0;

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

        if (id is null) return true;

        var item = await _db.ProfileTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.ProfileId == profileId);
        if (item is null) return false;
        Item = item;
        return true;
    }

    public async Task<IActionResult> OnPostAsync(int profileId, int? id, string? name,
        string? accentColor, string? secondaryColor, string? headingFont, string? bodyFont, string? buttonStyle,
        string? headingColor, string? textColor, string? backgroundColor, string? altBackground,
        string? containerWidth, string? buttonRadius, string? headerBackground, string? headerTextColor,
        string? headerPadding, string? customCss, string? customJs, string? layoutHtml,
        string? menuMapJson, string? parametersJson, string? partsJson)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Der Name ist erforderlich.";
            return RedirectToPage(new { profileId, id });
        }

        // Validate every JSON field here — a broken one would otherwise fail on each instance during
        // the sync, where it is far harder to trace back to this form.
        foreach (var (label, json) in new[]
        {
            ("Menü-Zuordnung", menuMapJson), ("Parameter", parametersJson), ("Layout-Teile", partsJson)
        })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try { using var _ = JsonDocument.Parse(json); }
            catch
            {
                TempData["FlashError"] = $"{label}: kein gültiges JSON.";
                return RedirectToPage(new { profileId, id });
            }
        }

        var trimmed = name.Trim();
        var row = id is null
            ? await _db.ProfileTemplates.FirstOrDefaultAsync(t => t.ProfileId == profileId && t.Name == trimmed)
            : await _db.ProfileTemplates.FirstOrDefaultAsync(t => t.Id == id && t.ProfileId == profileId);

        if (row is null)
        {
            row = new ProfileTemplate { ProfileId = profileId };
            _db.ProfileTemplates.Add(row);
        }

        row.Name = trimmed;
        row.AccentColor = Or(accentColor, "#de7e11");
        row.SecondaryColor = secondaryColor?.Trim() ?? "";
        row.HeadingFont = Or(headingFont, "Geologica");
        row.BodyFont = Or(bodyFont, "Inter");
        row.ButtonStyle = Or(buttonStyle, "solid");
        row.HeadingColor = Or(headingColor, "#010101");
        row.TextColor = Or(textColor, "#1a1a1a");
        row.BackgroundColor = Or(backgroundColor, "#ffffff");
        row.AltBackground = Or(altBackground, "#f6f7f9");
        row.ContainerWidth = Or(containerWidth, "1180");
        row.ButtonRadius = Or(buttonRadius, "0");
        row.HeaderBackground = headerBackground?.Trim() ?? "";
        row.HeaderTextColor = headerTextColor?.Trim() ?? "";
        row.HeaderPadding = Or(headerPadding, "16");
        row.CustomCss = customCss ?? "";
        row.CustomJs = customJs ?? "";
        row.LayoutHtml = layoutHtml ?? "";
        row.MenuMapJson = Or(menuMapJson, "{}");
        row.ParametersJson = Or(parametersJson, "[]");
        row.PartsJson = Or(partsJson, "{}");

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"Template \"{row.Name}\" gespeichert.";
        return RedirectToPage(new { profileId, id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int profileId, int id)
    {
        var row = await _db.ProfileTemplates.FirstOrDefaultAsync(t => t.Id == id && t.ProfileId == profileId);
        if (row is not null)
        {
            _db.ProfileTemplates.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Template aus dem Profil entfernt. Auf den Instanzen bleibt es bestehen.";
        }
        return RedirectToPage("Edit", new { id = profileId, tab = "templates" });
    }

    /// <summary>Exports this template in the same JSON shape MatCMS's own export produces, so a theme
    /// can travel back to an instance (or into another profile) without retyping it.</summary>
    public async Task<IActionResult> OnGetExportAsync(int profileId, int id)
    {
        var t = await _db.ProfileTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ProfileId == profileId);
        if (t is null) return RedirectToPage("Edit", new { id = profileId, tab = "templates" });

        var payload = new
        {
            t.Name,
            t.AccentColor, t.SecondaryColor, t.HeadingFont, t.BodyFont, t.ButtonStyle,
            t.HeadingColor, t.TextColor, t.BackgroundColor, t.AltBackground,
            t.ContainerWidth, t.ButtonRadius, t.HeaderBackground, t.HeaderTextColor, t.HeaderPadding,
            t.CustomCss, t.CustomJs, t.LayoutHtml,
            t.MenuMapJson, t.ParametersJson, t.ParamValuesJson,
            t.SchemaVersion, t.PartsJson
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var slug = new string(t.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"template-{slug}.json");
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
