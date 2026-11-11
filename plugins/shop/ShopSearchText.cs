using System.Net;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Shop;

/// <summary>
/// The text rules of the site search (<c>site.search</c>), copied from the host's <c>SiteSearch</c> when the shop left the image: a native plugin
/// sees only the plugin API. They must stay the SAME measure as the host's — the bot puts pages, posts and products in one order by this score —
/// so a change to <c>src/MatCMS/Services/SiteSearch.cs</c> (stop words, score, excerpt length) belongs here too.
/// </summary>
internal static class ShopSearchText
{
    private const int Excerpt = 900;

    // Words that say nothing about WHICH page is meant; without this, "wie kann ich die …" matched every page.
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "und", "oder", "aber", "mit", "für", "von", "vom", "zum", "zur",
        "auf", "aus", "bei", "bis", "ist", "sind", "war", "wie", "was", "wer", "wann", "wo", "warum", "welche", "welcher", "welches", "kann", "können",
        "ich", "du", "sie", "wir", "ihr", "mir", "mich", "mein", "meine", "dein", "ihre", "ihren", "es", "nicht", "auch", "noch", "nur", "gibt", "habe", "hat",
        "haben", "wird", "werden", "bitte", "danke", "hallo", "the", "and", "for", "you", "your", "what", "how", "are", "can", "with", "this", "that", "does",
        "is", "an", "in", "im", "am", "um", "so", "da", "als", "wenn", "dass", "man", "mal", "gerne", "eure", "euer", "euch", "uns", "unser", "unsere"
    };

    /// <summary>The terms of a question that are worth looking for: words of three letters or more that are not filler.</summary>
    public static IReadOnlyList<string> Terms(string query) =>
        Regex.Matches((query ?? "").ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().Take(12).ToList();

    /// <summary>How well a text matches the terms (0 = not at all). Both texts LOWER case.</summary>
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
    public static string ExcerptOf(string text, IReadOnlyList<string> terms)
    {
        if (text.Length <= Excerpt) return text;
        var lower = text.ToLowerInvariant();
        var at = terms.Select(t => lower.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - Excerpt / 3);
        var end = Math.Min(text.Length, start + Excerpt);
        return (start > 0 ? "… " : "") + text[start..end].Trim() + (end < text.Length ? " …" : "");
    }

    /// <summary>The text of some HTML: tags, scripts and styles out, entities decoded.</summary>
    public static string Plain(string html)
    {
        var s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(s);
    }

    private static int Count(string text, string term, int cap)
    {
        int n = 0, i = 0;
        while (n < cap && (i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0) { n++; i += term.Length; }
        return n;
    }
}
