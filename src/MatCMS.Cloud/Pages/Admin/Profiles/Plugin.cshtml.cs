using MatCMS.Shared;
using System.IO.Compression;
using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// Plugin editor as its own page. The stored bundle is unpacked into editable metadata + code and
/// repacked on save, so the instance keeps receiving the exact ZIP its own importer expects.
/// </summary>
public class PluginModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public PluginModel(AppDbContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    public Profile Owner { get; private set; } = new();
    public ProfilePlugin Item { get; private set; } = new();
    public string Code { get; private set; } = "";
    public List<string> Assets { get; private set; } = new();

    /// <summary>True while no plugin is loaded — the page then shows the upload form instead of the
    /// editor, because a plugin is created by uploading a bundle, not by typing one.</summary>
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int profileId, int? id)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return RedirectToPage("Index");
        Owner = owner;

        if (id is null) return Page();

        var item = await _db.ProfilePlugins.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.ProfileId == profileId);
        if (item is null) return RedirectToPage("Edit", new { id = profileId, tab = "plugins" });

        Item = item;
        var unpacked = ReadBundle(item.Bundle);
        Code = unpacked.Code;
        Assets = unpacked.Assets;
        return Page();
    }

    /// <summary>Takes a bundle in. A same-key upload replaces the stored one, which is how a plugin
    /// is updated from a freshly exported package.</summary>
    public async Task<IActionResult> OnPostUploadAsync(int profileId, IFormFile? bundle)
    {
        if (bundle is null || bundle.Length == 0)
        {
            TempData["FlashError"] = "Bitte eine Plugin-ZIP auswählen.";
            return RedirectToPage(new { profileId });
        }
        if (bundle.Length > 32 * 1024 * 1024)
        {
            TempData["FlashError"] = "Die Datei ist zu groß (max. 32 MB).";
            return RedirectToPage(new { profileId });
        }

        using var ms = new MemoryStream();
        await bundle.CopyToAsync(ms);
        var bytes = ms.ToArray();

        var meta = ReadBundleMeta(bytes);
        if (meta is null)
        {
            TempData["FlashError"] = "Das ist kein gültiges MatCMS-Plugin-Paket (plugin.json fehlt oder ist fehlerhaft).";
            return RedirectToPage(new { profileId });
        }

        var existing = await _db.ProfilePlugins.FirstOrDefaultAsync(p => p.ProfileId == profileId && p.Key == meta.Key);
        if (existing is null)
        {
            existing = new ProfilePlugin { ProfileId = profileId, Key = meta.Key };
            _db.ProfilePlugins.Add(existing);
        }
        existing.Name = meta.Name;
        existing.Version = meta.Version;
        existing.Description = meta.Description;
        existing.Bundle = bytes;
        existing.UploadedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"Plugin \"{meta.Name}\" (Version {meta.Version}) übernommen.";
        return RedirectToPage(new { profileId, id = existing.Id });
    }

    private sealed record BundleMeta(string Key, string Name, string Version, string Description);

    /// <summary>Reads plugin.json out of a bundle to label it. The instance does the real validation
    /// on import — this is only so the list shows a name and version.</summary>
    private static BundleMeta? ReadBundleMeta(byte[] zipBytes)
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

            string Get(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var name = Get("Name");
            if (string.IsNullOrWhiteSpace(name)) name = Get("name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var key = Get("Key");
            if (string.IsNullOrWhiteSpace(key)) key = Get("key");
            if (string.IsNullOrWhiteSpace(key)) key = name.ToLowerInvariant().Replace(' ', '-');

            var version = Get("Version");
            if (string.IsNullOrWhiteSpace(version)) version = Get("version");

            var description = Get("Description");
            if (string.IsNullOrWhiteSpace(description)) description = Get("description");

            return new BundleMeta(key.Trim(), name.Trim(), version.Trim(), description.Trim());
        }
        catch { return null; }
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, int id, string? name, string? description, string? version, string? code)
    {
        var row = await _db.ProfilePlugins.FirstOrDefaultAsync(p => p.Id == id && p.ProfileId == profileId);
        if (row is null) return RedirectToPage("Edit", new { id = profileId, tab = "plugins" });

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["FlashError"] = "Der Name ist erforderlich.";
            return RedirectToPage(new { profileId, id });
        }

        var repacked = Repack(row.Bundle, row.Key, name.Trim(), description?.Trim() ?? "", version?.Trim() ?? "", code ?? "");
        if (repacked is null)
        {
            TempData["FlashError"] = "Das Plugin-Paket konnte nicht neu gepackt werden.";
            return RedirectToPage(new { profileId, id });
        }

        row.Bundle = repacked;
        row.Name = name.Trim();
        row.Description = description?.Trim() ?? "";
        row.Version = version?.Trim() ?? "";
        row.UploadedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"Plugin \"{row.Name}\" gespeichert.";
        return RedirectToPage(new { profileId, id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int profileId, int id)
    {
        var row = await _db.ProfilePlugins.FirstOrDefaultAsync(p => p.Id == id && p.ProfileId == profileId);
        if (row is not null)
        {
            _db.ProfilePlugins.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Plugin aus dem Profil entfernt. Auf den Instanzen bleibt es bestehen.";
        }
        return RedirectToPage("Edit", new { id = profileId, tab = "plugins" });
    }

    /// <summary>Hands the stored bundle back out unchanged — useful to move a plugin between profiles
    /// or back onto an instance by hand.</summary>
    public async Task<IActionResult> OnGetDownloadAsync(int profileId, int id)
    {
        var row = await _db.ProfilePlugins.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.ProfileId == profileId);
        if (row is null || row.Bundle.Length == 0)
            return RedirectToPage("Edit", new { id = profileId, tab = "plugins" });
        return File(row.Bundle, "application/zip", $"{row.Key}.zip");
    }

    private sealed record Unpacked(string Code, List<string> Assets);

    /// <summary>Reads the editable parts out of a bundle. Never throws — a bundle we cannot read
    /// simply shows up with empty code, which is visible in the editor.</summary>
    private static Unpacked ReadBundle(byte[] zipBytes)
    {
        var assets = new List<string>();
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
                if (entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) && entry.Length > 0)
                    assets.Add(entry.FullName["assets/".Length..]);

            var meta = zip.GetEntry(PluginBundle.ManifestEntry);
            if (meta is null) return new("", assets);

            using var reader = new StreamReader(meta.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var prop in new[] { "Code", "code" })
                if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                    return new(v.GetString() ?? "", assets);

            return new("", assets);
        }
        catch { return new("", assets); }
    }

    /// <summary>Rebuilds the bundle with updated metadata, preserving every non-metadata entry.</summary>
    private static byte[]? Repack(byte[] original, string key, string name, string description, string version, string code)
    {
        try
        {
            using var source = new MemoryStream(original);
            using var zip = new ZipArchive(source, ZipArchiveMode.Read);

            // Start from the existing plugin.json so fields this editor does not surface (Format and
            // anything a future MatCMS adds) survive the round trip.
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
