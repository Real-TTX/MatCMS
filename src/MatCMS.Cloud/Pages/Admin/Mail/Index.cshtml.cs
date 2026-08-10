using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Mail;

/// <summary>
/// The whole spool, across every instance.
/// <para>The per-instance tab answers "what did this site send". This page answers the question an
/// operator actually starts with — "is mail moving at all" — which cannot be answered by opening
/// twenty instances one at a time.</para>
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly EmailService _email;

    public IndexModel(AppDbContext db, EmailService email)
    {
        _db = db; _email = email;
    }

    public List<SpooledMail> Items { get; private set; } = new();
    public List<Instance> Instances { get; private set; } = new();

    public int QueuedCount { get; private set; }
    public int FailedCount { get; private set; }
    public int SentCount { get; private set; }

    /// <summary>Instance the list is narrowed to, or null for all.</summary>
    public int? FilteredInstance { get; private set; }

    /// <summary>Status the list is narrowed to, or null for all.</summary>
    public SpoolStatus? FilteredStatus { get; private set; }

    public string InstanceName(int id) => Instances.FirstOrDefault(i => i.Id == id)?.Name ?? "—";

    public async Task OnGetAsync(int? instance, string? status)
    {
        Instances = await _db.Instances.AsNoTracking().OrderBy(i => i.Name).ToListAsync();

        // The counts are over EVERYTHING, not over the filtered set: they are the overview, and an
        // overview that changes when you narrow the list below it is not one.
        QueuedCount = await _db.SpooledMails.CountAsync(m => m.Status == SpoolStatus.Queued);
        FailedCount = await _db.SpooledMails.CountAsync(m => m.Status == SpoolStatus.Failed);
        SentCount = await _db.SpooledMails.CountAsync(m => m.Status == SpoolStatus.Sent);

        var query = _db.SpooledMails.AsNoTracking().AsQueryable();
        if (instance is int iid && Instances.Any(i => i.Id == iid))
        {
            FilteredInstance = iid;
            query = query.Where(m => m.InstanceId == iid);
        }
        if (Enum.TryParse<SpoolStatus>(status, ignoreCase: true, out var st))
        {
            FilteredStatus = st;
            query = query.Where(m => m.Status == st);
        }

        // Bodies stay in the database: this list wants sender, subject and outcome, and dragging every
        // message body through memory to show none of them would be pure waste.
        Items = await query
            .OrderByDescending(m => m.Id)
            .Take(200)
            .Select(m => new SpooledMail
            {
                Id = m.Id, InstanceId = m.InstanceId, QueuedAt = m.QueuedAt, Recipients = m.Recipients,
                Subject = m.Subject, Status = m.Status, Attempts = m.Attempts,
                NextAttemptAt = m.NextAttemptAt, SentAt = m.SentAt, LastError = m.LastError
            })
            .ToListAsync();
    }

    /// <summary>
    /// Puts given-up messages back in the queue — one, or every failed one there is.
    /// <para>An operator who just fixed the mail server wants the backlog delivered, not a clean
    /// slate, so the attempt counter is reset and the worker takes them on its next pass.</para>
    /// </summary>
    public async Task<IActionResult> OnPostRetryAsync(int? mailId, int? instance, string? status)
    {
        var query = _db.SpooledMails.Where(m => m.Status == SpoolStatus.Failed);
        if (mailId is int one) query = query.Where(m => m.Id == one);

        var rows = await query.ToListAsync();
        foreach (var m in rows)
        {
            m.Status = SpoolStatus.Queued;
            m.Attempts = 0;
            m.NextAttemptAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        TempData["Flash"] = rows.Count == 0
            ? "Nichts erneut zu versuchen."
            : $"{rows.Count} Nachricht(en) zurück in die Warteschlange.";
        return RedirectToPage(new { instance, status });
    }

    /// <summary>Clears out what is settled. Only Sent and Failed — anything still queued is owed to
    /// somebody, and a "clear the list" that silently dropped those would lose mail.</summary>
    public async Task<IActionResult> OnPostClearAsync(int? instance, string? status)
    {
        var rows = await _db.SpooledMails.Where(m => m.Status != SpoolStatus.Queued).ToListAsync();
        _db.SpooledMails.RemoveRange(rows);
        await _db.SaveChangesAsync();

        TempData["Flash"] = $"{rows.Count} abgeschlossene Nachricht(en) entfernt.";
        return RedirectToPage(new { instance, status });
    }
}
