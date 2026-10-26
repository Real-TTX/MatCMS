using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services.Assistant;

/// <summary>What one tool call did: for the editor a one-line <see cref="Summary"/>, for the model the <see cref="Result"/>
/// and the <see cref="Warnings"/> (what was dropped and why — so it can do better on the next step).</summary>
public sealed record ToolOutcome(bool Ok, string Summary, JsonNode? Result, List<string> Warnings, int? PageId = null, bool Changed = false);

/// <summary>Where the assistant was opened and whether it must stay there.</summary>
public sealed record AssistantScope(int? PageId, int? BlockId, bool Locked);

/// <summary>
/// The tools the assistant works with — the whole site through the same tables the editor writes, never through anything
/// the model could turn into code. Every argument is read defensively (a model sends numbers as strings, slugs for ids,
/// fields next to <c>data</c>), every block goes through <see cref="AssistantBlocks"/>, every write is journalled first
/// (<see cref="AiChange"/>, the state BEFORE the run) so the run can be taken back as a whole.
/// <para>Scope: opened from a page or a block with "Nur hier ändern" ticked, a write outside that page or block is refused
/// with a reason the model reads. Reading is always allowed — knowing the rest of the site is what makes a page fit in.</para>
/// <para>Plugin pages (the shop's product page …) are shown but not written: their layout belongs to the plugin's editor
/// flow, and a slug change or a delete would break the plugin's routes.</para>
/// </summary>
public sealed partial class AssistantTools
{
    private readonly AppDbContext _db;
    private readonly AssistantBlocks _blocks;
    private readonly Localizer _t;
    private readonly AssistantFiles _files;
    private readonly IServiceProvider _services;

    public int ConversationId { get; set; }
    public int Run { get; set; }
    public AssistantScope Scope { get; set; } = new(null, null, false);

    public AssistantTools(AppDbContext db, AssistantBlocks blocks, Localizer t, AssistantFiles files, IServiceProvider services)
    {
        _services = services;
        _db = db;
        _blocks = blocks;
        _t = t;
        _files = files;
    }

    /// <summary>The tools as the prompt describes them: name, arguments, what it does. The order is the order a sensible
    /// run uses them in.</summary>
    public static readonly (string Name, string Args, string What, bool Writes)[] Catalog = [.. PageCatalog, .. FormCatalog, .. MoreCatalog];

    private static (string Name, string Args, string What, bool Writes)[] PageCatalog =>
    [
        ("site_overview", "{}", "Überblick: Website-Name, Sprachen, alle Seiten (id, slug, Titel, Sprache, veröffentlicht, im Menü), Menüs, Design, Formulare.", false),
        ("read_page", "{\"page\": id oder slug, \"locale\"?: \"de\"}", "Eine Seite mit allen Blöcken als Baum: id, type, data (alle Felder), children.", false),
        ("list_media", "{\"search\"?: \"text\", \"tag\"?: \"…\", \"limit\"?: 40}", "Bilder und Dateien der Mediathek (url, alt, tags). Bilder NUR von hier verwenden.", false),
        ("read_theme", "{}", "Das aktive Design: Farben, Schriften, Button-Stil, Breiten, Parameter, eigenes CSS.", false),
        ("create_page", "{\"title\", \"slug\"?, \"locale\"?, \"translationOf\"?: id, \"metaDescription\"?, \"published\"?: true, \"addToMenu\"?: false, \"blocks\": [Block…]}", "Neue Seite anlegen. translationOf = id der Seite, deren Übersetzung das ist (locale angeben).", true),
        ("update_page", "{\"page\", \"title\"?, \"slug\"?, \"metaDescription\"?, \"published\"?, \"navLabel\"?, \"customCss\"?}", "Eigenschaften einer Seite ändern (nicht die Blöcke).", true),
        ("set_page_blocks", "{\"page\", \"blocks\": [Block…]}", "ALLE Blöcke einer Seite ersetzen (Neuaufbau). Für kleine Änderungen update_block nehmen.", true),
        ("add_blocks", "{\"page\", \"blocks\": [Block…], \"parent\"?: blockId, \"after\"?: blockId | \"start\" | \"end\"}", "Blöcke einfügen: oben auf der Seite oder in einen Container (parent), hinter einen Block oder an Anfang/Ende.", true),
        ("update_block", "{\"block\": id, \"data\": {feld: wert, …}}", "Felder EINES Blocks ändern; nicht genannte Felder bleiben, null löscht ein Feld. Listen werden ganz ersetzt.", true),
        ("move_block", "{\"block\": id, \"parent\"?: blockId | null, \"after\"?: blockId | \"start\" | \"end\"}", "Block verschieben (parent null = oberste Ebene).", true),
        ("delete_block", "{\"block\": id}", "Block samt Inhalt löschen.", true),
        ("delete_page", "{\"page\"}", "Seite löschen (nicht die Startseite).", true),
        ("set_menu", "{\"menu\": \"header\"|\"footer\"|…, \"locale\"?, \"items\": [{\"label\", \"url\", \"children\"?: […], \"newTab\"?: false}]}", "Ein Menü komplett neu setzen (Plugin-Einträge wie Warenkorb bleiben erhalten).", true),
        ("update_theme", "{\"values\": {\"AccentColor\"?, \"SecondaryColor\"?, \"HeadingColor\"?, \"TextColor\"?, \"BackgroundColor\"?, \"AltBackground\"?, \"HeaderBackground\"?, \"HeaderTextColor\"?, \"HeadingFont\"?, \"BodyFont\"?, \"ButtonStyle\"?, \"ButtonRadius\"?, \"ContainerWidth\"?, \"HeaderPadding\"?}, \"params\"?: {id: wert}, \"customCss\"?: \"…\" (ersetzt), \"appendCss\"?: \"…\" (hängt an)}", "Design des aktiven Templates ändern.", true),
        ("create_svg", "{\"name\": \"logo\", \"svg\": \"<svg xmlns=\\\"http://www.w3.org/2000/svg\\\" viewBox=\\\"0 0 200 60\\\">…</svg>\", \"alt\"?: \"…\"}", "Eine Grafik als SVG zeichnen (Logo, Icon, Illustration) und in die Mediathek legen. Die url danach als Bild, Logo (update_settings logoUrl) oder Favicon verwenden. Nur Zeichenelemente; Skripte, Links nach außen und eingebettete Bilder werden entfernt.", true),
        ("update_settings", "{\"siteName\"?, \"footerText\"?, \"logoUrl\"?, \"faviconUrl\"?, \"contactRecipient\"?}", "Grundeinstellungen der Website (Logo und Favicon aus der Mediathek, auch SVG).", true),
    ];

    private static readonly HashSet<string> WriteTools = Catalog.Where(c => c.Writes).Select(c => c.Name).ToHashSet();
    public static bool IsWrite(string tool) => WriteTools.Contains(tool);

