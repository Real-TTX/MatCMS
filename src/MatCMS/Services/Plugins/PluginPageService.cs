using System.Text.Encodings.Web;
using System.Text.Json;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Services.Plugins;

/// <summary>
/// The pages plugins let the site's editors lay out (<see cref="PageRoleSpec"/>): they exist as ordinary <see cref="PageEntity"/> rows owned by the plugin, are created for every content language
/// when the plugin loads, and are drawn by the plugin's own route with the data of that request. A page the editors never touched shows the plugin's starting layout; a page that is missing or switched
/// off is not used at all — the plugin draws its own default — so nothing on the site depends on these rows existing.
/// </summary>
public sealed class PluginPageService
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly AppDbContext _db;
    private readonly PluginRegistry _registry;
    private readonly BlockRegistry _blocks;
    private readonly RazorPartialRenderer _renderer;
    private readonly IHttpContextAccessor _http;
    private readonly IServiceProvider _services;

    public PluginPageService(AppDbContext db, PluginRegistry registry, BlockRegistry blocks, RazorPartialRenderer renderer, IHttpContextAccessor http, IServiceProvider services)
    {
        _db = db; _registry = registry; _blocks = blocks; _renderer = renderer; _http = http; _services = services;
    }

    /// <summary>The slug a plugin's page has in the page table. It starts with an underscore, which no editor can type into a slug, so no ordinary page can ever take it.</summary>
    public static string SlugOf(string owner, string role) => "_" + owner + "-" + role;

    // ---- creating --------------------------------------------------------------------------------------

    /// <summary>
    /// Makes sure every role the plugin declared has a page in every content language, filled with the starting layout. Idempotent: a page that exists is left exactly as the editors left it.
    /// Pages of one role share a translation group, so the editor's language switch finds the other languages.
    /// </summary>
    public static async Task EnsureAsync(AppDbContext db, PluginRegistry.Slice slice, IEnumerable<PluginRegistry.PageRoleEntry> roles, CancellationToken ct = default)
    {
        var languages = (await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.Languages, ct))?.Value;
        var locales = Localizer.ParseActive(languages);
        // A site that already changed one of these pages in the template editor (its own version of the plugin's template part) keeps what it made: the page is created switched off,
        // the part keeps drawing the page, and the editors can switch the page on when they want to lay it out in blocks instead.
        var ownParts = MatCMS.Content.TemplateSchema.Parse(await db.Templates.AsNoTracking().Where(t => t.IsActive).Select(t => t.PartsJson).FirstOrDefaultAsync(ct));
        foreach (var entry in roles)
        {
            var customised = ownParts.TryGetValue(entry.Owner + "." + entry.Spec.Name, out var own) && !string.IsNullOrWhiteSpace(own);
            var existing = await db.Pages.AsNoTracking().Where(p => p.OwnerPlugin == entry.Owner && p.PluginRole == entry.Spec.Name).Select(p => new { p.Locale, p.TranslationGroup }).ToListAsync(ct);
            var group = existing.Select(e => e.TranslationGroup).FirstOrDefault(g => !string.IsNullOrEmpty(g)) ?? Guid.NewGuid().ToString();
            foreach (var locale in locales)
            {
                if (existing.Any(e => e.Locale == locale)) continue;
                var page = new PageEntity
                {
                    Title = PluginStrings.Label(slice, locale, entry.Spec.Title), Slug = SlugOf(entry.Owner, entry.Spec.Name), Locale = locale, TranslationGroup = group,
                    OwnerPlugin = entry.Owner, PluginRole = entry.Spec.Name, IsPublished = !customised, ShowInNav = false, ShowInFooter = false
                };
                db.Pages.Add(page);
                await db.SaveChangesAsync(ct);
                await SeedBlocksAsync(db, page.Id, entry.Spec, slice, locale, ct);
            }
        }
    }

    private static async Task SeedBlocksAsync(AppDbContext db, int pageId, PageRoleSpec spec, PluginRegistry.Slice slice, string locale, CancellationToken ct)
    {
        if (spec.Layout is null) return;
        var seeds = spec.Layout(new PageSeedContext(locale, key => PluginStrings.Label(slice, locale, key)));
        await AddAsync(seeds, null);

        async Task AddAsync(IReadOnlyList<PageBlockSeed> list, int? parentId)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var row = new ContentBlock { PageId = pageId, ParentId = parentId, SortOrder = i, BlockType = list[i].Type, DataJson = JsonSerializer.Serialize(list[i].Data, Json) };
                db.ContentBlocks.Add(row);
                await db.SaveChangesAsync(ct);               // the children need this block's id
                if (list[i].Children.Count > 0) await AddAsync(list[i].Children, row.Id);
            }
        }
    }

    /// <summary>"Back to the starting layout": the page's blocks are replaced by what the plugin ships. Returns false when the plugin is not loaded or has no layout for the role.</summary>
    public async Task<bool> ResetAsync(PageEntity page, CancellationToken ct = default)
    {
        var entry = EntryOf(page);
        if (entry is null || entry.Spec.Layout is null) return false;
        var slice = _registry.SliceOf(entry.Owner);
        if (slice is null) return false;
        _db.ContentBlocks.RemoveRange(await _db.ContentBlocks.Where(b => b.PageId == page.Id).ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
        await SeedBlocksAsync(_db, page.Id, entry.Spec, slice, page.Locale, ct);
        var row = await _db.Pages.FirstAsync(p => p.Id == page.Id, ct);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ---- reading ---------------------------------------------------------------------------------------

    /// <summary>The role a page stands for, when its plugin is loaded and still declares it.</summary>
    public PluginRegistry.PageRoleEntry? EntryOf(PageEntity page) =>
        page.OwnerPlugin is null ? null : _registry.PageRoles.FirstOrDefault(r => r.Owner == page.OwnerPlugin && r.Spec.Name == page.PluginRole);

    /// <summary>The page of a role in a language, or — when the site has none in that language — in the default language.</summary>
    public async Task<PageEntity?> FindAsync(string owner, string role, string locale, CancellationToken ct = default)
    {
        var pages = await _db.Pages.AsNoTracking().Include(p => p.Blocks)
            .Where(p => p.OwnerPlugin == owner && p.PluginRole == role && (p.Locale == locale || p.Locale == Localizer.DefaultCulture)).ToListAsync(ct);
        return pages.FirstOrDefault(p => p.Locale == locale) ?? pages.FirstOrDefault();
    }

    /// <summary>The variables a plugin page offers, for the editor's list: complete names with their labels (the label is in the editor's language).</summary>
    public IReadOnlyList<(string Name, string Label, bool Html)> VariablesOf(PageEntity page, string locale)
    {
        var entry = EntryOf(page);
        if (entry is null) return Array.Empty<(string, string, bool)>();
        var slice = _registry.SliceOf(entry.Owner);
        var list = entry.Spec.Variables.Select(v => ($"{entry.Spec.Name}.{v.Name}", PluginStrings.Label(slice, locale, v.Label), v.Html)).ToList();
        list.AddRange(Host.Select(h => (h.Name, h.Label, false)));
        return list;
    }

    /// <summary>What every plugin page can use whatever its plugin: the site and the signed-in customer.</summary>
    public static readonly (string Name, string Label)[] Host =
    [
        ("site.name", "Name der Website"), ("member.name", "Name des angemeldeten Kunden"), ("member.email", "E-Mail-Adresse des angemeldeten Kunden"), ("member.signed_in", "Gesetzt, wenn ein Kunde angemeldet ist")
    ];

    // ---- drawing ---------------------------------------------------------------------------------------

    /// <summary>The scope of a request: the plugin's values with the role in front, plus the site's and the customer's.</summary>
    public BlockScope ScopeOf(string role, PageScope scope, PluginMember? member)
    {
        var site = _services.GetService(typeof(SiteContext)) as SiteContext;
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["site.name"] = site?.SiteName ?? "", ["member.name"] = member?.DisplayName ?? "", ["member.email"] = member?.Email ?? "", ["member.signed_in"] = member is null ? "" : "1"
        };
        return BlockScope.Of(role, scope.Values, scope.HtmlNames, extra);
    }

    /// <summary>
    /// The HTML of the page a plugin's role has, drawn with <paramref name="scope"/>; null when there is none or it is switched off (the plugin then draws its own). In the editor's preview
    /// the blocks stand in a container the editor's script replaces on every change, followed by that script.
    /// </summary>
    public async Task<string?> RenderAsync(string owner, string role, string locale, BlockScope scope, bool editor, CancellationToken ct = default)
    {
        var page = await FindAsync(owner, role, locale, ct);
        if (page is null || (!page.IsPublished && !editor)) return null;
        var list = await _renderer.RenderAsync("_BlockList", new BlockListModel(page.Blocks, _blocks, editor, scope), ct);
        var css = string.IsNullOrWhiteSpace(page.CustomCss) ? "" : "<style>" + System.Text.RegularExpressions.Regex.Replace(page.CustomCss, @"</\s*style", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) + "</style>";
        if (_http.HttpContext is { } http) http.Items["pluginPageId"] = page.Id;
        if (!editor) return css + list;
        var chrome = await _renderer.RenderAsync("_PreviewChrome", true, ct);
        return css + "<div id=\"mat-blocks\">" + list + "</div>" + chrome;
    }

    /// <summary>The editor's live preview: the unsaved draft drawn with the variables of the plugin's example (none when the plugin offers no example).</summary>
    public async Task<BlockScope> PreviewScopeAsync(PageEntity page, CancellationToken ct = default)
    {
        var entry = EntryOf(page);
        if (entry?.Spec.Scope is null) return BlockScope.Empty;
        try
        {
            var ctx = PluginRequestContext.ForBackground(entry.Info, _services, _registry, editorPreview: true);
            var scope = await entry.Spec.Scope(ctx, ct);
            return scope is null ? BlockScope.Empty : ScopeOf(entry.Spec.Name, scope, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _registry.AddLog(entry.Owner, $"Vorschau der Seite '{entry.Spec.Name}': {ex.Message}");
            return BlockScope.Empty;
        }
    }

    /// <summary>The address of a real example the editor opens (with <c>?editor=1</c>), or null.</summary>
    public async Task<string?> PreviewUrlAsync(PageEntity page, CancellationToken ct = default)
    {
        var entry = EntryOf(page);
        if (entry?.Spec.Preview is null) return null;
        try
        {
            var ctx = PluginRequestContext.ForBackground(entry.Info, _services, _registry, editorPreview: true);
            var url = await entry.Spec.Preview(ctx, ct);
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/') || url.StartsWith("//")) return null;
            return Localizer.PrefixFor(page.Locale) + url;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _registry.AddLog(entry.Owner, $"Beispiel der Seite '{entry.Spec.Name}': {ex.Message}");
            return null;
        }
    }
}
