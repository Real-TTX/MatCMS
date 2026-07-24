using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PageEntity = MatCMS.Models.Page;

namespace MatCMS.Pages;

public class ViewModel : PageModel
{
    private readonly AppDbContext _db;

    public ViewModel(AppDbContext db, BlockRegistry registry)
    {
        _db = db;
        Registry = registry;
    }

    public BlockRegistry Registry { get; }
    public PageEntity CurrentPage { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(string? slug)
    {
        var page = await LoadAsync(slug);
        // Admins may preview unpublished (draft) pages; everyone else gets 404.
        if (page is null || (!page.IsPublished && !User.IsInRole("Admin")))
            return NotFound();

        CurrentPage = page;
        ViewData["Title"] = page.Title;
        ViewData["MetaDescription"] = page.MetaDescription;
        return Page();
    }

    public async Task<IActionResult> OnPostContactAsync(string? slug)
    {
        var key = Normalize(slug);

        // Only accept submissions for a published page that actually contains a contact-form block.
        var page = await LoadAsync(slug);
        if (page is null || !page.IsPublished || page.Blocks.All(b => b.BlockType != "contactform"))
            return NotFound();

        var name = Request.Form["cf_name"].ToString().Trim();
        var email = Request.Form["cf_email"].ToString().Trim();
        var category = Request.Form["cf_category"].ToString().Trim();
        var message = Request.Form["cf_message"].ToString().Trim();

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Bitte geben Sie Ihren Namen an.");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) errors.Add("Bitte geben Sie eine gültige E-Mail-Adresse an.");
        if (string.IsNullOrWhiteSpace(message)) errors.Add("Bitte geben Sie eine Nachricht ein.");

        if (errors.Count > 0)
        {
            TempData["ContactError"] = string.Join(" ", errors);
            TempData["cf_name"] = name;
            TempData["cf_email"] = email;
            TempData["cf_category"] = category;
            TempData["cf_message"] = message;
        }
        else
        {
            _db.ContactSubmissions.Add(new ContactSubmission
            {
                Name = name,
                Email = email,
                Category = string.IsNullOrWhiteSpace(category) ? null : category,
                Message = message
            });
            await _db.SaveChangesAsync();
            TempData["ContactSuccess"] = "Vielen Dank! Ihre Nachricht wurde übermittelt. Wir melden uns zeitnah.";
        }

        return RedirectToPage(new { slug = key == "home" ? null : key });
    }

    private Task<PageEntity?> LoadAsync(string? slug) =>
        _db.Pages
            .Include(p => p.Blocks)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == Normalize(slug));

    private static string Normalize(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? "home" : slug.Trim().ToLowerInvariant();
}
