using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Pages.Admin.Users;

public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public CreateModel(AppDbContext db, AuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var email = (Email ?? "").Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "E-Mail und Passwort sind erforderlich.";
            return Page();
        }
        var emailLower = email.ToLower();
        if (await _db.Users.AnyAsync(u => (u.Email != null && u.Email.ToLower() == emailLower) || u.Username.ToLower() == emailLower))
        {
            Error = $"Die E-Mail-Adresse „{email}“ ist bereits vergeben.";
            return Page();
        }

        _db.Users.Add(new User
        {
            Username = email,   // the e-mail is the login identity; Username mirrors it (kept as the unique key)
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName!.Trim(),
            Role = "Admin",
            PasswordHash = _auth.HashPassword(Password!)
        });
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Benutzer angelegt.";
        return RedirectToPage("Index");
    }
}
