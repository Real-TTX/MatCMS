using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

/// <summary>
/// Editor v2 (docs/editor-v2.md): full screen in the admin's look — block tree on the left, preview in
/// the middle, the selected block's fields on the right. The whole page is a DRAFT in the browser; this
/// page only hands over the tree and the schema of every block type. Rendering a draft and saving it
/// stay the classic editor's handlers (<c>Edit?handler=RenderPreview</c> / <c>SaveAll</c>), so there is
/// one way a draft becomes a page — two would sooner or later disagree about what was saved.
/// </summary>
public class EditorModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly Localizer _t;
    private readonly AiService _ai;
    private readonly TranslationService _translator;

    public EditorModel(AppDbContext db, BlockRegistry registry, Localizer t, AiService ai, TranslationService translator)
    {
        _db = db;
        Registry = registry;
        _t = t;
        _ai = ai;
        _translator = translator;
    }

    public bool AiEnabled { get; private set; }
    public bool TranslatorConfigured { get; private set; }
    /// <summary>Language versions of this page (same TranslationGroup), the current one included.</summary>
    public List<PageEntity> Versions { get; private set; } = new();
    /// <summary>Active site languages this page has no version in yet.</summary>
    public List<string> MissingLocales { get; private set; } = new();
    /// <summary>Every page, for the page switcher in the title.</summary>
    public List<PageEntity> AllPages { get; private set; } = new();

    public BlockRegistry Registry { get; }
    public PageEntity Current { get; private set; } = default!;
    public string BlocksJson { get; private set; } = "[]";
    public string TypesJson { get; private set; } = "[]";
    public string PreviewUrl { get; private set; } = "/";
    public string PublicUrl { get; private set; } = "/";
    public List<Template> AllTemplates { get; private set; } = new();
    public List<string> AllRoles { get; private set; } = new();
    public IReadOnlyList<string> SupportedLocales { get; private set; } = new List<string>();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var page = await _db.Pages.Include(p => p.Blocks).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();
        Current = page;

        BlocksJson = JsonSerializer.Serialize(page.Blocks.OrderBy(b => b.SortOrder).Select(b => new
        {
            id = b.Id, blockType = b.BlockType, parentId = b.ParentId, sortOrder = b.SortOrder, dataJson = b.DataJson
        }), Opts);

        // Every block type with what the editor needs to offer and edit it. The schema of ALL types goes
        // along (a few hundred KB at most), so selecting a block never waits for the server.
        var defs = Registry.All.ToList();
        var sources = await BlockSchema.LoadSourcesAsync(_db, defs);
        TypesJson = JsonSerializer.Serialize(defs.Select(d => new
        {
            type = d.Type,
            name = (string)_t[d.Name],
            desc = (string)_t[d.Description],
            cat = d.Category,
            svg = d.Svg,
            allowed = d.AllowedChildren,
            childOnly = d.ChildOnly,
            schema = BlockSchema.For(d, _t, sources)
        }), Opts);

        PublicUrl = SiteContext.LocalizedUrl(page.Locale, page.Slug);
        PreviewUrl = PublicUrl + (PublicUrl.Contains('?') ? "&" : "?") + "editor=1";
        AllTemplates = await _db.Templates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        AllRoles = await _db.SiteRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        // Only the site's ACTIVE languages (i18n.languages), as on the classic editor's settings.
        SupportedLocales = Localizer.ParseActive(
            (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages))?.Value);
        AiEnabled = _ai.Enabled;
        TranslatorConfigured = (await _translator.GetConfigAsync()).IsConfigured;
        Versions = string.IsNullOrEmpty(page.TranslationGroup)
            ? new List<PageEntity> { page }
            : await _db.Pages.AsNoTracking().Where(p => p.TranslationGroup == page.TranslationGroup).OrderBy(p => p.Locale).ToListAsync();
        var used = Versions.Select(v => v.Locale).ToHashSet();
        MissingLocales = SupportedLocales.Where(c => !used.Contains(c)).ToList();
        AllPages = await _db.Pages.AsNoTracking().OrderBy(p => p.Title).ToListAsync();
        return Page();
    }
}
