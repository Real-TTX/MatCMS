using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Plugins;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly IWebHostEnvironment _env;
    public EditModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, IWebHostEnvironment env)
    {
        _db = db; _registry = registry; _runner = runner; _env = env;
    }

    public MatCMS.Models.Plugin Current { get; private set; } = default!;
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Version { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? ConfigJson { get; set; }
    [BindProperty] public string? FilesJson { get; set; }

    /// <summary>Anzeigename der Einstiegsdatei im Baum. Sie liegt im Feld Code und nicht in der
    /// Dateikarte — der Name ist Beschriftung, kein Pfad, und deshalb hier und nicht im Modell.
    /// <para>.csx, weil es ein Roslyn-Skript ist und die übrigen Dateien per #load ebenfalls .csx
    /// sind; eine .cs würde eine Übersetzungseinheit versprechen, die es hier nicht gibt.</para></summary>
    public const string EntryFile = "plugin.csx";
    [BindProperty] public bool Enabled { get; set; }
    public string? Error { get; private set; }
    public string? RunError { get; private set; }
    public IReadOnlyList<string> Log => _registry.Log;

    /// <summary>One file in this plugin's own asset folder.</summary>
    public sealed record AssetFile(string Name, long Size, string Kind);
    public List<AssetFile> Assets { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        Current = p;
        Name = p.Name; Description = p.Description; Code = p.Code; FilesJson = p.FilesJson; Enabled = p.Enabled; Version = p.Version; ConfigJson = p.ConfigJson;
        RunError = _registry.Errors.TryGetValue(id, out var e) ? e : null;
        LoadAssets();
        return Page();
    }

    /// <summary>Downloads this plugin as a self-contained ZIP bundle (plugin.json + assets/).</summary>
    public async Task<IActionResult> OnGetExportAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        var bytes = PluginPackager.Export(p, _env);
        var fileName = (string.IsNullOrWhiteSpace(p.Key) ? "plugin" : p.Key) + ".plugin.zip";
        return File(bytes, "application/zip", fileName);
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        Current = p;
        LoadAssets();

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "Bitte einen Namen angeben.";
            return Page();
        }

        p.Name = name;
        p.Description = (Description ?? "").Trim();
        p.Version = (Version ?? "").Trim();
        p.Code = Code ?? "";
        p.ConfigJson = SanitizeConfig(ConfigJson);
        // Nur eine gültige Karte Pfad → Inhalt wird gespeichert. Unsinn hier stillschweigend zu
        // übernehmen hieße, ihn erst beim nächsten Start des Plugins zu bemerken.
        p.FilesJson = SanitizeFiles(FilesJson);
        p.Enabled = Enabled;
        await _db.SaveChangesAsync();

        // Re-run all plugins so this one takes effect (or surfaces its error).
        await _runner.RunAllAsync();
        RunError = _registry.Errors.TryGetValue(id, out var e) ? e : null;

        if (RunError is not null)
        {
            // Stay on the page and show the compile/run error.
            Name = p.Name; Description = p.Description; Code = p.Code; Enabled = p.Enabled; Version = p.Version; ConfigJson = p.ConfigJson;
            return Page();
        }

        TempData["Flash"] = "Plugin gespeichert.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is not null)
        {
            var key = p.Key;
            _db.Plugins.Remove(p);
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
            var dir = StoragePaths.PluginAssetDir(_env, key);
            if (!string.IsNullOrWhiteSpace(key) && Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            TempData["Flash"] = "Plugin gelöscht.";
        }
        return RedirectToPage("Index");
    }

    // ---- this plugin's asset files ---------------------------------------

    public async Task<IActionResult> OnPostUploadAssetAsync(int id, IFormFile? file)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        if (string.IsNullOrWhiteSpace(p.Key))
        {
            TempData["FlashError"] = "Plugin hat keinen gültigen Schlüssel.";
            return RedirectToPage("Edit", new { id });
        }

        var dir = StoragePaths.PluginAssetDir(_env, p.Key);
        var existing = Directory.Exists(dir) ? Directory.GetFiles(dir) : [];

        if (file is null || file.Length == 0)
            TempData["FlashError"] = "Keine Datei erhalten.";
        else
        {
            var name = PluginPackager.SanitizeFileName(file.FileName);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || !PluginPackager.AllowedAssetExt.Contains(ext))
                TempData["FlashError"] = $"Dateityp nicht erlaubt ({ext}). Erlaubt: {string.Join(", ", PluginPackager.AllowedAssetExt)}";
            // Die Grenzen sind die des Bündelformats (MatCMS.Shared.PluginBundle) und keine eigenen:
            // was hier hochgeladen werden darf, muss auch durch Export und Import wieder
            // durchpassen — sonst legt man eine Datei an, die das eigene ZIP später verwirft.
            else if (file.Length > MatCMS.Shared.PluginBundle.MaxAssetBytes)
                TempData["FlashError"] = $"Datei zu groß (max. {MatCMS.Shared.PluginBundle.MaxAssetBytes / 1024 / 1024} MB).";
            // Eine Datei zu ersetzen bleibt erlaubt, auch wenn der Ordner voll ist — sonst hinge
            // man an der Grenze fest, ohne etwas ändern zu können.
            else if (existing.Length >= MatCMS.Shared.PluginBundle.MaxAssetFiles
                     && !existing.Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)))
                TempData["FlashError"] = $"Zu viele Dateien in diesem Plugin (max. {MatCMS.Shared.PluginBundle.MaxAssetFiles}).";
            else
            {
                Directory.CreateDirectory(dir);
                await using var stream = System.IO.File.Create(Path.Combine(dir, name));
                await file.CopyToAsync(stream);
                TempData["Flash"] = $"Datei „{name}“ hochgeladen.";
            }
        }
        return RedirectToPage("Edit", new { id });
    }

    public async Task<IActionResult> OnPostDeleteAssetAsync(int id, string name)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");

        name = PluginPackager.SanitizeFileName(name);
        if (!string.IsNullOrEmpty(name))
        {
            var path = Path.Combine(StoragePaths.PluginAssetDir(_env, p.Key), name);
            if (System.IO.File.Exists(path))
                try { System.IO.File.Delete(path); } catch { /* ignore */ }
            TempData["Flash"] = $"Datei „{name}“ gelöscht.";
        }
        return RedirectToPage("Edit", new { id });
    }

    private void LoadAssets()
    {
        var dir = StoragePaths.PluginAssetDir(_env, Current.Key);
        if (string.IsNullOrEmpty(Current.Key) || !Directory.Exists(dir)) { Assets = new(); return; }
        Assets = Directory.GetFiles(dir)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name)
            .Select(f => new AssetFile(f.Name, f.Length, KindOf(f.Name)))
            .ToList();
    }

    private static string KindOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".js" or ".mjs" => "js",
        ".css" => "css",
        _ => "file"
    };

    /// <summary>Normalizes the posted config into a clean JSON object of trimmed string key→value pairs.</summary>
    private static string SanitizeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return "{}";
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var key = p.Name.Trim();
                if (key.Length == 0) continue;
                obj[key] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? (p.Value.GetString() ?? "")
                    : p.Value.ToString();
            }
            return obj.ToJsonString();
        }
        catch { return "{}"; }
    }

    /// <summary>Leert das Feld bei ungültigem JSON oder falscher Form. Ein Plugin mit kaputter
    /// Dateikarte startet sonst gar nicht, und der Fehler stünde erst im Log statt im Editor.</summary>
    private static string SanitizeFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(map);
        }
        catch { return "{}"; }
    }
}
