using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Users;

/// <summary>Create + edit in one page: no id = new user (password required), id = edit (an empty
/// password field keeps the current one).</summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;

    public EditModel(AppDbContext db, AuthService auth)
    {
        _db = db;
        _auth = auth;
    }

    public int? Id { get; private set; }
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? DisplayName { get; set; }
    [BindProperty] public string? Password { get; set; }

    public bool IsNew => Id is null;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();

        var user = await _db.Users.FindAsync(id.Value);
        if (user is null) return RedirectToPage("Index");

        Id = user.Id;
        Username = user.Username;
        Email = user.Email;
        DisplayName = user.DisplayName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        Id = id;

        var name = (Username ?? "").Trim();
        if (name.Length == 0)
        {
            Error = "Benutzername ist erforderlich.";
            return Page();
        }

        var taken = await _db.Users.AnyAsync(u => u.Username == name && (id == null || u.Id != id));
        if (taken)
        {
            Error = "Dieser Benutzername ist bereits vergeben.";
            return Page();
        }

        if (id is null)
        {
            if (string.IsNullOrWhiteSpace(Password))
            {
                Error = "Passwort ist erforderlich.";
                return Page();
            }
            _db.Users.Add(new User
            {
                Username = name,
                Email = Email?.Trim(),
                DisplayName = DisplayName?.Trim(),
                Role = "Admin",
                PasswordHash = _auth.HashPassword(Password!)
            });
        }
        else
        {
            var user = await _db.Users.FindAsync(id.Value);
            if (user is null) return RedirectToPage("Index");

            user.Username = name;
            user.Email = Email?.Trim();
            user.DisplayName = DisplayName?.Trim();
            if (!string.IsNullOrWhiteSpace(Password))
                user.PasswordHash = _auth.HashPassword(Password);
        }

        await _db.SaveChangesAsync();
        TempData["Flash"] = "Benutzer gespeichert.";
        return RedirectToPage("Index");
    }
}
