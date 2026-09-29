using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Notifications;

/// <summary>
/// The notification matrix — rows are recipients (the two groups, every user, own addresses), columns are
/// events. Admin-only (folder lock in Program.cs): it decides who hears about the whole fleet. The same matrix
/// is <c>/api/v1/notifications</c> and the MCP tools; <see cref="NotificationService"/> resolves it.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notify;
    private readonly EmailService _mail;

    public IndexModel(AppDbContext db, NotificationService notify, EmailService mail) { _db = db; _notify = notify; _mail = mail; }

    public sealed record Row(string Key, string Label, string? Sub, string Kind, bool OperatorScoped, bool NoEmail, HashSet<string> Events);

    public List<Row> Rows { get; private set; } = new();
    public bool MailConfigured { get; private set; }

    public async Task OnGetAsync()
    {
        MailConfigured = await _mail.IsConfiguredAsync();
        var m = _notify.Load();
        HashSet<string> Ev(string key) => (m.Rows.FirstOrDefault(r => r.Key == key)?.Events ?? new()).ToHashSet();

        var users = await _db.Users.AsNoTracking().Include(u => u.Instances).OrderBy(u => u.Role).ThenBy(u => u.Username).ToListAsync();
        var admins = users.Where(u => u.Role == Models.User.RoleAdmin).ToList();
        var ops = users.Where(u => u.Role != Models.User.RoleAdmin).ToList();
        Rows.Add(new(NotificationService.GroupAdmins, "Administratoren", $"{admins.Count(u => !string.IsNullOrEmpty(u.Email))} mit E-Mail", "group", false, false, Ev(NotificationService.GroupAdmins)));
        Rows.Add(new(NotificationService.GroupOperators, "Operatoren", $"{ops.Count(u => !string.IsNullOrEmpty(u.Email))} mit E-Mail · nur ihre Instanzen", "group", true, false, Ev(NotificationService.GroupOperators)));
        foreach (var u in users)
        {
            var isOp = u.Role != Models.User.RoleAdmin;
            Rows.Add(new("u:" + u.Id, u.DisplayName ?? u.Username,
                (u.Email ?? "") + (isOp ? $" · Operator ({u.Instances.Count} Instanzen)" : " · Admin"),
                "user", isOp, string.IsNullOrEmpty(u.Email), Ev("u:" + u.Id)));
        }
        foreach (var r in m.Rows.Where(r => r.Key.StartsWith("e:")))
            Rows.Add(new(r.Key, r.Key[2..], null, "email", false, false, r.Events.ToHashSet()));
    }

    /// <summary>
    /// Saves the whole matrix from the form: every ticked cell (<c>row|event</c>), the own addresses listed in
    /// <paramref name="rows"/> (kept even with nothing ticked — removing is its own button), a new address from
    /// the "+" row, and a removal.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(List<string>? cell, List<string>? rows, string? newEmail, string? remove)
    {
        var cells = (cell ?? new()).Select(c => c.Split('|', 2)).Where(p => p.Length == 2).ToList();
        var keys = new List<string> { NotificationService.GroupAdmins, NotificationService.GroupOperators };
        keys.AddRange(await _db.Users.AsNoTracking().Where(u => u.Email != null && u.Email != "").Select(u => "u:" + u.Id).ToListAsync());
        keys.AddRange((rows ?? new()).Where(k => k.StartsWith("e:") && k != remove));

        var m = new NotifyMatrix();
        foreach (var k in keys.Distinct())
        {
            var ev = cells.Where(p => p[0] == k).Select(p => p[1]).ToList();
            if (ev.Count > 0 || k.StartsWith("e:")) m.Rows.Add(new NotifyRow { Key = k, Events = ev });
        }

        var add = (newEmail ?? "").Trim().ToLowerInvariant();
        if (add.Length > 0)
        {
            if (!NotificationService.IsEmail(add)) { TempData["FlashError"] = $"„{add}“ ist keine gültige E-Mail-Adresse."; return RedirectToPage(); }
            // A new address starts with every event — what someone adds an address for, most of the time.
            if (!m.Rows.Any(r => r.Key == "e:" + add)) m.Rows.Add(new NotifyRow { Key = "e:" + add, Events = NotifyEvents.All.ToList() });
        }

        await _notify.SaveAsync(m);
        TempData["Flash"] = remove is not null ? $"„{remove[2..]}“ entfernt." : add.Length > 0 ? $"„{add}“ hinzugefügt." : "Benachrichtigungen gespeichert.";
        return RedirectToPage();
    }
}
