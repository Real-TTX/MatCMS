using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using PagesIndex = MatCMS.Pages.Admin.Pages.IndexModel;

namespace MatCMS.Services;

/// <summary>
/// Exports a plugin as a self-contained ZIP bundle (plugin.json + <c>files/</c> + <c>assets/</c>) and
/// imports one back. This is the building block for a plugin store: a bundle fully describes a plugin.
/// <para>"Fully" is the point and was once a lie: a plugin may bring further script files besides its
/// entry file (<c>Plugin.FilesJson</c>, path→content, folders allowed), and a bundle carrying only the
/// entry file turned every export, cloud rollout and re-import into silent data loss — the plugin came
/// back looking complete and did nothing. They travel as one zip entry each under
/// <see cref="MatCMS.Shared.PluginBundle.FileFolder"/>; see there for why entries rather than a
/// manifest field, and for what old and new readers make of each other's bundles.</para>
/// </summary>
public static class PluginPackager
{
    /// <summary>Serialized plugin metadata stored as plugin.json inside a bundle.</summary>
    public sealed class Bundle
    {
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public string Key { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Code { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // The format itself — entry names, limits, allowed asset types — lives in MatCMS.Shared, because
    // the cloud reads and re-packs the same zips and the two must not drift apart.
    /// <summary>Asset file types a bundle may carry. SVG excluded (same-origin stored-XSS vector).</summary>
    public static readonly string[] AllowedAssetExt = MatCMS.Shared.PluginBundle.AllowedAssetExt;

    private const long MaxAssetBytes = MatCMS.Shared.PluginBundle.MaxAssetBytes;
    private const long MaxTotalBytes = MatCMS.Shared.PluginBundle.MaxTotalBytes;
    private const int MaxAssetFiles = MatCMS.Shared.PluginBundle.MaxAssetFiles;
    private const long MaxMetaBytes = MatCMS.Shared.PluginBundle.MaxManifestBytes;
    private const long MaxFileBytes = MatCMS.Shared.PluginBundle.MaxFileBytes;
    private const int MaxFiles = MatCMS.Shared.PluginBundle.MaxFiles;

    /// <summary>Builds a ZIP bundle (plugin.json + files/… + assets/…) for the given plugin.</summary>
    public static byte[] Export(Models.Plugin p, IWebHostEnvironment env)
    {
        var meta = new Bundle { Format = 1, Name = p.Name, Key = p.Key, Version = p.Version, Description = p.Description, Code = p.Code };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metaEntry = zip.CreateEntry(MatCMS.Shared.PluginBundle.ManifestEntry);
            using (var w = new StreamWriter(metaEntry.Open(), new UTF8Encoding(false)))
                w.Write(JsonSerializer.Serialize(meta, JsonOpts));

            // The further script files, one entry each. A path the shared rule refuses is skipped
            // rather than repaired: it cannot have come from the editor, and a rewritten path would
            // silently no longer be the one the entry file `#load`s.
            foreach (var (path, content) in ParseFiles(p.FilesJson))
            {
                var fileEntry = zip.CreateEntry(MatCMS.Shared.PluginBundle.FileFolder + path);
                using var w = new StreamWriter(fileEntry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            var dir = StoragePaths.PluginAssetDir(env, p.Key);
            if (!string.IsNullOrWhiteSpace(p.Key) && Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    var entry = zip.CreateEntry(MatCMS.Shared.PluginBundle.AssetFolder + Path.GetFileName(f));
                    using var es = entry.Open();
                    using var fs = File.OpenRead(f);
                    fs.CopyTo(es);
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Imports a bundle. If a plugin with the bundle's Key already exists it is UPDATED in place
    /// (code/description/version + assets replaced; Enabled state, Id and DataVersion preserved);
    /// otherwise a new plugin is created. In BOTH cases the plugin is left DISABLED — imported code is
    /// untrusted, so nothing runs until an admin reviews it and enables it. Assets are staged and then
    /// swapped in, so a corrupt/failed extraction never destroys the plugin's current assets. Returns
    /// the plugin, whether it was an update, and an error message on failure.
    /// </summary>
    public static async Task<(Models.Plugin? plugin, bool updated, string? error)> ImportAsync(
        Stream zipStream, IWebHostEnvironment env, AppDbContext db)
    {
        // Copy to a seekable buffer (upload streams are often non-seekable; ZipArchive read needs seek).
        using var ms = new MemoryStream();
        await zipStream.CopyToAsync(ms);
        ms.Position = 0;

        ZipArchive zip;
        try { zip = new ZipArchive(ms, ZipArchiveMode.Read); }
        catch { return (null, false, "Datei ist kein gültiges ZIP-Paket."); }

        using (zip)
        {
            ZipArchiveEntry? metaEntry;
            try { metaEntry = zip.GetEntry(MatCMS.Shared.PluginBundle.ManifestEntry); }
            catch { return (null, false, "Datei ist kein gültiges ZIP-Paket."); }
            if (metaEntry is null) return (null, false, "Ungültiges Paket: plugin.json fehlt.");
            if (metaEntry.Length > MaxMetaBytes) return (null, false, "Ungültiges Paket: plugin.json zu groß.");

            Bundle? meta;
            try
            {
                using var r = new StreamReader(metaEntry.Open());
                meta = JsonSerializer.Deserialize<Bundle>(await r.ReadToEndAsync(), JsonOpts);
            }
            catch { return (null, false, "Ungültiges Paket: plugin.json ist fehlerhaft."); }
            if (meta is null || string.IsNullOrWhiteSpace(meta.Name))
                return (null, false, "Ungültiges Paket: Name fehlt.");

            // The bundle Key is the package identity. Re-slugify (never trust input).
            var wantKey = PagesIndex.Slugify(!string.IsNullOrWhiteSpace(meta.Key) ? meta.Key : meta.Name);
            if (string.IsNullOrEmpty(wantKey)) wantKey = "plugin";

            var dir = StoragePaths.PluginAssetDir(env, wantKey);
            var staging = dir + ".importtmp";

            // 1) Extract the bundle's assets into a STAGING folder first, so a corrupt or failed
            //    extraction never destroys the plugin's current assets and never half-applies.
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                Directory.CreateDirectory(staging);
                var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                long total = 0;
                var count = 0;
                foreach (var e in zip.Entries)
                {
                    var norm = e.FullName.Replace('\\', '/');
                    if (!norm.StartsWith(MatCMS.Shared.PluginBundle.AssetFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    if (norm.EndsWith("/")) continue;                 // directory entry
                    if (e.Length <= 0 || e.Length > MaxAssetBytes) continue;
                    if (++count > MaxAssetFiles) break;
                    total += e.Length;
                    if (total > MaxTotalBytes) break;

                    var name = SanitizeFileName(Path.GetFileName(norm));
                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (string.IsNullOrEmpty(name) || !AllowedAssetExt.Contains(ext)) continue;

                    var dest = Path.Combine(staging, name);
                    // Defense-in-depth against path traversal: the resolved path must stay inside staging.
                    if (!Path.GetFullPath(dest).StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase)) continue;

                    using var es = e.Open();
                    using var fs = File.Create(dest);
                    await es.CopyToAsync(fs);
                }
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
                return (null, false, "Paket konnte nicht entpackt werden (beschädigtes ZIP).");
            }

            // 2) Apply the DB record. Imported code is UNTRUSTED → the plugin is DISABLED on create AND
            //    update, so nothing runs until an admin reviews the code and enables it.
            var existing = await db.Plugins.FirstOrDefaultAsync(p => p.Key == wantKey);
            bool updated;
            Models.Plugin plugin;
            try
            {
                if (existing is not null)
                {
                    existing.Name = meta.Name.Trim();
                    existing.Description = (meta.Description ?? "").Trim();
                    existing.Code = meta.Code ?? "";
                    existing.Version = (meta.Version ?? "").Trim();
                    existing.Enabled = false;
                    plugin = existing;
                    updated = true;
                }
                else
                {
                    plugin = new Models.Plugin
                    {
                        Name = meta.Name.Trim(),
                        Key = wantKey,
                        Version = (meta.Version ?? "").Trim(),
                        Description = (meta.Description ?? "").Trim(),
                        Code = meta.Code ?? "",
                        Enabled = false
                    };
                    db.Plugins.Add(plugin);
                    updated = false;
                }
                await db.SaveChangesAsync();
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
                return (null, false, "Import konnte nicht gespeichert werden.");
            }

            // 3) Swap the staged assets into place (only now is the old folder replaced).
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                Directory.Move(staging, dir);
            }
            catch
            {
                // DB is saved and the plugin is disabled; don't fail the import over the asset swap.
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* ignore */ }
            }

            return (plugin, updated, null);
        }
    }

    /// <summary>Strips any directory part and keeps only safe filename characters.</summary>
    public static string SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var baseName = Path.GetFileName(raw.Trim());
        var sb = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') sb.Append(ch);
        return sb.ToString().TrimStart('.');
    }
}
