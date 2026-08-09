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
    public IndexModel(AppDbContext db, CloudCatalogService catalog)
    {
        _db = db;
        _catalog = catalog;
    }

    /// <summary>Catalogue of the connected cloud, fetched only on demand (?browse=true).</summary>
    public StoreCatalog? Catalog { get; private set; }
    public bool CloudConnected { get; private set; }
    public string? CatalogError { get; private set; }

    /// <summary>The catalogue shaped for the store dialog. Built here rather than in the view so
    /// the "already installed?" lookup stays out of the markup.</summary>
    public Shared.StoreDialog StoreDialog => new(
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

    public async Task<IActionResult> OnPostInstallFromCloudAsync(string name)
    {
        var (ok, message) = await _catalog.InstallTemplateAsync(name, HttpContext.RequestAborted);
        TempData[ok ? "Flash" : "FlashError"] = message;
        return RedirectToPage(new { browse = true });
    }

    public List<Template> Items { get; private set; } = new();

    public async Task OnGetAsync(bool browse = false)
    {
        Items = await _db.Templates
            .OrderByDescending(t => t.IsActive).ThenBy(t => t.Name)
            .ToListAsync();
        CloudConnected = await _catalog.IsAvailableAsync();
        if (browse && CloudConnected)
            (Catalog, CatalogError) = await _catalog.GetCatalogAsync(HttpContext.RequestAborted);
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
        var name = JsonImport.Text(root, "Name").Trim();
        if (name.Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlt der Name.";
            return RedirectToPage();
        }

        var row = await _db.Templates.FirstOrDefaultAsync(t => t.Name == name);
        if (row is null)
        {
            row = new Template { Name = name };
            _db.Templates.Add(row);
        }
        row.AccentColor = JsonImport.Text(root, "AccentColor", "#de7e11");
        row.SecondaryColor = JsonImport.Text(root, "SecondaryColor");
        row.HeadingFont = JsonImport.Text(root, "HeadingFont", "Geologica");
        row.BodyFont = JsonImport.Text(root, "BodyFont", "Inter");
        row.ButtonStyle = JsonImport.Text(root, "ButtonStyle", "solid");
        row.HeadingColor = JsonImport.Text(root, "HeadingColor", "#010101");
        row.TextColor = JsonImport.Text(root, "TextColor", "#1a1a1a");
        row.BackgroundColor = JsonImport.Text(root, "BackgroundColor", "#ffffff");
        row.AltBackground = JsonImport.Text(root, "AltBackground", "#f6f7f9");
        row.ContainerWidth = JsonImport.Text(root, "ContainerWidth", "1180");
        row.ButtonRadius = JsonImport.Text(root, "ButtonRadius", "0");
        row.HeaderBackground = JsonImport.Text(root, "HeaderBackground");
        row.HeaderTextColor = JsonImport.Text(root, "HeaderTextColor");
        row.HeaderPadding = JsonImport.Text(root, "HeaderPadding", "16");
        row.CustomCss = JsonImport.Text(root, "CustomCss");
        row.CustomJs = JsonImport.Text(root, "CustomJs");
        row.LayoutHtml = JsonImport.Text(root, "LayoutHtml");
        // Raw, not Text: these are nested blobs. Hand-written JSON writes them as real objects and
        // arrays, our own export writes them as strings — both have to arrive intact.
        row.MenuMapJson = JsonImport.Raw(root, "MenuMapJson", "{}");
        row.ParametersJson = JsonImport.Raw(root, "ParametersJson", "[]");
        row.ParamValuesJson = JsonImport.Raw(root, "ParamValuesJson", "{}");
        row.PartsJson = JsonImport.Raw(root, "PartsJson", "{}");
        row.SchemaVersion = JsonImport.Int(root, "SchemaVersion", 1);

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Template \"{row.Name}\" importiert.";
        return RedirectToPage();
    }
}
