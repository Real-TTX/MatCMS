using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Übersicht: every instance that runs on a host the cloud can reach (its own daemon or a node), each
/// linking to its own Hosting tab. Admin-only (folder lock in Program.cs): it spans the whole fleet.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;

    public IndexModel(AppDbContext db, CloudContext cloud)
    {
        _db = db; _cloud = cloud;
    }

    public bool ModuleEnabled => _cloud.Flag(SettingKeys.HostingEnabled);
    public List<Instance> Local { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Local = await _db.Instances.AsNoTracking()
            .Include(i => i.Node)
            .Where(i => i.Hosting == InstanceHosting.Local || i.Hosting == InstanceHosting.Node)
            .OrderBy(i => i.Name).ToListAsync();
    }
}
