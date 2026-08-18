using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>One mail's wording, belonging to this profile rather than to the catalogue.</summary>
public class MailTemplateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public MailTemplateModel(AppDbContext db, ProfileService profiles)
    {
        _db = db; _profiles = profiles;
    }

    public Profile Owner { get; private set; } = new();
    public ProfileMailTemplate Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int profileId, int? id)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return RedirectToPage("Index");
        Owner = owner;

        if (id is null) return Page();
        var row = await _db.ProfileMailTemplates.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id && m.ProfileId == profileId);
        if (row is null) return RedirectToPage("Edit", new { id = profileId, tab = "mailtemplates" });
        Item = row;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, int? id, string? key, string? name, string? description,
        string? subject, string? body, bool enabled, bool isHtml)
    {
        var k = (key ?? "").Trim();
        var s = (subject ?? "").Trim();
        if (k.Length == 0 || s.Length == 0)
        {
            TempData["FlashError"] = "Schlüssel und Betreff sind erforderlich.";
            return RedirectToPage(new { profileId, id });
        }

        var row = id is int existing
            ? await _db.ProfileMailTemplates.FirstOrDefaultAsync(m => m.Id == existing && m.ProfileId == profileId)
            : null;
        if (row is null)
        {
            if (await _db.ProfileMailTemplates.AnyAsync(m => m.ProfileId == profileId && m.Key == k))
            {
                TempData["FlashError"] = $"Dieses Profil hat für „{k}“ bereits eine eigene Vorlage.";
                return RedirectToPage(new { profileId, id });
            }
            row = new ProfileMailTemplate { ProfileId = profileId };
            _db.ProfileMailTemplates.Add(row);
        }

        row.Key = k;
        row.Name = string.IsNullOrWhiteSpace(name) ? k : name.Trim();
        row.Description = description?.Trim() ?? "";
        row.Subject = s;
        row.Body = body ?? "";
        row.Enabled = enabled;
        row.IsHtml = isHtml;

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"„{row.Name}“ gespeichert.";
        return RedirectToPage(new { profileId, id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int profileId, int id)
    {
        var row = await _db.ProfileMailTemplates.FirstOrDefaultAsync(m => m.Id == id && m.ProfileId == profileId);
        if (row is null) return RedirectToPage("Edit", new { id = profileId, tab = "mailtemplates" });

        _db.ProfileMailTemplates.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);

        // Removing it here stops future rollouts; the sites that already got it keep their wording,
        // like every other payload.
        TempData["Flash"] = $"„{row.Name}“ aus dem Profil entfernt.";
        return RedirectToPage("Edit", new { id = profileId, tab = "mailtemplates" });
    }

    public async Task<IActionResult> OnGetExportAsync(int profileId, int id)
    {
        var m = await _db.ProfileMailTemplates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ProfileId == profileId);
        if (m is null) return RedirectToPage("Edit", new { id = profileId, tab = "mailtemplates" });

        var payload = new { m.Key, m.Name, m.Description, m.Subject, m.Body, m.Enabled, m.IsHtml };
        var json = System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var slug = new string(m.Key.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8", $"mail-{slug}.json");
    }

    /// <summary>
    /// Renders what is currently in the editor, so the frame beside the fields shows the finished
    /// mail. Through the SAME renderer the instance uses when it sends — a JavaScript copy would be
    /// live as you type and would drift, and a preview that lies is worse than none.
    /// </summary>
    public IActionResult OnPostPreview(string? body, string? key, bool isHtml)
    {
        var def = MatCMS.Shared.MailTemplates.Find(key ?? "");
        var values = (def?.Placeholders ?? [])
            .ToDictionary(p => p.Token, p => MailSamples.Scalar(p.Token), StringComparer.OrdinalIgnoreCase);
        var lists = MailSamples.Lists(def);

        var rendered = MatCMS.Shared.MailTemplates.Render(body ?? "", values, lists, isHtml);

        // A plain-text body is shown as text, or the preview would collapse every line break and
        // suggest a layout the mail does not have.
        var html = isHtml
            ? rendered
            : "<pre style=\"font-family:ui-monospace,Menlo,Consolas,monospace;font-size:13px;white-space:pre-wrap;\">"
              + System.Net.WebUtility.HtmlEncode(rendered) + "</pre>";

        // Wie im CMS: der Rahmen ist abgeschottet (sandbox), und dieser <base> schickt jeden Verweis
        // der Mail in ein neues Fenster — das der Sandkasten mangels allow-popups verbietet. Sonst
        // trüge ein Klick auf einen Link IN DER MAIL die fremde Seite in die Vorschau.
        return Content(
            "<!doctype html><html><head><meta charset=\"utf-8\"><base target=\"_blank\">"
            + "<style>body{margin:0;padding:16px;background:#fff;}</style></head><body>"
            + html + "</body></html>",
            "text/html; charset=utf-8");
    }
}
