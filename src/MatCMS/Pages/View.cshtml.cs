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
    private readonly EmailService _email;
    private readonly SiteContext _site;

    public ViewModel(AppDbContext db, BlockRegistry registry, EmailService email, SiteContext site)
    {
        _db = db;
        Registry = registry;
        _email = email;
        _site = site;
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
        var answered = new List<(string Label, string Value)>();
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
            answered.Add((label, val));
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

            TempData["FormSuccess_" + formSlug] = string.IsNullOrWhiteSpace(form.SuccessMessage)
                ? "Vielen Dank! Ihre Angaben wurden übermittelt."
                : form.SuccessMessage!.Trim();

            if (form.NotifyEnabled)
                await TrySendNotificationAsync(form, answered, inputs, values);
        }

        // The custom /{slug?} content routing isn't resolvable via RedirectToPage (throws
        // "No page named …"), so build the localized URL directly and PRG-redirect back to it.
        return Redirect(MatCMS.Services.SiteContext.LocalizedUrl(locale, key));
    }

    /// <summary>Best-effort submission notification e-mail. Never throws — a mail problem must never
    /// break the visitor's submission (which is already saved at this point).</summary>
    private async Task TrySendNotificationAsync(
        Form form, List<(string Label, string Value)> answered,
        List<FormElement> inputs, Dictionary<string, string> values)
    {
        try
        {
            var notify = FormNotify.Parse(form.NotifyJson);
            var recipients = new List<string>(notify.Emails);
            if (notify.UserIds.Count > 0)
            {
                var userEmails = await _db.Users.AsNoTracking()
                    .Where(u => notify.UserIds.Contains(u.Id) && u.Email != null && u.Email != "")
                    .Select(u => u.Email!)
                    .ToListAsync();
                recipients.AddRange(userEmails);
            }
            if (recipients.Count == 0) return;

            // The wording lives in an editable template now (Admin → Mail-Vorlagen); this only
            // supplies what goes into it. The answers go in twice, on purpose:
            //   {{fields}}            — the ready-made block, for a plain-text mail
            //   {{#fields}}…{{/fields}} — one pass per field, so an HTML template can lay them out
            // Which fields exist is decided by whoever built the form, so a template can never name
            // them one by one; it can only be handed the list.
            var fields = new System.Text.StringBuilder();
            var fieldRows = new List<MatCMS.Services.MailTemplates.Item>();
            foreach (var (label, value) in answered)
            {
                var shown = string.IsNullOrWhiteSpace(value) ? "—" : value;
                fields.AppendLine($"{label}: {shown}");
                fieldRows.Add(new MatCMS.Services.MailTemplates.Item { ["label"] = label, ["value"] = shown });
            }

            // Reply straight to the visitor if the form captured an e-mail address.
            var replyTo = inputs.FirstOrDefault(e => e.Type == "email") is { Id: var eid }
                          && values.TryGetValue(eid, out var ev) && !string.IsNullOrWhiteSpace(ev)
                ? ev.Trim() : null;

            await _email.SendTemplateAsync(MatCMS.Services.MailTemplates.FormSubmission, recipients,
                new Dictionary<string, string>
                {
                    ["form_name"] = form.Name,
                    ["fields"] = fields.ToString().TrimEnd(),
                    ["site_name"] = _site.SiteName,
                    ["date"] = DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
                },
                replyTo,
                new Dictionary<string, IReadOnlyList<MatCMS.Services.MailTemplates.Item>>
                {
                    ["fields"] = fieldRows,
                });
        }
        catch
        {
            // swallowed on purpose — the submission already succeeded
        }
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
