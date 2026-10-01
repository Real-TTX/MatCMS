using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>The old Hosting → Docker tab. Docker belongs to a machine, so it is a tab of each host's page now; old links
/// and bookmarks land on "Dieser Host"'s.</summary>
public class DockerModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Hosting/Nodes/Details", new { tab = "docker" });
}
