using MatCMS.Cloud.Services;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// One mail's wording in the catalogue.
/// <para>Unlike a template or a component, the KEY is not free: it names a mail MatCMS actually
/// sends, and a key nothing sends is wording that will never be used. The editor therefore offers
/// the keys this cloud knows about — but does not refuse a hand-typed one, because a fleet where the
/// cloud and the instances differ by one release is the normal state, not an error.</para>
/// </summary>
public class MailTemplateModel : PageModel
{
    private readonly AppDbContext _db;
    public MailTemplateModel(AppDbContext db) => _db = db;

    public StoreMailTemplate Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();
        var row = await _db.StoreMailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (row is null) return RedirectToPage("Index");
        Item = row;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        int? id, string? key, string? name, string? description, string? subject, string? body, bool enabled, bool isHtml)
    {
        var k = (key ?? "").Trim();
        var s = (subject ?? "").Trim();
        if (k.Length == 0 || s.Length == 0)
        {
            TempData["FlashError"] = "Schlüssel und Betreff sind erforderlich.";
            return RedirectToPage(new { id });
        }

        var row = id is int existing
            ? await _db.StoreMailTemplates.FirstOrDefaultAsync(x => x.Id == existing)
            : null;
        if (row is null)
        {
            // Same key twice would give a profile two rows to choose between with no way to tell
            // them apart, and the instance would apply whichever came last.
            if (await _db.StoreMailTemplates.AnyAsync(x => x.Key == k))
            {
                TempData["FlashError"] = $"Für „{k}“ gibt es bereits eine Vorlage im Store.";
                return RedirectToPage(new { id });
            }
            row = new StoreMailTemplate();
            _db.StoreMailTemplates.Add(row);
        }

        row.Key = k;
        row.Name = string.IsNullOrWhiteSpace(name) ? k : name.Trim();
        row.Description = description?.Trim() ?? "";
        row.Subject = s;
        row.Body = body ?? "";
        row.Enabled = enabled;
        row.IsHtml = isHtml;

        await _db.SaveChangesAsync();
        TempData["Flash"] = $"„{row.Name}“ gespeichert.";
        return RedirectToPage(new { id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.StoreMailTemplates.FirstOrDefaultAsync(x => x.Id == id);
        if (row is null) return RedirectToPage("Index");

        // The selections go with it: a profile pointing at a row that no longer exists would drop out
        // of its own configuration silently.
        var picks = await _db.ProfileStoreMailTemplates.Where(x => x.StoreMailTemplateId == id).ToListAsync();
        _db.ProfileStoreMailTemplates.RemoveRange(picks);
        _db.StoreMailTemplates.Remove(row);
        await _db.SaveChangesAsync();

        TempData["Flash"] = $"„{row.Name}“ aus dem Store entfernt.";
        return RedirectToPage("Index", new { tab = "mailtemplates" });
    }

    /// <summary>Exports as the same JSON every importer on both sides reads.</summary>
    public async Task<IActionResult> OnGetExportAsync(int id)
    {
        var m = await _db.StoreMailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (m is null) return RedirectToPage("Index");

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

        return Content(
            "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<style>body{margin:0;padding:16px;background:#fff;}</style></head><body>"
            + html + "</body></html>",
            "text/html; charset=utf-8");
    }
}
