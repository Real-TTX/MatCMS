using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Users;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    public IndexModel(AppDbContext db) => _db = db;

    public List<User> Items { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Items = await _db.Users.OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return RedirectToPage();

        // Never let the last admin delete themselves out of the app.
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
