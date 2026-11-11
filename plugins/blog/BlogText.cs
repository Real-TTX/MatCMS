using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MatCMS.Plugin.Blog;

/// <summary>
/// The small pure helpers the blog took from the core while it was built in (tags, dates, the site search's scoring, link checks). Copied,
/// not shared: the plugin references nothing but the plugin API, and these must behave exactly as before so a post looks and is found
/// the same as when the blog was part of the CMS.
/// </summary>
internal static class BlogText
{
    // ---- tags (the core's TagUtil) ------------------------------------------------------------------------

    /// <summary>The tags of a comma-separated list, trimmed, empty ones dropped.</summary>
    public static IEnumerable<string> SplitTags(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A tag list as it is stored: "a, b", each tag once (case does not count).</summary>
    public static string NormalizeTags(string? input) =>
        string.Join(", ", SplitTags(input).Distinct(StringComparer.OrdinalIgnoreCase));

    // ---- dates ----------------------------------------------------------------------------------------------

    private static readonly string[] MonthsDe = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"];
    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>
    /// The long date of a post in its own language. Written by hand: the image runs with invariant globalization, where a culture's
    /// month names are silently English. German is "04. Oktober 2026" exactly as the built-in blog wrote it; English "4 October 2026";
    /// every other language the numeric "04.10.2026" (better than a German month on a French page).
    /// </summary>
    public static string LongDate(DateTime d, string? locale) => (locale ?? "de") switch
    {
        "de" => $"{d.Day:00}. {MonthsDe[d.Month - 1]} {d.Year}",
        "en" => $"{d.Day} {MonthsEn[d.Month - 1]} {d.Year}",
        _ => $"{d.Day:00}.{d.Month:00}.{d.Year}"
    };

    // ---- search (the core's SiteSearch scoring, so posts rank like pages) --------------------------------------

    private const int Excerpt = 900;

    // Words that say nothing about WHICH post is meant — the same list the site's page search uses.
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "und", "oder", "aber", "mit", "für", "von", "vom", "zum", "zur",
        "auf", "aus", "bei", "bis", "ist", "sind", "war", "wie", "was", "wer", "wann", "wo", "warum", "welche", "welcher", "welches", "kann", "können",
        "ich", "du", "sie", "wir", "ihr", "mir", "mich", "mein", "meine", "dein", "ihre", "ihren", "es", "nicht", "auch", "noch", "nur", "gibt", "habe", "hat",
        "haben", "wird", "werden", "bitte", "danke", "hallo", "the", "and", "for", "you", "your", "what", "how", "are", "can", "with", "this", "that", "does",
        "is", "an", "in", "im", "am", "um", "so", "da", "als", "wenn", "dass", "man", "mal", "gerne", "eure", "euer", "euch", "uns", "unser", "unsere"
    };

    /// <summary>The words of a question worth looking for: three letters or more, not filler.</summary>
    public static IReadOnlyList<string> Terms(string query) =>
        Regex.Matches((query ?? "").ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().Take(12).ToList();

    /// <summary>How well a text answers the terms; 0 = not at all. A text with ALL the words beats one that repeats one of them.</summary>
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
        return matched == 0 ? 0 : score * (1 + (double)matched / terms.Count);
    }

    private static int Count(string text, string term, int cap)
    {
        int n = 0, i = 0;
        while (n < cap && (i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0) { n++; i += term.Length; }
        return n;
    }

    /// <summary>A piece of the text around the first term found.</summary>
    public static string ExcerptOf(string text, IReadOnlyList<string> terms)
    {
        if (text.Length <= Excerpt) return text;
        var lower = text.ToLowerInvariant();
        var at = terms.Select(t => lower.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - Excerpt / 3);
        var end = Math.Min(text.Length, start + Excerpt);
        return (start > 0 ? "… " : "") + text[start..end].Trim() + (end < text.Length ? " …" : "");
    }

    /// <summary>The text of some HTML: scripts and styles dropped, tags turned into spaces, entities decoded.</summary>
    public static string Plain(string html)
    {
        var s = Regex.Replace(html ?? "", @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(s);
    }

    // ---- links and lists --------------------------------------------------------------------------------------

    /// <summary>A link a post may name as its source: a full http(s) address (a missing scheme becomes https), else null — never
    /// <c>javascript:</c> or a typo.</summary>
    public static string? NormalizeUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.Length > 2000) return null;
        if (!s.Contains("://")) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") && u.Host.Contains('.')
            ? u.ToString() : null;
    }

    /// <summary>Keeps only well-formed gallery entries pointing at a local or http(s) image.</summary>
    public static string CleanGallery(string? json)
    {
        var list = new JsonArray();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var u = el.TryGetProperty("url", out var uv) ? uv.GetString() ?? "" : "";
                if (!(u.StartsWith('/') || u.StartsWith("https://") || u.StartsWith("http://"))
                    || u.IndexOfAny(new[] { '"', '\'', '<', '>' }) >= 0) continue;
                var alt = el.TryGetProperty("alt", out var av) ? av.GetString() ?? "" : "";
                list.Add(new JsonObject { ["url"] = u, ["alt"] = alt });
            }
        }
        catch (JsonException) { /* malformed → empty gallery */ }
        return list.ToJsonString();
    }
}
