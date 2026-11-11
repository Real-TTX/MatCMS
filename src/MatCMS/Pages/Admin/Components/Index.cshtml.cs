using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Pages.Admin.Components;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudCatalogService _catalog;
    private readonly MatcmsStoreClient _matcms;
    public IndexModel(AppDbContext db, CloudCatalogService catalog, MatcmsStoreClient matcms)
    {
        _db = db;
        _catalog = catalog;
        _matcms = matcms;
    }

    /// <summary>Whether this site may install from the MatCMS Store, and the store's components while its tab is open (<c>?browse=matcms</c>).</summary>
    public MatcmsStoreClient.Access StoreAccess { get; private set; } = new(false, false, true);
    public List<MatcmsStoreClient.Entry>? Matcms { get; private set; }
    /// <summary>Which store the dialog shows: "cloud", "matcms" or null (closed).</summary>
    public string? Browsing { get; private set; }
    /// <summary>Where "browse" leads: the cloud's store when one is connected, else the MatCMS Store when this site may use it.</summary>
    public string? BrowseUrl => CloudConnected ? Url.Page("Index", new { browse = "true" }) : StoreAccess.Allowed ? Url.Page("Index", new { browse = "matcms" }) : null;

    /// <summary>Catalogue of the connected cloud, fetched only on demand (?browse=true).</summary>
    public StoreCatalog? Catalog { get; private set; }
    public bool CloudConnected { get; private set; }
    public string? CatalogError { get; private set; }

    /// <summary>The catalogue shaped for the store dialog. Built here rather than in the view so
    /// the "already installed?" lookup stays out of the markup.</summary>
    public Shared.StoreDialog StoreDialog => Browsing == "matcms" ? MatcmsDialog : CloudDialog;

    private IReadOnlyList<Shared.StoreTab>? Tabs() => Shared.StoreTabs.For(HttpContext, Url, CloudConnected && StoreAccess.Allowed, Browsing);

    /// <summary>The MatCMS Store's components; a type the cloud's store also offers comes from there (note instead of a button).</summary>
    private Shared.StoreDialog MatcmsDialog
    {
        get
        {
            var t = HttpContext.RequestServices.GetRequiredService<Localizer>();
            var inCloud = (Catalog?.Components ?? []).Select(x => StoreItemPackage.Key(x.Type)).ToHashSet();
            return new(
                TitleKey: "components.matcmsCatalog", IntroKey: "components.matcmsIntro", RouteName: "key", Handler: "InstallFromMatcms", Tabs: Tabs(),
                Items: (Matcms ?? new()).Select(e => new Shared.StoreItem(
                    Title: e.Name, Sub: e.Key, Description: e.Summary.Length > 0 ? e.Summary : e.Description, RouteValue: e.Key,
                    InstalledVersion: Items.Any(i => StoreItemPackage.Key(i.Type) == e.Key) ? "" : null, Version: e.Version,
                    Publisher: e.Publisher + (e.PublisherVerified ? " ✓" : ""), PublisherUrl: e.PublisherUrl, Downloads: e.Downloads,
                    Note: inCloud.Contains(e.Key) ? t["plugins.matcmsViaCloud"] : null,
                    Images: e.Images.Select(img => new Shared.StoreImage(Url.Page("/Admin/Components/Index", "MatcmsImage", new { key = e.Key, imageId = img.Id })!, img.Caption)).ToList()
                )).ToList(),
                Error: CatalogError);
        }
    }

    private Shared.StoreDialog CloudDialog => new(
        Tabs: Tabs(),
        TitleKey: "components.cloudCatalog",
        IntroKey: "components.cloudIntro",
        RouteName: "type",
        Items: (Catalog?.Components ?? []).Select(c => new Shared.StoreItem(
            Title: c.Name,
            Sub: c.Type,
            Description: c.Description,
            RouteValue: c.Type,
            InstalledVersion: Items.Any(i => i.Type == c.Type) ? "" : null)).ToList(),
        Error: CatalogError);

    public async Task<IActionResult> OnPostInstallFromMatcmsAsync(string key)
    {
        var (ok, message) = await _matcms.InstallDesignAsync(MatcmsStoreClient.GroupComponent, key, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return RedirectToPage(new { browse = "matcms" });
    }

    public async Task<IActionResult> OnGetMatcmsImageAsync(string key, long imageId)
    {
        if (await _matcms.ImageAsync(key, imageId, HttpContext.RequestAborted) is not { } img) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(img.Data, img.Type);
    }

    public async Task<IActionResult> OnPostInstallFromCloudAsync(string type)
    {
        var (ok, message) = await _catalog.InstallComponentAsync(type, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return RedirectToPage(new { browse = true });
    }

    /// <summary>User-defined components ("Eigene Komponenten").</summary>
    public List<Component> Items { get; private set; } = new();

    /// <summary>Built-in block types ("Systemkomponenten"), shown read-only.</summary>
    public IReadOnlyList<BlockDefinition> System { get; private set; } = BlockRegistry.Builtins;

    public async Task OnGetAsync(string? browse = null)
    {
        Items = await _db.Components.OrderBy(c => c.Name).ToListAsync();
        CloudConnected = await _catalog.IsAvailableAsync();
        StoreAccess = await _matcms.AccessAsync(HttpContext.RequestAborted);
        if (browse == "matcms" && StoreAccess.Allowed)
        {
            Browsing = "matcms";
            (Matcms, CatalogError) = await _matcms.ListAsync(MatcmsStoreClient.GroupComponent, HttpContext.RequestAborted);
            if (CloudConnected) Catalog = (await _catalog.GetCatalogAsync(HttpContext.RequestAborted)).catalog;
        }
        else if (browse is "true" or "cloud" && CloudConnected)
        {
            Browsing = "cloud";
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var c = await _db.Components.FindAsync(id);
        if (c is not null)
        {
            _db.Components.Remove(c);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Komponente gelöscht.";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// Takes the JSON a component editor exports — this one or a cloud profile's. The TYPE is the
    /// identity, because that is what a {{placeholder}} block on an existing page refers to:
    /// importing a type that already exists has to update it, or the blocks already placed would
    /// point at the older of two components.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(string? componentJson)
    {
        using var doc = JsonImport.TryParse(componentJson);
        if (doc is null)
        {
            TempData["FlashError"] = "Bitte gültiges Komponenten-JSON einfügen.";
            return RedirectToPage();
        }

        var (row, _, error) = await DesignImport.ComponentAsync(_db, doc.RootElement, HttpContext.RequestAborted);
        if (row is null)
        {
            TempData["FlashError"] = error;
            return RedirectToPage();
        }
        TempData["Flash"] = $"Komponente \"{row.Name}\" importiert.";
        return RedirectToPage();
    }
}