    public async Task<ToolOutcome> RunAsync(string tool, JsonObject args, CancellationToken ct)
    {
        try
        {
            return tool switch
            {
                "site_overview" => await OverviewAsync(ct),
                "read_page" => await ReadPageAsync(args, ct),
                "list_media" => await ListMediaAsync(args, ct),
                "read_theme" => await ReadThemeAsync(ct),
                "create_page" => await CreatePageAsync(args, ct),
                "update_page" => await UpdatePageAsync(args, ct),
                "set_page_blocks" => await SetPageBlocksAsync(args, ct),
                "add_blocks" => await AddBlocksAsync(args, ct),
                "update_block" => await UpdateBlockAsync(args, ct),
                "move_block" => await MoveBlockAsync(args, ct),
                "delete_block" => await DeleteBlockAsync(args, ct),
                "delete_page" => await DeletePageAsync(args, ct),
                "set_menu" => await SetMenuAsync(args, ct),
                "update_theme" => await UpdateThemeAsync(args, ct),
                "update_settings" => await UpdateSettingsAsync(args, ct),
                "create_svg" => await CreateSvgAsync(args, ct),
                "read_form" => await ReadFormAsync(args, ct),
                "create_form" => await CreateFormAsync(args, ct),
                "update_form" => await UpdateFormAsync(args, ct),
                "plugin_action" => await PluginActionAsync(args, ct),
                "list_components" => await ListComponentsAsync(ct),
                "read_component" => await ReadComponentAsync(args, ct),
                "create_component" => await CreateComponentAsync(args, ct),
                "update_component" => await UpdateComponentAsync(args, ct),
                "list_templates" => await ListTemplatesAsync(ct),
                "read_template" => await ReadTemplateAsync(args, ct),
                "update_template" => await UpdateTemplateAsync(args, ct),
                "create_template" => await CreateTemplateAsync(args, ct),
                "activate_template" => await ActivateTemplateAsync(args, ct),
                "list_plugins" => await ListPluginsAsync(ct),
                "read_plugin" => await ReadPluginAsync(args, ct),
                "create_plugin" => await CreatePluginAsync(args, ct),
                "update_plugin" => await UpdatePluginAsync(args, ct),
                _ => Fail($"Unbekanntes Werkzeug „{tool}“.", $"Es gibt kein Werkzeug \"{tool}\". Verfügbar: {string.Join(", ", Catalog.Select(c => c.Name))}."),
            };
        }
        catch (ToolError e)
        {
            Discard();
            return Fail(e.Summary, e.Message);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException or JsonException or FormatException)
        {
            Discard();
            return Fail($"„{tool}“ fehlgeschlagen", "Fehler beim Ausführen: " + e.Message);
        }
    }

    /// <summary>A tool that stopped half-way may have changed tracked entities without saving them; the next save of the
    /// run (the next step's log line) would write that half-change. Everything unsaved is dropped — except the
    /// conversation, which the agent itself keeps tracked.</summary>
    private void Discard()
    {
        // Detached, not reset: a reset entity would keep its changed values in memory and the next query could hand them out.
        foreach (var e in _db.ChangeTracker.Entries().Where(e => e.Entity is not AiConversation).ToList())
            e.State = EntityState.Detached;
    }

    // ============================================================================================== reads

    private async Task<ToolOutcome> OverviewAsync(CancellationToken ct)
    {
        var settings = await _db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        string S(string k) => settings.TryGetValue(k, out var v) ? v ?? "" : "";
        var languages = ActiveLanguages(S(SettingKeys.Languages));

        var pages = await _db.Pages.AsNoTracking().OrderBy(p => p.Locale).ThenBy(p => p.NavOrder).ThenBy(p => p.Title)
            .Select(p => new { p.Id, p.Title, p.Slug, p.Locale, p.IsPublished, p.ShowInNav, p.OwnerPlugin, p.TranslationGroup, Blocks = p.Blocks.Count })
            .ToListAsync(ct);
        var pageArr = new JsonArray();
        foreach (var p in pages)
        {
            var o = new JsonObject
            {
                ["id"] = p.Id, ["title"] = p.Title, ["slug"] = p.Slug, ["locale"] = p.Locale, ["url"] = PublicUrl(p.Slug, p.Locale),
                ["published"] = p.IsPublished, ["blocks"] = p.Blocks,
            };
            if (p.OwnerPlugin is not null) o["plugin"] = p.OwnerPlugin + " (nur lesen)";
            var siblings = pages.Where(x => x.TranslationGroup == p.TranslationGroup && x.Id != p.Id && p.TranslationGroup != null).Select(x => $"{x.Locale}:{x.Id}").ToList();
            if (siblings.Count > 0) o["translations"] = string.Join(", ", siblings);
            pageArr.Add(o);
        }

        var menus = await _db.Menus.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(ct);
        var items = await _db.MenuItems.AsNoTracking().OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync(ct);
        var menuArr = new JsonArray();
        foreach (var m in menus)
            foreach (var loc in items.Where(i => i.Menu == m.Key).Select(i => i.Locale).Distinct().DefaultIfEmpty(Localizer.DefaultCulture))
                menuArr.Add(new JsonObject { ["menu"] = m.Key, ["name"] = m.Name, ["locale"] = loc, ["items"] = MenuTreeJson(items.Where(i => i.Menu == m.Key && i.Locale == loc).ToList(), null) });

        var tpl = await ActiveTemplateAsync(ct, tracked: false);
        var forms = await _db.Forms.AsNoTracking().OrderBy(f => f.Name).Select(f => new { f.Slug, f.Name }).ToListAsync(ct);
        var mediaCount = await _db.Media.CountAsync(ct);

        var result = new JsonObject
        {
            ["site"] = new JsonObject
            {
                ["name"] = S(SettingKeys.SiteName), ["footerText"] = S(SettingKeys.FooterText), ["logoUrl"] = S(SettingKeys.LogoUrl),
                ["contactRecipient"] = S(SettingKeys.ContactRecipient),
                ["languages"] = new JsonArray(languages.Select(l => (JsonNode)l).ToArray()), ["defaultLanguage"] = Localizer.DefaultCulture,
            },
            ["pages"] = pageArr,
            ["menus"] = menuArr,
            ["theme"] = tpl is null ? null : new JsonObject { ["id"] = tpl.Id, ["name"] = tpl.Name, ["accent"] = tpl.AccentColor, ["headingFont"] = tpl.HeadingFont, ["bodyFont"] = tpl.BodyFont },
            ["forms"] = new JsonArray(forms.Select(f => (JsonNode)new JsonObject { ["slug"] = f.Slug, ["name"] = f.Name }).ToArray()),
            ["mediaCount"] = mediaCount,
        };
        return Ok($"Überblick gelesen ({pages.Count} Seiten, {mediaCount} Medien)", result);
    }

    private JsonArray MenuTreeJson(List<MenuItem> items, int? parent)
    {
        var arr = new JsonArray();
        foreach (var i in items.Where(i => i.ParentId == parent))
        {
            var o = new JsonObject { ["label"] = i.Label, ["url"] = i.Url };
            if (i.Url.StartsWith("plugin:", StringComparison.Ordinal)) o["plugin"] = true;
            var kids = MenuTreeJson(items, i.Id);
            if (kids.Count > 0) o["children"] = kids;
            arr.Add(o);
        }
        return arr;
    }

