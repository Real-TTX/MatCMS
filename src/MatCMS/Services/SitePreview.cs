using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace MatCMS.Services;

/// <summary>
/// The site's own picture of its home page (<c>appdata/site-preview.webp</c>, served at <c>/_preview.webp</c>): what the admin's switcher,
/// the cloud's tiles and its site picker show instead of loading the whole live site into a frame for every one of them.
/// <para>There is no browser on the server, so the picture is taken in the ADMIN'S browser (admin-shell.js + the vendored
/// modern-screenshot): the admin layout says when it is due (<see cref="StateAsync"/>), the browser renders the home page in a hidden
/// same-origin frame and posts the picture, the server re-encodes it (never stores what the browser sent as it came). "Due" = no picture,
/// or the content it showed has changed (a fingerprint of the published pages' last change, the active template, the header menu and
/// the site's name and logo), or it is older than a week (what the fingerprint cannot see: a plugin's data, a picture replaced in place).</para>
/// </summary>
public sealed class SitePreview
{
    public const int Width = 800, Height = 500;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    // A fingerprint that keeps changing (an editor at work) asks again at most this often.
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);

    private readonly string _image, _meta;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public SitePreview(IWebHostEnvironment env)
    {
        var dir = StoragePaths.DataDir(env);
        _image = Path.Combine(dir, "site-preview.webp");
        _meta = Path.Combine(dir, "site-preview.json");
    }

    public string ImagePath => _image;
    public bool Exists => File.Exists(_image);

    /// <summary>A version for the picture's address (changes with every new picture), or null without one.</summary>
    public string? Version => Exists ? File.GetLastWriteTimeUtc(_image).Ticks.ToString("x") : null;

    private sealed record Meta(string Fingerprint, DateTime TakenUtc);

    private Meta? ReadMeta()
    {
        try { return File.Exists(_meta) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(_meta)) : null; }
        catch { return null; }
    }

    /// <summary>The picture's version (null = none yet) and whether the browser should take a new one now.</summary>
    public async Task<(string? Version, bool Due)> StateAsync(AppDbContext db, CancellationToken ct = default)
    {
        var meta = ReadMeta();
        if (!Exists || meta is null) return (Version, true);
        var age = DateTime.UtcNow - meta.TakenUtc;
        if (age < MinGap) return (Version, false);
        if (age > MaxAge) return (Version, true);
        return (Version, meta.Fingerprint != await FingerprintAsync(db, ct));
    }

    /// <summary>What the home page's look depends on, as one hash.</summary>
    public static async Task<string> FingerprintAsync(AppDbContext db, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var lastPage = await db.Pages.AsNoTracking().Where(p => p.IsPublished).MaxAsync(p => (DateTime?)p.UpdatedAt, ct);
        sb.Append(lastPage?.Ticks).Append('|');
        var tpl = await db.Templates.AsNoTracking().OrderByDescending(t => t.IsActive).ThenBy(t => t.Id).FirstOrDefaultAsync(ct);
        if (tpl is not null) sb.Append(JsonSerializer.Serialize(tpl)).Append('|');
        foreach (var m in await db.MenuItems.AsNoTracking().Where(m => m.Menu == "header" && m.Locale == Localizer.DefaultCulture)
                     .OrderBy(m => m.SortOrder).ThenBy(m => m.Id).Select(m => new { m.Label, m.Url, m.ParentId, m.Visibility }).ToListAsync(ct))
            sb.Append(m.Label).Append('>').Append(m.Url).Append('>').Append(m.ParentId).Append('>').Append(m.Visibility).Append(';');
        string[] keys = [SettingKeys.SiteName, SettingKeys.LogoUrl];
        foreach (var s in await db.SiteSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).OrderBy(s => s.Key).ToListAsync(ct))
            sb.Append(s.Key).Append('=').Append(s.Value).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Stores a picture the admin's browser took: decoded, cut to the top of the page in the fixed proportion, scaled and
    /// written as WebP. Anything that is not a readable image is refused (false).</summary>
    public async Task<bool> SaveAsync(Stream upload, AppDbContext db, CancellationToken ct = default)
    {
        Image image;
        try { image = await Image.LoadAsync(upload, ct); }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException) { return false; }
        using (image)
        {
            if (image.Width < 200 || image.Height < 100 || image.Width > 4000 || image.Height > 20000) return false;
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            // The top of the page at the preview's proportion (a home page is longer than its first screen).
            var h = Math.Min(image.Height, (int)Math.Round(image.Width * (double)Height / Width));
            image.Mutate(x => x.Crop(new Rectangle(0, 0, image.Width, h)).Resize(Width, (int)Math.Round(h * (double)Width / image.Width)));
            var fingerprint = await FingerprintAsync(db, ct);
            await Gate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_image)!);
                var tmp = _image + ".tmp";
                await image.SaveAsWebpAsync(tmp, new WebpEncoder { Quality = 80 }, ct);
                File.Move(tmp, _image, overwrite: true);
                await File.WriteAllTextAsync(_meta, JsonSerializer.Serialize(new Meta(fingerprint, DateTime.UtcNow)), ct);
            }
            finally { Gate.Release(); }
        }
        return true;
    }
}
