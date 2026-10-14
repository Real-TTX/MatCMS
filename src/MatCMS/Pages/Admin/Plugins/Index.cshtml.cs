using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Pages.Admin.Plugins;

// A native plugin may bring up to 60 MB of assemblies (PluginBundle.MaxSourceTotalBytes) next to 25 MB of assets; the framework's default
// limits (about 30 MB for a body) are raised for this page so such a bundle can arrive at all.
[RequestSizeLimit(96L * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 96L * 1024 * 1024)]
public class IndexModel : PageModel
{
    private const long MaxBundleBytes = 90L * 1024 * 1024;
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

    /// <summary>The question asked before deleting each plugin, with what is lost when it owns data (key → text).</summary>
    public Dictionary<string, string> DeleteConfirm { get; } = new();
    /// <summary>The keys of the loaded plugins that declared settings (they get a settings button).</summary>
    public HashSet<string> WithSettings { get; } = new();
    /// <summary>The scheduled jobs of the loaded plugins.</summary>
    public IReadOnlyList<MatCMS.Services.Plugins.PluginJobService.JobStatus> Jobs { get; private set; } = Array.Empty<MatCMS.Services.Plugins.PluginJobService.JobStatus>();

    /// <summary>Runs a job right now.</summary>
    public async Task<IActionResult> OnPostRunJobAsync(string owner, string job)
    {
        var jobs = HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginJobService>();
        var t = HttpContext.RequestServices.GetRequiredService<Localizer>();
        var ran = await jobs.RunNowAsync(owner, job, HttpContext.RequestAborted);
        TempData[ran ? "Flash" : "FlashError"] = t[ran ? "plugins.jobs.ran" : "plugins.jobs.notRun"].ToString();
        return RedirectToPage();
    }
    public IReadOnlyDictionary<int, string> Errors => _registry.Errors;

    /// <summary>Catalogue of the connected cloud - null while no cloud is connected or it is
    /// unreachable, which is why the browse section only renders when it has something.</summary>
    public StoreCatalog? Catalog { get; private set; }
    public bool CloudConnected { get; private set; }
    public string? CatalogError { get; private set; }

    /// <summary>Whether compiled (native) plugins may run on this site.</summary>
    public bool NativeAllowed { get; private set; }

    /// <summary>Native plugins whose memory could not be given back after unloading: the next restart does.</summary>
    public bool Lingering(string key) => HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.NativePluginHost>().IsLingering(key);

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
        var data = HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginDataService>();
        var t = HttpContext.RequestServices.GetRequiredService<Localizer>();
        foreach (var p in Items)
        {
            DeleteConfirm[p.Key] = await MatCMS.Services.Plugins.PluginUninstall.ConfirmTextAsync(_db, data, t, p.Key);
            if (_registry.SliceOf(p.Key) is { Settings.Count: > 0 }) WithSettings.Add(p.Key);
        }
        Jobs = HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginJobService>().Status();
        NativeAllowed = await IsNativeAllowedAsync();
        CloudConnected = await _catalog.IsAvailableAsync();
        // Only fetched on demand: the plugins page must not wait on a remote call every time it opens.
        if (browse && CloudConnected)
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
    }

    private Task<bool> IsNativeAllowedAsync() => _db.SiteSettings.AsNoTracking()
        .AnyAsync(s => s.Key == SettingKeys.AllowNativePlugins && (s.Value == "1" || s.Value == "true"));

    /// <summary>The operator's switch for compiled plugins. Switching it on or off reloads the native ones, so
    /// the change is live: off, every native plugin stops at once; on, the enabled ones start.</summary>
    public async Task<IActionResult> OnPostAllowNativeAsync(bool allow)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.AllowNativePlugins);
        if (row is null) _db.SiteSettings.Add(new MatCMS.Models.SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = allow ? "1" : "0" });
        else row.Value = allow ? "1" : "0";
        await _db.SaveChangesAsync();
        foreach (var id in await _db.Plugins.AsNoTracking()
                     .Where(p => p.Kind == MatCMS.Models.PluginKind.Native).Select(p => p.Id).ToListAsync())
            await _runner.ReloadAsync(id);
        TempData["Flash"] = allow ? "Native Plugins sind erlaubt." : "Native Plugins sind nicht mehr erlaubt.";
        return RedirectToPage();
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
        if (file.Length > MaxBundleBytes)
        {
            TempData["FlashError"] = "Paket zu groß (max. 90 MB).";
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
        // until the admin reviews and enables it. Only this plugin is touched.
        await _runner.ReloadAsync(plugin.Id);
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
            await _runner.ReloadAsync(p.Id);
        }
        return RedirectToPage();
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
        return RedirectToPage();
    }
}
