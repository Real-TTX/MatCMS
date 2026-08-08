using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Pages.Admin.Plugins;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly PluginRunner _runner;
    private readonly IWebHostEnvironment _env;
    private readonly CloudCatalogService _catalog;
    public IndexModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, IWebHostEnvironment env, CloudCatalogService catalog)
    {
        _db = db;
        _catalog = catalog; _registry = registry; _runner = runner; _env = env;
    }

    public List<MatCMS.Models.Plugin> Items { get; private set; } = new();
    public IReadOnlyDictionary<int, string> Errors => _registry.Errors;

    /// <summary>Catalogue of the connected cloud - null while no cloud is connected or it is
    /// unreachable, which is why the browse section only renders when it has something.</summary>
    public StoreCatalog? Catalog { get; private set; }
    public bool CloudConnected { get; private set; }
    public string? CatalogError { get; private set; }

    /// <summary>The catalogue shaped for the store dialog. Built here rather than in the view so the
    /// "already installed?" lookup stays out of the markup.</summary>
    public Shared.StoreDialog StoreDialog => new(
        TitleKey: "plugins.cloudCatalog",
        IntroKey: "plugins.cloudIntro",
        RouteName: "key",
        Items: (Catalog?.Plugins ?? []).Select(p => new Shared.StoreItem(
            Title: p.Name,
            Sub: p.Key,
            Description: p.Description,
            RouteValue: p.Key,
            InstalledVersion: Items.FirstOrDefault(i => i.Key == p.Key)?.Version,
            Version: p.Version)).ToList(),
        Error: CatalogError);

    public async Task OnGetAsync(bool browse = false)
    {
        Items = await _db.Plugins.OrderBy(p => p.Name).ToListAsync();
        CloudConnected = await _catalog.IsAvailableAsync();
        // Only fetched on demand: the plugins page must not wait on a remote call every time it opens.
        if (browse && CloudConnected)
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostInstallFromCloudAsync(string key)
    {
        var (ok, message) = await _catalog.InstallPluginAsync(key, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return RedirectToPage(new { browse = true });
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            TempData["FlashError"] = "Keine Datei erhalten.";
            return RedirectToPage();
        }
        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["FlashError"] = "Paket zu groß (max. 20 MB).";
            return RedirectToPage();
        }

        Models.Plugin? plugin = null; bool updated = false; string? error = null;
        try
        {
            await using var stream = file.OpenReadStream();
            (plugin, updated, error) = await PluginPackager.ImportAsync(stream, _env, _db);
        }
        catch
        {
            error = "Import fehlgeschlagen (ungültiges oder beschädigtes Paket).";
        }

        if (error is not null || plugin is null)
        {
            TempData["FlashError"] = error ?? "Import fehlgeschlagen.";
            return RedirectToPage();
        }

        // Refresh registrations. The imported plugin is DISABLED, so its (untrusted) code does not run
        // until the admin reviews and enables it.
        await _runner.RunAllAsync();
        TempData["Flash"] = updated
            ? $"Plugin „{plugin.Name}“ auf Version {plugin.Version} aktualisiert – deaktiviert; bitte prüfen und wieder aktivieren."
            : $"Plugin „{plugin.Name}“ importiert – deaktiviert; bitte prüfen und aktivieren.";
        return RedirectToPage("Edit", new { id = plugin.Id });
    }

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
            var key = p.Key;
            _db.Plugins.Remove(p);
            await _db.SaveChangesAsync();
            await _runner.RunAllAsync();
            // Remove the plugin's own asset folder.
            var dir = StoragePaths.PluginAssetDir(_env, key);
            if (!string.IsNullOrWhiteSpace(key) && Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            TempData["Flash"] = "Plugin gelöscht.";
        }
        return RedirectToPage();
    }
}
