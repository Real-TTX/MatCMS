using MatCMS.Shared;
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

    /// <summary>The template parts as the file editor posts them (Parts[post], Parts[maintenance]).
    /// A bound PROPERTY, not a handler parameter: dictionary binding needs the model prefix, which is
    /// what MatCMS does too.</summary>
    [BindProperty] public Dictionary<string, string> Parts { get; set; } = new();

    /// <summary>What is stored on the row, for rendering the editor. Never throws: a template
    /// imported with a broken parts blob must still open.</summary>
    public Dictionary<string, string> StoredParts
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Item.PartsJson)) return new();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(Item.PartsJson) ?? new();
            }
            catch { return new(); }
        }
    }

    public string Part(string key) => StoredParts.TryGetValue(key, out var v) ? v : "";
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
        string? menuMapJson, string? parametersJson)
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
            // Parts are no longer a raw JSON field — the file editor posts them one by one.
            ("Menü-Zuordnung", menuMapJson), ("Parameter", parametersJson)
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
        // The editor posts the template parts as parts[post] / parts[maintenance]; empty ones are
        // dropped so "no override" stays absent rather than being stored as an empty string.
        var kept = Parts.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        row.PartsJson = System.Text.Json.JsonSerializer.Serialize(kept);

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

            var name = JsonImport.Text(root, "Name").Trim();
            if (name.Length == 0)
            {
                TempData["FlashError"] = "Im JSON fehlt der Name.";
                return RedirectToPage();
            }

            parsed = new StoreTemplate
            {
                Name = name,
                AccentColor = JsonImport.Text(root, "AccentColor", "#de7e11"),
                SecondaryColor = JsonImport.Text(root, "SecondaryColor"),
                HeadingFont = JsonImport.Text(root, "HeadingFont", "Geologica"),
                BodyFont = JsonImport.Text(root, "BodyFont", "Inter"),
                ButtonStyle = JsonImport.Text(root, "ButtonStyle", "solid"),
                HeadingColor = JsonImport.Text(root, "HeadingColor", "#010101"),
                TextColor = JsonImport.Text(root, "TextColor", "#1a1a1a"),
                BackgroundColor = JsonImport.Text(root, "BackgroundColor", "#ffffff"),
                AltBackground = JsonImport.Text(root, "AltBackground", "#f6f7f9"),
                ContainerWidth = JsonImport.Text(root, "ContainerWidth", "1180"),
                ButtonRadius = JsonImport.Text(root, "ButtonRadius", "0"),
                HeaderBackground = JsonImport.Text(root, "HeaderBackground"),
                HeaderTextColor = JsonImport.Text(root, "HeaderTextColor"),
                HeaderPadding = JsonImport.Text(root, "HeaderPadding", "16"),
                CustomCss = JsonImport.Text(root, "CustomCss"),
                CustomJs = JsonImport.Text(root, "CustomJs"),
                LayoutHtml = JsonImport.Text(root, "LayoutHtml"),
                MenuMapJson = JsonImport.Raw(root, "MenuMapJson", "{}"),
                ParametersJson = JsonImport.Raw(root, "ParametersJson", "[]"),
                ParamValuesJson = JsonImport.Raw(root, "ParamValuesJson", "{}"),
                SchemaVersion = JsonImport.Int(root, "SchemaVersion", 1),
                PartsJson = JsonImport.Raw(root, "PartsJson", "{}")
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
