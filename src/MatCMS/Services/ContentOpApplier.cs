using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Applies the content operations the cloud offers on the heartbeat (Stage 2, Increment 1: creating pages) —
/// the instance side of an AI change made through the cloud's MCP server. The cloud only ever ASKS; this is
/// where the site actually does the work, and it does it through its OWN validated writer
/// (<see cref="BlockGenerator.ValidateBlocks(string?)"/>, the exact validator the in-app AI page/site
/// generators use), never applying anything the cloud sent as code. Add-only: an existing slug is skipped,
/// never overwritten. Each op yields a <see cref="ContentOpReport"/> the caller sends back on the next beat.
/// </summary>
public class ContentOpApplier
{
    private readonly AppDbContext _db;
    private readonly BlockGenerator _blockGen;

    public ContentOpApplier(AppDbContext db, BlockGenerator blockGen)
    {
        _db = db;
        _blockGen = blockGen;
    }

    public async Task<ContentOpReport> ApplyAsync(PendingContentOp op, CancellationToken ct = default)
    {
        try
        {
            return op.Kind switch
            {
                "page.create" => await CreatePageAsync(op, ct),
                "pages.list" => await ListPagesAsync(op, ct),
                "page.read" => await ReadPageAsync(op, ct),
                "page.updateBlocks" => await UpdatePageBlocksAsync(op, ct),
                _ => Report(op, "failed", $"Unbekannte Aktion: {op.Kind}"),
            };
        }
        catch (Exception ex)
        {
            // A single bad op must never take the heartbeat down — report it failed and move on.
            return Report(op, "failed", ex.Message);
        }
    }

    private async Task<ContentOpReport> CreatePageAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");

