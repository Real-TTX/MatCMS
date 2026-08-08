using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>Create/edit a profile user on its own page — same shape as every other payload item.</summary>
public class UserModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    private readonly AuthService _auth;

    public UserModel(AppDbContext db, ProfileService profiles, AuthService auth)
    {
        _db = db;
        _profiles = profiles;
        _auth = auth;
    }

    public Profile Owner { get; private set; } = new();
    public ProfileUser Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    public async Task<IActionResult> OnGetAsync(int profileId, int? id)
    {
        var owner = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        if (owner is null) return RedirectToPage("Index");
        Owner = owner;

        if (id is null) return Page();

        var item = await _db.ProfileUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.ProfileId == profileId);
        if (item is null) return RedirectToPage("Edit", new { id = profileId, tab = "users" });
        Item = item;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        int profileId, int? id, string? username, string? email, string? displayName, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            TempData["FlashError"] = "Benutzername ist erforderlich.";
            return RedirectToPage(new { profileId, id });
        }

        var name = username.Trim();
        var row = id is null
            ? await _db.ProfileUsers.FirstOrDefaultAsync(u => u.ProfileId == profileId && u.Username == name)
            : await _db.ProfileUsers.FirstOrDefaultAsync(u => u.Id == id && u.ProfileId == profileId);

        // A new account has nothing to fall back on, so the password is mandatory there; when editing
        // an empty field means "keep the current one" — the hash is never rendered back into the form.
        if (row is null && string.IsNullOrWhiteSpace(password))
        {
            TempData["FlashError"] = "Für ein neues Konto ist ein Passwort erforderlich.";
            return RedirectToPage(new { profileId, id });
        }

        if (row is null)
        {
            row = new ProfileUser { ProfileId = profileId };
            _db.ProfileUsers.Add(row);
        }
        // Renaming onto an identity another row already holds violates the unique index, which
        // surfaces as an unhandled DbUpdateException — a 500 instead of a readable message.
        else if (row.Username != name
                 && await _db.ProfileUsers.AnyAsync(t => t.ProfileId == profileId && t.Username == name && t.Id != row.Id))
        {
            TempData["FlashError"] = $"Der Benutzername \"{name}\" wird bereits verwendet.";
            return RedirectToPage(new { profileId, id });
        }

        row.Username = name;
        row.Email = email?.Trim();
        row.DisplayName = displayName?.Trim();
        // The plaintext is hashed here and immediately dropped — the cloud never stores it, and the
        // instances receive only the hash.
        if (!string.IsNullOrWhiteSpace(password)) row.PasswordHash = _auth.HashPassword(password);

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(profileId);
        TempData["Flash"] = $"Benutzer \"{row.Username}\" gespeichert.";
        return RedirectToPage(new { profileId, id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int profileId, int id)
    {
        var row = await _db.ProfileUsers.FirstOrDefaultAsync(u => u.Id == id && u.ProfileId == profileId);
        if (row is not null)
        {
            _db.ProfileUsers.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(profileId);
            // Deliberately not propagated: users are add-only on the instance, so removing one here
            // stops future rollouts but never deletes the account on a running site.
            TempData["Flash"] = "Benutzer aus dem Profil entfernt. Bereits ausgerollte Konten bleiben auf den Instanzen bestehen.";
        }
        return RedirectToPage("Edit", new { id = profileId, tab = "users" });
    }
}
