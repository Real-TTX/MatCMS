using System.Text;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Finds — and, on explicit request, removes — content that nothing on the site references any more:
/// orphaned media (images), unused custom components, and (for manual review) installed plugins. Built
/// because sites are rebuilt in a SHARED workbench whose media library leaks foreign images into a
/// backup, which then land on the live site on restore (see the "Aufräumen" page).
/// <para><b>Detection is deliberately conservative</b> — a file counts as USED as soon as its filename
/// appears ANYWHERE in the site's text (any block, page/template CSS, layout HTML, component HTML, post
/// body, setting value, menu, form, mail template). A false "unused" would delete a live image, so the
/// bar for "unused" is: referenced by nothing at all. The instance does this itself because only it holds
/// the full content model; the cloud would have to re-parse a backup (a coupling the repo avoids).</para>
/// </summary>
public class CleanupService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly PluginRunner _plugins;

    public CleanupService(AppDbContext db, IWebHostEnvironment env, PluginRunner plugins)
    {
        _db = db; _env = env; _plugins = plugins;
    }

    public record MediaItem(int Id, string Url, string FileName, string? Alt, long SizeBytes);
    public record ComponentItem(int Id, string Type, string Name);
    /// <summary>All installed plugins. Plugins carry NO safe "unused" signal (they run server-side and
    /// their block types are only known at runtime), so they are offered for MANUAL selection, never
    /// pre-ticked. `UsedBlockTypes`/`ProvidesBlocks` is informational only.</summary>
    public record PluginItem(int Id, string Key, string Name, string Version, bool Enabled);
    public record Report(
        List<MediaItem> UnusedMedia, long UnusedMediaBytes,
        List<ComponentItem> UnusedComponents,
        List<PluginItem> Plugins);

    /// <summary>Read-only: computes what is currently unused. Called for the preview AND re-run just
    /// before a delete, so anything that became referenced in the meantime is never removed.</summary>
    public async Task<Report> AnalyzeAsync(CancellationToken ct = default)
    {
        var text = await BuildReferenceCorpusAsync(ct);

        // Media whose file (by bare filename, to also catch CSS url() and thumbnails) appears nowhere.
        // Also keep any row that is the SOURCE of a crop — deleting an original out from under its crop
        // lineage would be surprising even if the original itself is not placed anywhere.
        var cropSourceIds = await _db.Media.Where(m => m.SourceMediaId != null)
            .Select(m => m.SourceMediaId!.Value).Distinct().ToListAsync(ct);
        var cropSources = cropSourceIds.ToHashSet();

        var unusedMedia = new List<MediaItem>();
        long unusedBytes = 0;
        foreach (var m in await _db.Media.AsNoTracking().OrderByDescending(m => m.Id).ToListAsync(ct))
        {
            var fn = Path.GetFileName(m.Url ?? "");
            if (string.IsNullOrWhiteSpace(fn)) continue;                     // malformed row → leave alone
            if (cropSources.Contains(m.Id)) continue;                        // an original with crops → keep
            if (text.Contains(fn, StringComparison.OrdinalIgnoreCase)) continue;
            unusedMedia.Add(new MediaItem(m.Id, m.Url, m.FileName, m.Alt, m.SizeBytes));
            unusedBytes += m.SizeBytes;
        }

        // Components used ⇔ a ContentBlock (top-level OR child) carries their Type.
        var usedTypes = (await _db.ContentBlocks.AsNoTracking().Select(b => b.BlockType).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedComponents = (await _db.Components.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
            .Where(c => !usedTypes.Contains(c.Type))
            .Select(c => new ComponentItem(c.Id, c.Type, c.Name))
            .ToList();

        var plugins = (await _db.Plugins.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct))
            .Select(p => new PluginItem(p.Id, p.Key, p.Name, p.Version, p.Enabled))
            .ToList();

        return new Report(unusedMedia, unusedBytes, unusedComponents, plugins);
    }

    /// <summary>Deletes exactly the given selections — but re-analyses first and drops any media/component
    /// that is NO LONGER unused (a safety re-check against a change since the preview). Plugins are removed
    /// as chosen (manual selection; no "unused" set). Returns what was actually removed.</summary>
    public async Task<(int media, int components, int plugins)> DeleteAsync(
        IEnumerable<int> mediaIds, IEnumerable<string> componentTypes, IEnumerable<string> pluginKeys,
        CancellationToken ct = default)
    {
        var report = await AnalyzeAsync(ct);
        var mediaAllowed = report.UnusedMedia.Select(m => m.Id).ToHashSet();
        var compAllowed = report.UnusedComponents.Select(c => c.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int media = 0, comps = 0, plugins = 0;

        // --- Media: file + thumbnails + row ---
        var toDeleteMedia = mediaIds.Where(mediaAllowed.Contains).ToHashSet();
        if (toDeleteMedia.Count > 0)
        {
            var rows = await _db.Media.Where(m => toDeleteMedia.Contains(m.Id)).ToListAsync(ct);
            foreach (var m in rows)
            {
                DeleteMediaFiles(m.Url);
                _db.Media.Remove(m);
                media++;
            }
            await _db.SaveChangesAsync(ct);
        }

        // --- Components: row ---
        var compTypes = componentTypes.Where(compAllowed.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (compTypes.Count > 0)
        {
            var rows = await _db.Components.Where(c => compTypes.Contains(c.Type)).ToListAsync(ct);
            _db.Components.RemoveRange(rows);
            comps = rows.Count;
            await _db.SaveChangesAsync(ct);
        }

        // --- Plugins: row + re-register + asset folder (mirrors the Plugins admin delete) ---
        var keys = pluginKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet();
        if (keys.Count > 0)
        {
            var rows = await _db.Plugins.Where(p => keys.Contains(p.Key)).ToListAsync(ct);
            var removedKeys = rows.Select(p => p.Key).ToList();
            _db.Plugins.RemoveRange(rows);
            plugins = rows.Count;
            await _db.SaveChangesAsync(ct);
            if (plugins > 0)
            {
                await _plugins.RunAllAsync();                 // drop the deleted plugins from the live registry
                foreach (var key in removedKeys)
                {
                    var dir = StoragePaths.PluginAssetDir(_env, key);
                    if (!string.IsNullOrWhiteSpace(key) && Directory.Exists(dir))
                        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
                }
            }
        }

        return (media, comps, plugins);
    }

    /// <summary>Removes a media file and its derived thumbnails. Mirrors the media admin's delete (which
    /// only removes the upload) and additionally sweeps every <c>thumbs/{width}/{file}.webp</c>(+<c>.failed</c>),
    /// which that handler omits — otherwise stale thumbnails linger for a filename that may be reused.</summary>
    private void DeleteMediaFiles(string? url)
    {
        var file = Path.GetFileName(url ?? "");
        if (string.IsNullOrWhiteSpace(file)) return;

        var upload = Path.Combine(StoragePaths.Uploads(_env), file);
        try { if (File.Exists(upload)) File.Delete(upload); } catch { /* ignore */ }

        var thumbsRoot = StoragePaths.Thumbs(_env);
        if (!Directory.Exists(thumbsRoot)) return;
        foreach (var widthDir in Directory.EnumerateDirectories(thumbsRoot))   // one sub-folder per width
        {
            foreach (var suffix in new[] { ".webp", ".failed" })
            {
                var t = Path.Combine(widthDir, file + suffix);
                try { if (File.Exists(t)) File.Delete(t); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>All site text a media filename could appear in, concatenated. Conservative on purpose —
    /// if a filename shows up anywhere here the media is kept.</summary>
    private async Task<string> BuildReferenceCorpusAsync(CancellationToken ct)
    {
        var sb = new StringBuilder(1 << 16);
        void Add(string? s) { if (!string.IsNullOrEmpty(s)) { sb.Append(s); sb.Append('\n'); } }

        foreach (var s in await _db.ContentBlocks.AsNoTracking().Select(b => b.DataJson).ToListAsync(ct)) Add(s);

        foreach (var p in await _db.Pages.AsNoTracking()
            .Select(p => new { p.CustomCss, p.TemplateParamsJson, p.MetaDescription }).ToListAsync(ct))
        { Add(p.CustomCss); Add(p.TemplateParamsJson); Add(p.MetaDescription); }

        foreach (var t in await _db.Templates.AsNoTracking()
            .Select(t => new { t.CustomCss, t.CustomJs, t.LayoutHtml, t.LoginHtml, t.ParamValuesJson, t.ParametersJson, t.PartsJson }).ToListAsync(ct))
        { Add(t.CustomCss); Add(t.CustomJs); Add(t.LayoutHtml); Add(t.LoginHtml); Add(t.ParamValuesJson); Add(t.ParametersJson); Add(t.PartsJson); }

        foreach (var c in await _db.Components.AsNoTracking().Select(c => new { c.TemplateHtml, c.FieldsJson }).ToListAsync(ct))
        { Add(c.TemplateHtml); Add(c.FieldsJson); }

        foreach (var p in await _db.Posts.AsNoTracking()
            .Select(p => new { p.TitleImage, p.ContentHtml, p.Excerpt, p.AttachmentsJson, p.GalleryJson }).ToListAsync(ct))
        { Add(p.TitleImage); Add(p.ContentHtml); Add(p.Excerpt); Add(p.AttachmentsJson); Add(p.GalleryJson); }

        foreach (var v in await _db.SiteSettings.AsNoTracking().Select(s => s.Value).ToListAsync(ct)) Add(v);
        foreach (var u in await _db.MenuItems.AsNoTracking().Select(m => m.Url).ToListAsync(ct)) Add(u);
        foreach (var d in await _db.Forms.AsNoTracking().Select(f => f.DefinitionJson).ToListAsync(ct)) Add(d);
        foreach (var b in await _db.MailTemplates.AsNoTracking().Select(m => m.Body).ToListAsync(ct)) Add(b);

        return sb.ToString();
    }
}
