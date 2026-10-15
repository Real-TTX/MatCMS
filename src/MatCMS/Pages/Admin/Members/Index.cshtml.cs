using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Members;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MatCMS.Services.MemberService _members;
    public IndexModel(AppDbContext db, MatCMS.Services.MemberService members) { _db = db; _members = members; }

    public List<SiteMember> Items { get; private set; } = new();

    public async Task OnGetAsync() =>
        Items = await _db.SiteMembers.AsNoTracking().OrderBy(m => m.Username).ToListAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        // Through the service, not a bare Remove: the plugins (a shop's orders) are told first and anonymise what they hold.
        if (await _members.DeleteAsync(id, HttpContext.RequestAborted)) TempData["Flash"] = "Mitglied gelöscht.";
        return RedirectToPage();
    }
}
