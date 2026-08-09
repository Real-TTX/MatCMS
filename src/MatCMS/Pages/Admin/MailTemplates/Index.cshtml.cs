using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.MailTemplates;

/// <summary>
/// The mails this site sends, and their wording.
/// <para>There is no create action and no delete: which mails exist is decided by the code that
/// sends them, not by an operator. A row you could add would never be sent, and one you could delete
/// would just come back on the next start. What CAN be changed is the wording — and whether the mail
/// goes out at all.</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<MailTemplate> Items { get; private set; } = new();

    /// <summary>True when no SMTP is configured — the list then says so once at the top instead of
    /// letting an operator tune wording for mails that cannot leave the building.</summary>
    public bool MailConfigured { get; private set; }

    public async Task OnGetAsync([FromServices] EmailService email)
    {
        // Ordered by the declaration, not by name: that is the order the product thinks in, and it
        // stays stable when somebody renames a template.
        var rows = await _db.MailTemplates.AsNoTracking().ToListAsync();
        Items = Services.MailTemplates.All
            .Select(d => rows.FirstOrDefault(r => r.Key == d.Key))
            .Where(r => r is not null).Select(r => r!)
            // Anything stored that the code no longer declares still shows, or it would be
            // unreachable but still in the database.
            .Concat(rows.Where(r => Services.MailTemplates.Find(r.Key) is null).OrderBy(r => r.Key))
            .ToList();

        MailConfigured = await email.IsConfiguredAsync();
    }
}
