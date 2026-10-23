using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Services;

/// <summary>
/// A plain keyword search over the site's published pages — what a plugin gets through <see cref="IPluginSite.SearchPagesAsync"/> (the AI bot
/// answers from it). The text of a page is what its blocks SAY: every string field, HTML stripped, without addresses, picture paths and the layout
/// fields (<c>_css</c>, …). A hidden block, an unpublished page and a plugin's own page (<c>_shop-product</c>) are never searched; a page for members
/// only is found only by a visitor who may open it — the same rule the page itself applies.
/// <para>The page texts are built once per minute and language and kept in memory; a search is then a scan of a few hundred strings.</para>
/// </summary>
public static class SiteSearch
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private const int MaxPages = 2000, MaxTextPerPage = 40_000, Excerpt = 900;

    private sealed record Entry(string Title, string Url, string Text, string TitleLower, string TextLower, PageAccess Access, string? Role);

    // Words that say nothing about WHICH page is meant; without this, "wie kann ich die …" matched every page.
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "und", "oder", "aber", "mit", "für", "von", "vom", "zum", "zur",
        "auf", "aus", "bei", "bis", "ist", "sind", "war", "wie", "was", "wer", "wann", "wo", "warum", "welche", "welcher", "welches", "kann", "können",
        "ich", "du", "sie", "wir", "ihr", "mir", "mich", "mein", "meine", "dein", "ihre", "ihren", "es", "nicht", "auch", "noch", "nur", "gibt", "habe", "hat",
        "haben", "wird", "werden", "bitte", "danke", "hallo", "the", "and", "for", "you", "your", "what", "how", "are", "can", "with", "this", "that", "does",
        "is", "an", "in", "im", "am", "um", "an", "so", "da", "als", "wenn", "dass", "man", "mal", "gerne", "eure", "euer", "euch", "uns", "unser", "unsere"
    };

    /// <summary>The terms of a question that are worth looking for: words of three letters or more that are not filler.</summary>
    public static IReadOnlyList<string> Terms(string query) =>
        Regex.Matches((query ?? "").ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().Take(12).ToList();

    public static async Task<IReadOnlyList<SiteSearchHit>> SearchAsync(AppDbContext db, IMemoryCache cache, string query, int max, PluginMember? visitor,
        string locale, CancellationToken ct = default)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return Array.Empty<SiteSearchHit>();
        var entries = await cache.GetOrCreateAsync("sitesearch:" + locale, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            return await LoadAsync(db, locale, ct);
        }) ?? new List<Entry>();

        var hits = new List<SiteSearchHit>();
        foreach (var p in entries)
        {
            if (!MayOpen(p, visitor)) continue;
            var score = Score(p.TitleLower, p.TextLower, terms);
            if (score > 0) hits.Add(new SiteSearchHit(p.Title, p.Url, Around(p.Text, p.TextLower, terms), score));
        }
        return hits.OrderByDescending(h => h.Score).Take(Math.Clamp(max, 1, 20)).ToList();
    }

    /// <summary>How well a text matches the terms (0 = not at all); the same measure for pages and for what plugins search (posts, products), so their
    /// hits can be put in one order. Both texts LOWER case.</summary>
    public static double Score(string titleLower, string textLower, IReadOnlyList<string> terms)
    {
        double score = 0; var matched = 0;
        foreach (var t in terms)
        {
            var inTitle = titleLower.Contains(t, StringComparison.Ordinal);
            var count = Count(textLower, t, 6);
            if (inTitle || count > 0) matched++;
            score += (inTitle ? 4 : 0) + count;
        }
        return matched == 0 ? 0 : score * (1 + (double)matched / terms.Count);   // a text with ALL the words beats one that repeats one of them
    }

    /// <summary>An excerpt of a plain text around the first term found.</summary>
    public static string ExcerptOf(string text, IReadOnlyList<string> terms) => Around(text, text.ToLowerInvariant(), terms);

    private static bool MayOpen(Entry p, PluginMember? visitor) =>
        p.Access != PageAccess.Members || (visitor is not null && (string.IsNullOrWhiteSpace(p.Role) || visitor.InRole(p.Role)));

    private static int Count(string text, string term, int cap)
    {
        int n = 0, i = 0;
        while (n < cap && (i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0) { n++; i += term.Length; }
        return n;
    }

    private static string Around(string text, string lower, IReadOnlyList<string> terms)
    {
        if (text.Length <= Excerpt) return text;
        var at = terms.Select(t => lower.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - Excerpt / 3);
        var end = Math.Min(text.Length, start + Excerpt);
        return (start > 0 ? "… " : "") + text[start..end].Trim() + (end < text.Length ? " …" : "");
    }

    private static async Task<List<Entry>> LoadAsync(AppDbContext db, string locale, CancellationToken ct)
    {
        var pages = await db.Pages.AsNoTracking()
            .Where(p => p.IsPublished && p.Locale == locale && p.OwnerPlugin == null && !p.Slug.StartsWith("_"))
            .OrderBy(p => p.Id).Take(MaxPages)
            .Select(p => new { p.Id, p.Title, p.Slug, p.Locale, p.MetaDescription, p.Access, p.RequiredRole })
            .ToListAsync(ct);
        var ids = pages.Select(p => p.Id).ToList();
        var blocks = await db.ContentBlocks.AsNoTracking().Where(b => ids.Contains(b.PageId))
            .OrderBy(b => b.PageId).ThenBy(b => b.ParentId).ThenBy(b => b.SortOrder)
            .Select(b => new { b.Id, b.PageId, b.ParentId, b.DataJson }).ToListAsync(ct);

        // A hidden block hides what is inside it as well.
        var hidden = new HashSet<int>(blocks.Where(b => IsHidden(b.DataJson)).Select(b => b.Id));
        var parent = blocks.ToDictionary(b => b.Id, b => b.ParentId);
        bool Hidden(int id) { for (int? x = id; x is int v; x = parent.GetValueOrDefault(v)) if (hidden.Contains(v)) return true; return false; }

        var byPage = blocks.Where(b => !Hidden(b.Id)).GroupBy(b => b.PageId).ToDictionary(g => g.Key, g => g.Select(b => b.DataJson).ToList());
        var list = new List<Entry>();
        foreach (var p in pages)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(p.MetaDescription)) sb.Append(p.MetaDescription).Append(". ");
            foreach (var json in byPage.GetValueOrDefault(p.Id) ?? new List<string>())
            {
                AppendText(json, sb);
                if (sb.Length > MaxTextPerPage) break;
            }
            var text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            if (text.Length > MaxTextPerPage) text = text[..MaxTextPerPage];
            var url = SiteContext.PageUrl(new Page { Slug = p.Slug, Locale = p.Locale });
            list.Add(new Entry(p.Title, url, text, p.Title.ToLowerInvariant(), text.ToLowerInvariant(), p.Access, p.RequiredRole));
        }
        return list;
    }

    private static bool IsHidden(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("_hidden", out var h)
                && h.ValueKind == JsonValueKind.String && h.GetString() is "1" or "true";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>The words of one block: every string value (also inside lists), never a field whose name starts with "_" (layout) and never a value that
    /// is an address or a file — those are where things are, not what the page says.</summary>
    private static void AppendText(string json, StringBuilder sb)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            Walk(doc.RootElement, null, sb);
        }
        catch (JsonException) { }
    }

    private static void Walk(JsonElement e, string? name, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (!p.Name.StartsWith('_')) Walk(p.Value, p.Name, sb);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Walk(x, name, sb);
                break;
            case JsonValueKind.String:
                var s = e.GetString() ?? "";
                if (s.Length < 2 || LooksLikeAddress(s) || name is "css" or "style" or "script" or "icon" or "color" or "layout") return;
                if (s.TrimStart().StartsWith('[') || s.TrimStart().StartsWith('{'))
                {
                    // A list stored as a JSON string (older blocks do that).
                    try { using var inner = JsonDocument.Parse(s); Walk(inner.RootElement, name, sb); return; } catch (JsonException) { }
                }
                sb.Append(Plain(s)).Append(' ');
                break;
        }
    }

    private static bool LooksLikeAddress(string s) =>
        s.StartsWith('/') || s.StartsWith('#') || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || s.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(s, @"^[\w\-]+$") && s.Length < 24 && !s.Contains(' ') && s.Any(char.IsDigit);

    /// <summary>HTML to text: scripts and styles dropped, tags become spaces, entities decoded.</summary>
    public static string Plain(string html)
    {
        var s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(s);
    }
}