        var title = (payload["title"]?.GetValue<string>() ?? "").Trim();
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? title);
        var showInNav = payload["showInNav"]?.GetValue<bool>() ?? true;
        var navLabel = payload["navLabel"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(title))
            return Report(op, "failed", "Kein Titel angegeben.");
        if (string.IsNullOrWhiteSpace(slug))
            return Report(op, "failed", "Kein gültiger Slug ableitbar.");
        if (Pages.Admin.Pages.IndexModel.IsReserved(slug))
            return Report(op, "failed", $"Slug „{slug}“ ist reserviert.");

        // Add-only: (Slug, Locale) is unique, so an existing page is left exactly as it is.
        if (await _db.Pages.AnyAsync(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture, ct))
            return Report(op, "skipped-exists", $"Seite „{slug}“ existiert bereits.");

        // The blocks travel as the "blocks" array; re-validate with the shared validator — unknown block
        // types and unknown fields are dropped here, exactly as for the in-app generators.
        var blocksJson = payload["blocks"]?.ToJsonString() ?? "[]";
        var validated = _blockGen.ValidateBlocks(blocksJson);

        var page = new Page
        {
            Title = title,
            Slug = slug,
            Locale = Localizer.DefaultCulture,
            TranslationGroup = Guid.NewGuid().ToString("N"),
            IsPublished = true,
            ShowInNav = showInNav,
            NavLabel = string.IsNullOrWhiteSpace(navLabel) ? null : navLabel.Trim(),
            NavOrder = showInNav ? await NextNavOrderAsync(ct) : 0,
        };
        var sort = 0;
        foreach (var (type, _, data) in validated)
            page.Blocks.Add(new ContentBlock { BlockType = type, DataJson = data.ToJsonString(), SortOrder = sort++ });

        _db.Pages.Add(page);
        await _db.SaveChangesAsync(ct);

        return Report(op, "applied", $"Seite „{slug}“ mit {validated.Count} Block/Blöcken angelegt.");
    }

    // --- Reads: the instance serializes its OWN content back (the cloud never parses the format) ----------

    private async Task<ContentOpReport> ListPagesAsync(PendingContentOp op, CancellationToken ct)
    {
        var pages = await _db.Pages.AsNoTracking()
            .OrderBy(p => p.Locale).ThenBy(p => p.NavOrder).ThenBy(p => p.Title)
            .Select(p => new { slug = p.Slug, title = p.Title, locale = p.Locale, published = p.IsPublished, showInNav = p.ShowInNav })
            .ToListAsync(ct);
        return ReportResult(op, JsonSerializer.Serialize(new { pages }), $"{pages.Count} Seite(n).");
    }

    private async Task<ContentOpReport> ReadPageAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? "");
        if (string.IsNullOrWhiteSpace(slug)) return Report(op, "failed", "Kein Slug angegeben.");

        var page = await _db.Pages.AsNoTracking()
            .Where(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture)
            .Select(p => new { p.Id, p.Title, p.Slug, p.IsPublished })
            .FirstOrDefaultAsync(ct);
        if (page is null) return Report(op, "failed", $"Seite „{slug}“ nicht gefunden.");

        // Top-level blocks in order. Nested/child blocks are out of scope for the v1 read (edits target the
        // top-level list); DataJson is forwarded as-is so the AI sees the real field values.
        var blocks = await _db.ContentBlocks.AsNoTracking()
            .Where(b => b.PageId == page.Id && b.ParentId == null)
            .OrderBy(b => b.SortOrder)
            .Select(b => new { type = b.BlockType, dataJson = b.DataJson })
            .ToListAsync(ct);

        var result = new JsonObject
        {
            ["title"] = page.Title,
            ["slug"] = page.Slug,
            ["published"] = page.IsPublished,
            ["blocks"] = new JsonArray(blocks.Select(b => (JsonNode)new JsonObject
            {
                ["type"] = b.type,
                ["data"] = JsonNode.Parse(string.IsNullOrWhiteSpace(b.dataJson) ? "{}" : b.dataJson),
            }).ToArray()),
        };
        return ReportResult(op, result.ToJsonString(), $"Seite „{slug}“ mit {blocks.Count} Block/Blöcken gelesen.");
    }

    private async Task<ContentOpReport> UpdatePageBlocksAsync(PendingContentOp op, CancellationToken ct)
    {
        var payload = JsonNode.Parse(op.PayloadJson) as JsonObject
            ?? throw new InvalidOperationException("PayloadJson ist kein Objekt.");
        var slug = Slugify(payload["slug"]?.GetValue<string>() ?? "");
        if (string.IsNullOrWhiteSpace(slug)) return Report(op, "failed", "Kein Slug angegeben.");

        var page = await _db.Pages
            .Include(p => p.Blocks)
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Locale == Localizer.DefaultCulture, ct);
        // Overwrite, so the page MUST already exist — this never creates one (that is page.create's job).
        if (page is null) return Report(op, "failed", $"Seite „{slug}“ nicht gefunden.");

        var blocksJson = payload["blocks"]?.ToJsonString() ?? "[]";
        var validated = _blockGen.ValidateBlocks(blocksJson);
        if (validated.Count == 0)
            return Report(op, "failed", "Keine gültigen Blöcke — Seite unverändert gelassen.");

        // Replace the whole top-level block list. Child blocks (if any) hang off their parents and are
        // removed with them via the cascade; the new list is a flat set of validated top-level blocks.
        _db.ContentBlocks.RemoveRange(page.Blocks);
        var sort = 0;
        foreach (var (type, _, data) in validated)
            page.Blocks.Add(new ContentBlock { PageId = page.Id, BlockType = type, DataJson = data.ToJsonString(), SortOrder = sort++ });
        page.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Report(op, "applied", $"Seite „{slug}“: {validated.Count} Block/Blöcke ersetzt.");
    }

    private async Task<int> NextNavOrderAsync(CancellationToken ct)
    {
        var max = await _db.Pages.Where(p => p.Locale == Localizer.DefaultCulture && p.ShowInNav)
            .MaxAsync(p => (int?)p.NavOrder, ct);
        return (max ?? 0) + 1;
    }

    private static ContentOpReport Report(PendingContentOp op, string outcome, string detail) =>
        new() { OpId = op.OpId, Outcome = outcome, Detail = detail };

    /// <summary>A successful READ: the serialized content rides in <see cref="ContentOpReport.ResultJson"/>.</summary>
    private static ContentOpReport ReportResult(PendingContentOp op, string resultJson, string detail) =>
        new() { OpId = op.OpId, Outcome = "applied", Detail = detail, ResultJson = resultJson };

    /// <summary>Lowercase, ASCII, hyphen-separated — the same shape a slug has everywhere else on the site.
    /// Deliberately conservative: a model-supplied slug is normalised, never trusted verbatim.</summary>
    private static string Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
            else if (ch == 'ä') sb.Append("ae");
            else if (ch == 'ö') sb.Append("oe");
            else if (ch == 'ü') sb.Append("ue");
            else if (ch == 'ß') sb.Append("ss");
            // everything else (punctuation, other scripts) is dropped
        }
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }
}
