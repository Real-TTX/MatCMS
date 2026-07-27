using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly Localizer _t;

    public EditModel(AppDbContext db, BlockRegistry registry, Localizer t)
    {
        _db = db;
        Registry = registry;
        _t = t;
    }

    public BlockRegistry Registry { get; }
    public PageEntity Current { get; private set; } = default!;

    // Translations of the current page (same TranslationGroup), keyed for the editor's language panel.
    public IReadOnlyList<PageEntity> Translations { get; private set; } = new List<PageEntity>();
    // Existing pages in another locale that could be linked as a translation of this one.
    public IReadOnlyList<PageEntity> LinkCandidates { get; private set; } = new List<PageEntity>();
    public IReadOnlyList<string> SupportedLocales => Localizer.SupportedCultures;
    // Supported locales that do not yet have a translation in this group (→ "create translation").
    public IReadOnlyList<string> MissingLocales { get; private set; } = new List<string>();

    // Inline block settings panel (Shopify-style): when ?block=<id> is set.
    public ContentBlock? SelectedBlock { get; private set; }
    public BlockDefinition? SelectedDef { get; private set; }
    public string SchemaJson { get; private set; } = "[]";
    public string CurrentJson { get; private set; } = "{}";

    [BindProperty] public PageMetaInput Meta { get; set; } = new();
    [BindProperty] public string DataJson { get; set; } = "{}";

    public class PageMetaInput
    {
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Locale { get; set; } = Localizer.DefaultCulture;
        public string? MetaDescription { get; set; }
        public bool IsPublished { get; set; }
    }

    private static readonly JsonSerializerOptions SchemaOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IActionResult> OnGetAsync(int id, int? block)
    {
        var page = await Load(id);
        if (page is null) return NotFound();

        Current = page;
        Meta = new PageMetaInput
        {
            Title = page.Title,
            Slug = page.Slug,
            Locale = page.Locale,
            MetaDescription = page.MetaDescription,
            IsPublished = page.IsPublished
        };

        await LoadTranslationsAsync(page);

        if (block is int blockId)
        {
            SelectedBlock = page.Blocks.FirstOrDefault(b => b.Id == blockId);
            if (SelectedBlock is not null)
            {
                SelectedDef = Registry.Get(SelectedBlock.BlockType);
                if (SelectedDef is not null)
                {
                    // Dynamic select sources (e.g. the "form" block's form picker) are resolved
                    // from the database at edit time.
                    // Dynamic <select> sources resolved from the DB at edit time (keyed by OptionsSource).
                    var dynamicSources = new Dictionary<string, List<SelectOption>>(StringComparer.Ordinal);
                    if (SelectedDef.Fields.Any(f => f.OptionsSource == "forms"))
                        dynamicSources["forms"] = await _db.Forms.AsNoTracking().OrderBy(f => f.Name)
                            .Select(f => new SelectOption(f.Slug, f.Name)).ToListAsync();
                    if (SelectedDef.Fields.Any(f => f.OptionsSource == "mediaTags"))
                        dynamicSources["mediaTags"] = await LoadMediaTagOptionsAsync();

                    // Resolve the localization keys (Label/Options/ItemLabel) into display text
                    // for the current UI culture before handing the schema to the JS editor.
                    var localized = SelectedDef.Fields.Select(f => LocalizeField(f, dynamicSources)).ToList();
                    SchemaJson = JsonSerializer.Serialize(localized, SchemaOpts);
                    CurrentJson = string.IsNullOrWhiteSpace(SelectedBlock.DataJson) ? "{}" : SelectedBlock.DataJson;
                }
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveBlockAsync(int id, int blockId)
    {
        var block = await _db.ContentBlocks.Include(b => b.Page).FirstOrDefaultAsync(b => b.Id == blockId && b.PageId == id);
        if (block is null) return NotFound();

        var json = string.IsNullOrWhiteSpace(DataJson) ? "{}" : DataJson;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject)
                throw new FormatException();
        }
        catch
        {
            TempData["FlashError"] = "Der Block konnte nicht gespeichert werden (ungültiges Format).";
            return RedirectToPage(new { id, block = blockId });
        }

        block.DataJson = json;
        if (block.Page is not null) block.Page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Block gespeichert.";
        return RedirectToPage(new { id, block = blockId });
    }

    public async Task<IActionResult> OnPostMetaAsync(int id)
    {
        var page = await _db.Pages.FindAsync(id);
        if (page is null) return NotFound();

        var slug = IndexModel.Slugify(string.IsNullOrWhiteSpace(Meta.Slug) ? Meta.Title : Meta.Slug);
        var locale = Localizer.IsSupported(Meta.Locale) ? Meta.Locale : page.Locale;
        if (string.IsNullOrWhiteSpace(Meta.Title) || string.IsNullOrWhiteSpace(slug))
        {
            TempData["FlashError"] = "Titel und Slug dürfen nicht leer sein.";
            return RedirectToPage(new { id });
        }
        if (IndexModel.IsReserved(slug))
        {
            TempData["FlashError"] = $"Der Slug „{slug}“ ist reserviert und kann nicht verwendet werden.";
            return RedirectToPage(new { id });
        }
        // A slug is unique per locale.
        if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == locale && p.Id != id))
        {
            TempData["FlashError"] = $"Der Slug „{slug}“ ist in dieser Sprache bereits vergeben.";
            return RedirectToPage(new { id });
        }

        page.Title = Meta.Title.Trim();
        page.Slug = slug;
        page.Locale = locale;
        if (string.IsNullOrEmpty(page.TranslationGroup))
            page.TranslationGroup = Guid.NewGuid().ToString("N");
        page.MetaDescription = Meta.MetaDescription;
        page.IsPublished = Meta.IsPublished;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Seiteneinstellungen gespeichert.";
        return RedirectToPage(new { id });
    }

    // Creates a translation of this page in another locale (same TranslationGroup), copying its
    // blocks as a starting point. The new page is a draft and opens in the editor.
    public async Task<IActionResult> OnPostCreateTranslationAsync(int id, string locale)
    {
        var page = await _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();

        if (!Localizer.IsSupported(locale) || locale == page.Locale)
        {
            TempData["FlashError"] = "Ungültige Zielsprache.";
            return RedirectToPage(new { id });
        }

        if (string.IsNullOrEmpty(page.TranslationGroup))
        {
            page.TranslationGroup = Guid.NewGuid().ToString("N");
            await _db.SaveChangesAsync();
        }

        // One page per locale within a group.
        if (await _db.Pages.AnyAsync(p => p.TranslationGroup == page.TranslationGroup && p.Locale == locale))
        {
            TempData["FlashError"] = "Für diese Sprache existiert bereits eine Übersetzung.";
            return RedirectToPage(new { id });
        }

        // A slug is unique per locale; keep the same slug if free, otherwise suffix the locale.
        var slug = page.Slug;
        if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == locale))
            slug = $"{page.Slug}-{locale}";

        var translation = new PageEntity
        {
            Title = page.Title,
            Slug = slug,
            Locale = locale,
            TranslationGroup = page.TranslationGroup,
            NavLabel = page.NavLabel,
            IsPublished = false,
            ShowInNav = page.ShowInNav,
            ShowInFooter = page.ShowInFooter,
            NavOrder = page.NavOrder,
            FooterOrder = page.FooterOrder,
            MetaDescription = page.MetaDescription,
            Blocks = page.Blocks.OrderBy(b => b.SortOrder).Select(b => new ContentBlock
            {
                BlockType = b.BlockType,
                SortOrder = b.SortOrder,
                DataJson = b.DataJson
            }).ToList()
        };
        _db.Pages.Add(translation);
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Übersetzung erstellt.";
        return RedirectToPage(new { id = translation.Id });
    }

    // Links an existing page (in another locale) as a translation of this one by merging it into
    // this page's TranslationGroup.
    public async Task<IActionResult> OnPostLinkTranslationAsync(int id, int targetId)
    {
        var page = await _db.Pages.FindAsync(id);
        var target = await _db.Pages.FindAsync(targetId);
        if (page is null || target is null) return NotFound();

        if (target.Locale == page.Locale)
        {
            TempData["FlashError"] = "Eine Übersetzung muss eine andere Sprache haben.";
            return RedirectToPage(new { id });
        }

        if (string.IsNullOrEmpty(page.TranslationGroup))
            page.TranslationGroup = Guid.NewGuid().ToString("N");

        if (await _db.Pages.AnyAsync(p => p.TranslationGroup == page.TranslationGroup
                                          && p.Locale == target.Locale && p.Id != target.Id))
        {
            TempData["FlashError"] = "Für diese Sprache ist bereits eine Übersetzung verknüpft.";
            return RedirectToPage(new { id });
        }

        target.TranslationGroup = page.TranslationGroup;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Übersetzung verknüpft.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddBlockAsync(int id, string type, int? parentId)
    {
        var def = Registry.Get(type);
        var page = await _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null || def is null) return NotFound();

        // When adding a child, validate the parent exists and allows this child type.
        if (parentId is int pid)
        {
            var parent = page.Blocks.FirstOrDefault(b => b.Id == pid);
            var parentDef = parent is null ? null : Registry.Get(parent.BlockType);
            if (parent is null || parentDef is null || !parentDef.AllowedChildren.Contains(type))
                return BadRequest();
        }
        else if (def.ChildOnly)
        {
            return BadRequest(); // child-only blocks can't be added at the top level
        }

        var siblings = page.Blocks.Where(b => b.ParentId == parentId).ToList();
        var order = siblings.Count == 0 ? 0 : siblings.Max(b => b.SortOrder) + 1;
        var block = new ContentBlock { PageId = id, ParentId = parentId, BlockType = def.Type, SortOrder = order, DataJson = "{}" };
        _db.ContentBlocks.Add(block);
        await _db.SaveChangesAsync();

        // Open the new block's settings inline in the editor.
        return RedirectToPage(new { id, block = block.Id });
    }

    // New order arrives as a sequence of block ids (drag & drop in the editor).
    public async Task<IActionResult> OnPostReorderAsync(int id, int[] order)
    {
        var page = await _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();

        if (order is { Length: > 0 })
        {
            var pos = 0;
            foreach (var blockId in order)
            {
                var block = page.Blocks.FirstOrDefault(b => b.Id == blockId);
                if (block is not null) block.SortOrder = pos++;
            }
            await _db.SaveChangesAsync();
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteBlockAsync(int id, int blockId)
    {
        var block = await _db.ContentBlocks.FirstOrDefaultAsync(b => b.Id == blockId && b.PageId == id);
        if (block is not null)
        {
            _db.ContentBlocks.Remove(block);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Block gelöscht.";
        }
        return RedirectToPage(new { id });
    }

    // Produces a JSON-friendly copy of a field with all localization keys resolved to text.
    private object LocalizeField(BlockField f) => LocalizeField(f, null);

    private object LocalizeField(BlockField f, IReadOnlyDictionary<string, List<SelectOption>>? dynamicSources)
    {
        // A dynamic source (e.g. "forms", "mediaTags") replaces the static options; its labels are
        // already display text and must not be run through the localizer.
        var options = (f.OptionsSource is not null && dynamicSources is not null
                       && dynamicSources.TryGetValue(f.OptionsSource, out var dyn))
            ? dyn.Select(o => new { value = o.Value, label = o.Label }).ToList()
            : f.Options.Select(o => new { value = o.Value, label = _t[o.Label] }).ToList();

        return new
        {
            id = f.Id,
            label = _t[f.Label],
            type = f.Type,
            placeholder = f.Placeholder,
            help = f.Help,
            @default = f.Default,
            options,
            itemFields = f.ItemFields.Select(x => LocalizeField(x, dynamicSources)).ToList(),
            itemLabel = _t[f.ItemLabel]
        };
    }

    /// <summary>Distinct media-library tags for the gallery tag picker, with an "all media" entry first.</summary>
    private async Task<List<SelectOption>> LoadMediaTagOptionsAsync()
    {
        var tagStrings = await _db.Media.AsNoTracking().Select(m => m.Tags).ToListAsync();
        var options = new List<SelectOption> { new("", "Alle Medien") };
        options.AddRange(tagStrings
            .SelectMany(TagUtil.Split)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(t => new SelectOption(t, t)));
        return options;
    }

    public string BlockSummary(ContentBlock b)
    {
        var data = new BlockData(b.DataJson);
        foreach (var key in new[] { "heading", "title", "text", "body", "subheading" })
        {
            var v = data.Str(key);
            if (!string.IsNullOrWhiteSpace(v))
                return Truncate(StripHtml(v), 60);
        }
        return _t["editor.blockEmpty"];
    }

    private static string StripHtml(string s) =>
        Regex.Replace(s, "<.*?>", " ").Replace("\n", " ").Replace("  ", " ").Trim();

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : string.Concat(s.AsSpan(0, n).TrimEnd(), "…");

    private Task<PageEntity?> Load(int id) =>
        _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);

    // Populates the language panel: existing translations, locales still missing a translation, and
    // candidate pages (other locales, not yet grouped here) that could be linked as translations.
    private async Task LoadTranslationsAsync(PageEntity page)
    {
        var group = page.TranslationGroup;
        Translations = string.IsNullOrEmpty(group)
            ? new List<PageEntity> { page }
            : await _db.Pages.AsNoTracking()
                .Where(p => p.TranslationGroup == group)
                .OrderBy(p => p.Locale)
                .ToListAsync();

        var usedLocales = Translations.Select(p => p.Locale).ToHashSet();
        MissingLocales = Localizer.SupportedCultures.Where(c => !usedLocales.Contains(c)).ToList();

        LinkCandidates = MissingLocales.Count == 0
            ? new List<PageEntity>()
            : await _db.Pages.AsNoTracking()
                .Where(p => p.Locale != page.Locale
                            && (p.TranslationGroup == null || p.TranslationGroup != group)
                            && MissingLocales.Contains(p.Locale))
                .OrderBy(p => p.Locale).ThenBy(p => p.Title)
                .ToListAsync();
    }
}
