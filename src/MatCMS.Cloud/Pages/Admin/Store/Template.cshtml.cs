using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// Store template editor — the third and last catalogue type. Same shape as the profile-local one:
/// tabs for Designer / Layout &amp; Code / Parameter with a live preview above them, plus JSON
/// import and export so a theme can travel between an instance, the store and a profile.
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

    public StoreTemplate Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;
    public List<string> UsedBy { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();

        var item = await _db.StoreTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
        if (item is null) return RedirectToPage("Index");

        Item = item;
        UsedBy = await _db.ProfileStoreTemplates.AsNoTracking()
            .Where(x => x.StoreTemplateId == item.Id).Select(x => x.Profile!.Name).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, string? name, string? description,
        string? accentColor, string? secondaryColor, string? headingFont, string? bodyFont, string? buttonStyle,
        string? headingColor, string? textColor, string? backgroundColor, string? altBackground,
        string? containerWidth, string? buttonRadius, string? headerBackground, string? headerTextColor,
        string? headerPadding, string? customCss, string? customJs, string? layoutHtml,
        string? menuMapJson, string? parametersJson, string? partsJson)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Der Name ist erforderlich.";
            return RedirectToPage(new { id });
        }

        // Validate the JSON fields here — a broken one would otherwise fail on every instance during
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
                return RedirectToPage(new { id });
            }
        }

        var trimmed = name.Trim();
        var row = id is null
            ? await _db.StoreTemplates.FirstOrDefaultAsync(t => t.Name == trimmed)
            : await _db.StoreTemplates.FirstOrDefaultAsync(t => t.Id == id);

        if (row is null)
        {
            row = new StoreTemplate();
            _db.StoreTemplates.Add(row);
        }
        // Renaming onto an identity another row already holds violates the unique index, which
        // surfaces as an unhandled DbUpdateException — a 500 instead of a readable message.
        else if (row.Name != trimmed
                 && await _db.StoreTemplates.AnyAsync(t => t.Name == trimmed && t.Id != row.Id))
        {
            TempData["FlashError"] = $"Der Name \"{trimmed}\" wird bereits verwendet.";
            return RedirectToPage(new { id });
        }

        row.Name = trimmed;
        row.Description = description?.Trim() ?? "";
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
        await TouchUsersAsync(row.Id);
        TempData["Flash"] = $"Template \"{row.Name}\" im Store gespeichert.";
        return RedirectToPage(new { id = row.Id });
    }

    /// <summary>Takes the JSON that MatCMS's template editor exports, so a theme designed on a real
    /// site lands in the catalogue without being retyped.</summary>
    public async Task<IActionResult> OnPostImportAsync(string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson))
        {
            TempData["FlashError"] = "Bitte das Template-JSON einfügen.";
            return RedirectToPage();
        }

        StoreTemplate parsed;
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            var root = doc.RootElement;
            string S(string prop, string fallback = "")
            {
                foreach (var candidate in new[] { prop, char.ToLowerInvariant(prop[0]) + prop[1..] })
                    if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? fallback;
                return fallback;
            }
            int I(string prop, int fallback)
            {
                foreach (var candidate in new[] { prop, char.ToLowerInvariant(prop[0]) + prop[1..] })
                    if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.Number)
                        return v.GetInt32();
                return fallback;
            }

            var name = S("Name").Trim();
            if (name.Length == 0)
            {
                TempData["FlashError"] = "Im JSON fehlt der Name.";
                return RedirectToPage();
            }

            parsed = new StoreTemplate
            {
                Name = name,
                AccentColor = S("AccentColor", "#de7e11"),
                SecondaryColor = S("SecondaryColor"),
                HeadingFont = S("HeadingFont", "Geologica"),
                BodyFont = S("BodyFont", "Inter"),
                ButtonStyle = S("ButtonStyle", "solid"),
                HeadingColor = S("HeadingColor", "#010101"),
                TextColor = S("TextColor", "#1a1a1a"),
                BackgroundColor = S("BackgroundColor", "#ffffff"),
                AltBackground = S("AltBackground", "#f6f7f9"),
                ContainerWidth = S("ContainerWidth", "1180"),
                ButtonRadius = S("ButtonRadius", "0"),
                HeaderBackground = S("HeaderBackground"),
                HeaderTextColor = S("HeaderTextColor"),
                HeaderPadding = S("HeaderPadding", "16"),
                CustomCss = S("CustomCss"),
                CustomJs = S("CustomJs"),
                LayoutHtml = S("LayoutHtml"),
                MenuMapJson = S("MenuMapJson", "{}"),
                ParametersJson = S("ParametersJson", "[]"),
                ParamValuesJson = S("ParamValuesJson", "{}"),
                SchemaVersion = I("SchemaVersion", 1),
                PartsJson = S("PartsJson", "{}")
            };
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Das JSON konnte nicht gelesen werden: {ex.Message}";
            return RedirectToPage();
        }

        var existing = await _db.StoreTemplates.FirstOrDefaultAsync(t => t.Name == parsed.Name);
        if (existing is null)
        {
            _db.StoreTemplates.Add(parsed);
            await _db.SaveChangesAsync();
            TempData["Flash"] = $"Template \"{parsed.Name}\" importiert.";
            return RedirectToPage(new { id = parsed.Id });
        }

        parsed.Id = existing.Id;
        _db.Entry(existing).CurrentValues.SetValues(parsed);
        await _db.SaveChangesAsync();
        await TouchUsersAsync(existing.Id);
        TempData["Flash"] = $"Template \"{parsed.Name}\" aktualisiert.";
        return RedirectToPage(new { id = existing.Id });
    }

    public async Task<IActionResult> OnGetExportAsync(int id)
    {
        var t = await _db.StoreTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return RedirectToPage("Index");

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

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.StoreTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (row is not null)
        {
            var affected = await _db.ProfileStoreTemplates.Where(x => x.StoreTemplateId == id)
                .Select(x => x.ProfileId).Distinct().ToListAsync();
            _db.StoreTemplates.Remove(row);
            await _db.SaveChangesAsync();
            foreach (var profileId in affected) await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Template aus dem Store entfernt. Auf den Instanzen bleibt es bestehen.";
        }
        return RedirectToPage("Index");
    }

    private async Task TouchUsersAsync(int storeTemplateId)
    {
        var profileIds = await _db.ProfileStoreTemplates.AsNoTracking()
            .Where(x => x.StoreTemplateId == storeTemplateId).Select(x => x.ProfileId).Distinct().ToListAsync();
        foreach (var profileId in profileIds) await _profiles.TouchAsync(profileId);
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
