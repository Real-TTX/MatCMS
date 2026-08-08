using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// Creating a profile on its own page, like every other record in this admin — the list used to
/// carry an inline form, which is the one place the convention was broken.
/// <para>Only name and description are asked for. Everything else (join code, payloads, strategy)
/// belongs to the profile once it exists and is edited there, so the form does not open with a wall
/// of switches for something that has no instances yet.</para>
/// </summary>
public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public CreateModel(AppDbContext db, ProfileService profiles)
    {
        _db = db;
        _profiles = profiles;
    }

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Description { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var name = (Name ?? "").Trim();
        if (name.Length == 0)
        {
            TempData["FlashError"] = "Bitte einen Namen angeben.";
            return Page();
        }

        if (await _db.Profiles.AnyAsync(p => p.Name == name))
        {
            TempData["FlashError"] = "Ein Profil mit diesem Namen existiert bereits.";
            return Page();
        }

        var profile = await _profiles.CreateAsync(name, Description);
        TempData["Flash"] = $"Profil \"{profile.Name}\" angelegt.";
        // Straight into the new profile: there is nothing to see back in the list, and everything
        // that still needs deciding lives on the profile itself.
        return RedirectToPage("Edit", new { id = profile.Id });
    }
}
