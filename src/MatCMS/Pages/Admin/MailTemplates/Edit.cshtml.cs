using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.MailTemplates;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly EmailService _email;
    private readonly SiteContext _site;

    public EditModel(AppDbContext db, EmailService email, SiteContext site)
    {
        _db = db; _email = email; _site = site;
    }

    public MailTemplate Current { get; private set; } = default!;

    /// <summary>What the code will actually put in — null when the stored row's key is not declared
    /// (anymore). The editor then says so rather than offering placeholders nobody fills.</summary>
    public Services.MailTemplates.Definition? Definition { get; private set; }

    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? Body { get; set; }
    [BindProperty] public bool Enabled { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var t = await _db.MailTemplates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");
        Fill(t);
        return Page();
    }

    private void Fill(MailTemplate t)
    {
        Current = t;
        Definition = Services.MailTemplates.Find(t.Key);
        Subject = t.Subject;
        Body = t.Body;
        Enabled = t.Enabled;
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var t = await _db.MailTemplates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        var subject = (Subject ?? "").Trim();
        if (subject.Length == 0)
        {
            Fill(t);
            Subject = Subject; Body = Body; Enabled = Enabled;   // keep what was typed
            Error = "Der Betreff darf nicht leer sein.";
            return Page();
        }

        t.Subject = subject;
        t.Body = Body ?? "";
        t.Enabled = Enabled;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Flash"] = $"„{t.Name}“ gespeichert.";
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Sends this template to one address with example values, so the wording can be judged as mail
    /// rather than as a form field. Uses the SAVED row, not what is in the boxes — a preview of
    /// unsaved text would answer a question nobody asked.
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(int id, string? testTo)
    {
        var t = await _db.MailTemplates.FindAsync(id);
        if (t is null) return RedirectToPage("Index");

        if (string.IsNullOrWhiteSpace(testTo))
        {
            TempData["FlashError"] = "Bitte eine Empfängeradresse angeben.";
            return RedirectToPage(new { id });
        }

        var def = Services.MailTemplates.Find(t.Key);
        var values = (def?.Placeholders ?? [])
            .ToDictionary(p => p.Token, p => Sample(p.Token), StringComparer.OrdinalIgnoreCase);

        var (ok, error) = await _email.SendAsync([testTo.Trim()],
            Services.MailTemplates.Render(t.Subject, values),
            Services.MailTemplates.Render(t.Body, values));

        // Wording follows what actually happened: through the relay the message is ACCEPTED, not
        // delivered — the cloud spools it and sends it moments later. Saying "sent" would be a small
        // lie that turns into a support question the first time a queue backs up.
        if (ok)
            TempData["Flash"] = await _email.UseCloudRelayAsync()
                ? $"Testmail an {testTo.Trim()} an die Cloud übergeben — sie stellt sie zu."
                : $"Testmail an {testTo.Trim()} gesendet.";
        else TempData["FlashError"] = $"Testmail fehlgeschlagen: {error}";
        return RedirectToPage(new { id });
    }

    /// <summary>Example content per placeholder — the point of a test mail is to see the shape of the
    /// message, so the values look like real ones rather than like "{{token}}".</summary>
    private string Sample(string token) => token.ToLowerInvariant() switch
    {
        "form_name" => "Kontakt",
        "fields" => "Name: Erika Musterfrau\nE-Mail: erika@beispiel.de\nNachricht: Beispieltext",
        "site_name" => _site.SiteName,
        "date" => DateTime.Now.ToString("dd.MM.yyyy HH:mm"),
        _ => $"[{token}]",
    };
}
