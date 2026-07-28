using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages;

public class PostModel : PageModel
{
    private readonly AppDbContext _db;
    public PostModel(AppDbContext db) => _db = db;

    public Post Current { get; private set; } = default!;
    public List<(string Url, string Name, bool IsImage)> Attachments { get; } = new();

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        var s = (slug ?? "").Trim().ToLowerInvariant();
        var post = await _db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == s && p.Locale == "de");
        if (post is null || (!post.IsPublished && !User.IsInRole("Admin"))) return NotFound();

        Current = post;
        ViewData["Title"] = post.Title;
        ViewData["MetaDescription"] = post.Excerpt;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(post.AttachmentsJson) ? "[]" : post.AttachmentsJson);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var url = el.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                var name = el.TryGetProperty("name", out var nm) ? nm.GetString() : url;
                var ext = System.IO.Path.GetExtension(url).ToLowerInvariant();
                var isImg = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
                Attachments.Add((url!, string.IsNullOrWhiteSpace(name) ? url! : name!, isImg));
            }
        }
        catch { /* ignore malformed attachments */ }

        return Page();
    }
}
