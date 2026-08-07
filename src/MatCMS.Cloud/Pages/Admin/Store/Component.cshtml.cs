using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// Store component editor — same designer as the profile-local one (field rows, sample data, live
/// preview, placeholder debug), but the entry belongs to the catalogue rather than to one profile.
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

    public StoreComponent Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;
    public List<string> UsedBy { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();

        var item = await _db.StoreComponents.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (item is null) return RedirectToPage("Index");

        Item = item;
        UsedBy = await _db.ProfileStoreComponents.AsNoTracking()
            .Where(x => x.StoreComponentId == item.Id).Select(x => x.Profile!.Name).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        int? id, string? type, string? name, string? description, string? icon,
        string? fieldsJson, string? templateHtml)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Typ und Name sind erforderlich.";
            return RedirectToPage(new { id });
        }

        // Reject broken field JSON here rather than letting every instance fail on apply.
        var fields = string.IsNullOrWhiteSpace(fieldsJson) ? "[]" : fieldsJson.Trim();
        try { using var _ = JsonDocument.Parse(fields); }
        catch
        {
            TempData["FlashError"] = "Die Feld-Definition ist kein gültiges JSON.";
            return RedirectToPage(new { id });
        }

        var slug = type.Trim().ToLowerInvariant();
        var row = id is null
            ? await _db.StoreComponents.FirstOrDefaultAsync(c => c.Type == slug)
            : await _db.StoreComponents.FirstOrDefaultAsync(c => c.Id == id);

        if (row is null)
        {
            row = new StoreComponent();
            _db.StoreComponents.Add(row);
        }

        row.Type = slug;
        row.Name = name.Trim();
        row.Description = description?.Trim() ?? "";
        row.Icon = icon?.Trim() ?? "";
        row.FieldsJson = fields;
        row.TemplateHtml = templateHtml ?? "";

        await _db.SaveChangesAsync();
        await TouchUsersAsync(row.Id);
        TempData["Flash"] = $"Komponente \"{row.Name}\" im Store gespeichert.";
        return RedirectToPage(new { id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.StoreComponents.FirstOrDefaultAsync(c => c.Id == id);
        if (row is not null)
        {
            var affected = await _db.ProfileStoreComponents.Where(x => x.StoreComponentId == id)
                .Select(x => x.ProfileId).Distinct().ToListAsync();
            _db.StoreComponents.Remove(row);
            await _db.SaveChangesAsync();
            foreach (var profileId in affected) await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Komponente aus dem Store entfernt. Auf den Instanzen bleibt sie bestehen.";
        }
        return RedirectToPage("Index");
    }

    /// <summary>Bumps every profile that selected this entry, so their instances pull the change.</summary>
    private async Task TouchUsersAsync(int storeComponentId)
    {
        var profileIds = await _db.ProfileStoreComponents.AsNoTracking()
            .Where(x => x.StoreComponentId == storeComponentId).Select(x => x.ProfileId).Distinct().ToListAsync();
        foreach (var profileId in profileIds) await _profiles.TouchAsync(profileId);
    }
}
