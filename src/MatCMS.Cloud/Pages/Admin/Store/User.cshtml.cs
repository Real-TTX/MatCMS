using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Store;

/// <summary>
/// Store user editor — the practical case being the operator's own admin login, rolled out to every
/// site they run. <see cref="StoreUser.Username"/> is the identity on the instance.
/// </summary>
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

    public StoreUser Item { get; private set; } = new();
    public bool IsNew => Item.Id == 0;

    /// <summary>Profiles that roll this account out — shown before a change is saved.</summary>
    public List<string> UsedBy { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null) return Page();

        var item = await _db.StoreUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
        if (item is null) return RedirectToPage("Index");

        Item = item;
        UsedBy = await _db.ProfileStoreUsers.AsNoTracking()
            .Where(x => x.StoreUserId == item.Id).Select(x => x.Profile!.Name).ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id, string? username, string? email, string? displayName, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            TempData["FlashError"] = "Benutzername ist erforderlich.";
            return RedirectToPage(new { id });
        }

        var name = username.Trim();
        var row = id is null
            ? await _db.StoreUsers.FirstOrDefaultAsync(u => u.Username == name)
            : await _db.StoreUsers.FirstOrDefaultAsync(u => u.Id == id);

        // A new account has nothing to fall back on, so the password is mandatory there; when editing,
        // an empty field means "keep the current one" — the hash is never rendered back into the form.
        if (row is null && string.IsNullOrWhiteSpace(password))
        {
            TempData["FlashError"] = "Für ein neues Konto ist ein Passwort erforderlich.";
            return RedirectToPage(new { id });
        }

        if (row is null)
        {
            row = new StoreUser();
            _db.StoreUsers.Add(row);
        }

        row.Username = name;
        row.Email = email?.Trim();
        row.DisplayName = displayName?.Trim();
        // Hashed here and the plaintext dropped immediately — the cloud never stores it, and the
        // instances receive only the hash.
        if (!string.IsNullOrWhiteSpace(password)) row.PasswordHash = _auth.HashPassword(password);

        await _db.SaveChangesAsync();
        await TouchUsersAsync(row.Id);
        TempData["Flash"] = $"Benutzer \"{row.Username}\" im Store gespeichert.";
        return RedirectToPage(new { id = row.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var row = await _db.StoreUsers.FirstOrDefaultAsync(u => u.Id == id);
        if (row is not null)
        {
            var affected = await _db.ProfileStoreUsers.Where(x => x.StoreUserId == id)
                .Select(x => x.ProfileId).Distinct().ToListAsync();
            _db.StoreUsers.Remove(row);
            await _db.SaveChangesAsync();
            // The selections cascade away with it, so every profile that used it changes — bump them.
            foreach (var profileId in affected) await _profiles.TouchAsync(profileId);
            TempData["Flash"] = "Benutzer aus dem Store entfernt. Bereits ausgerollte Konten bleiben auf den Instanzen bestehen.";
        }
        return RedirectToPage("Index");
    }

    /// <summary>Bumps every profile that selected this entry, so their instances pull the change.
    /// Without this a store edit would sit here and never reach anybody.</summary>
    private async Task TouchUsersAsync(int storeUserId)
    {
        var profileIds = await _db.ProfileStoreUsers.AsNoTracking()
            .Where(x => x.StoreUserId == storeUserId).Select(x => x.ProfileId).Distinct().ToListAsync();
        foreach (var profileId in profileIds) await _profiles.TouchAsync(profileId);
    }
}
