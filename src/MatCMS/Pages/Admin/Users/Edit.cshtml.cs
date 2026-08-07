using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Users;

public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public EditModel(AppDbContext db, AuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public User Current { get; private set; } = default!;

    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? NewPassword { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();

        Current = user;
        DisplayName = user.DisplayName;
        Email = user.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user is null) return NotFound();

        user.DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName!.Trim();
        user.Email = string.IsNullOrWhiteSpace(Email) ? null : Email!.Trim();
        if (!string.IsNullOrWhiteSpace(NewPassword))
            user.PasswordHash = _auth.HashPassword(NewPassword!);

        await _db.SaveChangesAsync();
        TempData["Flash"] = "Benutzer gespeichert.";
        return RedirectToPage("Index");
    }
}
