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
        var name = JsonImport.Text(root, "Name").Trim();
        if (name.Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlt der Name.";
            return RedirectToPage();
        }

        var row = await _db.Templates.FirstOrDefaultAsync(t => t.Name == name);
        var isNew = row is null;
        if (row is null)
        {
            row = new Template { Name = name };
            _db.Templates.Add(row);
        }

        // Every setter below runs only when the document actually carries the property; what it did
        // not carry is collected instead. The fallbacks are still passed for the second case Has()
        // does not cover — a property that IS there but holds the wrong JSON kind.
        var missing = new List<string>();
        void Str(string prop, Action<string> set, string fallback = "")
        {
            if (JsonImport.Has(root, prop)) set(JsonImport.Text(root, prop, fallback));
            else missing.Add(prop);
        }
        // Raw, not Text: these are nested blobs. Hand-written JSON writes them as real objects and
        // arrays, our own export writes them as strings — both have to arrive intact.
        void Blob(string prop, Action<string> set, string fallback)
        {
            if (JsonImport.Has(root, prop)) set(JsonImport.Raw(root, prop, fallback));
            else missing.Add(prop);
        }

        Str("AccentColor", v => row.AccentColor = v, "#de7e11");
        Str("SecondaryColor", v => row.SecondaryColor = v);
        Str("HeadingFont", v => row.HeadingFont = v, "Geologica");
        Str("BodyFont", v => row.BodyFont = v, "Inter");
        Str("ButtonStyle", v => row.ButtonStyle = v, "solid");
        Str("HeadingColor", v => row.HeadingColor = v, "#010101");
        Str("TextColor", v => row.TextColor = v, "#1a1a1a");
        Str("BackgroundColor", v => row.BackgroundColor = v, "#ffffff");
        Str("AltBackground", v => row.AltBackground = v, "#f6f7f9");
        Str("ContainerWidth", v => row.ContainerWidth = v, "1180");
        Str("ButtonRadius", v => row.ButtonRadius = v, "0");
        Str("HeaderBackground", v => row.HeaderBackground = v);
        Str("HeaderTextColor", v => row.HeaderTextColor = v);
        Str("HeaderPadding", v => row.HeaderPadding = v, "16");
        Str("CustomCss", v => row.CustomCss = v);
        Str("CustomJs", v => row.CustomJs = v);
        Str("LayoutHtml", v => row.LayoutHtml = v);
        Blob("MenuMapJson", v => row.MenuMapJson = v, "{}");
        Blob("ParametersJson", v => row.ParametersJson = v, "[]");
        Blob("ParamValuesJson", v => row.ParamValuesJson = v, "{}");
        Blob("PartsJson", v => row.PartsJson = v, "{}");
        if (JsonImport.Has(root, "SchemaVersion")) row.SchemaVersion = JsonImport.Int(root, "SchemaVersion", 1);
        else missing.Add("SchemaVersion");

        await _db.SaveChangesAsync();
        // Only worth saying on an update: on a new template a missing field is a default, not a
        // decision that overruled something. On an update it is the difference between "your JSON
        // did this" and "your JSON stayed out of this", and the operator has to be able to tell.
        TempData["Flash"] = isNew || missing.Count == 0
            ? $"Template \"{row.Name}\" importiert."
            : $"Template \"{row.Name}\" aktualisiert. Nicht im JSON enthalten und daher unverändert: {string.Join(", ", missing)}.";
        return RedirectToPage();
    }
}
