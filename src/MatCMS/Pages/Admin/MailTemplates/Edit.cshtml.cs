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
    public MatCMS.Shared.MailTemplates.Definition? Definition { get; private set; }

    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? Body { get; set; }
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public bool IsHtml { get; set; }

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
        Definition = MatCMS.Shared.MailTemplates.Find(t.Key);
        Subject = t.Subject;
        Body = t.Body;
        Enabled = t.Enabled;
        IsHtml = t.IsHtml;
    }

    /// <summary>The HTML starting point for this mail, offered when an operator switches the format
    /// on an unedited template. Comes from the declaration, so it uses exactly the loops and
    /// placeholders that will actually be filled.</summary>
    public string HtmlStarter => Definition?.HtmlBody ?? "";

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
        t.IsHtml = IsHtml;
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

        var def = MatCMS.Shared.MailTemplates.Find(t.Key);
        var values = (def?.Placeholders ?? [])
            .ToDictionary(p => p.Token, p => Sample(p.Token), StringComparer.OrdinalIgnoreCase);

        var lists = SampleLists(def);

        var (ok, error) = await _email.SendAsync([testTo.Trim()],
            MatCMS.Shared.MailTemplates.Render(t.Subject, values, lists),
            MatCMS.Shared.MailTemplates.Render(t.Body, values, lists, t.IsHtml),
            null, t.IsHtml);

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

    /// <summary>
    /// Renders what is currently in the editor and returns it as a document, so the iframe next to
    /// the fields shows the finished mail.
    /// <para>Server-side and through the SAME renderer the sending path uses. A second, JavaScript
    /// implementation would be live as you type and would drift — and a preview that lies about what
    /// will be sent is worse than no preview at all.</para>
    /// <para>It takes the POSTED text rather than the stored row, so it answers "what am I writing"
    /// and not "what did I save".</para>
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync(int id, string? body, bool isHtml)
    {
        var t = await _db.MailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return Content("", "text/html; charset=utf-8");

        var def = MatCMS.Shared.MailTemplates.Find(t.Key);
        var values = (def?.Placeholders ?? [])
            .ToDictionary(p => p.Token, p => Sample(p.Token), StringComparer.OrdinalIgnoreCase);
        var lists = SampleLists(def);

        var rendered = MatCMS.Shared.MailTemplates.Render(body ?? "", values, lists, isHtml);

        // A plain-text body is shown as text, or the preview would collapse every line break and
        // suggest a layout the mail does not have.
        var html = isHtml
            ? rendered
            : "<pre style=\"font-family:ui-monospace,Menlo,Consolas,monospace;font-size:13px;white-space:pre-wrap;\">"
              + System.Net.WebUtility.HtmlEncode(rendered) + "</pre>";

        // Die Vorschau ist zum Ansehen da: der Rahmen ist abgeschottet (sandbox am iframe), und
        // dieser <base> schickt jeden Verweis der Mail in ein neues Fenster — das der Sandkasten
        // mangels allow-popups verbietet. Ohne beides trug ein Klick auf einen Link IN DER MAIL die
        // fremde Seite in die Vorschau, und ein target="_top" die Editorseite samt Ungespeichertem
        // fort.
        return Content(
            "<!doctype html><html><head><meta charset=\"utf-8\"><base target=\"_blank\">"
            + "<style>body{margin:0;padding:16px;background:#fff;}</style></head><body>"
            + html + "</body></html>",
            "text/html; charset=utf-8");
    }

    /// <summary>Two example passes per loop: one shows the layout, two show what repetition does to
    /// it — which is the thing a table either survives or does not.</summary>
    private static Dictionary<string, IReadOnlyList<MatCMS.Shared.MailTemplates.Item>> SampleLists(
        MatCMS.Shared.MailTemplates.Definition? def) =>
        (def?.Loops ?? []).ToDictionary(
            l => l.Name,
            l => (IReadOnlyList<MatCMS.Shared.MailTemplates.Item>)Enumerable.Range(1, 2).Select(n =>
            {
                var item = new MatCMS.Shared.MailTemplates.Item();
                foreach (var fld in l.Fields) item[fld.Token] = SampleLoop(fld.Token, n);
                return item;
            }).ToList(),
            StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Example content inside a loop, numbered so two passes are visibly different.
    /// <para>The second value deliberately contains <c>&lt;</c> and <c>&amp;</c>. Visitors write such
    /// characters, and in an HTML mail they have to come out escaped — so the sample that a preview
    /// is judged on is the awkward one, not a tidy name that would prove nothing.</para>
    /// </summary>
    private static string SampleLoop(string token, int n) => token.ToLowerInvariant() switch
    {
        "label" => n == 1 ? "Name" : "Nachricht",
        "value" => n == 1 ? "Erika Musterfrau" : "Angebot <Apartment 3> für Mai & Juni?",
        _ => $"[{token} {n}]",
    };
}
