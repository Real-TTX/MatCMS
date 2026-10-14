using System.Globalization;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>A media-library item for a gallery block, with its tags already split.</summary>
public sealed record GalleryMedia(string Url, string? Alt, IReadOnlyList<string> Tags);

/// <summary>
/// What the content blocks of the public site read from the database (gallery from the media library,
/// the form a form block shows), so the block views only render. Everything is read-only.
/// </summary>
public class PublicContentService
{
    private readonly AppDbContext _db;

    public PublicContentService(AppDbContext db) => _db = db;

    /// <summary>The media-library items a gallery block shows: all of them, or those carrying at least one of
    /// <paramref name="tags"/>. In the library's manual order.</summary>
    public async Task<List<GalleryMedia>> ListMediaAsync(IReadOnlyCollection<string> tags)
    {
        // Follow the manual media order; new uploads get the next SortOrder so they land at the end.
        var media = await _db.Media.AsNoTracking().OrderBy(m => m.SortOrder).ThenBy(m => m.Id).ToListAsync();
        var result = new List<GalleryMedia>();
        foreach (var m in media)
        {
            var mtags = TagUtil.Split(m.Tags).ToList();
            if (tags.Count > 0 && !tags.Any(w => mtags.Contains(w, StringComparer.OrdinalIgnoreCase)))
                continue;
            result.Add(new GalleryMedia(m.Url, m.Alt, mtags));
        }
        return result;
    }

    /// <summary>The form a form block points at, or null when none is chosen or it no longer exists.</summary>
    public async Task<Form?> FindFormAsync(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        return await _db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Slug == slug);
    }
}
