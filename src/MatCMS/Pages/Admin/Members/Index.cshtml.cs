using MatCMS.Data;
using MatCMS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Members;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<SiteMember> Items { get; private set; } = new();

    public async Task OnGetAsync() =>
        Items = await _db.SiteMembers.AsNoTracking().OrderBy(m => m.Username).ToListAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var member = await _db.SiteMembers.FindAsync(id);
        if (member is not null)
        {
            _db.SiteMembers.Remove(member);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Mitglied gelöscht.";
        }
        return RedirectToPage();
    }
}
