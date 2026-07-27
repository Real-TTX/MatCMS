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
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public bool Enabled { get; set; }
    public string? Error { get; private set; }
    public string? RunError { get; private set; }
    public IReadOnlyList<string> Log => _registry.Log;

    /// <summary>One file in this plugin's own asset folder.</summary>
    public sealed record AssetFile(string Name, long Size, string Kind);
    public List<AssetFile> Assets { get; private set; } = new();

    // Files a plugin may carry. SVG is intentionally excluded (like the media uploader): it can carry
    // active content and would be a same-origin stored-XSS vector when served from /plugin-assets.
    private static readonly string[] AllowedExt =
        [".js", ".mjs", ".css", ".json", ".map", ".woff", ".woff2", ".ttf", ".eot", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        Current = p;
        Name = p.Name; Description = p.Description; Code = p.Code; Enabled = p.Enabled;
        RunError = _registry.Errors.TryGetValue(id, out var e) ? e : null;
        LoadAssets();
        return Page();
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
        p.Code = Code ?? "";
        p.Enabled = Enabled;
        await _db.SaveChangesAsync();

        // Re-run all plugins so this one takes effect (or surfaces its error).
        await _runner.RunAllAsync();
        RunError = _registry.Errors.TryGetValue(id, out var e) ? e : null;

        if (RunError is not null)
        {
            // Stay on the page and show the compile/run error.
            Name = p.Name; Description = p.Description; Code = p.Code; Enabled = p.Enabled;
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

        if (file is null || file.Length == 0)
            TempData["FlashError"] = "Keine Datei erhalten.";
        else
        {
            var name = SanitizeName(file.FileName);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (string.IsNullOrEmpty(name) || !AllowedExt.Contains(ext))
                TempData["FlashError"] = $"Dateityp nicht erlaubt ({ext}). Erlaubt: {string.Join(", ", AllowedExt)}";
            else if (file.Length > 5 * 1024 * 1024)
                TempData["FlashError"] = "Datei zu groß (max. 5 MB).";
            else
            {
                var dir = StoragePaths.PluginAssetDir(_env, p.Key);
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

        name = SanitizeName(name);
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

    /// <summary>Strips any directory part and keeps only safe filename characters.</summary>
    private static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var baseName = Path.GetFileName(raw.Trim());
        var sb = new System.Text.StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') sb.Append(ch);
        return sb.ToString().TrimStart('.');
    }
}
