using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting.Nodes;

/// <summary>
/// The Docker hosts sites can run on: "Dieser Host" (the cloud's own daemon, configured under Einstellungen →
/// Hosting) and every node. Admin-only through the /Admin/Hosting folder lock. Everything here also exists as
/// /api/v1/nodes and the MCP node tools.
/// </summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly VersionService _version;

    public IndexModel(AppDbContext db, DockerHostService docker, VersionService version) { _db = db; _docker = docker; _version = version; }

    public string CloudVersion => _version.Current;

    public List<Node> Items { get; private set; } = new();
    public Dictionary<int, int> Counts { get; private set; } = new();
    public int LocalCount { get; private set; }
    public bool LocalReachable { get; private set; }

    public async Task OnGetAsync()
    {
        Items = await _db.Nodes.AsNoTracking().OrderBy(n => n.Name).ToListAsync();
        Counts = await _db.Instances.Where(i => i.NodeId != null).GroupBy(i => i.NodeId!.Value)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        LocalCount = await _db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local);
        LocalReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
    }
}
