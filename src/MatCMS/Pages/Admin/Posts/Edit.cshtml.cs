using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Posts;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    public EditModel(AppDbContext db) => _db = db;

    public int PostId { get; private set; }
    public bool IsNew => PostId == 0;
    /// <summary>Distinct tags across all posts — offered as one-click suggestions in the tag picker.</summary>
    public List<string> AllTags { get; private set; } = new();

    [BindProperty] public InputModel Input { get; set; } = new();

    private async Task<List<string>> LoadAllTagsAsync() =>
        (await _db.Posts.AsNoTracking().Select(p => p.Tags).ToListAsync())
            .SelectMany(t => MatCMS.Content.TagUtil.Split(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

    public class InputModel
    {
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? TitleImage { get; set; }
        public string Excerpt { get; set; } = "";
        public string ContentHtml { get; set; } = "";
        public string Tags { get; set; } = "";
        public string AttachmentsJson { get; set; } = "[]";
        public bool IsPublished { get; set; }
        public string PublishedAt { get; set; } = "";  // yyyy-MM-dd
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        PostId = id;
        AllTags = await LoadAllTagsAsync();
        if (id == 0)
        {
            Input.PublishedAt = DateTime.UtcNow.ToString("yyyy-MM-dd");
            return Page();
        }
        var p = await _db.Posts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return RedirectToPage("Index");
        Input = new InputModel
        {
            Title = p.Title, Slug = p.Slug, TitleImage = p.TitleImage, Excerpt = p.Excerpt,
            ContentHtml = p.ContentHtml, Tags = p.Tags,
            AttachmentsJson = string.IsNullOrWhiteSpace(p.AttachmentsJson) ? "[]" : p.AttachmentsJson,
            IsPublished = p.IsPublished, PublishedAt = p.PublishedAt.ToString("yyyy-MM-dd")
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        PostId = id;
        if (string.IsNullOrWhiteSpace(Input.Title))
        {
            AllTags = await LoadAllTagsAsync();
            ModelState.AddModelError("Input.Title", "Titel ist erforderlich.");
            return Page();
        }

        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(
            string.IsNullOrWhiteSpace(Input.Slug) ? Input.Title : Input.Slug);
        if (string.IsNullOrWhiteSpace(slug)) slug = "beitrag";
        var baseSlug = slug; var n = 2;
        while (await _db.Posts.AnyAsync(x => x.Slug == slug && x.Locale == "de" && x.Id != id))
            slug = baseSlug + "-" + n++;

        Post p;
        if (id == 0) { p = new Post { CreatedAt = DateTime.UtcNow }; _db.Posts.Add(p); }
        else
        {
            p = await _db.Posts.FirstOrDefaultAsync(x => x.Id == id) ?? new Post { CreatedAt = DateTime.UtcNow };
            if (p.Id == 0) _db.Posts.Add(p);
        }

        p.Title = Input.Title.Trim();
        p.Slug = slug;
        p.TitleImage = string.IsNullOrWhiteSpace(Input.TitleImage) ? null : Input.TitleImage.Trim();
        p.Excerpt = (Input.Excerpt ?? "").Trim();
        p.ContentHtml = Input.ContentHtml ?? "";
        p.Tags = MatCMS.Content.TagUtil.Normalize(Input.Tags);
        p.AttachmentsJson = string.IsNullOrWhiteSpace(Input.AttachmentsJson) ? "[]" : Input.AttachmentsJson;
        p.IsPublished = Input.IsPublished;
        p.Locale = "de";
        p.PublishedAt = DateTime.TryParse(Input.PublishedAt, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : DateTime.UtcNow;
        p.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        TempData["Flash"] = "Beitrag gespeichert.";
        return RedirectToPage("Edit", new { id = p.Id });
    }
}
