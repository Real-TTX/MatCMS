using System.Security.Claims;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages.Admin.Account;

/// <summary>
/// The signed-in user's own account: identity, self-service password change, and a link to two-factor
/// setup. This is where PERSONAL settings live — Users/* is the admin managing OTHER accounts, and a
/// password change there does not require the current password. Here it does: the current password is
/// re-checked so a hijacked cookie alone cannot silently rotate the password (there is no security
/// stamp in v1, so the check is the guard).
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public IndexModel(AppDbContext db, AuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public User Account { get; private set; } = default!;
    public bool TwoFactorEnabled { get; private set; }

    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";

    private async Task<User?> CurrentUserAsync()
    {
        var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(id, out var uid) ? await _db.Users.FindAsync(uid) : null;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Redirect("/login");
        Account = user;
        TwoFactorEnabled = user.TwoFactorEnabled;
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Redirect("/login");

        // Passwords are never trimmed — leading/trailing spaces are legitimate characters.
        var next = NewPassword ?? "";
        if (!_auth.VerifyPassword(user, CurrentPassword ?? ""))
            TempData["FlashError"] = "Das aktuelle Passwort ist falsch.";
        else if (next.Length < 8)
            TempData["FlashError"] = "Das neue Passwort muss mindestens 8 Zeichen haben.";
        else if (next != (ConfirmPassword ?? ""))
            TempData["FlashError"] = "Die Passwort-Bestätigung stimmt nicht überein.";
        else if (_auth.VerifyPassword(user, next))
            TempData["FlashError"] = "Das neue Passwort muss sich vom aktuellen unterscheiden.";
        else
        {
            user.PasswordHash = _auth.HashPassword(next);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Passwort geändert.";
        }
        return RedirectToPage();
    }
}
