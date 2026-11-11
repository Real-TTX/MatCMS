using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Pages.Admin.Templates;

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

    /// <summary>Whether this site may install from the MatCMS Store, and the store's templates while its tab is open (<c>?browse=matcms</c>).</summary>
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

    /// <summary>The MatCMS Store's templates; a name the cloud's store also offers comes from there (note instead of a button).</summary>
    private Shared.StoreDialog MatcmsDialog
    {
        get
        {
            var t = HttpContext.RequestServices.GetRequiredService<Localizer>();
            var inCloud = (Catalog?.Templates ?? []).Select(x => StoreItemPackage.Key(x.Name)).ToHashSet();
            return new(
                TitleKey: "templates.matcmsCatalog", IntroKey: "templates.matcmsIntro", RouteName: "key", Handler: "InstallFromMatcms", Tabs: Tabs(),
                Items: (Matcms ?? new()).Select(e => new Shared.StoreItem(
                    Title: e.Name, Sub: e.Name, Description: e.Summary.Length > 0 ? e.Summary : e.Description, RouteValue: e.Key,
                    InstalledVersion: Items.Any(i => StoreItemPackage.Key(i.Name) == e.Key) ? "" : null, Version: e.Version, Accent: e.Accent,
                    Publisher: e.Publisher + (e.PublisherVerified ? " ✓" : ""), PublisherUrl: e.PublisherUrl, Downloads: e.Downloads, Rating: e.Rating,
                    Note: inCloud.Contains(e.Key) ? t["plugins.matcmsViaCloud"] : null,
                    Images: e.Images.Select(img => new Shared.StoreImage(Url.Page("/Admin/Templates/Index", "MatcmsImage", new { key = e.Key, imageId = img.Id })!, img.Caption)).ToList()
                )).ToList(),
                Error: CatalogError);
        }
    }

    private Shared.StoreDialog CloudDialog => new(
        Tabs: Tabs(),
        TitleKey: "templates.cloudCatalog",
        IntroKey: "templates.cloudIntro",
        RouteName: "name",
        Items: (Catalog?.Templates ?? []).Select(t => new Shared.StoreItem(
            Title: t.Name,
            Sub: t.Name,
            Description: t.Description,
            RouteValue: t.Name,
            InstalledVersion: Items.Any(i => i.Name == t.Name) ? "" : null,
            Accent: t.AccentColor)).ToList(),
        Error: CatalogError);

    public async Task<IActionResult> OnPostInstallFromMatcmsAsync(string key)
    {
        var (ok, message) = await _matcms.InstallDesignAsync(MatcmsStoreClient.GroupTemplate, key, HttpContext.RequestAborted);
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

    public async Task<IActionResult> OnPostInstallFromCloudAsync(string name)
    {
        var (ok, message) = await _catalog.InstallTemplateAsync(name, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return RedirectToPage(new { browse = true });
    }

    public List<Template> Items { get; private set; } = new();

    public async Task OnGetAsync(string? browse = null)
    {
        Items = await _db.Templates
            .OrderByDescending(t => t.IsActive).ThenBy(t => t.Name)
            .ToListAsync();
        CloudConnected = await _catalog.IsAvailableAsync();
        StoreAccess = await _matcms.AccessAsync(HttpContext.RequestAborted);
        if (browse == "matcms" && StoreAccess.Allowed)
        {
            Browsing = "matcms";
            (Matcms, CatalogError) = await _matcms.ListAsync(MatcmsStoreClient.GroupTemplate, HttpContext.RequestAborted);
            if (CloudConnected) Catalog = (await _catalog.GetCatalogAsync(HttpContext.RequestAborted)).catalog;
        }
        else if (browse is "true" or "cloud" && CloudConnected)
        {
            Browsing = "cloud";
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
        }
    }

    public async Task<IActionResult> OnPostActivateAsync(int id)
    {
        var target = await _db.Templates.FindAsync(id);
        if (target is null)
        {
            TempData["FlashError"] = "Template nicht gefunden.";
            return RedirectToPage();
        }

        var all = await _db.Templates.ToListAsync();
        foreach (var t in all)
            t.IsActive = t.Id == id;

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Template „{target.Name}“ ist jetzt aktiv.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var target = await _db.Templates.FindAsync(id);
        if (target is null) return RedirectToPage();

        if (target.IsActive)
        {
            TempData["FlashError"] = "Das aktive Template kann nicht gelöscht werden.";
            return RedirectToPage();
        }
        if (await _db.Templates.CountAsync() <= 1)
        {
            TempData["FlashError"] = "Das letzte Template kann nicht gelöscht werden.";
            return RedirectToPage();
        }

        _db.Templates.Remove(target);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Template gelöscht.";
        return RedirectToPage();
    }

    /// <summary>
    /// Takes the JSON that this same editor exports (Templates → öffnen → „Als JSON exportieren"),
    /// which is also the format a cloud profile hands out. The NAME is the identity, exactly as in
    /// the backup restore — importing a name that already exists updates that template rather than
    /// leaving two rows nobody can tell apart.
    /// <para><c>IsActive</c> is not part of it, in either direction: which design a site runs is a
    /// per-site decision and must not travel with a theme.</para>
    /// <para><b>A field the JSON does not contain is not a field the JSON empties.</b> This used to
    /// read every property with a fallback, so pasting the two-line document the input's own
    /// placeholder invites (<c>{ "Name": "…" }</c>) wiped <c>LayoutHtml</c>, <c>CustomCss</c> and the
    /// template parameters off an existing theme — and reported "importiert" while doing it. Refusing
    /// such a document outright would be the worse answer: on a NEW template there is nothing to lose,
    /// and a hand-written partial theme is a legitimate thing to paste. So a missing field leaves what
    /// stands (on a new row: the column default, which is what the fallbacks were), and the flash
    /// NAMES the fields it left alone — the silence was the actual damage here.</para>
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync(string? templateJson)
    {
        using var doc = JsonImport.TryParse(templateJson);
        if (doc is null)
        {
            TempData["FlashError"] = "Bitte gültiges Template-JSON einfügen.";
            return RedirectToPage();
        }

        var root = doc.RootElement;
        if (JsonImport.Text(root, "Name").Trim().Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlt der Name.";
            return RedirectToPage();
        }
        // Pasted by hand: the parameter values in it are meant (keepParamValues: false), unlike a store package's.
        var (row, isNew, missing) = await DesignImport.TemplateAsync(_db, root, HttpContext.RequestAborted, keepParamValues: false);
        // Only worth saying on an update: on a new template a missing field is a default, not a
        // decision that overruled something. On an update it is the difference between "your JSON
        // did this" and "your JSON stayed out of this", and the operator has to be able to tell.
        TempData["Flash"] = isNew || missing.Count == 0
            ? $"Template \"{row.Name}\" importiert."
            : $"Template \"{row.Name}\" aktualisiert. Nicht im JSON enthalten und daher unverändert: {string.Join(", ", missing)}.";
        return RedirectToPage();
    }
}
