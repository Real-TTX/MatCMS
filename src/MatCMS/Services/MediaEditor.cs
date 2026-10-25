using System.Text.Json;
using System.Xml.Linq;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace MatCMS.Services;

/// <summary>
/// "Bild bearbeiten" in the media library. The editing itself happens in the browser (Cropper.js: crop, rotate, flip, target
/// size) — this is what the server does with the result: check it IS a picture, re-encode it in the media's own format
/// (nothing of the upload's bytes is kept as they came), and store it in one of two ways:
/// <list type="bullet">
/// <item><b>Übernehmen</b> — in place: the media keeps its address, so every page that shows the picture shows the edit at once.
/// The untouched original is set aside the first time (<see cref="Media.OriginalUrl"/>); every later edit starts from it again,
/// and "Auf Original zurücksetzen" brings it back. The old way — a crop was always a NEW file — left the pages showing the old
/// picture, and somebody had to find and re-pick it everywhere.</item>
/// <item><b>Als neues Bild</b> — a new library entry linked to its source, as crops were before.</item>
/// </list>
/// Thumbnails are invalidated and their URLs change (<see cref="ThumbnailService"/>), and <c>/uploads</c> is revalidated, so no
/// browser keeps showing the picture as it was.
/// <para>An SVG is not a bitmap: no crop, no resize — its CODE is edited instead, saved through <see cref="SafeSvg"/>. A GIF is
/// left alone (re-encoding would freeze its animation).</para>
/// </summary>
public sealed class MediaEditor
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ThumbnailService _thumbs;

    public const int MaxEdge = 8000;
    public const long MaxBytes = 30L * 1024 * 1024;
    private static readonly string[] Editable = [".jpg", ".jpeg", ".png", ".webp"];

    public MediaEditor(AppDbContext db, IWebHostEnvironment env, ThumbnailService thumbs)
    {
        _db = db;
        _env = env;
        _thumbs = thumbs;
    }

    public static string Ext(string? url) => Path.GetExtension(url ?? "").ToLowerInvariant();
    public static bool IsEditable(Media m) => Editable.Contains(Ext(m.Url));
    public static bool IsSvg(Media m) => Ext(m.Url) == ".svg";
    public static bool IsImage(Media m) => Ext(m.Url) is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".svg";

    private string PathOf(string url) => Path.Combine(StoragePaths.Uploads(_env), Path.GetFileName(url));

    // ---------------------------------------------------------------------------------------------- the edit

    /// <summary>Stores an edited picture. <paramref name="asCopy"/> = a new library entry; otherwise in place.</summary>
    public async Task<(Media? media, string? error)> SaveEditAsync(int id, Stream upload, bool asCopy, string? editJson, CancellationToken ct = default)
    {
        var m = await _db.Media.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return (null, "Medium nicht gefunden.");
        if (!IsEditable(m)) return (null, "Dieses Medium kann nicht so bearbeitet werden.");
        var (bytes, w, h, error) = await EncodeAsync(upload, Ext(m.Url), ct);
        if (bytes is null) return (null, error);
        var edit = CleanEditJson(editJson);

        if (asCopy)
        {
            var name = $"{Guid.NewGuid():N}{Ext(m.Url)}";
            await File.WriteAllBytesAsync(PathOf(name), bytes, ct);
            var copy = new Media
            {
                Url = "/uploads/" + name, FileName = CopyName(m.FileName), Alt = m.Alt, Tags = m.Tags, ContentType = m.ContentType,
                SizeBytes = bytes.Length, Width = w, Height = h, SortOrder = (await _db.Media.MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 1,
                SourceMediaId = m.SourceMediaId ?? m.Id, CropJson = edit, FocalX = m.FocalX, FocalY = m.FocalY,
            };
            _db.Media.Add(copy);
            await _db.SaveChangesAsync(ct);
            return (copy, null);
        }

        // In place: the original is set aside ONCE (a copy of the file under its own name, no library row), then the media's own
        // file is replaced — written beside it and moved over it, so a visitor never gets half a file.
        if (m.OriginalUrl is null || !File.Exists(PathOf(m.OriginalUrl)))
        {
            var orig = $"{Guid.NewGuid():N}{Ext(m.Url)}";
            File.Copy(PathOf(m.Url), PathOf(orig));
            m.OriginalUrl = "/uploads/" + orig;
        }
        await ReplaceFileAsync(m.Url, bytes, ct);
        m.SizeBytes = bytes.Length;
        m.Width = w;
        m.Height = h;
        m.CropJson = edit;
        await _db.SaveChangesAsync(ct);
        return (m, null);
    }

    /// <summary>Back to the original the in-place edits started from.</summary>
    public async Task<(Media? media, string? error)> ResetAsync(int id, CancellationToken ct = default)
    {
        var m = await _db.Media.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return (null, "Medium nicht gefunden.");
        if (m.OriginalUrl is null || !File.Exists(PathOf(m.OriginalUrl))) return (null, "Es gibt kein Original, auf das zurückgesetzt werden kann.");
        var bytes = await File.ReadAllBytesAsync(PathOf(m.OriginalUrl), ct);
        await ReplaceFileAsync(m.Url, bytes, ct);
        (m.Width, m.Height) = Measure(PathOf(m.Url));
        m.SizeBytes = bytes.Length;
        m.CropJson = null;
        await _db.SaveChangesAsync(ct);
        return (m, null);
    }

    /// <summary>A new file for the same library entry ("Datei ersetzen"): the address stays, every page shows the new picture. A
    /// picture is re-encoded into the entry's own format (its address ends in that extension); an SVG is cleaned; a document must
    /// be of the same type. The old original is no longer what this entry shows, so it goes.</summary>
    public async Task<(Media? media, string? error)> ReplaceAsync(int id, IFormFile file, CancellationToken ct = default)
    {
        var m = await _db.Media.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return (null, "Medium nicht gefunden.");
        if (file.Length == 0 || file.Length > MaxBytes) return (null, "Die Datei ist leer oder zu groß (höchstens 30 MB).");
        var ext = Ext(m.Url);
        byte[] bytes;
        int? w = null, h = null;
        if (IsSvg(m))
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var safe = SafeSvg.Sanitize(await reader.ReadToEndAsync(ct));
            if (safe is null) return (null, "Die Datei ist kein gültiges SVG.");
            bytes = System.Text.Encoding.UTF8.GetBytes(safe);
            (w, h) = SvgSize(safe);
        }
        else if (Editable.Contains(ext) || ext == ".gif")
        {
            if (ext == ".gif")
            {
                if (Ext(file.FileName) != ".gif") return (null, "Ein GIF kann nur durch ein GIF ersetzt werden.");
                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
                try { var info = Image.Identify(bytes); (w, h) = (info.Width, info.Height); }
                catch { return (null, "Die Datei ist kein lesbares Bild."); }
            }
            else
            {
                var enc = await EncodeAsync(file.OpenReadStream(), ext, ct);
                if (enc.bytes is null) return (null, enc.error);
                (bytes, w, h) = (enc.bytes, enc.w, enc.h);
            }
        }
        else
        {
            if (Ext(file.FileName) != ext) return (null, $"Bitte eine Datei vom selben Typ ({ext}) wählen.");
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        await ReplaceFileAsync(m.Url, bytes, ct);
        if (m.OriginalUrl is not null) { TryDelete(PathOf(m.OriginalUrl)); m.OriginalUrl = null; }
        m.SizeBytes = bytes.Length;
        m.Width = w;
        m.Height = h;
        m.CropJson = null;
        if (!string.IsNullOrWhiteSpace(file.FileName)) m.FileName = Path.GetFileName(file.FileName);
        await _db.SaveChangesAsync(ct);
        return (m, null);
    }

    // ---------------------------------------------------------------------------------------------- SVG

    public async Task<string?> ReadSvgAsync(int id, CancellationToken ct = default)
    {
        var m = await _db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null || !IsSvg(m) || !File.Exists(PathOf(m.Url))) return null;
        var text = await File.ReadAllTextAsync(PathOf(m.Url), ct);
        // Indented for the editor (the file itself stays compact — SafeSvg writes it so on every save).
        try { return XDocument.Parse(text).ToString(); } catch { return text; }
    }

    public async Task<(Media? media, string? clean, string? error)> SaveSvgAsync(int id, string code, CancellationToken ct = default)
    {
        var m = await _db.Media.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return (null, null, "Medium nicht gefunden.");
        if (!IsSvg(m)) return (null, null, "Das ist keine SVG-Datei.");
        var safe = SafeSvg.Sanitize(code);
        if (safe is null) return (null, null, "Kein gültiges SVG (wohlgeformtes XML mit <svg> als Wurzel).");
        var bytes = System.Text.Encoding.UTF8.GetBytes(safe);
        await ReplaceFileAsync(m.Url, bytes, ct);
        m.SizeBytes = bytes.Length;
        (m.Width, m.Height) = SvgSize(safe);
        await _db.SaveChangesAsync(ct);
        return (m, safe, null);
    }

    // ---------------------------------------------------------------------------------------------- measuring

    /// <summary>Fills in the size of pictures that do not have one yet (uploads from before it was recorded) — at most
    /// <paramref name="max"/> per call, so a big library catches up over a few views instead of stalling one.</summary>
    public async Task BackfillSizesAsync(IEnumerable<Media> items, int max = 60, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var m in items.Where(x => x.Width is null && IsImage(x)))
        {
            if (n++ >= max) break;
            var p = PathOf(m.Url);
            if (!File.Exists(p)) continue;
            (m.Width, m.Height) = IsSvg(m) ? SvgSize(await File.ReadAllTextAsync(p, ct)) : Measure(p);
            if (m.Width is null) m.Width = 0;   // unreadable: asked once, not on every view
        }
        if (n > 0) await _db.SaveChangesAsync(ct);
    }

    private static (int?, int?) Measure(string path)
    {
        try { var info = Image.Identify(path); return (info.Width, info.Height); }
        catch { return (null, null); }
    }

    /// <summary>An SVG's size: its viewBox, else its width/height attributes (plain numbers or px).</summary>
    public static (int?, int?) SvgSize(string svg)
    {
        try
        {
            var root = XDocument.Parse(svg).Root;
            if (root is null) return (null, null);
            var vb = (string?)root.Attribute("viewBox");
            if (vb is not null)
            {
                var p = vb.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4 && double.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out var vw)
                                  && double.TryParse(p[3], System.Globalization.CultureInfo.InvariantCulture, out var vh))
                    return ((int)Math.Round(vw), (int)Math.Round(vh));
            }
            static int? Num(string? s) => double.TryParse((s ?? "").Replace("px", ""), System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : null;
            return (Num((string?)root.Attribute("width")), Num((string?)root.Attribute("height")));
        }
        catch { return (null, null); }
    }

    // ---------------------------------------------------------------------------------------------- where it is used

    public sealed record Use(string Kind, string Title, string? Link);

    /// <summary>Where a picture is used, as far as the core can say where: pages (with a link to the editor), templates, the
    /// site settings, menus, forms, components. A plugin's data (blog posts, shop products) is named as one line.</summary>
    public async Task<List<Use>> UsesAsync(Media m, PluginsCorpus? pluginCorpus, CancellationToken ct = default)
    {
        var file = Path.GetFileName(m.Url);
        var uses = new List<Use>();
        if (file.Length == 0) return uses;

        var pageIds = await _db.ContentBlocks.AsNoTracking().Where(b => b.DataJson.Contains(file)).Select(b => b.PageId).Distinct().ToListAsync(ct);
        var cssPages = await _db.Pages.AsNoTracking().Where(p => (p.CustomCss ?? "").Contains(file) || (p.TemplateParamsJson ?? "").Contains(file)).Select(p => p.Id).ToListAsync(ct);
        foreach (var p in await _db.Pages.AsNoTracking().Where(p => pageIds.Contains(p.Id) || cssPages.Contains(p.Id))
                     .OrderBy(p => p.Title).Select(p => new { p.Id, p.Title, p.Locale, p.OwnerPlugin }).ToListAsync(ct))
            uses.Add(new Use("page", p.Title + (p.Locale == Localizer.DefaultCulture ? "" : $" ({p.Locale})"), $"/admin/pages/editor/{p.Id}"));

        foreach (var t in await _db.Templates.AsNoTracking()
                     .Where(t => t.CustomCss.Contains(file) || t.LayoutHtml.Contains(file) || t.ParamValuesJson.Contains(file) || t.PartsJson.Contains(file))
                     .Select(t => new { t.Id, t.Name }).ToListAsync(ct))
            uses.Add(new Use("template", t.Name, $"/admin/templates/edit/{t.Id}"));

        foreach (var key in await _db.SiteSettings.AsNoTracking().Where(s => s.Value.Contains(file)).Select(s => s.Key).ToListAsync(ct))
            uses.Add(new Use("setting", key switch
            {
                SettingKeys.LogoUrl => "Logo",
                SettingKeys.LogoDarkUrl => "Logo (dunkel)",
                SettingKeys.FaviconUrl => "Favicon",
                _ => key,
            }, "/admin/settings"));

        if (await _db.MenuItems.AsNoTracking().AnyAsync(i => i.Url.Contains(file) || (i.Icon ?? "").Contains(file), ct))
            uses.Add(new Use("menu", "Menü", "/admin/menus"));
        foreach (var f in await _db.Forms.AsNoTracking().Where(f => f.DefinitionJson.Contains(file)).Select(f => new { f.Id, f.Name }).ToListAsync(ct))
            uses.Add(new Use("form", f.Name, $"/admin/forms/edit/{f.Id}"));
        foreach (var c in await _db.Components.AsNoTracking().Where(c => c.TemplateHtml.Contains(file)).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            uses.Add(new Use("component", c.Name, $"/admin/components/edit/{c.Id}"));
        if (pluginCorpus is not null && (await pluginCorpus(ct)).Contains(file, StringComparison.OrdinalIgnoreCase))
            uses.Add(new Use("plugin", "Daten eines Plugins (z. B. Blog-Beiträge, Shop-Produkte)", null));
        return uses;
    }

    public delegate Task<string> PluginsCorpus(CancellationToken ct);

    /// <summary>Deletes the files that belong to a library entry: its own and the original an in-place edit set aside.</summary>
    public void DeleteFiles(Media m)
    {
        TryDelete(PathOf(m.Url));
        if (m.OriginalUrl is not null) TryDelete(PathOf(m.OriginalUrl));
        _thumbs.Invalidate(Path.GetFileName(m.Url));
    }

    // ---------------------------------------------------------------------------------------------- helpers

    /// <summary>Reads what the browser sent, checks it is a picture of a sane size, and encodes it in <paramref name="ext"/>'s
    /// format without metadata. Returns the bytes and the pixel size, or an error a person can read.</summary>
    private static async Task<(byte[]? bytes, int w, int h, string? error)> EncodeAsync(Stream upload, string ext, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        await upload.CopyToAsync(ms, ct);
        if (ms.Length == 0) return (null, 0, 0, "Kein Bild erhalten.");
        if (ms.Length > MaxBytes) return (null, 0, 0, "Das Bild ist zu groß (höchstens 30 MB).");
        ms.Position = 0;
        Image image;
        try { image = await Image.LoadAsync(ms, ct); }
        catch { return (null, 0, 0, "Das ist kein lesbares Bild."); }
        using (image)
        {
            if (image.Width > MaxEdge || image.Height > MaxEdge) return (null, 0, 0, $"Das Bild ist zu groß (höchstens {MaxEdge} Pixel je Seite).");
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;
            await using var outMs = new MemoryStream();
            switch (ext)
            {
                case ".png": await image.SaveAsPngAsync(outMs, new PngEncoder(), ct); break;
                case ".webp": await image.SaveAsWebpAsync(outMs, new WebpEncoder { Quality = 88 }, ct); break;
                default: await image.SaveAsJpegAsync(outMs, new JpegEncoder { Quality = 90 }, ct); break;
            }
            return (outMs.ToArray(), image.Width, image.Height, null);
        }
    }

    private async Task ReplaceFileAsync(string url, byte[] bytes, CancellationToken ct)
    {
        var target = PathOf(url);
        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, target, overwrite: true);
        _thumbs.Invalidate(Path.GetFileName(url));
    }

    /// <summary>The editor's settings, kept to reopen the original with them; bounded and parsed so only JSON is stored.</summary>
    private static string? CleanEditJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4000) return null;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Object ? d.RootElement.GetRawText() : null; }
        catch (JsonException) { return null; }
    }

    private static string CopyName(string name)
    {
        var ext = Path.GetExtension(name);
        return Path.GetFileNameWithoutExtension(name) + " (bearbeitet)" + ext;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
