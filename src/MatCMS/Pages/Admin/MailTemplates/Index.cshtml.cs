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

    public async Task OnGetAsync()
    {
        // Ordered by the declaration, not by name: that is the order the product thinks in, and it
        // stays stable when somebody renames a template.
        var rows = await _db.MailTemplates.AsNoTracking().ToListAsync();
        Items = MatCMS.Shared.MailTemplates.All
            .Select(d => rows.FirstOrDefault(r => r.Key == d.Key))
            .Where(r => r is not null).Select(r => r!)
            // Anything stored that the code no longer declares still shows, or it would be
            // unreachable but still in the database.
            .Concat(rows.Where(r => MatCMS.Shared.MailTemplates.Find(r.Key) is null).OrderBy(r => r.Key))
            .ToList();
    }

    /// <summary>True when nothing in this build sends that key. Such a row can only have arrived from
    /// a cloud rollout for a mail this version does not have — it is wording that will never be used,
    /// so unlike a declared template it CAN be deleted.</summary>
    public static bool IsUnknown(MailTemplate t) => MatCMS.Shared.MailTemplates.Find(t.Key) is null;

    /// <summary>
    /// Removes a row for a key nothing sends. A declared one is deliberately not deletable: the code
    /// still asks for it, and the seeder would put it back on the next start anyway.
    /// <para>Without this a rolled-out template for an unknown key could arrive but never leave —
    /// which is how this gap was found.</para>
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.MailTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (row is null) return RedirectToPage();
        if (!IsUnknown(row))
        {
            TempData["FlashError"] = "Diese Vorlage gehört zu einer Mail, die diese Version verschickt — sie lässt sich abschalten, aber nicht löschen.";
            return RedirectToPage();
        }

        _db.MailTemplates.Remove(row);
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"„{row.Name}“ gelöscht.";
        return RedirectToPage();
    }
}
