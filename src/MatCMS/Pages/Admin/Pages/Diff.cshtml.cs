using System.Net;
using System.Text.Json.Nodes;
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
/// Compares every language version of a page, block by block. The comparison is FIELD-LEVEL: each
/// block's translatable text fields (Text/Textarea/RichText, plus text sub-fields of list fields) are
/// compared individually against the source after normalisation (HTML stripped, whitespace collapsed,
/// case-folded, edge punctuation removed). That yields a robust "not yet translated" signal (a field
/// still equal to the source) and a per-block/-locale translation percentage — no noisy word-diff.
/// Each cell links into an inline dialog to fix the translation on the spot.
/// </summary>
public class DiffModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly BlockRegistry _registry;
    public DiffModel(AppDbContext db, BlockRegistry registry) { _db = db; _registry = registry; }

    public PageEntity Source { get; private set; } = default!;   // the reference (default locale)
    public List<string> Locales { get; private set; } = new();   // translation locales (columns), in order

    public List<Row> Rows { get; private set; } = new();
    /// <summary>Per-locale tallies for the summary bar (locale → counts).</summary>
    public Dictionary<string, Counts> Totals { get; private set; } = new();

    public sealed class Counts
    {
        public int Translated, Partial, Identical, Missing, Extra;   // block counts by status
        public int TransFields, NonEmptyFields;                      // for the percentage
        public int Percent => NonEmptyFields > 0 ? (int)Math.Round(100.0 * TransFields / NonEmptyFields) : 100;
    }

    /// <summary>One translation's cell in a block row.</summary>
    public record Cell(string Locale, string Status, int Sim, int TargetPageId, int TargetBlockId);

    /// <summary>One block, aligned across every version.</summary>
    public record Row(int Index, string BlockType, string SourcePreview, string SourceDataJson,
                      int SourceBlockId, IReadOnlyList<Cell> Cells);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var page = await _db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (page is null) return NotFound();

        var group = string.IsNullOrWhiteSpace(page.TranslationGroup)
            ? new List<PageEntity> { await _db.Pages.Include(p => p.Blocks).AsNoTracking().FirstAsync(p => p.Id == id) }
            : await _db.Pages.Include(p => p.Blocks).AsNoTracking()
                .Where(p => p.TranslationGroup == page.TranslationGroup).ToListAsync();

        Source = group.FirstOrDefault(p => p.Locale == Localizer.DefaultCulture) ?? group[0];
        var translations = group.Where(p => p.Id != Source.Id)
            .OrderBy(p => Array.IndexOf(Localizer.SupportedCultures, p.Locale)).ToList();
        Locales = translations.Select(p => p.Locale).ToList();
        var localePageId = translations.ToDictionary(p => p.Locale, p => p.Id);
        foreach (var loc in Locales) Totals[loc] = new Counts();

        var src = Flatten(Source.Blocks);
        var flat = translations.ToDictionary(p => p.Locale, p => Flatten(p.Blocks));

        // Align every translation to the source by block type (LCS): a block missing/added anywhere is
        // detected, not just at the tail.
        var matched = new Dictionary<string, ContentBlock?[]>();
        var extras = new Dictionary<string, List<ContentBlock>>();
        foreach (var loc in Locales)
        {
            var (m, ex) = Align(src, flat[loc]);
            matched[loc] = m; extras[loc] = ex;
        }

        // One row per source block.
        for (var i = 0; i < src.Count; i++)
        {
            var sb = src[i];
            var srcTexts = ExtractTexts(sb.BlockType, sb.DataJson);
            var cells = new List<Cell>();
            foreach (var loc in Locales)
            {
                var tb = matched[loc][i];
                string status; int sim;
                if (tb is null) { status = "missing"; sim = 0; Totals[loc].Missing++; }
                else
                {
                    var tgtTexts = ExtractTexts(tb.BlockType, tb.DataJson);
                    var (st, sm, trans, nonEmpty) = Compare(srcTexts, tgtTexts);
                    status = st; sim = sm;
                    Totals[loc].TransFields += trans;
                    Totals[loc].NonEmptyFields += nonEmpty;
                    switch (status)
                    {
                        case "translated": Totals[loc].Translated++; break;
                        case "identical": Totals[loc].Identical++; break;
                        case "partial": Totals[loc].Partial++; break;
                    }
                }
                cells.Add(new Cell(loc, status, sim, tb is null ? 0 : localePageId[loc], tb?.Id ?? 0));
            }
            Rows.Add(new Row(i + 1, sb.BlockType, Preview(srcTexts), sb.DataJson ?? "{}", sb.Id, cells));
        }

        // Extra rows: blocks only present in a translation (no source counterpart).
        var extraNo = src.Count;
        foreach (var loc in Locales)
        {
            foreach (var eb in extras[loc])
            {
                Totals[loc].Extra++;
                var cells = Locales.Select(l => l == loc
                    ? new Cell(l, "extra", 0, localePageId[loc], eb.Id)
                    : new Cell(l, "na", 0, 0, 0)).ToList();
                Rows.Add(new Row(++extraNo, eb.BlockType, "", "{}", 0, cells));
            }
        }
        return Page();
    }

    // ---- Field-level comparison ------------------------------------------------------------------

    private static bool IsTextField(FieldType t) => t is FieldType.Text or FieldType.Textarea or FieldType.RichText;

    /// <summary>All translatable strings of a block, in a stable order: top-level Text/Textarea/RichText
    /// fields, then text sub-fields of each list item. Uses the block's schema so only real content
    /// (not layout/enum/url/image values) is compared.</summary>
    private List<string> ExtractTexts(string? blockType, string? dataJson)
    {
        var result = new List<string>();
        var def = blockType is null ? null : _registry.Get(blockType);
        if (def is null || string.IsNullOrWhiteSpace(dataJson)) return result;
        JsonObject? obj;
        try { obj = JsonNode.Parse(dataJson) as JsonObject; } catch { return result; }
        if (obj is null) return result;

        static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        foreach (var f in def.Fields)
        {
            if (IsTextField(f.Type))
            {
                var s = Clean(Str(obj[f.Id]));
                if (s.Length > 0) result.Add(s);
            }
            else if (f.Type == FieldType.List && f.ItemFields.Count > 0 && obj[f.Id] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject io) continue;
                    foreach (var sub in f.ItemFields)
                        if (IsTextField(sub.Type))
                        {
                            var s = Clean(Str(io[sub.Id]));
                            if (s.Length > 0) result.Add(s);
                        }
                }
            }
        }
        return result;
    }

    /// <summary>Returns (status, sim%, translatedFields, nonEmptyFields) comparing two field-text lists.</summary>
    private static (string status, int sim, int trans, int nonEmpty) Compare(List<string> src, List<string> tgt)
    {
        int nonEmpty = 0, trans = 0;
        var max = Math.Max(src.Count, tgt.Count);
        for (var i = 0; i < max; i++)
        {
            var s = i < src.Count ? src[i] : "";
            var t = i < tgt.Count ? tgt[i] : "";
            if (s.Length == 0) continue;               // nothing to translate in this field
            nonEmpty++;
            if (t.Length == 0) continue;               // target empty → not translated
            if (Norm(s) != Norm(t)) trans++;           // differs after normalisation → translated
            // else: identical to source → not yet translated
        }
        if (nonEmpty == 0) return ("empty", 100, 0, 0);
        var sim = (int)Math.Round(100.0 * trans / nonEmpty);
        var status = trans == nonEmpty ? "translated" : trans == 0 ? "identical" : "partial";
        return (status, sim, trans, nonEmpty);
    }

    private static string Preview(List<string> texts)
    {
        var s = string.Join(" · ", texts);
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    // Strip HTML/markup + collapse whitespace (keeps case/punctuation for display).
    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.IndexOf('<') >= 0
            ? WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " "))
            : s;
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
    }

    // Normalise for the "identical / not translated" test: case-fold + strip edge punctuation.
    private static string Norm(string s)
    {
        s = Clean(s).ToLowerInvariant();
        return s.Trim(' ', '.', ',', ';', ':', '!', '?', '„', '"', '“', '”', '\'', '»', '«', '-', '–', '—', '…');
    }

    // Depth-first tree order (parent then children), so groups/columns compare in reading order.
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

    // Aligns a translation's blocks to the source by BlockType (LCS): per source index the matched
    // block (or null = missing) plus the translation-only blocks (extras).
    private static (ContentBlock?[] matched, List<ContentBlock> extras) Align(
        List<ContentBlock> src, List<ContentBlock> tgt)
    {
        int n = src.Count, m = tgt.Count;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = string.Equals(src[i].BlockType, tgt[j].BlockType, StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var matched = new ContentBlock?[n];
        var extras = new List<ContentBlock>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(src[x].BlockType, tgt[y].BlockType, StringComparison.Ordinal))
            { matched[x] = tgt[y]; x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { matched[x] = null; x++; }
            else { extras.Add(tgt[y]); y++; }
        }
        while (x < n) { matched[x] = null; x++; }
        while (y < m) { extras.Add(tgt[y]); y++; }
        return (matched, extras);
    }
}
