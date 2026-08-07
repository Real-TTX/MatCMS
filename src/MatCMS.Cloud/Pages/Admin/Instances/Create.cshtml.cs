using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Instances;

/// <summary>
/// The two ways an instance joins:
/// <list type="number">
/// <item><b>Join-Code</b> — the operator enters cloud URL + a profile's code in the instance and it
/// enrolls itself. Works behind NAT, and is the way to roll out many sites.</item>
/// <item><b>Adoption</b> — the operator enters an existing instance's URL and one of its admin
/// accounts here, and the cloud pushes the link over. Needs the instance to be reachable once.</item>
/// </list>
/// </summary>
public class CreateModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AdoptionService _adoption;
    private readonly CloudContext _cloud;

    public CreateModel(AppDbContext db, AdoptionService adoption, CloudContext cloud)
    {
        _db = db;
        _adoption = adoption;
        _cloud = cloud;
    }

    public List<Profile> Profiles { get; private set; } = new();
    public string CloudUrl => _cloud.CanonicalBaseUrl(Request);

    [BindProperty] public string InstanceUrl { get; set; } = "";
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public int? ProfileId { get; set; }

    public string? Error { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        Profiles = await _db.Profiles.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
        ProfileId ??= Profiles.FirstOrDefault(p => p.IsDefault)?.Id;
    }

    public async Task<IActionResult> OnPostAdoptAsync()
    {
        var result = await _adoption.AdoptAsync(
            InstanceUrl, Username, Password, ProfileId, Request, HttpContext.RequestAborted);

        if (result.Instance is null)
        {
            Error = result.Error;
            await LoadAsync();
            return Page();
        }

        TempData["Flash"] = $"Instanz \"{result.Instance.Name}\" verbunden.";
        return RedirectToPage("Details", new { id = result.Instance.Id });
    }
}
