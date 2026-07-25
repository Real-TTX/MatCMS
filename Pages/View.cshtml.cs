using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
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

    public async Task<IActionResult> OnGetAsync(string? slug, string? culture)
    {
        var locale = ResolveLocale(culture);
        var page = await LoadAsync(slug, locale);
        // Admins may preview unpublished (draft) pages; everyone else gets 404.
        if (page is null || (!page.IsPublished && !User.IsInRole("Admin")))
            return NotFound();

        CurrentPage = page;
        ViewData["Title"] = page.Title;
        ViewData["MetaDescription"] = page.MetaDescription;
        return Page();
    }

    public async Task<IActionResult> OnPostContactAsync(string? slug, string? culture)
    {
        var key = Normalize(slug);
        var locale = ResolveLocale(culture);

        // Only accept submissions for a published page that actually contains a contact-form block.
        var page = await LoadAsync(slug, locale);
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

        return RedirectToPage(RouteFor(key, locale));
    }

    public async Task<IActionResult> OnPostFormAsync(string? slug, string? culture)
    {
        var key = Normalize(slug);
        var locale = ResolveLocale(culture);
        var formSlug = Request.Form["__formSlug"].ToString().Trim();

        // Accept only for a published page that actually hosts a "form" block for this form.
        var page = await LoadAsync(slug, locale);
        if (page is null || !page.IsPublished || string.IsNullOrWhiteSpace(formSlug))
            return NotFound();

        var hosts = page.Blocks.Any(b =>
            b.BlockType == "form" &&
            string.Equals(new BlockData(b.DataJson).Str("form"), formSlug, StringComparison.OrdinalIgnoreCase));
        if (!hosts) return NotFound();

        var form = await _db.Forms.AsNoTracking().FirstOrDefaultAsync(f => f.Slug == formSlug);
        if (form is null) return NotFound();

        var elements = FormDefinition.Parse(form.DefinitionJson);
        var inputs = FormDefinition.Flatten(elements)
            .Where(e => FormDefinition.IsInput(e.Type))
            .ToList();

        // Collect raw values first (needed to evaluate conditions).
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in inputs)
            values[el.Id] = Request.Form[el.Id].ToString().Trim();

        // Validate only currently-visible (active) fields.
        var errors = new List<string>();
        var stored = new List<object>();
        foreach (var el in inputs)
        {
            if (!FormDefinition.IsActive(el, values)) continue;
            var val = values[el.Id];
            var label = string.IsNullOrWhiteSpace(el.Label) ? el.Id : el.Label;

            if (el.Required && string.IsNullOrWhiteSpace(val))
                errors.Add($"Bitte „{label}“ ausfüllen.");
            else if (!string.IsNullOrWhiteSpace(val) && !string.IsNullOrWhiteSpace(el.Regex))
            {
                bool ok;
                try { ok = System.Text.RegularExpressions.Regex.IsMatch(val, el.Regex!); }
                catch { ok = true; } // never block on an invalid author-supplied pattern
                if (!ok) errors.Add($"„{label}“ ist ungültig.");
            }

            stored.Add(new { id = el.Id, label, value = val });
        }

        if (errors.Count > 0)
        {
            TempData["FormErrors_" + formSlug] = System.Text.Json.JsonSerializer.Serialize(errors);
            TempData["FormValues_" + formSlug] = System.Text.Json.JsonSerializer.Serialize(values);
        }
        else
        {
            _db.FormSubmissions.Add(new FormSubmission
            {
                FormId = form.Id,
                DataJson = System.Text.Json.JsonSerializer.Serialize(stored, FormDefinition.Opts)
            });
            await _db.SaveChangesAsync();
            TempData["FormSuccess_" + formSlug] = "Vielen Dank! Ihre Angaben wurden übermittelt.";
        }

        return RedirectToPage(RouteFor(key, locale));
    }

    private Task<PageEntity?> LoadAsync(string? slug, string locale) =>
        _db.Pages
            .Include(p => p.Blocks)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == Normalize(slug) && p.Locale == locale);

    private static string Normalize(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? "home" : slug.Trim().ToLowerInvariant();

    private static string ResolveLocale(string? culture) =>
        Localizer.IsSupported(culture) ? culture! : Localizer.DefaultCulture;

    // Route values for a redirect back to a page, keeping its locale prefix (default locale = none).
    private static object RouteFor(string key, string locale) => new
    {
        slug = key == "home" ? null : key,
        culture = locale == Localizer.DefaultCulture ? null : locale
    };
}
