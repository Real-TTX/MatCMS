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
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var username = (Username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Benutzername und Passwort sind erforderlich.";
            return Page();
        }
        if (await _db.Users.AnyAsync(u => u.Username == username))
        {
            Error = $"Der Benutzername „{username}“ ist bereits vergeben.";
            return Page();
        }

        _db.Users.Add(new User
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName!.Trim(),
            Role = "Admin",
            PasswordHash = _auth.HashPassword(Password!)
        });
        await _db.SaveChangesAsync();

        TempData["Flash"] = "Benutzer angelegt.";
        return RedirectToPage("Index");
    }
}
