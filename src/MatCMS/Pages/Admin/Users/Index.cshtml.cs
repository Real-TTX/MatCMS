using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Users;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<User> Items { get; private set; } = new();

    /// <summary>Site members (the public "guest area" accounts) — shown on the second tab of this page
    /// so the two kinds of account live in one place, without a separate menu entry.</summary>
    public List<SiteMember> Members { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Items = await _db.Users.OrderBy(u => u.Username).ToListAsync();
        Members = await _db.SiteMembers.AsNoTracking().OrderBy(m => m.Username).ToListAsync();
    }

    /// <summary>Deletes a site member (from the Members tab). Kept here so the operator stays on this
    /// page; the standalone Members page keeps its own copy for direct access.</summary>
    public async Task<IActionResult> OnPostDeleteMemberAsync(int memberId)
    {
        var member = await _db.SiteMembers.FindAsync(memberId);
        if (member is not null)
        {
            _db.SiteMembers.Remove(member);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Mitglied gelöscht.";
        }
        return RedirectToPage(new { tab = "members" });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return RedirectToPage();

        var admins = await _db.Users.CountAsync(u => u.Role == "Admin");
        if (user.Role == "Admin" && admins <= 1)
        {
            TempData["FlashError"] = "Der letzte Administrator kann nicht gelöscht werden.";
            return RedirectToPage();
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        TempData["Flash"] = "Benutzer gelöscht.";
        return RedirectToPage();
    }
}
