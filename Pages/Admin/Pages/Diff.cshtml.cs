using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages.Admin.Pages;

/// <summary>Side-by-side comparison of two pages in the same translation group, block by block —
/// so a translator sees which blocks are missing, extra, still identical (untranslated) or changed.</summary>
public class DiffModel : PageModel
{
    private readonly AppDbContext _db;
    public DiffModel(AppDbContext db) => _db = db;

    public PageEntity Left { get; private set; } = default!;   // source (default locale if available)
    public PageEntity Right { get; private set; } = default!;  // the translation
    public List<Row> Rows { get; private set; } = new();
    public int SameCount, DiffCount, MissingCount, ExtraCount;

    public record Row(int Index, string? LeftType, string? RightType, string LeftText, string RightText, string Status);

    public async Task<IActionResult> OnGetAsync(int id, int other)
    {
        var a = await _db.Pages.Include(p => p.Blocks).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        var b = await _db.Pages.Include(p => p.Blocks).AsNoTracking().FirstOrDefaultAsync(p => p.Id == other);
        if (a is null || b is null) return NotFound();

        // Put the source (default-locale page if either is default) on the left, the translation on the right.
        if (a.Locale == Localizer.DefaultCulture) { Left = a; Right = b; }
        else if (b.Locale == Localizer.DefaultCulture) { Left = b; Right = a; }
        else { Left = b; Right = a; }

        var l = Flatten(Left.Blocks);
        var r = Flatten(Right.Blocks);
        var max = Math.Max(l.Count, r.Count);
        for (var i = 0; i < max; i++)
        {
            var lb = i < l.Count ? l[i] : null;
            var rb = i < r.Count ? r[i] : null;
            var lt = lb is null ? "" : TextOf(lb.DataJson);
            var rt = rb is null ? "" : TextOf(rb.DataJson);
            string status;
            if (lb is null) { status = "extra"; ExtraCount++; }
            else if (rb is null) { status = "missing"; MissingCount++; }
            else if (!string.Equals(lb.BlockType, rb.BlockType, StringComparison.Ordinal)) { status = "struct"; DiffCount++; }
            else if (string.Equals(lt.Trim(), rt.Trim(), StringComparison.Ordinal))
            {
                // Identical text: nothing to translate, OR not yet translated (only meaningful if there IS text).
                status = lt.Trim().Length == 0 ? "empty" : "same";
                if (status == "same") SameCount++;
            }
            else { status = "diff"; DiffCount++; }

            Rows.Add(new Row(i + 1, lb?.BlockType, rb?.BlockType, lt, rt, status));
        }
        return Page();
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
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.String:
                        var s = (e.GetString() ?? "").Trim();
                        if (s.Length > 1 && s.Any(char.IsLetter) && !s.StartsWith("/") && !s.StartsWith("http"))
                            parts.Add(s);
                        break;
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject()) Walk(p.Value);
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
}
