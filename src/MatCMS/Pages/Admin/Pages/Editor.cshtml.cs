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

/// <summary>
/// The page editor (docs/editor-v2.md): full screen in the admin's look — block tree on the left, preview in
/// the middle, the selected block's fields (or the page's settings) on the right. The whole page is a DRAFT in
/// the browser; this page hands over the tree and the schema of every block type, renders a draft without
/// saving it (<c>RenderPreview</c>) and reconciles the whole tree in one go (<c>SaveAll</c>). It is the ONLY
/// page editor: the classic two-pane editor (<c>/admin/pages/edit/{id}</c>) was removed and its address
/// redirects here (<see cref="EditModel"/>).
/// </summary>
public class EditorModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly Localizer _t;
    private readonly AiService _ai;
    private readonly TranslationService _translator;

    private readonly MatCMS.Services.Plugins.PluginPageService _pluginPages;

    public EditorModel(AppDbContext db, BlockRegistry registry, Localizer t, AiService ai, TranslationService translator, MatCMS.Services.Plugins.PluginPageService pluginPages)
    {
        _pluginPages = pluginPages;
        _db = db;
        Registry = registry;
        _t = t;
        _ai = ai;
        _translator = translator;
    }

    /// <summary>One language version of a page, the way the title's page switcher shows it.</summary>
    public record SwitchVersion(int Id, string Title, string Locale, string Url, bool IsPublished, bool IsPrimary);

    /// <summary>A logical page (one translation group) with all its language versions.</summary>
    public record SwitchGroup(string Key, string PrimaryTitle, IReadOnlyList<SwitchVersion> Versions);

    public bool AiEnabled { get; private set; }
    public bool TranslatorConfigured { get; private set; }
    /// <summary>Language versions of this page (same TranslationGroup), the current one included.</summary>
    public List<PageEntity> Versions { get; private set; } = new();
    /// <summary>Active site languages this page has no version in yet.</summary>
    public List<string> MissingLocales { get; private set; } = new();
    /// <summary>Existing pages in a missing language that could be linked as a translation of this one.</summary>
    public List<PageEntity> LinkCandidates { get; private set; } = new();
    /// <summary>The page switcher's data: the current page's own group (listed first) and every other page.</summary>
    public SwitchGroup? CurrentGroup { get; private set; }
    public IReadOnlyList<SwitchGroup> SwitchGroups { get; private set; } = new List<SwitchGroup>();

    public BlockRegistry Registry { get; }
    public PageEntity Current { get; private set; } = default!;
    public string BlocksJson { get; private set; } = "[]";
    public string TypesJson { get; private set; } = "[]";
    public string PreviewUrl { get; private set; } = "/";
    /// <summary>A plugin's page: the variables it offers (complete name, label, holds HTML) and whether the plugin has an example to preview with. Empty/false on an ordinary page.</summary>
    public IReadOnlyList<(string Name, string Label, bool Html)> PluginVariables { get; private set; } = Array.Empty<(string, string, bool)>();
    public bool PluginHasExample { get; private set; }
    public string PublicUrl { get; private set; } = "/";
    public List<Template> AllTemplates { get; private set; } = new();
    public List<string> AllRoles { get; private set; } = new();
    public IReadOnlyList<string> SupportedLocales { get; private set; } = new List<string>();
    /// <summary>Parameter ids the template this page renders with declares (its own, else the active one) —
    /// shown under the parameters field so the operator knows which keys the JSON accepts.</summary>
    public IReadOnlyList<string> DeclaredParams { get; private set; } = Array.Empty<string>();

    [BindProperty] public PageMetaInput Meta { get; set; } = new();
    [BindProperty] public string? Draft { get; set; }

    public class PageMetaInput
    {
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string Locale { get; set; } = Localizer.DefaultCulture;
        public string? MetaDescription { get; set; }
        public string? CustomCss { get; set; }
        public bool IsPublished { get; set; }
        public PageAccess Access { get; set; } = PageAccess.Public;
        public string? RequiredRole { get; set; }
        public int? TemplateId { get; set; }
        public string? TemplateParamsJson { get; set; }
    }

    /// <summary>One block in the editor's client draft.</summary>
    public class DraftBlockInput
    {
        public int Id { get; set; }            // real (positive) or new (negative, from JS)
        public string BlockType { get; set; } = "";
        public int? ParentId { get; set; }     // container parent (may be negative if the parent is new)
        public int SortOrder { get; set; }
        public string DataJson { get; set; } = "{}";
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions DraftOpts = new() { PropertyNameCaseInsensitive = true };

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
        var sources = await BlockSchema.LoadSourcesAsync(_db, defs, HttpContext.RequestServices.GetRequiredService<MatCMS.Services.Plugins.PluginContributions>(), HttpContext.RequestServices);
        TypesJson = JsonSerializer.Serialize(defs.Select(d => new
        {
            type = d.Type,
            name = (string)_t[d.Name],
            desc = (string)_t[d.Description],
            cat = d.Category,
            svg = d.Svg,
            allowed = d.AllowedChildren,
            childOnly = d.ChildOnly,
            schema = BlockSchema.For(d, _t, sources, scoped: page.OwnerPlugin is not null)
        }), Opts);

        PublicUrl = SiteContext.LocalizedUrl(page.Locale, page.Slug);
        // A plugin's page is drawn by the plugin's own route: the editor previews it at a real example of that route (a product), not at the page's own address.
        if (page.OwnerPlugin is not null)
        {
            PluginVariables = _pluginPages.VariablesOf(page, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            var example = await _pluginPages.PreviewUrlAsync(page);
            PluginHasExample = example is not null;
            PublicUrl = example ?? "";
            PreviewUrl = (example ?? PublicUrl).Length > 0 ? example! + (example!.Contains('?') ? "&" : "?") + "editor=1" : SiteContext.LocalizedUrl(page.Locale, page.Slug) + "?editor=1";
        }
        else
        {
            PreviewUrl = PublicUrl + (PublicUrl.Contains('?') ? "&" : "?") + "editor=1";
        }
        AllTemplates = await _db.Templates.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        AllRoles = await _db.SiteRoles.AsNoTracking().OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        var paramTpl = page.TemplateId is int ptid ? AllTemplates.FirstOrDefault(t => t.Id == ptid) : AllTemplates.FirstOrDefault(t => t.IsActive);
        DeclaredParams = paramTpl is null ? Array.Empty<string>() : TemplateParams.Schema(paramTpl.ParametersJson).Select(p => p.Id).ToList();
        // Only the site's ACTIVE languages (i18n.languages) are offered.
        SupportedLocales = Localizer.ParseActive(
            (await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages))?.Value);
        AiEnabled = _ai.Enabled;
        TranslatorConfigured = (await _translator.GetConfigAsync()).IsConfigured;
        Versions = string.IsNullOrEmpty(page.TranslationGroup)
            ? new List<PageEntity> { page }
            : await _db.Pages.AsNoTracking().Where(p => p.TranslationGroup == page.TranslationGroup).OrderBy(p => p.Locale).ToListAsync();
        var used = Versions.Select(v => v.Locale).ToHashSet();
        MissingLocales = SupportedLocales.Where(c => !used.Contains(c)).ToList();
        // A plugin's page has one version per language made by the plugin — linking pages into it makes no sense.
        if (MissingLocales.Count > 0 && page.OwnerPlugin is null)
        {
            var group = page.TranslationGroup;
            LinkCandidates = await _db.Pages.AsNoTracking()
                .Where(p => p.Locale != page.Locale && p.OwnerPlugin == null
                            && (p.TranslationGroup == null || p.TranslationGroup != group)
                            && MissingLocales.Contains(p.Locale))
                .OrderBy(p => p.Locale).ThenBy(p => p.Title)
                .ToListAsync();
        }
        (CurrentGroup, SwitchGroups) = await BuildSwitcherAsync(_db, page);
        return Page();
    }

    // ===== Page settings =====

    /// <summary>Saves the page's settings. The editor posts them with <c>back=json</c> together with the blocks
    /// (one "Speichern") and stays on the page: it needs the outcome as data, including the address the page now
    /// lives at.</summary>
    public async Task<IActionResult> OnPostMetaAsync(int id, string? back = null)
    {
        var page = await _db.Pages.FindAsync(id);
        if (page is null) return NotFound();

        // A plugin's page keeps its address and its language (the plugin's route draws it), is always for everybody, and uses the site's template.
        var owned = page.OwnerPlugin is not null;
        var slug = owned ? page.Slug : IndexModel.Slugify(string.IsNullOrWhiteSpace(Meta.Slug) ? Meta.Title : Meta.Slug);
        var locale = owned ? page.Locale : Localizer.IsSupported(Meta.Locale) ? Meta.Locale : page.Locale;
        if (string.IsNullOrWhiteSpace(Meta.Title) || string.IsNullOrWhiteSpace(slug))
            return MetaFail(id, back, "Titel und Slug dürfen nicht leer sein.");
        if (IndexModel.IsReserved(slug))
            return MetaFail(id, back, $"Der Slug „{slug}“ ist reserviert und kann nicht verwendet werden.");
        // A slug is unique per locale.
        if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == locale && p.Id != id))
            return MetaFail(id, back, $"Der Slug „{slug}“ ist in dieser Sprache bereits vergeben.");

        page.Title = Meta.Title.Trim();
        page.Slug = slug;
        page.Locale = locale;
        if (string.IsNullOrEmpty(page.TranslationGroup))
            page.TranslationGroup = Guid.NewGuid().ToString("N");
        page.MetaDescription = Meta.MetaDescription;
        page.CustomCss = string.IsNullOrWhiteSpace(Meta.CustomCss) ? null : Meta.CustomCss;
        page.IsPublished = Meta.IsPublished;
        if (!owned)
        {
            page.Access = Meta.Access;
            // A required role only means anything for a members-only page; drop it otherwise so a page
            // flipped back to public does not keep a stale gate.
            page.RequiredRole = Meta.Access == PageAccess.Members && !string.IsNullOrWhiteSpace(Meta.RequiredRole)
                ? Meta.RequiredRole!.Trim() : null;
            // Per-page template: 0/empty means "use the active template".
            page.TemplateId = Meta.TemplateId is > 0 ? Meta.TemplateId : null;
            // Per-page template parameter overrides (JSON object). Kept only if it parses to an object, so a
            // typo can't poison rendering; blank clears it.
            page.TemplateParamsJson = NormalizeParamsJson(Meta.TemplateParamsJson);
        }
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await MatCMS.Services.Plugins.PluginHostEvents.RaiseAsync(HttpContext.RequestServices, MatCMS.Plugin.HostEvents.PageSaved,
            new JsonObject { ["id"] = page.Id, ["slug"] = page.Slug, ["locale"] = page.Locale, ["published"] = page.IsPublished }, HttpContext.RequestAborted);

        if (back == "json")
            return new JsonResult(new { ok = true, title = page.Title, slug = page.Slug, published = page.IsPublished,
                url = SiteContext.LocalizedUrl(page.Locale, page.Slug) });
        TempData["Flash"] = "Seiteneinstellungen gespeichert.";
        return RedirectToPage(new { id });
    }

    private IActionResult MetaFail(int id, string? back, string error)
    {
        if (back == "json") return new JsonResult(new { ok = false, error });
        TempData["FlashError"] = error;
        return RedirectToPage(new { id });
    }

    /// <summary>Keeps a per-page template-params value only if it is a JSON object; anything else (blank,
    /// a typo, a non-object) becomes null so it can never break template rendering.</summary>
    private static string? NormalizeParamsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? json.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>SEO: propose a meta description for THIS page, summarised from its SAVED block content
    /// (through the site's AI connection). Returns { ok, proposed } without saving — the dialog shows a
    /// before/after and writes the accepted text into the settings field, saved with the normal "Speichern".</summary>
    public async Task<IActionResult> OnPostAiMetaDescriptionAsync(int id, string? instruction)
    {
        if (!_ai.Enabled)
            return new JsonResult(new { ok = false, error = "KI ist für diese Website nicht aktiviert." });
        var page = await Load(id);
        if (page is null) return new JsonResult(new { ok = false, error = "Seite nicht gefunden." });

        var content = GatherPageText(page);
        if (content.Length < 10)
            return new JsonResult(new { ok = false, error = "Die Seite hat noch zu wenig Inhalt. Bitte erst Inhalt speichern." });

        var (ok, text, error) = await _ai.SummarizeForSeoAsync("meta", page.Title, content, instruction, HttpContext.RequestAborted);
        if (!ok) return new JsonResult(new { ok = false, error = error ?? "KI-Aufruf fehlgeschlagen." });
        var proposed = (text ?? "").Trim();
        return proposed.Length == 0
            ? new JsonResult(new { ok = false, error = "Die KI hatte keinen Vorschlag." })
            : new JsonResult(new { ok = true, proposed });
    }

    /// <summary>Concatenated prose of a page's blocks (machine fields skipped, HTML stripped), bounded —
    /// the content a summary reads. Same machine-vs-content split as auto-translate.</summary>
    private static string GatherPageText(PageEntity page)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "align", "width", "layout", "columns", "imageHeight", "size", "display", "showFilter",
            "source", "perPage", "limit", "form", "tag", "tags", "_width", "_spaceTop", "_spaceBottom",
            "buttonStyle", "icon", "imageSide", "bg", "fg", "position", "variant", "style", "_css", "_hidden"
        };
        static bool Prose(string s) => s.Length > 1 && s.Any(char.IsLetter) && !s.StartsWith("/") && !s.StartsWith("http");
        var sb = new System.Text.StringBuilder();
        void Walk(JsonNode? node)
        {
            if (sb.Length > 5000) return;
            switch (node)
            {
                case JsonObject obj:
                    foreach (var p in obj)
                    {
                        if (skip.Contains(p.Key)) continue;
                        if (p.Value is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            var t = StripHtml(s);
                            if (Prose(t)) { sb.Append(t); sb.Append(' '); }
                        }
                        else Walk(p.Value);
                    }
                    break;
                case JsonArray arr:
                    foreach (var it in arr) Walk(it);
                    break;
            }
        }
        foreach (var b in page.Blocks.OrderBy(b => b.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(b.DataJson)) continue;
            try { Walk(JsonNode.Parse(b.DataJson)); } catch { /* skip malformed */ }
        }
        return sb.ToString().Trim();
    }

    private static string StripHtml(string s) =>
        Regex.Replace(s, "<.*?>", " ").Replace("\n", " ").Replace("  ", " ").Trim();

    // ===== Language versions =====

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

    /// <summary>Creates the version of this page in another language: a copy of the block tree (hierarchy kept),
    /// unpublished, and opens it.</summary>
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

    /// <summary>Links an existing page (in another locale) as a translation of this one by merging it into
    /// this page's TranslationGroup.</summary>
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
        if (page.OwnerPlugin is not null || target.OwnerPlugin is not null)
        {
            TempData["FlashError"] = "Seiten eines Plugins lassen sich nicht verknüpfen.";
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

    // ===== Blocks: draft preview (no DB write) + save-the-whole-tree =====
    // The editor keeps the whole block tree as a client-side draft. New blocks use NEGATIVE ids
    // (assigned in JS); existing blocks keep their real positive ids. Nothing persists until "Speichern".

    private List<DraftBlockInput> ParseDraft()
    {
        if (string.IsNullOrWhiteSpace(Draft)) return new();
        try { return JsonSerializer.Deserialize<List<DraftBlockInput>>(Draft, DraftOpts) ?? new(); }
        catch { return new(); }
    }

    /// <summary>"Back to default" for a plugin's page: its blocks are replaced by the plugin's starting layout.</summary>
    public async Task<IActionResult> OnPostResetPluginAsync(int id)
    {
        var page = await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (page?.OwnerPlugin is null) return NotFound();
        if (await _pluginPages.ResetAsync(page)) TempData["Flash"] = _t["pages.pluginReset.done"].ToString();
        else TempData["FlashError"] = "Das Plugin ist nicht geladen.";
        return RedirectToPage(new { id });
    }

    /// <summary>Renders the draft block tree (editor mode) WITHOUT touching the DB — for the live preview. A plugin's page is drawn with the variables of the plugin's example.</summary>
    public async Task<IActionResult> OnPostRenderPreview(int id)
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
        var page = await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        var scope = page?.OwnerPlugin is not null ? await _pluginPages.PreviewScopeAsync(page) : null;
        return Partial("_BlockList", new BlockListModel(blocks, Registry, true, scope));
    }

    /// <summary>Reconciles the whole draft into the page's ContentBlock rows.</summary>
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

        // Draft id → real id: the editor keeps working on the same tree after a save, and a block it
        // created a moment ago (negative id) must now be addressed by the id the database gave it.
        return new JsonResult(new { ok = true, ids = byDraftId.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Id) });
    }

    internal static bool IsJsonObject(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject; }
        catch { return false; }
    }

    private Task<PageEntity?> Load(int id) =>
        _db.Pages.Include(p => p.Blocks).FirstOrDefaultAsync(p => p.Id == id);

    /// <summary>The page switcher's data, deliberately built like Pages/Index: pages grouped by TranslationGroup,
    /// the versions ordered by the site's language order, the default-locale page as the primary. A page without
    /// a group is its own singleton. The switcher must not sort the site differently from the overview the
    /// operator came from.</summary>
    public static async Task<(SwitchGroup? Current, IReadOnlyList<SwitchGroup> Others)> BuildSwitcherAsync(AppDbContext db, PageEntity page)
    {
        var all = await db.Pages.AsNoTracking()
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

        var current = groups.FirstOrDefault(g => g.Versions.Any(v => v.Id == page.Id));
        // The current group appears exactly once, at the top — repeating it below would offer the
        // same page twice and make the arrow keys walk over it a second time.
        return (current, groups.Where(g => g != current).ToList());
    }
}

/// <summary>Model of the page switcher (_PageSwitcher.cshtml): the current page, its address, the grouped site
/// pages and what counts as unsaved work in the editor (a global probe function returning true while something
/// is unsaved).</summary>
public sealed record PageSwitcherModel(PageEntity Current, string CurrentUrl, EditorModel.SwitchGroup? CurrentGroup,
    IReadOnlyList<EditorModel.SwitchGroup> SwitchGroups, string DirtyProbe);
