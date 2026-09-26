using MatCMS.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Resolves a media file's focal point (Media.FocalX/FocalY, percent) to a CSS position value used
/// wherever an image is shown CROPPED (object-fit: cover, or a background). Scoped per request: the
/// handful of images that carry a custom focal point are loaded once and cached by URL, so a page with
/// many images does not issue one query per image.
/// </summary>
public class MediaFocus
{
    private readonly AppDbContext _db;
    private Dictionary<string, string>? _map;

    public MediaFocus(AppDbContext db) => _db = db;

    private Dictionary<string, string> Map => _map ??= _db.Media.AsNoTracking()
        .Where(m => m.FocalX != null || m.FocalY != null)
        .ToDictionary(m => m.Url, m => $"{m.FocalX ?? 50}% {m.FocalY ?? 50}%");

    /// <summary>The image's focal point as "x% y%", or null when it has none (leave the CSS default).</summary>
    public string? Position(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Map.TryGetValue(url!, out var pos) ? pos : null;
    }
}