    private async Task<ToolOutcome> ReadPageAsync(JsonObject args, CancellationToken ct)
    {
        var page = await FindPageAsync(args, ct, tracked: false);
        var blocks = await _db.ContentBlocks.AsNoTracking().Where(b => b.PageId == page.Id).OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync(ct);
        var group = page.TranslationGroup ?? "\0none";
        var translations = await _db.Pages.AsNoTracking()
            .Where(p => p.TranslationGroup == group && p.Id != page.Id).Select(p => new { p.Id, p.Locale, p.Slug }).ToListAsync(ct);
        var truncated = new List<string>();
        var result = new JsonObject
        {
            ["id"] = page.Id, ["title"] = page.Title, ["slug"] = page.Slug, ["locale"] = page.Locale, ["url"] = PublicUrl(page.Slug, page.Locale),
            ["published"] = page.IsPublished, ["navLabel"] = page.NavLabel, ["metaDescription"] = page.MetaDescription,
            ["customCss"] = string.IsNullOrEmpty(page.CustomCss) ? null : page.CustomCss,
            ["translations"] = new JsonArray(translations.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["locale"] = x.Locale, ["slug"] = x.Slug }).ToArray()),
            ["blocks"] = BlockTree(blocks, null, truncated),
        };
        if (page.OwnerPlugin is not null) result["plugin"] = page.OwnerPlugin + " – diese Seite gehört einem Plugin und kann hier nur gelesen werden.";
        var warnings = truncated.Count > 0
            ? new List<string> { $"Lange Felder gekürzt angezeigt ({string.Join(", ", truncated.Take(6))}). Schreibe sie nur zurück, wenn du sie vollständig neu verfasst." }
            : new List<string>();
        return Ok($"Seite „{page.Title}“ gelesen ({blocks.Count} Blöcke)", result, warnings, page.Id);
    }

    private JsonArray BlockTree(List<ContentBlock> all, int? parent, List<string> truncated)
    {
        var arr = new JsonArray();
        foreach (var b in all.Where(b => b.ParentId == parent))
        {
            JsonObject data;
            try { data = JsonNode.Parse(string.IsNullOrWhiteSpace(b.DataJson) ? "{}" : b.DataJson) as JsonObject ?? new(); }
            catch { data = new(); }
            foreach (var key in data.Select(kv => kv.Key).ToList())
                if (data[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 6000)
                {
                    data[key] = s[..6000] + $" …[gekürzt, {s.Length} Zeichen]";
                    truncated.Add($"Block {b.Id}.{key}");
                }
            var o = new JsonObject { ["id"] = b.Id, ["type"] = b.BlockType, ["name"] = _t[_blocks.Get(b.BlockType)?.Name ?? b.BlockType], ["data"] = data };
            var kids = BlockTree(all, b.Id, truncated);
            if (kids.Count > 0) o["children"] = kids;
            arr.Add(o);
        }
        return arr;
    }

    private async Task<ToolOutcome> ListMediaAsync(JsonObject args, CancellationToken ct)
    {
        var search = Str(args["search"]).Trim();
        var tag = Str(args["tag"]).Trim();
        var limit = Math.Clamp(Int(args["limit"]) ?? 40, 1, 120);
        var q = _db.Media.AsNoTracking().AsQueryable();
        if (search.Length > 0) q = q.Where(m => m.FileName.Contains(search) || (m.Alt ?? "").Contains(search) || m.Tags.Contains(search));
        if (tag.Length > 0) q = q.Where(m => m.Tags.Contains(tag));
        var list = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(limit)
            .Select(m => new { m.Url, m.Alt, m.Tags, m.FileName, m.ContentType }).ToListAsync(ct);
        var arr = new JsonArray(list.Select(m => (JsonNode)new JsonObject
        {
            ["url"] = m.Url, ["alt"] = m.Alt ?? "", ["tags"] = m.Tags, ["file"] = m.FileName,
            ["kind"] = m.ContentType.StartsWith("image/") || IsImageName(m.Url) ? "image" : "file",
        }).ToArray());
        return Ok($"Mediathek durchsucht ({list.Count} Treffer{(search.Length > 0 ? $" für „{search}“" : "")})", new JsonObject { ["media"] = arr });
    }

    private static bool IsImageName(string url) =>
        new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" }.Contains(Path.GetExtension(url).ToLowerInvariant());

    private async Task<ToolOutcome> ReadThemeAsync(CancellationToken ct)
    {
        var t = await ActiveTemplateAsync(ct, tracked: false) ?? throw new ToolError("Kein Design", "Es gibt kein aktives Template.");
        var values = TemplateParams.Resolve(t);
        var result = new JsonObject
        {
            ["id"] = t.Id, ["name"] = t.Name,
            ["values"] = new JsonObject
            {
                ["AccentColor"] = t.AccentColor, ["SecondaryColor"] = t.SecondaryColor, ["HeadingColor"] = t.HeadingColor, ["TextColor"] = t.TextColor,
                ["BackgroundColor"] = t.BackgroundColor, ["AltBackground"] = t.AltBackground, ["HeaderBackground"] = t.HeaderBackground,
                ["HeaderTextColor"] = t.HeaderTextColor, ["HeadingFont"] = t.HeadingFont, ["BodyFont"] = t.BodyFont, ["ButtonStyle"] = t.ButtonStyle,
                ["ButtonRadius"] = t.ButtonRadius, ["ContainerWidth"] = t.ContainerWidth, ["HeaderPadding"] = t.HeaderPadding,
            },
            ["fonts"] = new JsonArray(TemplateFonts.Families.Select(f => (JsonNode)f).ToArray()),
            ["buttonStyles"] = new JsonArray("solid", "outline"),
            ["params"] = new JsonArray(TemplateParams.Schema(t.ParametersJson).Where(p => p.Id.Length > 0).Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["label"] = p.Label, ["type"] = p.Type, ["options"] = p.Options, ["value"] = values.TryGetValue(p.Id, out var v) ? v : p.Default,
            }).ToArray()),
            ["customCss"] = t.CustomCss.Length > 8000 ? t.CustomCss[..8000] + " …[gekürzt]" : t.CustomCss,
        };
        return Ok($"Design „{t.Name}“ gelesen", result);
    }

    // ============================================================================================== pages

    private async Task<ToolOutcome> CreatePageAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Neue Seiten anlegen");
        var title = Str(args["title"]).Trim();
        if (title.Length == 0) throw new ToolError("Seite ohne Titel", "title fehlt.");
        var locale = Locale(args["locale"]);
        var slug = Pages.Admin.Pages.IndexModel.Slugify(Str(args["slug"]) is { Length: > 0 } s ? s : title);
        if (slug.Length == 0) throw new ToolError("Ungültige Adresse", "Aus slug/title lässt sich keine Adresse bilden.");
        if (Pages.Admin.Pages.IndexModel.IsReserved(slug)) throw new ToolError($"Adresse „{slug}“ reserviert", $"Der slug \"{slug}\" ist reserviert – wähle einen anderen.");
        var existing = await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.Locale == locale, ct);
        if (existing is not null)
            throw new ToolError($"Seite „{slug}“ gibt es schon", $"Eine Seite mit slug \"{slug}\" ({locale}) existiert bereits (id {existing.Id}). Ändere sie mit update_page/set_page_blocks oder wähle einen anderen slug.");

        string? group = null;
        if (Int(args["translationOf"]) is int of)
        {
            var source = await _db.Pages.FirstOrDefaultAsync(p => p.Id == of, ct)
                ?? throw new ToolError("Original nicht gefunden", $"translationOf: Seite {of} gibt es nicht.");
            if (source.Locale == locale) throw new ToolError("Gleiche Sprache", "Eine Übersetzung braucht eine andere Sprache (locale) als das Original.");
            if (source.TranslationGroup is not null && await _db.Pages.AnyAsync(p => p.TranslationGroup == source.TranslationGroup && p.Locale == locale, ct))
                throw new ToolError("Übersetzung existiert", $"Für Seite {of} gibt es bereits eine Übersetzung in \"{locale}\".");
            if (source.TranslationGroup is null)
            {
                // An old page without a group gets one now — the original is changed too, so it is journalled first.
                await SnapshotPageAsync(source.Id, ct);
                source.TranslationGroup = Guid.NewGuid().ToString("N");
            }
            group = source.TranslationGroup;
        }

        var warnings = new List<string>();
        var budget = AssistantBlocks.MaxBlocksPerCall;
        var input = _blocks.ParseList(args["blocks"], null, warnings, ref budget);

        var page = new Page
        {
            Title = Cap(title, 200), Slug = slug, Locale = locale, TranslationGroup = group ?? Guid.NewGuid().ToString("N"),
            IsPublished = Bool(args["published"]) ?? true,
            MetaDescription = Str(args["metaDescription"]) is { Length: > 0 } md ? Cap(md, 400) : null,
            NavLabel = Str(args["navLabel"]) is { Length: > 0 } nl ? Cap(nl, 80) : null,
        };
        AddTree(page, null, input, 0);
        _db.Pages.Add(page);
        await _db.SaveChangesAsync(ct);
        Journal("page", page.Id.ToString(), null);

        var url = PublicUrl(page.Slug, page.Locale);
        if (Bool(args["addToMenu"]) == true)
        {
            await SnapshotMenuAsync("header", locale, ct);
            var max = await _db.MenuItems.Where(m => m.Menu == "header" && m.Locale == locale && m.ParentId == null).MaxAsync(m => (int?)m.SortOrder, ct) ?? 0;
            _db.MenuItems.Add(new MenuItem { Menu = "header", Locale = locale, Label = page.NavLabel ?? page.Title, Url = url, SortOrder = max + 1 });
        }
        await _db.SaveChangesAsync(ct);

        var ids = await IdsTreeAsync(page.Id, null, ct);
        return Ok($"Seite „{page.Title}“ angelegt ({url}, {Count(input)} Blöcke){(Bool(args["addToMenu"]) == true ? ", ins Menü aufgenommen" : "")}",
            new JsonObject { ["id"] = page.Id, ["url"] = url, ["blocks"] = ids }, warnings, page.Id, changed: true);
    }

    private async Task<ToolOutcome> UpdatePageAsync(JsonObject args, CancellationToken ct)
    {
        var page = await FindPageAsync(args, ct, tracked: true);
        RequireWritablePage(page);
        await SnapshotPageAsync(page.Id, ct);
        var changed = new List<string>();
        var warnings = new List<string>();
        if (args.ContainsKey("title") && Str(args["title"]).Trim() is { Length: > 0 } title) { page.Title = Cap(title, 200); changed.Add("Titel"); }
        if (args.ContainsKey("slug") && Str(args["slug"]) is { Length: > 0 } rawSlug)
        {
            var slug = Pages.Admin.Pages.IndexModel.Slugify(rawSlug);
            if (page.Slug == "home") warnings.Add("Die Startseite behält ihren slug \"home\".");
            else if (slug.Length == 0 || Pages.Admin.Pages.IndexModel.IsReserved(slug)) warnings.Add($"slug \"{rawSlug}\" ist nicht erlaubt – unverändert.");
            else if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == page.Locale && p.Id != page.Id, ct)) warnings.Add($"slug \"{slug}\" ist schon vergeben – unverändert.");
            else if (slug != page.Slug) { page.Slug = slug; changed.Add("Adresse"); warnings.Add("Adresse geändert – prüfe Menüs und Links, die auf die alte Adresse zeigen."); }
        }
        if (args.ContainsKey("metaDescription")) { page.MetaDescription = Str(args["metaDescription"]) is { Length: > 0 } md ? Cap(md, 400) : null; changed.Add("Meta-Beschreibung"); }
        if (args.ContainsKey("navLabel")) { page.NavLabel = Str(args["navLabel"]) is { Length: > 0 } nl ? Cap(nl, 80) : null; changed.Add("Menütext"); }
        if (Bool(args["published"]) is bool pub && pub != page.IsPublished) { page.IsPublished = pub; changed.Add(pub ? "veröffentlicht" : "unveröffentlicht"); }
        if (args.ContainsKey("customCss")) { page.CustomCss = Cap(Str(args["customCss"]).Replace("</", "<\\/"), TemplateFonts.MaxInlineCode); changed.Add("Seiten-CSS"); }
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(changed.Count == 0 ? $"Seite „{page.Title}“: nichts geändert" : $"Seite „{page.Title}“ geändert: {string.Join(", ", changed)}",
            new JsonObject { ["id"] = page.Id, ["slug"] = page.Slug, ["url"] = PublicUrl(page.Slug, page.Locale) }, warnings, page.Id, changed: changed.Count > 0);
    }

    private async Task<ToolOutcome> SetPageBlocksAsync(JsonObject args, CancellationToken ct)
    {
        var page = await FindPageAsync(args, ct, tracked: true);
        RequireWritablePage(page);
        if (Scope is { Locked: true, BlockId: not null }) throw OutOfScope("die ganze Seite neu aufbauen");
        var warnings = new List<string>();
        var budget = AssistantBlocks.MaxBlocksPerCall;
        var input = _blocks.ParseList(args["blocks"], null, warnings, ref budget);
        if (input.Count == 0) throw new ToolError("Keine gültigen Blöcke", "Kein einziger gültiger Block – die Seite bleibt unverändert. " + string.Join(" ", warnings.Take(8)));
        await SnapshotPageAsync(page.Id, ct);
        var old = await _db.ContentBlocks.Where(b => b.PageId == page.Id).ToListAsync(ct);
        _db.ContentBlocks.RemoveRange(old);
        await _db.SaveChangesAsync(ct);
        AddTree(page, null, input, 0);
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var ids = await IdsTreeAsync(page.Id, null, ct);
        return Ok($"Seite „{page.Title}“ neu aufgebaut ({Count(input)} Blöcke: {Names(input)})", new JsonObject { ["id"] = page.Id, ["blocks"] = ids }, warnings, page.Id, changed: true);
    }

    private async Task<ToolOutcome> AddBlocksAsync(JsonObject args, CancellationToken ct)
    {
        var page = await FindPageAsync(args, ct, tracked: true);
        RequireWritablePage(page);
        ContentBlock? parent = null;
        if (Int(args["parent"]) is int pid)
        {
            parent = await _db.ContentBlocks.FirstOrDefaultAsync(b => b.Id == pid && b.PageId == page.Id, ct)
                ?? throw new ToolError("Container nicht gefunden", $"parent: Block {pid} ist nicht auf dieser Seite.");
            if (_blocks.Get(parent.BlockType) is not { IsContainer: true }) throw new ToolError("Kein Container", $"Block {pid} ({parent.BlockType}) kann keine Blöcke enthalten.");
        }
        if (Scope.Locked && Scope.BlockId is int sb && (parent is null || !await InSubtreeAsync(parent.Id, sb, ct)))
            throw OutOfScope("Blöcke außerhalb des gewählten Blocks einfügen");

        var warnings = new List<string>();
        var budget = AssistantBlocks.MaxBlocksPerCall;
        var input = _blocks.ParseList(args["blocks"], parent?.BlockType, warnings, ref budget);
        if (input.Count == 0) throw new ToolError("Keine gültigen Blöcke", "Kein gültiger Block zum Einfügen. " + string.Join(" ", warnings.Take(8)));

        await SnapshotPageAsync(page.Id, ct);
        var siblings = await _db.ContentBlocks.Where(b => b.PageId == page.Id && b.ParentId == (parent == null ? null : parent.Id))
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync(ct);
        var at = InsertIndex(siblings, args["after"], warnings);
        var created = new List<ContentBlock>();
        foreach (var b in input) created.Add(NewBlock(page, b));
        foreach (var c in created) { c.ParentId = parent?.Id; _db.ContentBlocks.Add(c); }
        siblings.InsertRange(at, created);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var ids = new JsonArray(created.Select(c => (JsonNode)IdNode(c)).ToArray());
        return Ok($"{Count(input)} Block/Blöcke in „{page.Title}“ eingefügt ({Names(input)})", new JsonObject { ["created"] = ids }, warnings, page.Id, changed: true);
    }

    private async Task<ToolOutcome> UpdateBlockAsync(JsonObject args, CancellationToken ct)
    {
        var block = await FindBlockAsync(args["block"], ct);
        var page = await _db.Pages.FirstAsync(p => p.Id == block.PageId, ct);
        RequireWritablePage(page);
        if (Scope.Locked && Scope.BlockId is int sb && !await InSubtreeAsync(block.Id, sb, ct)) throw OutOfScope("einen anderen Block ändern");
        var def = _blocks.Get(block.BlockType) ?? throw new ToolError("Block nicht änderbar", $"Block {block.Id} ({block.BlockType}) kann der Assistent nicht ändern.");
        if (args["data"] is not JsonObject patch) throw new ToolError("Keine Felder", "data fehlt (ein Objekt mit den zu ändernden Feldern).");

        var warnings = new List<string>();
        var clean = _blocks.ValidateData(def, patch, block.ParentId is null, warnings, $"block {block.Id}", keepNulls: true);
        if (clean.Count == 0) throw new ToolError("Keine gültigen Felder", "Kein Feld ließ sich übernehmen. " + string.Join(" ", warnings.Take(8)));

        await SnapshotPageAsync(page.Id, ct);
        JsonObject data;
        try { data = JsonNode.Parse(string.IsNullOrWhiteSpace(block.DataJson) ? "{}" : block.DataJson) as JsonObject ?? new(); }
        catch { data = new(); }
        var names = new List<string>();
        foreach (var (k, v) in clean)
        {
            if (v is null) data.Remove(k); else data[k] = v.DeepClone();
            names.Add(FieldLabel(def, k));
        }
        block.DataJson = data.ToJsonString();
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok($"Block „{_t[def.Name]}“ geändert ({string.Join(", ", names)}) – Seite „{page.Title}“",
            new JsonObject { ["id"] = block.Id, ["type"] = block.BlockType, ["data"] = JsonNode.Parse(block.DataJson) }, warnings, page.Id, changed: true);
    }

    private async Task<ToolOutcome> MoveBlockAsync(JsonObject args, CancellationToken ct)
    {
        var block = await FindBlockAsync(args["block"], ct);
        var page = await _db.Pages.FirstAsync(p => p.Id == block.PageId, ct);
        RequireWritablePage(page);
        ContentBlock? parent = null;
        if (args.ContainsKey("parent") && Int(args["parent"]) is int pid)
        {
            parent = await _db.ContentBlocks.FirstOrDefaultAsync(b => b.Id == pid && b.PageId == page.Id, ct)
                ?? throw new ToolError("Ziel nicht gefunden", $"parent: Block {pid} ist nicht auf dieser Seite.");
            if (await InSubtreeAsync(parent.Id, block.Id, ct)) throw new ToolError("Unmögliche Verschiebung", "Ein Block kann nicht in sich selbst verschoben werden.");
            var pdef = _blocks.Get(parent.BlockType);
            if (pdef is null || !pdef.AllowedChildren.Contains(block.BlockType, StringComparer.OrdinalIgnoreCase))
                throw new ToolError("Passt dort nicht hinein", $"\"{block.BlockType}\" darf nicht in \"{parent.BlockType}\" stehen.");
        }
        else if (!args.ContainsKey("parent"))
            parent = block.ParentId is int cur ? await _db.ContentBlocks.FirstAsync(b => b.Id == cur, ct) : null;
        if (parent is null && _blocks.Get(block.BlockType) is { ChildOnly: true })
            throw new ToolError("Passt nicht auf die Seite", $"\"{block.BlockType}\" kann nur in einem Container stehen.");
        if (Scope.Locked && Scope.BlockId is int sb && (!await InSubtreeAsync(block.Id, sb, ct) || parent is null || !await InSubtreeAsync(parent.Id, sb, ct) || block.Id == sb))
            throw OutOfScope("Blöcke außerhalb des gewählten Blocks verschieben");

        await SnapshotPageAsync(page.Id, ct);
        var warnings = new List<string>();
        var siblings = await _db.ContentBlocks.Where(b => b.PageId == page.Id && b.ParentId == (parent == null ? null : parent.Id) && b.Id != block.Id)
            .OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync(ct);
        var at = InsertIndex(siblings, args["after"], warnings);
        block.ParentId = parent?.Id;
        siblings.Insert(at, block);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var name = _t[_blocks.Get(block.BlockType)?.Name ?? block.BlockType];
        return Ok($"Block „{name}“ verschoben – Seite „{page.Title}“", new JsonObject { ["id"] = block.Id, ["parent"] = parent?.Id, ["position"] = at }, warnings, page.Id, changed: true);
    }

    private async Task<ToolOutcome> DeleteBlockAsync(JsonObject args, CancellationToken ct)
    {
        var block = await FindBlockAsync(args["block"], ct);
        var page = await _db.Pages.FirstAsync(p => p.Id == block.PageId, ct);
        RequireWritablePage(page);
        if (Scope.Locked && Scope.BlockId is int sb && (block.Id == sb || !await InSubtreeAsync(block.Id, sb, ct))) throw OutOfScope("diesen Block löschen");
        await SnapshotPageAsync(page.Id, ct);
        var all = await _db.ContentBlocks.Where(b => b.PageId == page.Id).ToListAsync(ct);
        var doomed = new List<ContentBlock>();
        void Collect(int id) { foreach (var c in all.Where(x => x.ParentId == id)) { Collect(c.Id); doomed.Add(c); } }
        Collect(block.Id);
        doomed.Add(block);
        _db.ContentBlocks.RemoveRange(doomed);
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var name = _t[_blocks.Get(block.BlockType)?.Name ?? block.BlockType];
        return Ok($"Block „{name}“ gelöscht{(doomed.Count > 1 ? $" (mit {doomed.Count - 1} enthaltenen)" : "")} – Seite „{page.Title}“", new JsonObject { ["deleted"] = doomed.Count }, new(), page.Id, changed: true);
    }

    private async Task<ToolOutcome> DeletePageAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Seiten löschen");
        var page = await FindPageAsync(args, ct, tracked: true);
        RequireWritablePage(page);
        if (page.Slug == "home") throw new ToolError("Startseite bleibt", "Die Startseite kann nicht gelöscht werden.");
        await SnapshotPageAsync(page.Id, ct);
        var blocks = await _db.ContentBlocks.Where(b => b.PageId == page.Id).ToListAsync(ct);
        _db.ContentBlocks.RemoveRange(blocks);
        _db.Pages.Remove(page);
        await _db.SaveChangesAsync(ct);
        var url = PublicUrl(page.Slug, page.Locale);
        var linked = await _db.MenuItems.AsNoTracking().CountAsync(m => m.Url == url, ct);
        var warnings = linked > 0 ? new List<string> { $"{linked} Menüeintrag/-einträge zeigen noch auf {url} – mit set_menu anpassen." } : new List<string>();
        return Ok($"Seite „{page.Title}“ gelöscht", new JsonObject { ["deleted"] = page.Id }, warnings, null, changed: true);
    }

    // ============================================================================================== menu, theme, settings

    private async Task<ToolOutcome> SetMenuAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Menüs ändern");
        var key = Str(args["menu"]).Trim();
        if (key.Length == 0) key = "header";
        if (!await _db.Menus.AnyAsync(m => m.Key == key, ct))
            throw new ToolError($"Menü „{key}“ gibt es nicht", $"Es gibt kein Menü \"{key}\". Vorhanden: {string.Join(", ", await _db.Menus.Select(m => m.Key).ToListAsync(ct))}.");
        var locale = Locale(args["locale"]);
        if (args["items"] is not JsonArray items) throw new ToolError("Keine Einträge", "items fehlt (Liste von {label, url, children?}).");

        var warnings = new List<string>();
        await SnapshotMenuAsync(key, locale, ct);
        var current = await _db.MenuItems.Where(m => m.Menu == key && m.Locale == locale).ToListAsync(ct);
        // A plugin's elements (cart, sign-in, search) are kept: the model cannot build them, and the visitor would lose them.
        var keep = current.Where(m => m.Url.StartsWith("plugin:", StringComparison.Ordinal) && m.ParentId == null).ToList();
        var keepIds = keep.Select(k => k.Id).ToHashSet();
        _db.MenuItems.RemoveRange(current.Where(m => !keepIds.Contains(m.Id)));
        await _db.SaveChangesAsync(ct);

        var count = 0;
        var sort = 0;
        async Task AddLevel(JsonArray level, int? parentId, int depth, string path)
        {
            var i = 0;
            foreach (var node in level)
            {
                var here = $"{path}[{i++}]";
                if (node is not JsonObject o) { warnings.Add($"{here}: kein Eintrag."); continue; }
                var label = Str(o["label"]).Trim();
                var url = Str(o["url"]).Trim();
                if (label.Length == 0) { warnings.Add($"{here}: label fehlt – übersprungen."); continue; }
                if (url.Length == 0) url = "#";
                if (!AssistantBlocks.IsSafeLink(url)) { warnings.Add($"{here}: \"{url}\" ist kein erlaubter Link – übersprungen."); continue; }
                if (count >= 80) { warnings.Add("Mehr als 80 Menüeinträge – Rest ignoriert."); return; }
                var item = new MenuItem
                {
                    Menu = key, Locale = locale, ParentId = parentId, Label = Cap(label, 80), Url = Cap(url, 500), SortOrder = sort++,
                    OpenInNewTab = Bool(o["newTab"]) ?? false, Visibility = MenuItem.NormaliseVisibility(Str(o["visibility"])),
                };
                _db.MenuItems.Add(item);
                await _db.SaveChangesAsync(ct);
                count++;
                if (o["children"] is JsonArray kids && kids.Count > 0)
                {
                    if (depth + 1 >= MenuTree.MaxLevels) warnings.Add($"{here}: höchstens {MenuTree.MaxLevels} Ebenen – Untereinträge ignoriert.");
                    else await AddLevel(kids, item.Id, depth + 1, here + ".children");
                }
            }
        }
        await AddLevel(items, null, 0, "items");
        foreach (var k in keep) k.SortOrder = sort++;
        await _db.SaveChangesAsync(ct);
        return Ok($"Menü „{key}“ ({locale}) neu gesetzt: {count} Einträge{(keep.Count > 0 ? $", {keep.Count} Plugin-Element(e) behalten" : "")}",
            new JsonObject { ["menu"] = key, ["locale"] = locale, ["items"] = count }, warnings, null, changed: true);
    }

    private async Task<ToolOutcome> UpdateThemeAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("das Design ändern");
        var t = await ActiveTemplateAsync(ct, tracked: true) ?? throw new ToolError("Kein Design", "Es gibt kein aktives Template.");
        await SnapshotTemplateAsync(t, ct);
        var warnings = new List<string>();
        var changed = new List<string>();
        if (args["values"] is JsonObject values)
            foreach (var (k, v) in values)
            {
                var s = Str(v).Trim();
                // A colour must be #rgb/#rrggbb; the optional ones may also be emptied (= "follow the defaults").
                string? Color(bool optional)
                {
                    if (optional && s.Length == 0) return "";
                    var n = TemplateFonts.NormalizeColorOr(s, "");
                    return n.Length > 0 ? n : null;
                }
                string? ok = k switch
                {
                    "AccentColor" or "HeadingColor" or "TextColor" or "BackgroundColor" or "AltBackground" => Color(false),
                    "SecondaryColor" or "HeaderBackground" or "HeaderTextColor" => Color(true),
                    "HeadingFont" or "BodyFont" => TemplateFonts.Families.Contains(s) ? s : null,
                    "ButtonStyle" => s is "solid" or "outline" ? s : null,
                    "ButtonRadius" => int.TryParse(s, out var r) ? Math.Clamp(r, 0, 60).ToString() : null,
                    "ContainerWidth" => int.TryParse(s, out var w) ? Math.Clamp(w, 800, 1800).ToString() : null,
                    "HeaderPadding" => int.TryParse(s, out var hp) ? Math.Clamp(hp, 0, 60).ToString() : null,
                    _ => null,
                };
                if (ok is null) { warnings.Add($"values.{k}: \"{s}\" ist nicht gültig oder das Feld gibt es nicht – ignoriert."); continue; }
                var prop = typeof(Template).GetProperty(k)!;
                prop.SetValue(t, ok);
                changed.Add(k);
            }
        if (args["params"] is JsonObject ps)
        {
            var schema = TemplateParams.Schema(t.ParametersJson).Where(p => p.Id.Length > 0).ToDictionary(p => p.Id, StringComparer.Ordinal);
            var stored = JsonNode.Parse(string.IsNullOrWhiteSpace(t.ParamValuesJson) ? "{}" : t.ParamValuesJson) as JsonObject ?? new JsonObject();
            foreach (var (k, v) in ps)
            {
                if (!schema.TryGetValue(k, out var p)) { warnings.Add($"params.{k}: Parameter gibt es nicht."); continue; }
                var n = Pages.Admin.Templates.CustomizeModel.Normalize(p, Str(v));
                if (n is null) { warnings.Add($"params.{k}: \"{Str(v)}\" passt nicht zum Typ {p.Type}."); continue; }
                stored[k] = n;
                changed.Add("Parameter " + k);
            }
            t.ParamValuesJson = stored.ToJsonString();
        }
        // The template CSS goes into a <style> on every page: no markup may ride along, and it keeps the editor's own length limit.
        string CleanCss(string css) => TemplateFonts.Code(css).Replace("</", "<\\/");
        if (args.ContainsKey("customCss")) { t.CustomCss = CleanCss(Str(args["customCss"])); changed.Add("CSS ersetzt"); }
        if (Str(args["appendCss"]) is { Length: > 0 } add) { t.CustomCss = (t.CustomCss.TrimEnd() + "\n\n" + CleanCss(add)).Trim(); changed.Add("CSS ergänzt"); }
        if (t.CustomCss.Length > TemplateFonts.MaxInlineCode)
            throw new ToolError("CSS zu lang", $"Das CSS wäre {t.CustomCss.Length} Zeichen lang, erlaubt sind {TemplateFonts.MaxInlineCode}. Kürzer fassen.");
        await _db.SaveChangesAsync(ct);
        return Ok(changed.Count == 0 ? "Design: nichts geändert" : $"Design „{t.Name}“ geändert: {string.Join(", ", changed)}", new JsonObject { ["changed"] = changed.Count }, warnings, null, changed: changed.Count > 0);
    }

    private static readonly (string Arg, string Key, string Label)[] SettingArgs =
    [
        ("siteName", SettingKeys.SiteName, "Name der Website"),
        ("footerText", SettingKeys.FooterText, "Fußzeilentext"),
        ("logoUrl", SettingKeys.LogoUrl, "Logo"),
        ("faviconUrl", SettingKeys.FaviconUrl, "Favicon"),
        ("contactRecipient", SettingKeys.ContactRecipient, "Empfänger Kontaktformular"),
    ];

    private async Task<ToolOutcome> UpdateSettingsAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("Einstellungen ändern");
        var warnings = new List<string>();
        var changed = new List<string>();
        var media = await _db.Media.AsNoTracking().Select(m => m.Url).ToListAsync(ct);
        foreach (var (arg, key, label) in SettingArgs)
        {
            if (!args.ContainsKey(arg)) continue;
            var value = Str(args[arg]).Trim();
            if (arg is "logoUrl" or "faviconUrl" && value.Length > 0 && !media.Contains(value, StringComparer.OrdinalIgnoreCase)) { warnings.Add($"{arg} \"{value}\" ist nicht in der Mediathek – ignoriert."); continue; }
            if (arg == "contactRecipient" && value.Length > 0 && !System.Net.Mail.MailAddress.TryCreate(value, out _)) { warnings.Add($"\"{value}\" ist keine E-Mail-Adresse – ignoriert."); continue; }
            var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            Journal("setting", key, row is null ? null : JsonSerializer.Serialize(row.Value));
            if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = Cap(value, 2000) });
            else row.Value = Cap(value, 2000);
            changed.Add(label);
        }
        await _db.SaveChangesAsync(ct);
        return Ok(changed.Count == 0 ? "Einstellungen: nichts geändert" : $"Einstellungen geändert: {string.Join(", ", changed)}", new JsonObject { ["changed"] = changed.Count }, warnings, null, changed: changed.Count > 0);
    }

    /// <summary>The assistant draws a picture itself: its SVG is rebuilt by <see cref="SafeSvg"/> (drawing elements only) and goes
    /// into the media library like an upload. What the cleaning dropped is said, so a model that tried an embedded photo or a
    /// font from outside knows why it is missing.</summary>
    private async Task<ToolOutcome> CreateSvgAsync(JsonObject args, CancellationToken ct)
    {
        RequireUnlocked("neue Grafiken in die Mediathek legen");
        var svg = Str(args["svg"]).Trim();
        if (svg.Length == 0) throw new ToolError("Keine Grafik", "svg fehlt (der SVG-Code).");
        var safe = SafeSvg.Sanitize(svg) ?? throw new ToolError("Kein gültiges SVG", "Das ist kein gültiges SVG (wohlgeformtes XML mit <svg> als Wurzel, xmlns=\"http://www.w3.org/2000/svg\").");
        var warnings = new List<string>();
        if (safe.Length < svg.Length * 0.8)
            warnings.Add("Beim Säubern ist ein Teil weggefallen (z. B. Skripte, eingebettete Bilder, Links oder Schriften von außen). Nur Zeichenelemente bleiben erhalten.");
        if (!safe.Contains("viewBox")) warnings.Add("Ohne viewBox skaliert die Grafik schlecht – setze viewBox auf dem <svg>.");
        var name = Pages.Admin.Pages.IndexModel.Slugify(Str(args["name"])) is { Length: > 0 } n ? n : "grafik";
        var url = await _files.SaveSvgAsync(safe, name + ".svg", ct);
        var media = await _db.Media.FirstAsync(m => m.Url == url, ct);
        if (Str(args["alt"]) is { Length: > 0 } alt) media.Alt = Cap(alt, 300);
        media.Tags = "KI-Grafik";
        Journal("media", media.Id.ToString(), null);
        await _db.SaveChangesAsync(ct);
        _blocks.KnowMedia(url);
        return Ok($"Grafik „{name}.svg“ gezeichnet und in die Mediathek gelegt", new JsonObject { ["url"] = url, ["chars"] = safe.Length }, warnings, null, changed: true);
    }

    // ============================================================================================== journal (for undo)

    private bool Journalled(string kind, string key) =>
        _db.AiChanges.Local.Any(c => c.ConversationId == ConversationId && c.Run == Run && c.Kind == kind && c.Key == key)
        || _db.AiChanges.Any(c => c.ConversationId == ConversationId && c.Run == Run && c.Kind == kind && c.Key == key);

    /// <summary>Records the state before the run, the first time the run touches it (null = the run created it).</summary>
    private void Journal(string kind, string key, string? before)
    {
        if (Journalled(kind, key)) return;
        _db.AiChanges.Add(new AiChange { ConversationId = ConversationId, Run = Run, Kind = kind, Key = key, BeforeJson = before });
    }

    private async Task SnapshotPageAsync(int pageId, CancellationToken ct)
    {
        if (Journalled("page", pageId.ToString())) return;
        var p = await _db.Pages.AsNoTracking().FirstAsync(x => x.Id == pageId, ct);
        var blocks = await _db.ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId).ToListAsync(ct);
        Journal("page", pageId.ToString(), AssistantUndo.PageSnapshot(p, blocks));
        await _db.SaveChangesAsync(ct);
    }

    private async Task SnapshotMenuAsync(string menu, string locale, CancellationToken ct)
    {
        var key = menu + "|" + locale;
        if (Journalled("menu", key)) return;
        var items = await _db.MenuItems.AsNoTracking().Where(m => m.Menu == menu && m.Locale == locale).ToListAsync(ct);
        Journal("menu", key, JsonSerializer.Serialize(items.Select(i => new AssistantUndo.MenuRow(i.Id, i.ParentId, i.Label, i.Url, i.SortOrder, i.OpenInNewTab, i.Icon, i.Visibility)).ToList()));
        await _db.SaveChangesAsync(ct);
    }

    private async Task SnapshotTemplateAsync(Template t, CancellationToken ct)
    {
        if (Journalled("template", t.Id.ToString())) return;
        Journal("template", t.Id.ToString(), AssistantUndo.TemplateSnapshot(t));
        await _db.SaveChangesAsync(ct);
    }

    // ============================================================================================== helpers

    /// <summary>Hangs validated blocks under a page (top level). The page navigation is set on EVERY block, children
    /// included: for a page that is being created its id is still 0, and a child that only knew its parent would be saved
    /// with page 0.</summary>
    private static void AddTree(Page page, ContentBlock? parent, List<AssistantBlocks.Input> input, int start)
    {
        var sort = start;
        foreach (var b in input)
        {
            var cb = NewBlock(page, b, sort++);
            if (parent is null) page.Blocks.Add(cb); else parent.Children.Add(cb);
        }
    }

    private static ContentBlock NewBlock(Page page, AssistantBlocks.Input b, int sort = 0)
    {
        var cb = new ContentBlock { Page = page, PageId = page.Id, BlockType = b.Type, DataJson = b.Data.ToJsonString(), SortOrder = sort };
        var i = 0;
        foreach (var c in b.Children) cb.Children.Add(NewBlock(page, c, i++));
        return cb;
    }

    private static JsonObject IdNode(ContentBlock c)
    {
        var o = new JsonObject { ["id"] = c.Id, ["type"] = c.BlockType };
        if (c.Children.Count > 0) o["children"] = new JsonArray(c.Children.OrderBy(x => x.SortOrder).Select(x => (JsonNode)IdNode(x)).ToArray());
        return o;
    }

    private async Task<JsonArray> IdsTreeAsync(int pageId, int? parent, CancellationToken ct)
    {
        var all = await _db.ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId).OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync(ct);
        JsonArray Level(int? p) => new(all.Where(b => b.ParentId == p).Select(b =>
        {
            var o = new JsonObject { ["id"] = b.Id, ["type"] = b.BlockType };
            var kids = Level(b.Id);
            if (kids.Count > 0) o["children"] = kids;
            return (JsonNode)o;
        }).ToArray());
        return Level(parent);
    }

    private static int InsertIndex(List<ContentBlock> siblings, JsonNode? after, List<string> warnings)
    {
        var a = Str(after).Trim();
        if (a.Length == 0 || a == "end") return siblings.Count;
        if (a == "start") return 0;
        if (int.TryParse(a, out var id))
        {
            var i = siblings.FindIndex(s => s.Id == id);
            if (i >= 0) return i + 1;
            warnings.Add($"after: Block {id} steht nicht auf dieser Ebene – am Ende eingefügt.");
        }
        else warnings.Add($"after: \"{a}\" verstanden als Ende.");
        return siblings.Count;
    }

    private async Task<bool> InSubtreeAsync(int blockId, int rootId, CancellationToken ct)
    {
        int? cur = blockId;
        for (var guard = 0; cur is not null && guard < 50; guard++)
        {
            if (cur == rootId) return true;
            cur = await _db.ContentBlocks.AsNoTracking().Where(b => b.Id == cur).Select(b => b.ParentId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    private async Task<Page> FindPageAsync(JsonObject args, CancellationToken ct, bool tracked)
    {
        var raw = args["page"] ?? args["id"] ?? args["slug"];
        var q = tracked ? _db.Pages : _db.Pages.AsNoTracking();
        Page? page = null;
        if (Int(raw) is int id) page = await q.FirstOrDefaultAsync(p => p.Id == id, ct);
        else
        {
            var s = Str(raw).Trim().Trim('/');
            if (s.Length == 0 && Scope.PageId is int sp) page = await q.FirstOrDefaultAsync(p => p.Id == sp, ct);
            else
            {
                var slug = s.Length == 0 ? "home" : s;
                var locale = Locale(args["locale"]);
                page = await q.FirstOrDefaultAsync(p => p.Slug == slug && p.Locale == locale, ct)
                    ?? await q.FirstOrDefaultAsync(p => p.Slug == Pages.Admin.Pages.IndexModel.Slugify(slug) && p.Locale == locale, ct);
            }
        }
        return page ?? throw new ToolError("Seite nicht gefunden", $"Seite \"{Str(raw)}\" gibt es nicht. site_overview listet alle Seiten mit id.");
    }

    private async Task<ContentBlock> FindBlockAsync(JsonNode? raw, CancellationToken ct)
    {
        if (Int(raw) is not int id) throw new ToolError("Block-ID fehlt", "block muss die id eines Blocks sein (aus read_page).");
        return await _db.ContentBlocks.FirstOrDefaultAsync(b => b.Id == id, ct)
            ?? throw new ToolError("Block nicht gefunden", $"Block {id} gibt es nicht (mehr). Lies die Seite neu mit read_page.");
    }

    private void RequireWritablePage(Page page)
    {
        if (page.OwnerPlugin is not null)
            throw new ToolError($"Plugin-Seite „{page.Title}“", "Diese Seite gehört einem Plugin und wird im Seiten-Editor gestaltet, nicht vom Assistenten.");
        if (Scope.Locked && Scope.PageId is int sp && page.Id != sp) throw OutOfScope($"die Seite „{page.Title}“ ändern");
    }

    private void RequireUnlocked(string what)
    {
        if (Scope.Locked) throw OutOfScope(what);
    }

    private ToolError OutOfScope(string what) => new("Außerhalb des freigegebenen Bereichs",
        $"Nicht erlaubt: {what}. Der Nutzer hat die Änderungen auf {(Scope.BlockId is not null ? "den gewählten Block" : "diese Seite")} beschränkt. Sag ihm, was du außerhalb ändern würdest – er kann die Beschränkung aufheben.");

    private async Task<Template?> ActiveTemplateAsync(CancellationToken ct, bool tracked)
    {
        var q = tracked ? _db.Templates : _db.Templates.AsNoTracking();
        return await q.FirstOrDefaultAsync(t => t.IsActive, ct) ?? await q.OrderBy(t => t.Id).FirstOrDefaultAsync(ct);
    }

    private string Locale(JsonNode? raw)
    {
        var l = Str(raw).Trim().ToLowerInvariant();
        return l.Length > 0 && Localizer.SupportedCultures.Contains(l) ? l : Localizer.DefaultCulture;
    }

    private static List<string> ActiveLanguages(string setting)
    {
        var list = setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => Localizer.SupportedCultures.Contains(l)).ToList();
        if (!list.Contains(Localizer.DefaultCulture)) list.Insert(0, Localizer.DefaultCulture);
        return list;
    }

    /// <summary>Where a page is on the public site: the default language at the root (unless the Standardroute is off), others under their prefix.</summary>
    public static string PublicUrl(string slug, string locale)
    {
        var prefix = Localizer.PrefixFor(locale);
        return slug == "home" ? (prefix.Length == 0 ? "/" : prefix) : $"{prefix}/{slug}";
    }

    private string FieldLabel(BlockDefinition def, string id) =>
        def.Fields.FirstOrDefault(f => f.Id == id) is { } f ? _t[f.Label] : id;

    private string Names(List<AssistantBlocks.Input> input) =>
        string.Join(", ", input.Take(6).Select(i => _t[_blocks.Get(i.Type)?.Name ?? i.Type])) + (input.Count > 6 ? ", …" : "");

    private static int Count(List<AssistantBlocks.Input> input) => input.Sum(i => 1 + Count(i.Children));

    private static string Cap(string s, int max) => s.Length > max ? s[..max] : s;
    private static string Str(JsonNode? n) => AssistantBlocks.Str(n);

    private static int? Int(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l) && l is > 0 and < int.MaxValue) return (int)l;
            if (v.TryGetValue<double>(out var d) && d is > 0 and < int.MaxValue && d == Math.Floor(d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out var p)) return p;
        }
        return null;
    }

    private static bool? Bool(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s.Trim().ToLowerInvariant() switch { "true" or "1" or "ja" or "yes" => true, "false" or "0" or "nein" or "no" => false, _ => null };
        return null;
    }

    private static ToolOutcome Ok(string summary, JsonNode result, List<string>? warnings = null, int? pageId = null, bool changed = false) =>
        new(true, summary, result, warnings ?? new(), pageId, changed);

    private static ToolOutcome Fail(string summary, string reason) =>
        new(false, summary, new JsonObject { ["error"] = reason }, new());

    /// <summary>A refusal that ends the tool call: a short German summary for the editor, the reason for the model.</summary>
    private sealed class ToolError(string summary, string message) : Exception(message)
    {
        public string Summary { get; } = summary;
    }
}
