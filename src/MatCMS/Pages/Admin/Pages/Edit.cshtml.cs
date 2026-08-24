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
    private readonly TranslationService _translator;

    public EditModel(AppDbContext db, BlockRegistry registry, Localizer t, TranslationService translator)
    {
        _db = db;
        Registry = registry;
        _t = t;
        _translator = translator;
    }

    /// <summary>True when a machine-translation provider is configured (shows the auto-translate button).</summary>
    public bool TranslatorConfigured { get; private set; }

    public BlockRegistry Registry { get; }
    public PageEntity Current { get; private set; } = default!;

    // Translations of the current page (same TranslationGroup), keyed for the editor's language panel.
    public IReadOnlyList<PageEntity> Translations { get; private set; } = new List<PageEntity>();
    // Existing pages in another locale that could be linked as a translation of this one.
    public IReadOnlyList<PageEntity> LinkCandidates { get; private set; } = new List<PageEntity>();
    // Active content languages (admin setting) — the only ones offered for translating this page.
    public IReadOnlyList<string> SupportedLocales { get; private set; } = new List<string>();
    // Active locales that do not yet have a translation in this group (→ "create translation").
    public IReadOnlyList<string> MissingLocales { get; private set; } = new List<string>();

    /// <summary>One language version of a page, the way the toolbar's page switcher shows it.</summary>
    public record SwitchVersion(int Id, string Title, string Locale, string Url, bool IsPublished, bool IsPrimary);

    /// <summary>A logical page (one translation group) with all its language versions.</summary>
    public record SwitchGroup(string Key, string PrimaryTitle, IReadOnlyList<SwitchVersion> Versions);

    // The whole site's pages for the toolbar switcher — grouped exactly the way the page LIST groups
    // them (translation group = one logical page, languages = its versions). The same slug four times
    // with four different titles is otherwise indistinguishable in a flat list of titles.
    public IReadOnlyList<SwitchGroup> SwitchGroups { get; private set; } = new List<SwitchGroup>();
    // The current page's own group, listed first and on its own: switching the language is by far the
    // most frequent switch on a multilingual site and must not need a search first.
    public SwitchGroup? CurrentGroup { get; private set; }

    // Inline block settings panel (Shopify-style): when ?block=<id> is set.
    public ContentBlock? SelectedBlock { get; private set; }
    public BlockDefinition? SelectedDef { get; private set; }
    public string SchemaJson { get; private set; } = "[]";
    public string CurrentJson { get; private set; } = "{}";
    // The whole page's blocks, seeded to the editor as the client draft for live preview.
    public string BlocksJson { get; private set; } = "[]";

    [BindProperty] public PageMetaInput Meta { get; set; } = new();
    [BindProperty] public string DataJson { get; set; } = "{}";

    public class PageMetaInput
    {
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Locale { get; set; } = Localizer.DefaultCulture;
        public string? MetaDescription { get; set; }
        public string? CustomCss { get; set; }
        public bool IsPublished { get; set; }
    }

    private static readonly JsonSerializerOptions SchemaOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The picker opens by itself after ＋ saved the block that was open — otherwise the
    /// save would land the operator back on a closed dialog they had already asked for.</summary>
    public bool OpenAddPicker { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, int? block, bool add = false)
    {
        OpenAddPicker = add;
        var page = await Load(id);
        if (page is null) return NotFound();

        Current = page;
        TranslatorConfigured = (await _translator.GetConfigAsync()).IsConfigured;
        BlocksJson = JsonSerializer.Serialize(
            page.Blocks.OrderBy(b => b.SortOrder).Select(b => new
            {
                id = b.Id,
                blockType = b.BlockType,
                parentId = b.ParentId,
                sortOrder = b.SortOrder,
                dataJson = b.DataJson
            }), SchemaOpts);
        Meta = new PageMetaInput
        {
            Title = page.Title,
            Slug = page.Slug,
            Locale = page.Locale,
            MetaDescription = page.MetaDescription,
            CustomCss = page.CustomCss,
            IsPublished = page.IsPublished
        };

        await LoadTranslationsAsync(page);
        await LoadSwitcherAsync(page);

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
                    if (SelectedDef.Fields.Any(f => f.OptionsSource == "themeColors"))
                        dynamicSources["themeColors"] = await LoadThemeColorOptionsAsync();

                    // Resolve the localization keys (Label/Options/ItemLabel) into display text
                    // for the current UI culture before handing the schema to the JS editor.
                    var localized = SelectedDef.Fields.Select(f => LocalizeField(f, dynamicSources)).ToList();
                    // Global layout options (width + spacing) apply to every top-level block.
                    if (!SelectedDef.ChildOnly)
                        localized.AddRange(GlobalLayoutFields.Select(f => LocalizeField(f, dynamicSources)));
                    SchemaJson = JsonSerializer.Serialize(localized, SchemaOpts);
                    CurrentJson = string.IsNullOrWhiteSpace(SelectedBlock.DataJson) ? "{}" : SelectedBlock.DataJson;
                }
            }
        }
        return Page();
    }

    /// <param name="close">Set by the ✓ button: save, then leave the block editor. It used to be a
    /// plain link back, which threw the edits away — a tick that discards is the one control nobody
    /// expects to lose work to.</param>
    /// <param name="thenAdd">Set by ＋ while a block is open: same trap, different button. Saves
    /// first and reopens with the picker showing.</param>
    /// <param name="addChildType">Set by the ＋ buttons in the block tree beside the settings: save
    /// this block, then add a child of that type to <paramref name="addChildParent"/> and open it.
    /// The tree used to be read-only, so building a container meant going back to the block list for
    /// every single child.</param>
    public async Task<IActionResult> OnPostSaveBlockAsync(
        int id, int blockId, bool close = false, bool thenAdd = false,
        string? addChildType = null, int? addChildParent = null)
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
        if (close) return RedirectToPage(new { id });
        if (thenAdd) return RedirectToPage(new { id, block = blockId, add = true });
        // Handed to the ONE method that creates blocks, rather than a second copy here: it is the
        // place that checks the parent allows this child type and keeps the sort order contiguous,
        // and it already opens what it created.
        if (!string.IsNullOrWhiteSpace(addChildType) && addChildParent is int parent)
            return await OnPostAddBlockAsync(id, addChildType, parent, null);
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
        page.CustomCss = string.IsNullOrWhiteSpace(Meta.CustomCss) ? null : Meta.CustomCss;
        page.IsPublished = Meta.IsPublished;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Seiteneinstellungen gespeichert.";
        return RedirectToPage(new { id });
    }

    // Creates a translation of this page in another locale (same TranslationGroup), copying its
    // blocks as a starting point. The new page is a draft and opens in the editor.
    /// <summary>
    /// Machine-translates THIS (non-default-locale) version from its default-locale sibling: every
    /// translatable text field of every source block is translated and written into this page's
    /// blocks (matched in tree order, same block type), plus Title/MetaDescription. Overwrites the
    /// texts of this version — intended to produce a fresh MT draft right after "create translation";
    /// the editor/diff remain the place to polish it.
    /// </summary>
    public async Task<IActionResult> OnPostAutoTranslateAsync(int id)
    {
        var page = await Load(id);
        if (page is null) return NotFound();

        if (page.Locale == Localizer.DefaultCulture)
        {
            TempData["FlashError"] = "Die Standardsprache ist die Quelle – bitte eine Übersetzungs-Version öffnen.";
            return RedirectToPage(new { id });
        }
        var source = string.IsNullOrWhiteSpace(page.TranslationGroup) ? null
            : await _db.Pages.Include(p => p.Blocks).AsNoTracking()
                .FirstOrDefaultAsync(p => p.TranslationGroup == page.TranslationGroup
                                       && p.Locale == Localizer.DefaultCulture);
        if (source is null)
        {
            TempData["FlashError"] = "Keine Quellversion in der Standardsprache gefunden.";
            return RedirectToPage(new { id });
        }

        // Machine settings, not content — never send these to the translator (same list as the diff).
        var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "align", "width", "layout", "columns", "imageHeight", "size", "display",
            "showFilter", "source", "perPage", "limit", "form", "tag", "tags",
            "_width", "_spaceTop", "_spaceBottom", "buttonStyle", "icon",
            "imageSide", "bg", "fg", "position", "variant", "style"
        };
        static bool Translatable(string s) =>
            s.Length > 1 && s.Any(char.IsLetter) && !s.StartsWith("/") && !s.StartsWith("http");

        // Collect all texts first (two batches: plain and HTML-bearing), remembering write-back slots.
        var plainTexts = new List<string>(); var htmlTexts = new List<string>();
        var slots = new List<(JsonObject Obj, string Prop, bool Html, int Index)>();
        var roots = new List<(ContentBlock Target, JsonNode Root)>();

        void Collect(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var p in obj.ToList())
                    {
                        if (skipKeys.Contains(p.Key)) continue;
                        if (p.Value is JsonValue v && v.TryGetValue<string>(out var s) && Translatable(s))
                        {
                            var isHtml = s.Contains('<');
                            var list = isHtml ? htmlTexts : plainTexts;
                            slots.Add((obj, p.Key, isHtml, list.Count));
                            list.Add(s);
                        }
                        else Collect(p.Value);
                    }
                    break;
                case JsonArray arr:
                    foreach (var it in arr) Collect(it);
                    break;
            }
        }

        // Match source→target blocks in tree order; only same-type pairs are translated.
        static List<ContentBlock> Flat(ICollection<ContentBlock> blocks)
        {
            var result = new List<ContentBlock>();
            void Add(int? parentId)
            {
                foreach (var b in blocks.Where(x => x.ParentId == parentId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
                { result.Add(b); Add(b.Id); }
            }
            Add(null);
            return result;
        }
        var srcFlat = Flat(source.Blocks);
        var dstFlat = Flat(page.Blocks);
        for (var i = 0; i < Math.Min(srcFlat.Count, dstFlat.Count); i++)
        {
            if (!string.Equals(srcFlat[i].BlockType, dstFlat[i].BlockType, StringComparison.Ordinal)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(srcFlat[i].DataJson) ? "{}" : srcFlat[i].DataJson); }
            catch { continue; }
            if (root is null) continue;
            roots.Add((dstFlat[i], root));
            Collect(root);
        }

        // Page meta rides along in the plain batch.
        var titleIdx = -1; var metaIdx = -1;
        if (Translatable(source.Title)) { titleIdx = plainTexts.Count; plainTexts.Add(source.Title); }
        if (!string.IsNullOrWhiteSpace(source.MetaDescription) && Translatable(source.MetaDescription!))
        { metaIdx = plainTexts.Count; plainTexts.Add(source.MetaDescription!); }

        if (plainTexts.Count == 0 && htmlTexts.Count == 0)
        {
            TempData["FlashError"] = "Keine übersetzbaren Texte gefunden.";
            return RedirectToPage(new { id });
        }

        var (okP, resP, errP) = await _translator.TranslateAsync(plainTexts, source.Locale, page.Locale, html: false);
        if (!okP) { TempData["FlashError"] = $"Übersetzung fehlgeschlagen: {errP}"; return RedirectToPage(new { id }); }
        var (okH, resH, errH) = await _translator.TranslateAsync(htmlTexts, source.Locale, page.Locale, html: true);
        if (!okH) { TempData["FlashError"] = $"Übersetzung fehlgeschlagen: {errH}"; return RedirectToPage(new { id }); }

        // Write back into the source-derived JSON trees, then persist them as THIS page's block data.
        foreach (var (obj, prop, isHtml, index) in slots)
            obj[prop] = isHtml ? resH[index] : resP[index];
        foreach (var (target, root) in roots)
            target.DataJson = root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (titleIdx >= 0) page.Title = resP[titleIdx];
        if (metaIdx >= 0) page.MetaDescription = resP[metaIdx];
        page.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Automatisch übersetzt: {slots.Count} Feld(er) aus {source.Locale.ToUpperInvariant()} → {page.Locale.ToUpperInvariant()}.";
        return RedirectToPage(new { id });
    }

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
            MetaDescription = page.MetaDescription
        };
        _db.Pages.Add(translation);
        await _db.SaveChangesAsync();

        // Clone the block tree, PRESERVING the parent/child hierarchy (nested cards/columns/groups):
        // pass 1 creates every block, pass 2 remaps each ParentId to the newly-created parent.
        var map = new Dictionary<int, ContentBlock>();
        foreach (var b in page.Blocks.OrderBy(b => b.SortOrder))
        {
            var nb = new ContentBlock { PageId = translation.Id, BlockType = b.BlockType, SortOrder = b.SortOrder, DataJson = b.DataJson };
            _db.ContentBlocks.Add(nb);
            map[b.Id] = nb;
        }
        await _db.SaveChangesAsync();
        foreach (var b in page.Blocks)
            if (b.ParentId is int pid && map.TryGetValue(pid, out var parent) && map.TryGetValue(b.Id, out var child))
                child.ParentId = parent.Id;
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

    public async Task<IActionResult> OnPostAddBlockAsync(int id, string type, int? parentId, int? position)
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

        // Insert at the requested index (from an "insert between blocks" zone), else append.
        var siblings = page.Blocks.Where(b => b.ParentId == parentId).OrderBy(b => b.SortOrder).ToList();
        var block = new ContentBlock { PageId = id, ParentId = parentId, BlockType = def.Type, DataJson = "{}" };
        var insertAt = position is int p && p >= 0 && p <= siblings.Count ? p : siblings.Count;
        siblings.Insert(insertAt, block);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i; // contiguous reindex
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

    // ===== Live WYSIWYG: draft preview (no DB write) + save-the-whole-tree =====
    // The editor keeps the whole block tree as a client-side draft. New blocks use NEGATIVE ids
    // (assigned in JS); existing blocks keep their real positive ids. Nothing persists until "Save".

    /// <summary>One block in the editor's client draft.</summary>
    public class DraftBlockInput
    {
        public int Id { get; set; }            // real (positive) or new (negative, from JS)
        public string BlockType { get; set; } = "";
        public int? ParentId { get; set; }     // container parent (may be negative if the parent is new)
        public int SortOrder { get; set; }
        public string DataJson { get; set; } = "{}";
    }

    [BindProperty] public string? Draft { get; set; }

    private static readonly JsonSerializerOptions DraftOpts = new() { PropertyNameCaseInsensitive = true };

    private List<DraftBlockInput> ParseDraft()
    {
        if (string.IsNullOrWhiteSpace(Draft)) return new();
        try { return JsonSerializer.Deserialize<List<DraftBlockInput>>(Draft, DraftOpts) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Renders the draft block tree (editor mode) WITHOUT touching the DB — for the live preview.</summary>
    public IActionResult OnPostRenderPreview(int id)
    {
        var blocks = ParseDraft().Select(d => new ContentBlock
        {
            Id = d.Id,
            PageId = id,
            ParentId = d.ParentId,
            BlockType = d.BlockType ?? "",
            SortOrder = d.SortOrder,
            DataJson = string.IsNullOrWhiteSpace(d.DataJson) ? "{}" : d.DataJson
        }).ToList();
        return Partial("_BlockList", new BlockListModel(blocks, Registry, true));
    }

    /// <summary>Reconciles the whole draft into the page's ContentBlock rows in one transaction.</summary>
    public async Task<IActionResult> OnPostSaveAllAsync(int id)
    {
        var page = await _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();

        var draft = ParseDraft();
        // Only accept known block types + valid JSON data.
        draft = draft.Where(d => Registry.Get(d.BlockType) is not null && IsJsonObject(d.DataJson)).ToList();

        var existing = page.Blocks.ToDictionary(b => b.Id);
        var draftIds = draft.Where(d => d.Id > 0).Select(d => d.Id).ToHashSet();

        // 1) Delete rows that are no longer in the draft.
        foreach (var b in page.Blocks.Where(b => !draftIds.Contains(b.Id)).ToList())
            _db.ContentBlocks.Remove(b);

        // 2) Upsert; set positive parents now, defer new (negative) parents to pass 2. Keyed by the
        //    draft id (positive real / negative new) so pass 2 can map a negative parent to its entity.
        var byDraftId = new Dictionary<int, ContentBlock>();
        foreach (var d in draft)
        {
            var e = (d.Id > 0 && existing.TryGetValue(d.Id, out var ex)) ? ex : new ContentBlock { PageId = id };
            if (e.Id == 0) _db.ContentBlocks.Add(e);
            e.BlockType = d.BlockType;
            e.SortOrder = d.SortOrder;
            e.DataJson = string.IsNullOrWhiteSpace(d.DataJson) ? "{}" : d.DataJson;
            e.ParentId = (d.ParentId is int pp && pp > 0) ? pp : null;
            byDraftId[d.Id] = e;
        }
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(); // new blocks now have real ids

        // Pass 2: wire up children whose parent was a NEW block (negative draft id) → its real id.
        var changed = false;
        foreach (var d in draft)
        {
            if (d.ParentId is int np && np < 0
                && byDraftId.TryGetValue(np, out var parentEntity)
                && byDraftId.TryGetValue(d.Id, out var childEntity)
                && childEntity.ParentId != parentEntity.Id)
            {
                childEntity.ParentId = parentEntity.Id;
                changed = true;
            }
        }
        if (changed) await _db.SaveChangesAsync();

        return new JsonResult(new { ok = true });
    }

    private static bool IsJsonObject(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject; }
        catch { return false; }
    }

    // Shared layout options appended to every top-level block (Shopify-style). Labels are literal
    // German (the localizer falls back to the given text when there's no matching key).
    private static readonly BlockField[] GlobalLayoutFields =
    [
        new BlockField { Id = "_width", Label = "Breite", Type = FieldType.Select, Default = "",
            Options = [ new("", "Normal"), new("narrow", "Schmal"), new("full", "Volle Breite") ] },
        new BlockField { Id = "_spaceTop", Label = "Abstand oben", Type = FieldType.Select, Default = "",
            Options = [ new("", "Standard"), new("s", "Klein"), new("m", "Mittel"), new("l", "Groß") ] },
        new BlockField { Id = "_spaceBottom", Label = "Abstand unten", Type = FieldType.Select, Default = "",
            Options = [ new("", "Standard"), new("s", "Klein"), new("m", "Mittel"), new("l", "Groß") ] },
        // Per-block custom CSS (advanced). Rendered scoped under this block's own `.blk-<id>` wrapper
        // via native CSS nesting, so rules never leak to other blocks. Write bare declarations
        // (e.g. `background:#f6f6f6`) or nested selectors with `&` (e.g. `& h2 { color:#289068 }`).
        new BlockField { Id = "_css", Label = "Custom CSS", Type = FieldType.Textarea,
            Placeholder = "background: #f6f6f6;\n& h2 { color: #289068; }",
            Help = "Nur für diesen Block. Gescoped über & (native CSS-Verschachtelung), z. B. „& .btn { … }“." },
    ];

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
            help = f.Help is null ? null : _t[f.Help],   // translation key when one is used; raw text passes through unchanged
            @default = f.Default,
            options,
            showWhen = f.ShowWhenField is null ? null : new { field = f.ShowWhenField, value = f.ShowWhenValue },
            itemFields = f.ItemFields.Select(x => LocalizeField(x, dynamicSources)).ToList(),
            itemLabel = _t[f.ItemLabel]
        };
    }

    /// <summary>Active theme's palette as selectable colour options (value = hex), "Standard" first.</summary>
    private async Task<List<SelectOption>> LoadThemeColorOptionsAsync()
    {
        var t = await _db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.IsActive)
                ?? await _db.Templates.AsNoTracking().FirstOrDefaultAsync();
        var opts = new List<SelectOption> { new("", "Standard") };
        if (t is null) return opts;
        void Add(string? val, string label) { if (!string.IsNullOrWhiteSpace(val)) opts.Add(new SelectOption(val!, label)); }
        Add(t.AccentColor, "Akzent");
        Add(t.SecondaryColor, "Sekundär");
        Add(t.HeadingColor, "Überschrift");
        Add(t.TextColor, "Text");
        Add(t.BackgroundColor, "Hintergrund");
        Add(t.AltBackground, "Alt-Hintergrund");
        Add("#ffffff", "Weiß");
        Add("#111111", "Dunkel");
        return opts;
    }

    /// <summary>Distinct media-library tags for the gallery tag picker, with an "all media" entry first.</summary>
    private async Task<List<SelectOption>> LoadMediaTagOptionsAsync()
    {
        var tagStrings = await _db.Media.AsNoTracking().Select(m => m.Tags).ToListAsync();
        // No "all media" entry any more: the only field using this is a multi-select now, where an
        // empty-valued tick box would be a chip that means "ignore the other ticks". Nothing ticked
        // already means all, and the field's help text says so.
        var options = new List<SelectOption>();
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

        // Only the site's ACTIVE languages are offered for translation (admin setting i18n.languages).
        var active = Localizer.ParseActive(
            (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages))?.Value);
        SupportedLocales = active;
        var usedLocales = Translations.Select(p => p.Locale).ToHashSet();
        MissingLocales = active.Where(c => !usedLocales.Contains(c)).ToList();

        LinkCandidates = MissingLocales.Count == 0
            ? new List<PageEntity>()
            : await _db.Pages.AsNoTracking()
                .Where(p => p.Locale != page.Locale
                            && (p.TranslationGroup == null || p.TranslationGroup != group)
                            && MissingLocales.Contains(p.Locale))
                .OrderBy(p => p.Locale).ThenBy(p => p.Title)
                .ToListAsync();
    }

    // Feeds the toolbar's page switcher. Deliberately built like Pages/Index: pages grouped by
    // TranslationGroup, the versions ordered by the site's language order, the default-locale page
    // as the primary. A page without a group is its own singleton. Building the list the same way
    // the page list builds it is the point — the switcher must not sort the site differently from
    // the overview the operator came from.
    private async Task LoadSwitcherAsync(PageEntity page)
    {
        var all = await _db.Pages.AsNoTracking()
            .OrderBy(p => p.NavOrder).ThenBy(p => p.FooterOrder).ThenBy(p => p.Title)
            .ToListAsync();

        static int LocaleRank(string loc)
        {
            var i = Array.IndexOf(Localizer.SupportedCultures.ToArray(), loc);
            return i < 0 ? 99 : i;
        }

        var groups = all
            .GroupBy(p => string.IsNullOrWhiteSpace(p.TranslationGroup) ? $"__single:{p.Id}" : p.TranslationGroup!)
            .Select(g =>
            {
                var ordered = g.OrderBy(p => LocaleRank(p.Locale)).ThenBy(p => p.Id).ToList();
                var primary = ordered.FirstOrDefault(p => p.Locale == Localizer.DefaultCulture) ?? ordered[0];
                var versions = ordered
                    .Select(p => new SwitchVersion(p.Id, p.Title, p.Locale,
                        SiteContext.LocalizedUrl(p.Locale, p.Slug), p.IsPublished, p.Id == primary.Id))
                    .ToList();
                return new SwitchGroup(g.Key, primary.Title, versions);
            })
            .OrderBy(g => g.PrimaryTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        CurrentGroup = groups.FirstOrDefault(g => g.Versions.Any(v => v.Id == page.Id));
        // The current group appears exactly once, at the top — repeating it below would offer the
        // same page twice and make the arrow keys walk over it a second time.
        SwitchGroups = groups.Where(g => g != CurrentGroup).ToList();
    }
}
