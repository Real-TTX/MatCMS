using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Hosting;

/// <summary>
/// Hosting → Docker: the cloud's own daemon — whether it is reachable, what it is, and cleaning up the images old
/// updates leave behind. The endpoint itself is set by environment (the socket is mounted at the compose level),
/// so it is shown, not edited.
/// </summary>
public class DockerModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly Localizer _t;

    public DockerModel(AppDbContext db, DockerHostService docker, Localizer t)
    {
        _db = db; _docker = docker; _t = t;
    }

    public bool DockerConfigured => _docker.Configured;
    public string? Endpoint => _docker.Configured ? _docker.Endpoint : null;
    public bool DockerReachable { get; private set; }
    public (string? Version, string? HostName)? Daemon { get; private set; }
    public int LocalCount { get; private set; }

    public async Task OnGetAsync()
    {
        DockerReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
        if (DockerReachable)
        {
            var info = await _docker.DaemonInfoAsync(HttpContext.RequestAborted);
            if (info.Error is null) Daemon = (info.Version, info.HostName);
        }
        LocalCount = await _db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local);
    }

    public async Task<IActionResult> OnPostPruneImagesAsync()
    {
        var r = await _docker.PruneMatCmsImagesAsync(HttpContext.RequestAborted);
        TempData["Flash"] = r.Removed == 0
            ? _t["docker.pruneNone"]
            : _t["docker.pruneDone", r.Removed, (r.BytesReclaimed / (1024.0 * 1024.0)).ToString("0.#")];
        return RedirectToPage();
    }
}
