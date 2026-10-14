using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Plugins;

/// <summary>
/// The page of a NATIVE plugin. It is what <see cref="EditModel"/> is for a script, minus the one thing a compiled
/// plugin cannot offer — editing its source: the same states, the same switch, the same configuration, export and
/// delete, the same row of buttons. Where a script shows its files in an editor, this shows what is on disk.
/// </summary>
public class NativeModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly NativePluginHost _native;
    private readonly IWebHostEnvironment _env;

    public NativeModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, NativePluginHost native, IWebHostEnvironment env)
    {
        _db = db; _registry = registry; _runner = runner; _native = native; _env = env;
    }

    public MatCMS.Models.Plugin Current { get; private set; } = default!;
    [BindProperty] public bool Enabled { get; set; }
    /// <summary>The configuration as parallel lists, not as JSON: this page has no script, and a plain form must
    /// work without one.</summary>
    [BindProperty] public List<string> ConfigKeys { get; set; } = new();
    [BindProperty] public List<string> ConfigValues { get; set; } = new();

    public string? RunError { get; private set; }
    public bool Loaded { get; private set; }
    public bool Lingering { get; private set; }
    public bool NativeAllowed { get; private set; }
    public IReadOnlyList<string> Log { get; private set; } = Array.Empty<string>();

    /// <summary>One file of the plugin's <c>src/</c> folder, as it lies on disk.</summary>
    public sealed record SourceFile(string Path, long Size);
    public List<SourceFile> Files { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var p = await _db.Plugins.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return RedirectToPage("Index");
        // A script has its own page; a link to the wrong one lands on the right one.
        if (PluginKind.Normalize(p.Kind) != PluginKind.Native) return RedirectToPage(PluginKind.Normalize(p.Kind) == PluginKind.BuiltIn ? "BuiltIn" : "Edit", new { id });
        await FillAsync(p);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is null) return RedirectToPage("Index");
        if (PluginKind.Normalize(p.Kind) != PluginKind.Native) return RedirectToPage("Edit", new { id });

        var config = new System.Text.Json.Nodes.JsonObject();
        for (var i = 0; i < Math.Min(ConfigKeys.Count, ConfigValues.Count); i++)
        {
            var key = (ConfigKeys[i] ?? "").Trim();
            if (key.Length > 0) config[key] = (ConfigValues[i] ?? "").Trim();
        }
        p.ConfigJson = config.ToJsonString();
        p.Enabled = Enabled;
        await _db.SaveChangesAsync();

        // Only this plugin is loaded or unloaded; the others keep running.
        await _runner.ReloadAsync(id);
        TempData["Flash"] = "Plugin gespeichert.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetExportAsync(int id)
    {
        var p = await _db.Plugins.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return RedirectToPage("Index");
        var bytes = PluginPackager.Export(p, _env);
        return File(bytes, "application/zip", (string.IsNullOrWhiteSpace(p.Key) ? "plugin" : p.Key) + ".plugin.zip");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var p = await _db.Plugins.FindAsync(id);
        if (p is not null && MatCMS.Models.PluginKind.Normalize(p.Kind) == MatCMS.Models.PluginKind.BuiltIn)
        {
            TempData["FlashError"] = "Ein eingebautes Plugin lässt sich nicht löschen, nur ausschalten.";
        }
        else if (p is not null)
        {
            await MatCMS.Services.Plugins.PluginUninstall.DeleteAsync(_db, _runner, HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginDataService>(), _env, p);
            TempData["Flash"] = "Plugin gelöscht.";
        }
        return RedirectToPage("Index");
    }

    private async Task FillAsync(MatCMS.Models.Plugin p)
    {
        Current = p;
        Enabled = p.Enabled;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                ConfigKeys.Add(prop.Name);
                ConfigValues.Add(prop.Value.ValueKind == System.Text.Json.JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString());
            }
        }
        catch { /* an unreadable config shows as empty rather than breaking the page */ }

        RunError = _registry.Errors.TryGetValue(p.Id, out var e) ? e : null;
        Loaded = _native.IsLoaded(p.Key);
        Lingering = _native.IsLingering(p.Key);
        NativeAllowed = await _db.SiteSettings.AsNoTracking().AnyAsync(s => s.Key == SettingKeys.AllowNativePlugins && (s.Value == "1" || s.Value == "true"));
        Log = _registry.LogOf(p.Key);

        var src = StoragePaths.PluginSrc(_env, p.Key, p.Version);
        if (Directory.Exists(src))
            Files = Directory.GetFiles(src, "*", SearchOption.AllDirectories)
                .Select(f => new SourceFile(Path.GetRelativePath(src, f).Replace('\\', '/'), new FileInfo(f).Length))
                .OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }
}
