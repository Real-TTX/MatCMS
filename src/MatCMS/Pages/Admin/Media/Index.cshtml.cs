using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Media;

/// <summary>
/// The media library. A click on an item opens its INFO (what it is, where it is used, alt text, tags); editing a picture is
/// a dialog of its own (<see cref="MediaEditor"/>: crop, rotate, flip, size, focal point — or the code of an SVG). The picture
/// operations answer as JSON for that dialog; metadata saving and deleting stay plain form posts (PRG).
/// </summary>
[RequestFormLimits(MultipartBodyLengthLimit = 40L * 1024 * 1024)]
[RequestSizeLimit(40L * 1024 * 1024)]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MediaEditor _editor;
    private readonly IServiceProvider _services;

    public IndexModel(AppDbContext db, MediaEditor editor, IServiceProvider services)
    {
        _db = db;
        _editor = editor;
        _services = services;
    }

    public List<MatCMS.Models.Media> Items { get; private set; } = new();
    public List<string> AllTags { get; private set; } = new();
    public string? ActiveTag { get; private set; }

    public async Task OnGetAsync(string? tag)
    {
        ActiveTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        // Ordering is done per gallery block now (not here), so just show newest uploads first.
        var all = await _db.Media.OrderByDescending(m => m.Id).ToListAsync();
        await _editor.BackfillSizesAsync(all, ct: HttpContext.RequestAborted);

        AllTags = all.SelectMany(m => MatCMS.Content.TagUtil.Split(m.Tags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Items = ActiveTag is null
            ? all
            : all.Where(m => MatCMS.Content.TagUtil.Split(m.Tags).Contains(ActiveTag, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Alt text and tags (the info dialog).</summary>
    public async Task<IActionResult> OnPostSaveAsync(int id, string? tags, string? alt)
    {
        var m = await _db.Media.FindAsync(id);
        if (m is not null)
        {
            m.Tags = MatCMS.Content.TagUtil.Normalize(tags);
            m.Alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Medium gespeichert.";
        }
        return RedirectToPage(new { tag = ActiveTag });
    }

    /// <summary>The focal point (the editor's "Fokuspunkt"), in percent; no values = back to the centre.</summary>
    public async Task<IActionResult> OnPostFocalAsync(int id, int? focalX, int? focalY)
    {
        var m = await _db.Media.FindAsync(id);
        if (m is null) return new JsonResult(new { ok = false, error = "Medium nicht gefunden." });
        m.FocalX = focalX is null || focalY is null ? null : Math.Clamp(focalX.Value, 0, 100);
        m.FocalY = focalX is null || focalY is null ? null : Math.Clamp(focalY.Value, 0, 100);
        await _db.SaveChangesAsync();
        return new JsonResult(new { ok = true, media = Dto(m) });
    }

    /// <summary>The edited picture from the editor (Cropper.js result): in place ("Übernehmen") or as a copy.</summary>
    public async Task<IActionResult> OnPostEditAsync(int id, string? mode, string? edit, IFormFile? file)
    {
        if (file is null) return new JsonResult(new { ok = false, error = "Kein Bild erhalten." });
        await using var stream = file.OpenReadStream();
        var (m, error) = await _editor.SaveEditAsync(id, stream, asCopy: mode == "copy", edit, HttpContext.RequestAborted);
        return new JsonResult(new { ok = m is not null, error, media = m is null ? null : Dto(m) });
    }

    public async Task<IActionResult> OnPostResetAsync(int id)
    {
        var (m, error) = await _editor.ResetAsync(id, HttpContext.RequestAborted);
        return new JsonResult(new { ok = m is not null, error, media = m is null ? null : Dto(m) });
    }

    public async Task<IActionResult> OnPostReplaceAsync(int id, IFormFile? file)
    {
        if (file is null) return new JsonResult(new { ok = false, error = "Keine Datei erhalten." });
        var (m, error) = await _editor.ReplaceAsync(id, file, HttpContext.RequestAborted);
        return new JsonResult(new { ok = m is not null, error, media = m is null ? null : Dto(m) });
    }

    public async Task<IActionResult> OnGetSvgAsync(int id)
    {
        var code = await _editor.ReadSvgAsync(id, HttpContext.RequestAborted);
        return code is null ? NotFound() : Content(code, "text/plain; charset=utf-8");
    }

    public async Task<IActionResult> OnPostSvgAsync(int id, string? code)
    {
        var (m, clean, error) = await _editor.SaveSvgAsync(id, code ?? "", HttpContext.RequestAborted);
        return new JsonResult(new { ok = m is not null, error, clean, media = m is null ? null : Dto(m) });
    }

    /// <summary>Where the picture is used — asked when its info opens.</summary>
    public async Task<IActionResult> OnGetUsesAsync(int id)
    {
        var m = await _db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return NotFound();
        var pluginData = _services.GetRequiredService<MatCMS.Services.Plugins.PluginDataService>();
        var uses = await _editor.UsesAsync(m, ct => pluginData.CorpusAsync(_db, ct), HttpContext.RequestAborted);
        return new JsonResult(uses.Select(u => new { kind = u.Kind, title = u.Title, link = u.Link }));
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var m = await _db.Media.FindAsync(id);
        if (m is not null)
        {
            _editor.DeleteFiles(m);
            _db.Media.Remove(m);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Medium gelöscht.";
        }
        return RedirectToPage(new { tag = ActiveTag });
    }

    /// <summary>What the dialogs know about an item — the same shape the cards carry as data attributes.</summary>
    public static object Dto(MatCMS.Models.Media m) => new
    {
        id = m.Id, url = m.Url, name = m.FileName, alt = m.Alt ?? "", tags = m.Tags, type = m.ContentType, size = m.SizeBytes,
        width = m.Width, height = m.Height, focalX = m.FocalX, focalY = m.FocalY, edit = m.CropJson, hasOriginal = m.OriginalUrl is not null,
        original = m.OriginalUrl, created = m.CreatedAt,
        kind = MediaEditor.IsSvg(m) ? "svg" : MediaEditor.IsEditable(m) ? "image" : MediaEditor.IsImage(m) ? "gif" : "file",
        version = DateTime.UtcNow.Ticks,
    };
}
