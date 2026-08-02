using System.Net;
using System.Text;
using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

/// <summary>
/// Compares ALL language versions of a page at once, block by block, with a Git-style word-level
/// diff of every translation against the source: unchanged words plain, source-only words as
/// deletions, translation-only words as insertions. Lets a translator see exactly what differs and
/// spot fragments still identical to the source (i.e. not yet translated).
/// </summary>
public class DiffModel : PageModel
{
    private readonly AppDbContext _db;
    public DiffModel(AppDbContext db) => _db = db;

    public PageEntity Source { get; private set; } = default!;   // the reference (default locale)
    public List<string> Locales { get; private set; } = new();   // translation locales (columns), in order

    public List<Row> Rows { get; private set; } = new();
    /// <summary>Per-locale tallies for the summary bar (locale → counts).</summary>
    public Dictionary<string, Counts> Totals { get; private set; } = new();

    public sealed class Counts { public int Translated, Identical, Missing; }

    /// <summary>One translation's cell in a block row.</summary>
    public record Cell(string Locale, string DiffHtml, string Status);

    /// <summary>One block, aligned across every version.</summary>
    public record Row(int Index, string? BlockType, string SourceText, IReadOnlyList<Cell> Cells);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var page = await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();

        // All versions in the translation group (or just this page if it isn't grouped).
        var group = string.IsNullOrWhiteSpace(page.TranslationGroup)
            ? new List<PageEntity> { await _db.Pages.Include(p => p.Blocks).AsNoTracking().FirstAsync(p => p.Id == id) }
            : await _db.Pages.Include(p => p.Blocks).AsNoTracking()
                .Where(p => p.TranslationGroup == page.TranslationGroup).ToListAsync();

        Source = group.FirstOrDefault(p => p.Locale == Localizer.DefaultCulture) ?? group[0];
        var translations = group.Where(p => p.Id != Source.Id)
            .OrderBy(p => Array.IndexOf(Localizer.SupportedCultures, p.Locale)).ToList();
        Locales = translations.Select(p => p.Locale).ToList();
        foreach (var loc in Locales) Totals[loc] = new Counts();

        var src = Flatten(Source.Blocks);
        var flat = translations.ToDictionary(p => p.Locale, p => Flatten(p.Blocks));
        var max = new[] { src.Count }.Concat(flat.Values.Select(v => v.Count)).Max();

        for (var i = 0; i < max; i++)
        {
            var sb = i < src.Count ? src[i] : null;
            var sText = sb is null ? "" : TextOf(sb.DataJson);
            var cells = new List<Cell>();
            foreach (var loc in Locales)
            {
                var tb = i < flat[loc].Count ? flat[loc][i] : null;
                var tText = tb is null ? "" : TextOf(tb.DataJson);
                string status; string html;
                if (tb is null) { status = "missing"; html = ""; Totals[loc].Missing++; }
                else if (sb is not null && !string.Equals(sb.BlockType, tb.BlockType, StringComparison.Ordinal))
                { status = "struct"; html = WordDiff(sText, tText); Totals[loc].Translated++; }
                else if (string.Equals(sText.Trim(), tText.Trim(), StringComparison.Ordinal))
                {
                    status = sText.Trim().Length == 0 ? "empty" : "identical";
                    html = WebUtility.HtmlEncode(tText);
                    if (status == "identical") Totals[loc].Identical++;
                }
                else { status = "translated"; html = WordDiff(sText, tText); Totals[loc].Translated++; }
                cells.Add(new Cell(loc, html, status));
            }
            Rows.Add(new Row(i + 1, sb?.BlockType, sText, cells));
        }
        return Page();
    }

    // ---- Git-style word-level diff: renders `target` vs `source` with <del>/<ins>/plain spans. ----
    // Only useful when the texts actually SHARE words (partially translated / copied content): there
    // the diff highlights exactly what still equals the source. A proper translation into another
    // language shares almost nothing — interleaving every word as <del>de</del> <ins>en</ins> is pure
    // noise. So below a similarity threshold we show the plain target text instead.
    // 0.5: proper translations sharing many proper nouns (place/brand names) still stay below this;
    // genuinely half-translated content sits well above it.
    private const double DiffSimilarityThreshold = 0.5;

    private static string WordDiff(string source, string target)
    {
        var a = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int n = a.Length, m = b.Length;
        // LCS length table (small texts → plain DP is fine).
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        // Similarity = shared-word ratio. Fully different texts (real translations) render plainly.
        var maxLen = Math.Max(n, m);
        if (maxLen > 0 && (double)dp[0, 0] / maxLen < DiffSimilarityThreshold)
            return WebUtility.HtmlEncode(target);

        var sb = new StringBuilder();
        int x = 0, y = 0;
        static string Enc(string w) => WebUtility.HtmlEncode(w);
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { sb.Append(Enc(b[y])).Append(' '); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { sb.Append("<del>").Append(Enc(a[x])).Append("</del> "); x++; }
            else { sb.Append("<ins>").Append(Enc(b[y])).Append("</ins> "); y++; }
        }
        while (x < n) { sb.Append("<del>").Append(Enc(a[x])).Append("</del> "); x++; }
        while (y < m) { sb.Append("<ins>").Append(Enc(b[y])).Append("</ins> "); y++; }
        return sb.ToString().TrimEnd();
    }

    // Depth-first tree order (parent then its children), so groups/columns compare in reading order.
    private static List<ContentBlock> Flatten(ICollection<ContentBlock> blocks)
    {
        var result = new List<ContentBlock>();
        void Add(int? parentId)
        {
            foreach (var b in blocks.Where(x => x.ParentId == parentId).OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
            {
                result.Add(b);
                Add(b.Id);
            }
        }
        Add(null);
        return result;
    }

    // Collect translatable text from a block's DataJson (skips URLs/ids/enums), for a readable preview + compare.
    private static string TextOf(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson)) return "";
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var parts = new List<string>();
            // Layout/config properties hold enum-ish values ("left", "normal", "3") — machine
            // settings, not translatable text. They'd only pollute the comparison.
            var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "align", "width", "layout", "columns", "imageHeight", "size", "display",
                "showFilter", "source", "perPage", "limit", "form", "tag", "tags",
                "_width", "_spaceTop", "_spaceBottom", "buttonStyle", "icon",
                "imageSide", "bg", "fg", "position", "variant", "style"
            };
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = StripHtml((e.GetString() ?? "").Trim());
                        if (s.Length > 1 && s.Any(char.IsLetter) && !s.StartsWith("/") && !s.StartsWith("http"))
                            parts.Add(s);
                        break;
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject())
                            if (!skipKeys.Contains(p.Name))
                                Walk(p.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var it in e.EnumerateArray()) Walk(it);
                        break;
                }
            }
            Walk(doc.RootElement);
            return string.Join(" · ", parts);
        }
        catch { return ""; }
    }

    // Rich-text fields carry raw HTML (<p>, <br> …). Tags are markup, not translatable text — they
    // only pollute the word diff ("<p>Die" ≠ "<p>Villa"). Strip them and collapse the whitespace.
    private static string StripHtml(string s)
    {
        if (s.IndexOf('<') < 0) return s;
        var stripped = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        stripped = WebUtility.HtmlDecode(stripped);
        return System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
    }
}
