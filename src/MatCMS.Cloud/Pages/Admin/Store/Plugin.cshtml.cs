using MatCMS.Shared;
using System.IO.Compression;
using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// Store plugin editor. Mirrors the profile-local one (Profiles/Plugin) — a plugin is created by
/// uploading a bundle and edited in place by repacking it, so the instance keeps receiving exactly
/// the ZIP its own importer expects.
/// <para>Bumping the store entry reaches every profile that selected it, which is the whole point of
/// the store; the "used by N profiles" count on the list page is there so that is never a surprise.</para>
/// </summary>
public class PluginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly Services.ProfileService _profiles;

    public PluginModel(AppDbContext db, Services.ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public StorePlugin Item { get; private set; } = new();
    public string Code { get; private set; } = "";
    public List<string> Assets { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    /// <summary>Profiles that roll this entry out — shown before a change is saved.</summary>
    public List<string> UsedBy { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();

        var item = await _db.StorePlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (item is null) return RedirectToPage("Index");

        Item = item;
        Code = StoreBundle.ReadCode(item.Bundle);
        Assets = StoreBundle.ReadAssets(item.Bundle);
        UsedBy = await _db.ProfileStorePlugins.AsNoTracking()
            .Where(x => x.StorePluginId == item.Id).Select(x => x.Profile!.Name).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(int? id, IFormFile? bundle)
    {
        if (bundle is null || bundle.Length == 0)
        {
            TempData["FlashError"] = "Bitte eine Plugin-ZIP auswählen.";
            return RedirectToPage(new { id });
        }
        if (bundle.Length > 32 * 1024 * 1024)
        {
            TempData["FlashError"] = "Die Datei ist zu groß (max. 32 MB).";
            return RedirectToPage(new { id });
        }

        using var ms = new MemoryStream();
        await bundle.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var meta = StoreBundle.ReadMeta(bytes);
        if (meta is null)
        {
            TempData["FlashError"] = "Das ist kein gültiges MatCMS-Plugin-Paket (plugin.json fehlt oder ist fehlerhaft).";
            return RedirectToPage(new { id });
        }

        var row = await _db.StorePlugins.FirstOrDefaultAsync(p => p.Key == meta.Key);
        if (row is null)
        {
            row = new StorePlugin { Key = meta.Key };
            _db.StorePlugins.Add(row);
        }
        row.Name = meta.Name;
        row.Version = meta.Version;
        row.Description = meta.Description;
        row.Bundle = bytes;
        row.UploadedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await TouchUsersAsync(row.Id);
        TempData["Flash"] = $"Plugin \"{meta.Name}\" (Version {meta.Version}) im Store gespeichert.";
        return RedirectToPage(new { id = row.Id });
    }

    public async Task<IActionResult> OnPostAsync(int id, string? name, string? description, string? version, string? code)
    {
        var row = await _db.StorePlugins.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return RedirectToPage("Index");

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Der Name ist erforderlich.";
            return RedirectToPage(new { id });
        }

        var repacked = StoreBundle.Repack(row.Bundle, row.Key, name.Trim(), description?.Trim() ?? "", version?.Trim() ?? "", code ?? "");
        if (repacked is null)
        {
            TempData["FlashError"] = "Das Plugin-Paket konnte nicht neu gepackt werden.";
            return RedirectToPage(new { id });
        }

        row.Bundle = repacked;
        row.Name = name.Trim();
        row.Description = description?.Trim() ?? "";
        row.Version = version?.Trim() ?? "";
        row.UploadedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await TouchUsersAsync(id);
        TempData["Flash"] = $"Plugin \"{row.Name}\" gespeichert.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.StorePlugins.FirstOrDefaultAsync(p => p.Id == id);
        if (row is not null)
        {
            var affected = await _db.ProfileStorePlugins.Where(x => x.StorePluginId == id).Select(x => x.ProfileId).ToListAsync();
            _db.StorePlugins.Remove(row);
            await _db.SaveChangesAsync();
            // The selections cascade away with it, so every profile that used it changes — bump them.
            foreach (var profileId in affected.Distinct()) await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Plugin aus dem Store entfernt. Auf den Instanzen bleibt es bestehen.";
        }
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnGetDownloadAsync(int id)
    {
        var row = await _db.StorePlugins.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (row is null || row.Bundle.Length == 0) return RedirectToPage("Index");
        return File(row.Bundle, "application/zip", $"{row.Key}.zip");
    }

    /// <summary>Bumps every profile that selected this entry, so their instances pull the change.
    /// Without this a store edit would sit there and never reach anybody.</summary>
    private async Task TouchUsersAsync(int storePluginId)
    {
        var profileIds = await _db.ProfileStorePlugins.AsNoTracking()
            .Where(x => x.StorePluginId == storePluginId).Select(x => x.ProfileId).Distinct().ToListAsync();
        foreach (var profileId in profileIds) await _profiles.TouchAsync(profileId);
    }
}

/// <summary>Reading and rewriting MatCMS plugin bundles. Shared by the store and profile editors so
/// the ZIP handling exists once.</summary>
public static class StoreBundle
{
    public sealed record Meta(string Key, string Name, string Version, string Description);

    public static Meta? ReadMeta(byte[] zipBytes)
    {
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry(PluginBundle.ManifestEntry);
            if (entry is null || entry.Length > 1024 * 1024) return null;

            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;

            string Get(string prop)
            {
                foreach (var candidate in new[] { prop, char.ToLowerInvariant(prop[0]) + prop[1..] })
                    if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? "";
                return "";
            }

            var name = Get("Name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var key = Get("Key");
            if (string.IsNullOrWhiteSpace(key)) key = name.ToLowerInvariant().Replace(' ', '-');

            return new Meta(key.Trim(), name.Trim(), Get("Version").Trim(), Get("Description").Trim());
        }
        catch { return null; }
    }

    public static string ReadCode(byte[] zipBytes)
    {
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var meta = zip.GetEntry(PluginBundle.ManifestEntry);
            if (meta is null) return "";
            using var reader = new StreamReader(meta.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var prop in new[] { "Code", "code" })
                if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? "";
            return "";
        }
        catch { return ""; }
    }

    public static List<string> ReadAssets(byte[] zipBytes)
    {
        var assets = new List<string>();
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
                if (entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) && entry.Length > 0)
                    assets.Add(entry.FullName["assets/".Length..]);
        }
        catch { /* unreadable bundle → no assets listed */ }
        return assets;
    }

    /// <summary>Rebuilds a bundle with updated metadata, preserving every non-metadata entry.</summary>
    public static byte[]? Repack(byte[] original, string key, string name, string description, string version, string code)
    {
        try
        {
            using var source = new MemoryStream(original);
            using var zip = new ZipArchive(source, ZipArchiveMode.Read);

            // Start from the existing plugin.json so fields this editor does not surface survive.
            var meta = new Dictionary<string, object?>(StringComparer.Ordinal);
            var metaEntry = zip.GetEntry(PluginBundle.ManifestEntry);
            if (metaEntry is not null)
            {
                using var reader = new StreamReader(metaEntry.Open());
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                foreach (var prop in doc.RootElement.EnumerateObject())
                    meta[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetInt32(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.ToString()
                    };
            }

            meta["Format"] = meta.TryGetValue("Format", out var f) && f is int i ? i : 1;
            meta["Name"] = name;
            meta["Key"] = key;
            meta["Version"] = version;
            meta["Description"] = description;
            meta["Code"] = code;

            using var target = new MemoryStream();
            using (var outZip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
            {
                var json = outZip.CreateEntry(PluginBundle.ManifestEntry);
                using (var writer = new StreamWriter(json.Open()))
                    writer.Write(JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.Equals(PluginBundle.ManifestEntry, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;

                    var copy = outZip.CreateEntry(entry.FullName);
                    using var from = entry.Open();
                    using var to = copy.Open();
                    from.CopyTo(to);
                }
            }

            return target.ToArray();
        }
        catch { return null; }
    }
}
