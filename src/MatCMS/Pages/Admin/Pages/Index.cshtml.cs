using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AiService _ai;
    private readonly BlockGenerator _blockGen;
    public IndexModel(AppDbContext db, AiService ai, BlockGenerator blockGen)
    {
        _db = db;
        _ai = ai;
        _blockGen = blockGen;
    }

    /// <summary>True when AI is switched on for this site (shows the "generate website" action).</summary>
    public bool AiEnabled { get; private set; }

    /// <summary>One language version of a logical page (with its own public URL).</summary>
    public record Version(int Id, string Locale, bool IsPublished, string Url);

    /// <summary>A logical page and all its language versions (the "versions" of one page).</summary>
    public record Group(PageEntity Primary, string Url, IReadOnlyList<Version> Versions);

    public List<Group> Groups { get; private set; } = new();

    public async Task OnGetAsync()
    {
        AiEnabled = _ai.Enabled;
        var pages = await _db.Pages
            .OrderBy(p => p.NavOrder).ThenBy(p => p.FooterOrder).ThenBy(p => p.Title)
            .ToListAsync();

        // Group pages by their translation group (languages = versions of one logical page).
        // A page without a group is its own singleton. The default-locale page is the "primary".
        int localeRank(string loc) { var i = Array.IndexOf(Localizer.SupportedCultures.ToArray(), loc); return i < 0 ? 99 : i; }

        Groups = pages
            .GroupBy(p => string.IsNullOrWhiteSpace(p.TranslationGroup) ? $"__single:{p.Id}" : p.TranslationGroup!)
            .Select(g =>
            {
                var ordered = g.OrderBy(p => localeRank(p.Locale)).ThenBy(p => p.Id).ToList();
                var primary = ordered.FirstOrDefault(p => p.Locale == Localizer.DefaultCulture) ?? ordered[0];
                var versions = ordered
                    .Select(p => new Version(p.Id, p.Locale, p.IsPublished, MatCMS.Services.SiteContext.LocalizedUrl(p.Locale, p.Slug)))
                    .ToList();
                var url = versions.FirstOrDefault(v => v.Id == primary.Id)?.Url ?? "/";
                return new Group(primary, url, versions);
            })
            .OrderBy(g => g.Primary.NavOrder).ThenBy(g => g.Primary.FooterOrder).ThenBy(g => g.Primary.Title)
            .ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var page = await _db.Pages.FindAsync(id);
        if (page is not null)
        {
            // Delete the whole logical page — all its language versions (same translation group).
            var toDelete = string.IsNullOrWhiteSpace(page.TranslationGroup)
                ? new List<PageEntity> { page }
                : await _db.Pages.Where(p => p.TranslationGroup == page.TranslationGroup).ToListAsync();
            _db.Pages.RemoveRange(toDelete);
            await _db.SaveChangesAsync();
            var extra = toDelete.Count > 1 ? $" ({toDelete.Count} Sprachversionen)" : "";
            TempData["Flash"] = $"Seite „{page.Title}“ gelöscht{extra}.";
        }
        return RedirectToPage();
    }

    private static readonly HashSet<string> ReservedSlugs =
        new(StringComparer.OrdinalIgnoreCase) { "admin", "login", "logout", "error" };

    /// <summary>Slugs that collide with fixed application routes and would be unreachable.</summary>
    public static bool IsReserved(string slug) => ReservedSlugs.Contains(slug);

    public static string Slugify(string input)
    {
        input = input.Trim().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch == 'ä') sb.Append("ae");
            else if (ch == 'ö') sb.Append("oe");
            else if (ch == 'ü') sb.Append("ue");
            else if (ch == 'ß') sb.Append("ss");
            else if (ch is ' ' or '-' or '_' or '/') sb.Append('-');
            // else drop
        }
        var slug = sb.ToString();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    // --- AI "generate whole website" --------------------------------------------------------------
    // A briefing → several pages (title, slug, nav flag, blocks). Blocks reuse the shared BlockGenerator
    // validator; pages are ADD-ONLY (slugs deduped, existing pages never overwritten).

    public sealed record GenPage(string Title, string Slug, bool Nav, List<(string Type, string Name, JsonObject Data)> Blocks);

    /// <summary>Validates the model's site JSON: per page a non-empty title, a safe slug, a nav flag and
    /// a validated block list (shared validator). Pages with no valid blocks are dropped. Bounded to 6.</summary>
    private List<GenPage> ValidateSite(string? json)
    {
        var pages = new List<GenPage>();
        JsonNode? root;
        try { root = JsonNode.Parse(BlockGenerator.ExtractJsonArray(json)); } catch { return pages; }
        if (root is not JsonArray arr) return pages;

        foreach (var item in arr)
        {
            if (pages.Count >= 6) break;
            if (item is not JsonObject obj) continue;
            var title = ((obj["title"] as JsonValue)?.ToString() ?? "").Trim();
            if (title.Length == 0) continue;
            if (title.Length > 120) title = title[..120];
            var slug = Slugify((obj["slug"] as JsonValue)?.ToString() ?? title);
            if (slug.Length == 0) slug = Slugify(title);
            if (slug.Length == 0 || IsReserved(slug)) slug = "seite";
            var nav = false;
            if (obj["nav"] is JsonValue nv)
            {
                if (nv.TryGetValue<bool>(out var nb)) nav = nb;
                else if (nv.TryGetValue<string>(out var ns)) nav = ns.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "ja";
            }
            var blocks = _blockGen.ValidateBlocks(obj["blocks"] as JsonArray);
            if (blocks.Count == 0) continue;
            pages.Add(new GenPage(title, slug, nav, blocks));
        }
        return pages;
    }

    /// <summary>Proposes a whole website (several pages, each with blocks) from a briefing + the global
    /// AI instruction, validated against the registry. Returns { ok, pages[summary], proposed } WITHOUT
    /// saving; the client shows the list and — on confirm — posts `proposed` to CreateSite.</summary>
    public async Task<IActionResult> OnPostAiGenerateSiteAsync(string? briefing)
    {
        if (!_ai.Enabled)
            return new JsonResult(new { ok = false, error = "KI ist für diese Website nicht aktiviert." });
        if (string.IsNullOrWhiteSpace(briefing))
            return new JsonResult(new { ok = false, error = "Bitte beschreibe kurz, was die Website enthalten soll." });

        var (ok, text, error) = await _ai.GenerateSiteAsync(briefing.Trim(), _blockGen.BuildSpecText(), HttpContext.RequestAborted);
        if (!ok) return new JsonResult(new { ok = false, error = error ?? "KI-Aufruf fehlgeschlagen." });

        var pages = ValidateSite(text);
        if (pages.Count == 0)
            return new JsonResult(new { ok = false, error = "Die KI hat keine verwertbaren Seiten geliefert." });

        var proposed = new JsonArray();
        var summary = new List<object>();
        foreach (var p in pages)
        {
            var blocksArr = new JsonArray();
            foreach (var (type, _, data) in p.Blocks) blocksArr.Add(new JsonObject { ["type"] = type, ["data"] = data });
            proposed.Add(new JsonObject { ["title"] = p.Title, ["slug"] = p.Slug, ["nav"] = p.Nav, ["blocks"] = blocksArr });
            summary.Add(new { title = p.Title, slug = p.Slug, nav = p.Nav, blocks = p.Blocks.Count });
        }
        return new JsonResult(new { ok = true, pages = summary, proposed = proposed.ToJsonString() });
    }

    /// <summary>Creates the confirmed AI-proposed website. Re-validates the round-trip (client never
    /// trusted). Pages are ADD-ONLY — slugs are deduped so nothing existing is overwritten — published,
    /// with the nav pages appended to the main menu. Normal antiforgery form post → PRG to the list.</summary>
    public async Task<IActionResult> OnPostAiCreateSiteAsync(string? sitesJson)
    {
        if (!_ai.Enabled) return RedirectToPage();
        var pages = ValidateSite(sitesJson);
        if (pages.Count == 0)
        {
            TempData["FlashError"] = "Es gab keine gültigen Seiten zum Anlegen.";
            return RedirectToPage();
        }

        var used = new HashSet<string>(
            await _db.Pages.Where(p => p.Locale == Localizer.DefaultCulture).Select(p => p.Slug).ToListAsync(),
            StringComparer.OrdinalIgnoreCase);
        var navOrder = await _db.Pages.Select(p => (int?)p.NavOrder).MaxAsync() ?? 0;

        var created = 0;
        foreach (var gp in pages)
        {
            var slug = gp.Slug;
            var n = 2;
            while (used.Contains(slug)) slug = $"{gp.Slug}-{n++}";
            used.Add(slug);

            var page = new PageEntity
            {
                Title = gp.Title,
                Slug = slug,
                Locale = Localizer.DefaultCulture,
                IsPublished = true,
                ShowInNav = gp.Nav,
                NavOrder = gp.Nav ? ++navOrder : 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            var sort = 0;
            foreach (var (type, _, data) in gp.Blocks)
                page.Blocks.Add(new ContentBlock { BlockType = type, DataJson = data.ToJsonString(), SortOrder = sort++ });
            _db.Pages.Add(page);
            created++;
        }
        await _db.SaveChangesAsync();

        TempData["Flash"] = $"{created} Seite(n) von der KI angelegt.";
        return RedirectToPage();
    }
}
