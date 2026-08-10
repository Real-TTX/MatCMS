using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// One spooled message in full.
/// <para>The list deliberately does not carry bodies — it would drag every message through memory to
/// show none of them. But "what did we actually send on this site's behalf" is the question the
/// spool exists to answer, and it cannot be answered from a subject line.</para>
/// </summary>
public class MailModel : PageModel
{
    private readonly AppDbContext _db;
    public MailModel(AppDbContext db) => _db = db;

    public SpooledMail Item { get; private set; } = new();
    public Instance? Owner { get; private set; }

    public string[] Recipients => Item.Recipients
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when the body looks like markup. The view then shows it rendered AND as source —
    /// judging a layout from escaped tags is not possible, and judging escaping from a rendering is
    /// not either.</summary>
    public bool LooksHtml => Item.Body.Contains("</") || Item.Body.Contains("<div") || Item.Body.Contains("<table");

    public async Task<IActionResult> OnGetAsync(int id, int mailId)
    {
        var row = await _db.SpooledMails.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == mailId && m.InstanceId == id);
        if (row is null) return RedirectToPage("Details", new { id, tab = "mail" });
        Item = row;
        Owner = await _db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        return Page();
    }
}
