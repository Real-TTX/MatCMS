using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Plugins;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly IWebHostEnvironment _env;
    public IndexModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, IWebHostEnvironment env)
    {
        _db = db; _registry = registry; _runner = runner; _env = env;
    }

    public List<MatCMS.Models.Plugin> Items { get; private set; } = new();
    public IReadOnlyDictionary<int, string> Errors => _registry.Errors;

    /// <summary>One uploaded plugin asset (library file).</summary>
    public sealed record AssetFile(string Name, long Size, bool AutoInclude, string Kind);
    public List<AssetFile> Assets { get; private set; } = new();

    // Files allowed as plugin assets. SVG is allowed here (unlike media uploads) because these are
    // served as static files referenced by <script>/<link>, not navigated to directly.
    private static readonly string[] AllowedExt =
        [".js", ".mjs", ".css", ".json", ".map", ".woff", ".woff2", ".ttf", ".eot", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public async Task OnGetAsync()
    {
        Items = await _db.Plugins.OrderBy(p => p.Name).ToListAsync();
        await LoadAssetsAsync();
    }

    private async Task LoadAssetsAsync()
    {
        var include = (await GetIncludeAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dir = StoragePaths.PluginAssets(_env);
        Directory.CreateDirectory(dir);
        Assets = Directory.GetFiles(dir)
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name)
            .Select(f => new AssetFile(f.Name, f.Length, include.Contains(f.Name), KindOf(f.Name)))
            .ToList();
    }

    private static string KindOf(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".js" or ".mjs" => "js",
            ".css" => "css",
            _ => "file"
        };
    }

    // ---- plugin toggle / delete (existing) --------------------------------

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is not null)
        {
            p.Enabled = !p.Enabled;
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is not null)
        {
            _db.Plugins.Remove(p);
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
            TempData["Flash"] = "Plugin gelöscht.";
        }
        return RedirectToPage();
    }

    // ---- plugin asset files ----------------------------------------------

    public async Task<IActionResult> OnPostUploadAssetAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["FlashError"] = "Keine Datei erhalten.";
            return RedirectToPage();
        }
        var name = SanitizeName(file.FileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || !AllowedExt.Contains(ext))
        {
            TempData["FlashError"] = $"Dateityp nicht erlaubt ({ext}). Erlaubt: {string.Join(", ", AllowedExt)}";
            return RedirectToPage();
        }
        if (file.Length > 5 * 1024 * 1024)
        {
            TempData["FlashError"] = "Datei zu groß (max. 5 MB).";
            return RedirectToPage();
        }

        var dir = StoragePaths.PluginAssets(_env);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);
        await using (var stream = System.IO.File.Create(dest))
            await file.CopyToAsync(stream);

        TempData["Flash"] = $"Datei „{name}“ hochgeladen.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAssetAsync(string name)
    {
        name = SanitizeName(name);
        if (!string.IsNullOrEmpty(name))
        {
            var path = Path.Combine(StoragePaths.PluginAssets(_env), name);
            if (System.IO.File.Exists(path))
                try { System.IO.File.Delete(path); } catch { /* ignore */ }
            // Also drop it from the auto-include list.
            var list = (await GetIncludeAsync()).Where(f => !string.Equals(f, name, StringComparison.OrdinalIgnoreCase)).ToList();
            await SaveIncludeAsync(list);
            TempData["Flash"] = $"Datei „{name}“ gelöscht.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleIncludeAsync(string name)
    {
        name = SanitizeName(name);
        var path = Path.Combine(StoragePaths.PluginAssets(_env), name);
        if (!string.IsNullOrEmpty(name) && System.IO.File.Exists(path))
        {
            var list = (await GetIncludeAsync()).ToList();
            if (list.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                list.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            else
                list.Add(name);
            await SaveIncludeAsync(list);
        }
        return RedirectToPage();
    }

    // ---- auto-include list (stored as a comma-separated SiteSetting) ------

    private async Task<List<string>> GetIncludeAsync()
    {
        var s = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingKeys.PluginAutoInclude);
        return (s?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();
    }

    private async Task SaveIncludeAsync(IEnumerable<string> names)
    {
        var value = string.Join(",", names.Distinct());
        var setting = await _db.SiteSettings.FirstOrDefaultAsync(x => x.Key == SettingKeys.PluginAutoInclude);
        if (setting is null)
            _db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.PluginAutoInclude, Value = value });
        else
            setting.Value = value;
        await _db.SaveChangesAsync();
    }

    /// <summary>Strips any directory part and keeps only safe filename characters.</summary>
    private static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var baseName = Path.GetFileName(raw.Trim());
        var sb = new System.Text.StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') sb.Append(ch);
        var name = sb.ToString().TrimStart('.');
        return name;
    }
}
