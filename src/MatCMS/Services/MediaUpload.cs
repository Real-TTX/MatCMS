using System.Security.Cryptography;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;

namespace MatCMS.Services;

/// <summary>
/// Putting a file into the media library — ONE set of rules for the admin's upload (<c>/admin/api/upload</c>) and for plugins
/// (<c>IPluginSite.AddMediaAsync</c>): allowed types, size limits, SVG rebuilt by <see cref="SafeSvg"/>, a fresh random file name, the library row,
/// the small copies warmed. A plugin reaches the library only through here, so an import cannot bring what an upload could not.
/// </summary>
public static class MediaUpload
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"];
    public static readonly string[] DocumentExtensions = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".zip"];
    public const int MaxImageMb = 8;
    public const int MaxDocumentMb = 25;

    public sealed record Result(bool Ok, string? Url, string? Error, bool Existing = false);

    /// <summary>The bytes as they would be stored: an SVG rebuilt, everything else as it came. Null + error when the file may not be stored.</summary>
    public static (byte[]? Bytes, string? Error) Prepare(string fileName, byte[] content)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        var isImage = ImageExtensions.Contains(ext);
        if (!isImage && !DocumentExtensions.Contains(ext))
            return (null, "Dateityp nicht erlaubt (Bilder: PNG/JPG/GIF/WEBP/SVG · Dateien: PDF, DOC(X), XLS(X), PPT(X), TXT, CSV, ZIP).");
        var maxMb = isImage ? MaxImageMb : MaxDocumentMb;
        if (content.LongLength == 0) return (null, "Keine Datei erhalten.");
        if (content.LongLength > maxMb * 1024L * 1024) return (null, $"Datei zu groß (max. {maxMb} MB).");
        if (ext != ".svg") return (content, null);
        var safe = SafeSvg.Sanitize(System.Text.Encoding.UTF8.GetString(content));
        return safe is null ? (null, "Die Datei ist kein gültiges SVG.") : (System.Text.Encoding.UTF8.GetBytes(safe), null);
    }

    /// <summary>The address of a file of the library with exactly these bytes (as they would be stored), or null.</summary>
    public static async Task<string?> FindIdenticalAsync(AppDbContext db, IWebHostEnvironment env, byte[] stored, CancellationToken ct = default)
    {
        var len = stored.LongLength;
        var candidates = await db.Media.Where(m => m.SizeBytes == len).Select(m => m.Url).ToListAsync(ct);
        if (candidates.Count == 0) return null;
        var hash = SHA256.HashData(stored);
        foreach (var url in candidates)
        {
            if (PathOf(env, url) is not { } path || !File.Exists(path)) continue;
            await using var s = File.OpenRead(path);
            if (s.Length != len) continue;
            if ((await SHA256.HashDataAsync(s, ct)).AsSpan().SequenceEqual(hash)) return url;
        }
        return null;
    }

    /// <summary>The file behind a plain <c>/uploads/{name}</c> address (no folders, no "..", no query); null for anything else.</summary>
    public static string? PathOf(IWebHostEnvironment env, string? url)
    {
        var u = (url ?? "").Trim();
        if (!u.StartsWith("/uploads/", StringComparison.Ordinal)) return null;
        var name = u["/uploads/".Length..];
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..") || name.Contains('?') || name.Contains('#')) return null;
        return Path.Combine(StoragePaths.Uploads(env), name);
    }

    /// <summary>Stores a file in the library. With <paramref name="reuse"/> an identical file already there is returned instead of a copy.</summary>
    public static async Task<Result> SaveAsync(AppDbContext db, IWebHostEnvironment env, ThumbnailService thumbs, string fileName, byte[] content,
        string? contentType, bool reuse = false, CancellationToken ct = default)
    {
        var (bytes, error) = Prepare(fileName, content);
        if (bytes is null) return new Result(false, null, error);
        if (reuse && await FindIdenticalAsync(db, env, bytes, ct) is { } existing) return new Result(true, existing, null, Existing: true);

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var isImage = ImageExtensions.Contains(ext);
        var uploads = StoragePaths.Uploads(env);
        Directory.CreateDirectory(uploads);
        var name = $"{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(uploads, name);
        await File.WriteAllBytesAsync(path, bytes, ct);

        (int? w, int? h) dims = (null, null);
        if (ext == ".svg") dims = MediaEditor.SvgSize(System.Text.Encoding.UTF8.GetString(bytes));
        else if (isImage) try { var info = Image.Identify(path); dims = (info.Width, info.Height); } catch { /* not readable: no size */ }
        // appended after existing media (highest SortOrder)
        var nextOrder = (await db.Media.MaxAsync(m => (int?)m.SortOrder, ct) ?? 0) + 1;
        var url = $"/uploads/{name}";
        db.Media.Add(new Models.Media
        {
            Url = url,
            FileName = Path.GetFileName(fileName),
            ContentType = ext == ".svg" ? "image/svg+xml" : contentType ?? "",
            SizeBytes = bytes.LongLength,
            SortOrder = nextOrder,
            // the size the library shows and the image editor starts from (null for a document or an unreadable file)
            Width = dims.w,
            Height = dims.h
        });
        await db.SaveChangesAsync(ct);
        // the small copies lists and cards show are stored right away, beside the original (the original stays as it came)
        if (isImage) thumbs.Warm(name);
        return new Result(true, url, null);
    }
}
