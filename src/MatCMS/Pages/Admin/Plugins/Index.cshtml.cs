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
    private readonly MatcmsStoreClient _matcms;
    public IndexModel(AppDbContext db, PluginRegistry registry, PluginRunner runner, IWebHostEnvironment env, CloudCatalogService catalog, MatcmsStoreClient matcms)
    {
        _db = db;
        _catalog = catalog; _registry = registry; _runner = runner; _env = env; _matcms = matcms;
    }

    /// <summary>Whether this site may install from the MatCMS Store (matcms.de) and who decides — see <see cref="MatcmsStoreClient"/>.</summary>
    public MatcmsStoreClient.Access StoreAccess { get; private set; } = new(false, false, true);

    /// <summary>The MatCMS Store tab's list, fetched only while that tab is open (<c>?browse=matcms</c>).</summary>
    public List<MatcmsStoreClient.Entry>? Matcms { get; private set; }

    /// <summary>Which store the dialog shows: "cloud", "matcms" or null (closed).</summary>
    public string? Browsing { get; private set; }

    /// <summary>Where "browse" leads: the cloud's store when one is connected, else the MatCMS Store when this site may use it.</summary>
    public string? BrowseUrl => CloudConnected ? Url.Page("Index", new { browse = "true" }) : StoreAccess.Allowed ? Url.Page("Index", new { browse = "matcms" }) : null;

    public List<MatCMS.Models.Plugin> Items { get; private set; } = new();

    /// <summary>Installed plugins with a newer version in the MatCMS Store (key → version), from what the site last heard — no remote call here.</summary>
    public Dictionary<string, string> Updates { get; private set; } = new();

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
    public Shared.StoreDialog StoreDialog => Browsing == "matcms" ? MatcmsDialog : CloudDialog;

    private IReadOnlyList<Shared.StoreTab>? Tabs()
    {
        if (!CloudConnected || !StoreAccess.Allowed) return null;
        var t = HttpContext.RequestServices.GetRequiredService<Localizer>();
        return new[]
        {
            new Shared.StoreTab(t["plugins.storeTab.cloud"], Url.Page("Index", new { browse = "true" })!, Browsing != "matcms"),
            new Shared.StoreTab(t["plugins.storeTab.matcms"], Url.Page("Index", new { browse = "matcms" })!, Browsing == "matcms")
        };
    }

    /// <summary>The MatCMS Store as the dialog shows it. A key the cloud's store also offers is installed from there (its operator may hold a
    /// checked version on purpose), so it gets a note instead of a button here.</summary>
    private Shared.StoreDialog MatcmsDialog
    {
        get
        {
            var t = HttpContext.RequestServices.GetRequiredService<Localizer>();
            var inCloud = (Catalog?.Plugins ?? []).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new(
                TitleKey: "plugins.matcmsCatalog",
                IntroKey: "plugins.matcmsIntro",
                RouteName: "key",
                Handler: "InstallFromMatcms",
                Tabs: Tabs(),
                Items: (Matcms ?? new()).Select(p => new Shared.StoreItem(
                    Title: p.Name,
                    Sub: p.Key,
                    Description: p.Summary.Length > 0 ? p.Summary : p.Description,
                    RouteValue: p.Key,
                    InstalledVersion: Items.FirstOrDefault(i => i.Key == p.Key)?.Version,
                    Version: p.Version,
                    Publisher: p.Publisher + (p.PublisherVerified ? " ✓" : ""),
                    PublisherUrl: p.PublisherUrl,
                    Badge: p.Kind == "native" ? t["plugins.kind.native"] : null,
                    Note: inCloud.Contains(p.Key) ? t["plugins.matcmsViaCloud"] : null,
                    Downloads: p.Downloads,
                    Images: p.Images.Select(img => new Shared.StoreImage(
                        Url.Page("/Admin/Plugins/Index", "MatcmsImage", new { key = p.Key, imageId = img.Id })!, img.Caption)).ToList())).ToList(),
                Error: CatalogError);
        }
    }

    private Shared.StoreDialog CloudDialog => new(
        Tabs: Tabs(),
        TitleKey: "plugins.cloudCatalog",
        IntroKey: "plugins.cloudIntro",
        RouteName: "key",
        Items: (Catalog?.Plugins ?? []).Select(p => new Shared.StoreItem(
            Title: p.Name,
            Sub: p.Key,
            Description: p.Description,
            RouteValue: p.Key,
            InstalledVersion: Items.FirstOrDefault(i => i.Key == p.Key)?.Version,
            Version: p.Version,
            Publisher: p.Publisher,
            PublisherUrl: p.PublisherUrl,
            Images: (p.Images ?? []).Select(img => new Shared.StoreImage(
                Url.Page("/Admin/Plugins/Index", "StoreImage", new { imageId = img.Id })!, img.Caption)).ToList())).ToList(),
        Error: CatalogError);

    /// <summary>A store plugin's picture for the dialog, passed through from the cloud (see <see cref="CloudCatalogService.GetImageAsync"/>).</summary>
    public async Task<IActionResult> OnGetStoreImageAsync(int imageId)
    {
        var cache = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        if (await _catalog.GetImageAsync(imageId, cache, HttpContext.RequestAborted) is not { } img) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(img.data, img.type);
    }

    /// <summary>A MatCMS Store picture for the dialog, fetched and served by this site (see <see cref="MatcmsStoreClient.ImageAsync"/>).</summary>
    public async Task<IActionResult> OnGetMatcmsImageAsync(string key, long imageId)
    {
        if (await _matcms.ImageAsync(key, imageId, HttpContext.RequestAborted) is not { } img) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(img.Data, img.Type);
    }

    public async Task<IActionResult> OnPostInstallFromMatcmsAsync(string key, string? back = null)
    {
        var (ok, message, id) = await _matcms.InstallAsync(key, HttpContext.RequestAborted);
        if (id is int pid) await _runner.ReloadAsync(pid);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return back == "list" ? RedirectToPage() : RedirectToPage(new { browse = "matcms" });
    }

    /// <summary>The site's own switch for the MatCMS Store — only while no cloud is connected (then the cloud's operator decides).</summary>
    public async Task<IActionResult> OnPostMatcmsStoreAsync(bool allow)
    {
        if ((await _matcms.AccessAsync()).CloudDecides) return RedirectToPage();
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.MatcmsStore);
        if (row is null) _db.SiteSettings.Add(new MatCMS.Models.SiteSetting { Key = SettingKeys.MatcmsStore, Value = allow ? "1" : "0" });
        else row.Value = allow ? "1" : "0";
        await _db.SaveChangesAsync();
        TempData["Flash"] = allow ? "Der MatCMS Store ist eingeschaltet." : "Der MatCMS Store ist ausgeschaltet.";
        return RedirectToPage();
    }

    public async Task OnGetAsync(string? browse = null)
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
        StoreAccess = await _matcms.AccessAsync(HttpContext.RequestAborted);
        Updates = await _matcms.UpdatesAsync(HttpContext.RequestAborted);
        // Only fetched on demand: the plugins page must not wait on a remote call every time it opens.
        if (browse == "matcms" && StoreAccess.Allowed)
        {
            Browsing = "matcms";
            (Matcms, CatalogError) = await _matcms.ListAsync(HttpContext.RequestAborted);
            // The cloud's list too, only to know which keys come from there (the cloud wins); its failure is not this tab's error.
            if (CloudConnected) Catalog = (await _catalog.GetCatalogAsync(HttpContext.RequestAborted)).catalog;
        }
        else if (browse is "true" or "cloud" && CloudConnected)
        {
            Browsing = "cloud";
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
        }
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
