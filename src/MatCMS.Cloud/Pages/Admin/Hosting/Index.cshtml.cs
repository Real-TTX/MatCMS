using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// The Hosting module's home: the cloud itself (version + self-update) and every instance that runs on
/// this cloud's Docker host, each linking to its own Hosting tab. Admin-only (folder lock in Program.cs):
/// it spans the whole fleet. Later increments hang nodes and proxy providers off this page
/// (docs/hosting-platform.md).
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly CloudUpdaterService _updater;

    public IndexModel(AppDbContext db, CloudContext cloud, CloudUpdaterService updater)
    {
        _db = db; _cloud = cloud; _updater = updater;
    }

    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public CloudUpdateCard Card { get; private set; } = null!;
    public List<Instance> Local { get; private set; } = new();
    public int NodeCount { get; private set; }

    public async Task OnGetAsync(bool check = false)
    {
        // The registry check is on demand, like on the About page: a system page must still open when the
        // registry is unreachable.
        Card = new CloudUpdateCard(await _updater.StatusAsync(check, HttpContext.RequestAborted), CanAct: true);
        Local = await _db.Instances.AsNoTracking()
            .Include(i => i.Node)
            .Where(i => i.Hosting == InstanceHosting.Local || i.Hosting == InstanceHosting.Node)
            .OrderBy(i => i.Name).ToListAsync();
        NodeCount = await _db.Nodes.CountAsync();
    }

    public async Task<IActionResult> OnPostSelfUpdateAsync()
    {
        var r = await _updater.StartAsync(HttpContext.RequestAborted);
        TempData[r.Ok ? "Flash" : "FlashError"] = r.Message;
        return RedirectToPage();
    }
}
