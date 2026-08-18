using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// The backup policy used to be edited here, on a page of its own reachable only through a row in
/// the profile's settings list. It is a tab on <c>Profiles/Edit</c> now — the same place the cloud's
/// own Einstellungen already put backups, so the operator looks for it in one spot instead of two.
///
/// <para>The page stays as a redirect rather than disappearing: bookmarks and links to it exist, and
/// sending them to the tab is friendlier than a 404. The format itself moved to
/// <see cref="Services.BackupSchedule"/>, which is where it belonged all along — it is a wire format
/// the instance reads, not state of the page that happens to edit it.</para>
/// </summary>
public class BackupModel : PageModel
{
    public IActionResult OnGet(int profileId) =>
        RedirectToPage("Edit", new { id = profileId, tab = "backup" });
}
