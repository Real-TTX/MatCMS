using System.Text.Json;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Templates;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    public EditModel(AppDbContext db) => _db = db;

    [BindProperty] public int Id { get; set; }
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? AccentColor { get; set; }
    [BindProperty] public string? SecondaryColor { get; set; }
    [BindProperty] public string? HeadingColor { get; set; }
    [BindProperty] public string? TextColor { get; set; }
    [BindProperty] public string? BackgroundColor { get; set; }
    [BindProperty] public string? AltBackground { get; set; }
    [BindProperty] public string? HeadingFont { get; set; }
    [BindProperty] public string? BodyFont { get; set; }
    [BindProperty] public string? ButtonStyle { get; set; }
    [BindProperty] public string? ContainerWidth { get; set; }
    [BindProperty] public string? ButtonRadius { get; set; }
    [BindProperty] public string? HeaderBackground { get; set; }
    [BindProperty] public string? HeaderTextColor { get; set; }
    [BindProperty] public string? HeaderPadding { get; set; }
    [BindProperty] public string? CustomCss { get; set; }
    [BindProperty] public string? CustomJs { get; set; }
    [BindProperty] public string? LayoutHtml { get; set; }
    [BindProperty] public string? LoginHtml { get; set; }
    [BindProperty] public string? ParametersJson { get; set; }
    [BindProperty] public Dictionary<string, string> MenuMap { get; set; } = new();
    // Per-page-type layout overrides, keyed by part (currently just "post"). Bound from Parts[post].
    [BindProperty] public Dictionary<string, string> Parts { get; set; } = new();
    public bool IsActive { get; private set; }
    public string? Error { get; private set; }

    // Template FORMAT version of this row + the version the engine currently writes (for the badge).
    public int SchemaVersion { get; private set; }
    public int CurrentSchemaVersion => TemplateSchema.Current;
    /// <summary>Built-in default for the blog-detail part, used to prefill/reset the editor.</summary>
    public string DefaultPostPart => TemplateSchema.DefaultPostPart;
    /// <summary>Built-in default for the maintenance page ("maintenance.html"), used to prefill/reset.</summary>
    public string DefaultMaintenancePart => TemplateSchema.DefaultMaintenancePart;

    // Menu slots referenced by the layout + the menus available to map them to.
    public List<string> MenuSlots { get; private set; } = new();
    public List<Menu> AvailableMenus { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var t = await _db.Templates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        Id = t.Id;
        Name = t.Name;
        AccentColor = t.AccentColor;
        SecondaryColor = t.SecondaryColor;
        HeadingColor = t.HeadingColor;
        TextColor = t.TextColor;
        BackgroundColor = t.BackgroundColor;
        AltBackground = t.AltBackground;
        HeadingFont = t.HeadingFont;
        BodyFont = t.BodyFont;
        ButtonStyle = t.ButtonStyle;
        ContainerWidth = t.ContainerWidth;
        ButtonRadius = t.ButtonRadius;
        HeaderBackground = t.HeaderBackground;
        HeaderTextColor = t.HeaderTextColor;
        HeaderPadding = t.HeaderPadding;
        CustomCss = t.CustomCss;
        CustomJs = t.CustomJs;
        LayoutHtml = t.LayoutHtml;
        LoginHtml = t.LoginHtml;
        ParametersJson = t.ParametersJson;
        IsActive = t.IsActive;
        SchemaVersion = t.SchemaVersion;
        Parts = TemplateSchema.Parse(t.PartsJson);

        await LoadMenuMappingAsync(t.LayoutHtml, LayoutRenderer.ParseMap(t.MenuMapJson));
        return Page();
    }

    /// <summary>
    /// Exports the template as plain JSON. This is the hand-off format for MatCMS.Cloud profiles:
    /// design a theme once on a real site, export it here, paste it into the cloud profile and roll
    /// it out. Deliberately JSON and not the backup ZIP — a template is one row, not an archive.
    /// <para><c>IsActive</c> is excluded: which design a site runs is a per-site decision.</para>
    /// </summary>
    public async Task<IActionResult> OnGetExportJsonAsync(int id)
    {
        var t = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return RedirectToPage("Index");

        var payload = new
        {
            t.Name,
            t.AccentColor, t.SecondaryColor, t.HeadingFont, t.BodyFont, t.ButtonStyle,
            t.HeadingColor, t.TextColor, t.BackgroundColor, t.AltBackground,
            t.ContainerWidth, t.ButtonRadius, t.HeaderBackground, t.HeaderTextColor, t.HeaderPadding,
            t.CustomCss, t.CustomJs, t.LayoutHtml, t.LoginHtml,
            t.MenuMapJson, t.ParametersJson, t.ParamValuesJson,
            t.SchemaVersion, t.PartsJson
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var slug = MatCMS.Services.BackupManager.FileSlug(t.Name);
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", $"template-{slug}.json");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var t = await _db.Templates.FindAsync(Id);
        if (t is null) return RedirectToPage("Index");

        var name = (Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            IsActive = t.IsActive;
            SchemaVersion = t.SchemaVersion;
            Error = "Der Name ist erforderlich.";
            await LoadMenuMappingAsync(LayoutHtml, MenuMap);
            return Page();
        }

        // The code fields are normalized FIRST and their length is reported, not applied. These five
        // pseudo-files are the only place on this page where a value can be too long, and until now
        // "too long" meant TemplateFonts.Code silently returned the first 20 000 characters — a save
        // that answered "Template gespeichert." while cutting a stylesheet off mid-declaration. The
        // limit itself stays (CSS and JS are inlined into every public page, so this is page weight on
        // every request), but it is now something the operator is told rather than something that
        // happens to their work. Checked before anything is written to the row, so a refused save
        // leaves the record exactly as it was and the editor still holds every character they typed.
        var css = TemplateFonts.Code(CustomCss);
        var js = TemplateFonts.Code(CustomJs);
        var layout = TemplateFonts.Code(LayoutHtml);
        var login = TemplateFonts.Code(LoginHtml);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in TemplateSchema.KnownParts)
            parts[key] = TemplateFonts.Code(Parts.TryGetValue(key, out var pv) ? pv : "");

        // The pseudo-file names the editor's tree shows, so the message names what the operator sees.
        var tooLong = new List<string>();
        void Check(string file, string value, int max)
        {
            if (value.Length > max) tooLong.Add($"„{file}“: {value.Length} Zeichen, erlaubt sind {max}");
        }
        Check("styles.css", css, TemplateFonts.MaxInlineCode);
        Check("script.js", js, TemplateFonts.MaxInlineCode);
        Check("body.html", layout, TemplateFonts.MaxLayoutHtml);
        Check("login.html", login, TemplateFonts.MaxLayoutHtml);
        Check("article.html", parts[TemplateSchema.PartPost], TemplateFonts.MaxLayoutHtml);
        Check("maintenance.html", parts[TemplateSchema.PartMaintenance], TemplateFonts.MaxLayoutHtml);
        if (tooLong.Count > 0)
        {
            IsActive = t.IsActive;
            SchemaVersion = t.SchemaVersion;
            Error = "Nicht gespeichert, weil zu lang — " + string.Join("; ", tooLong)
                  + ". Diese Dateien werden in jede Seite der Website eingebettet, darum die Grenze. "
                  + "Der eingegebene Inhalt steht unverändert im Editor und ist nicht abgeschnitten.";
            await LoadMenuMappingAsync(LayoutHtml, MenuMap);
            return Page();
        }

        t.Name = name;
        t.AccentColor = TemplateFonts.NormalizeColor(AccentColor);
        t.SecondaryColor = TemplateFonts.OptionalColor(SecondaryColor);
        t.HeadingColor = TemplateFonts.NormalizeColorOr(HeadingColor, "#010101");
        t.TextColor = TemplateFonts.NormalizeColorOr(TextColor, "#1a1a1a");
        t.BackgroundColor = TemplateFonts.NormalizeColorOr(BackgroundColor, "#ffffff");
        t.AltBackground = TemplateFonts.NormalizeColorOr(AltBackground, "#f6f7f9");
        t.HeadingFont = TemplateFonts.Coerce(HeadingFont, "Geologica");
        t.BodyFont = TemplateFonts.Coerce(BodyFont, "Inter");
        t.ButtonStyle = ButtonStyle == "outline" ? "outline" : "solid";
        t.ContainerWidth = TemplateFonts.Int(ContainerWidth, "1180", 600, 2000);
        t.ButtonRadius = TemplateFonts.Int(ButtonRadius, "0", 0, 60);
        t.HeaderBackground = TemplateFonts.OptionalColor(HeaderBackground);
        t.HeaderTextColor = TemplateFonts.OptionalColor(HeaderTextColor);
        t.HeaderPadding = TemplateFonts.Int(HeaderPadding, "16", 4, 60);
        t.CustomCss = css;
        t.CustomJs = js;
        t.LayoutHtml = layout;
        t.LoginHtml = login;
        t.ParametersJson = SanitizeParameters(ParametersJson);

        // Persist only slots that actually map to an existing menu.
        var menuKeys = await _db.Menus.Select(m => m.Key).ToListAsync();
        var cleanMap = MenuMap
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && menuKeys.Contains(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        t.MenuMapJson = JsonSerializer.Serialize(cleanMap);

        // Per-page-type layout parts: keep only known parts that were actually customised (a part left
        // at its built-in default is stored as "unset" so the row stays clean and default changes in a
        // later engine version still apply). Already trimmed and LF-normalized above, which is what
        // lets the browser's CRLF compare equal to the LF-only default constant.
        var cleanParts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, html) in parts)
        {
            if (!string.IsNullOrWhiteSpace(html) && html.Trim() != TemplateSchema.DefaultFor(key).Trim())
                cleanParts[key] = html;
        }
        t.PartsJson = TemplateSchema.Serialize(cleanParts);
        // A saved template is written in the current format.
        t.SchemaVersion = TemplateSchema.Current;

        await _db.SaveChangesAsync();

        TempData["Flash"] = "Template gespeichert.";
        return RedirectToPage("Index");
    }

    private async Task LoadMenuMappingAsync(string? layoutHtml, Dictionary<string, string> currentMap)
    {
        MenuSlots = LayoutRenderer.ExtractSlots(layoutHtml);
        AvailableMenus = await _db.Menus.OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
        MenuMap = currentMap;
    }

    // Normalize the published parameter schema: clean [{id,label,type,options,default}] with slug ids.
    private static string SanitizeParameters(string? json)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) is not System.Text.Json.Nodes.JsonArray arr)
                return "[]";
            string[] allowed = { "text", "select", "color", "number", "bool" };
            var outArr = new System.Text.Json.Nodes.JsonArray();
            var used = new HashSet<string>();
            foreach (var el in arr)
            {
                var label = el?["label"]?.GetValue<string>()?.Trim() ?? "";
                var id = el?["id"]?.GetValue<string>()?.Trim() ?? "";
                var type = el?["type"]?.GetValue<string>()?.Trim() ?? "text";
                var options = el?["options"]?.GetValue<string>()?.Trim() ?? "";
                var def = el?["default"]?.GetValue<string>() ?? "";
                if (!allowed.Contains(type)) type = "text";
                if (string.IsNullOrEmpty(id)) id = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(label);
                if (string.IsNullOrEmpty(id) || !used.Add(id)) continue;
                outArr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["label"] = string.IsNullOrEmpty(label) ? id : label,
                    ["type"] = type,
                    ["options"] = options,
                    ["default"] = def
                });
            }
            return outArr.ToJsonString();
        }
        catch { return "[]"; }
    }
}
