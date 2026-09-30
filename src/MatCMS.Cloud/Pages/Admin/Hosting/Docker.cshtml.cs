using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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
    private readonly VersionService _version;
    private readonly GhcrClient _ghcr;
    private readonly IMemoryCache _cache;

    public DockerModel(AppDbContext db, DockerHostService docker, Localizer t, VersionService version, GhcrClient ghcr,
        IMemoryCache cache)
    {
        _db = db; _docker = docker; _t = t; _version = version; _ghcr = ghcr; _cache = cache;
    }

    /// <summary>For an image without the label: the release tag the registry has under its digest. Keyed by image id.</summary>
    public Dictionary<string, string> RegistryVersion { get; } = new();

    /// <summary>For an image without the <c>matcms.version</c> label (built before it existed): the version a
    /// container on it reports — the instance's heartbeat, or this cloud's own version for its own container.
    /// Keyed by image id.</summary>
    public Dictionary<string, string> ReportedVersion { get; } = new();

    public bool DockerConfigured => _docker.Configured;
    public string? Endpoint => _docker.Configured ? _docker.Endpoint : null;
    public bool DockerReachable { get; private set; }
    public (string? Version, string? HostName)? Daemon { get; private set; }
    public int LocalCount { get; private set; }
    public (int Cpus, long MemTotal, string? Os)? Resources { get; private set; }

    /// <summary>The MatCMS images on this daemon; null = could not be listed.</summary>
    public List<DockerHostService.ImageInfo>? Images { get; private set; }
    /// <summary>What the cleanup would remove: untagged and used by no container.</summary>
    public List<DockerHostService.ImageInfo> Prunable => Images?.Where(i => i.Dangling && i.InUse == 0).ToList() ?? new();

    public async Task OnGetAsync()
    {
        DockerReachable = await _docker.IsReachableAsync(HttpContext.RequestAborted);
        if (DockerReachable)
        {
            var info = await _docker.DaemonInfoAsync(HttpContext.RequestAborted);
            if (info.Error is null) Daemon = (info.Version, info.HostName);
            Resources = await _docker.HostResourcesAsync(HttpContext.RequestAborted);
            Images = await _docker.ListMatCmsImagesAsync(HttpContext.RequestAborted);
            if (Images is not null)
            {
                await ResolveFromRegistryAsync(Images.Where(i => i.Version is null).ToList());
                var local = await _db.Instances.AsNoTracking()
                    .Where(i => i.Hosting == InstanceHosting.Local && i.ContainerId != null && i.Version != null)
                    .Select(i => new { i.ContainerId, i.Version }).ToListAsync();
                var self = SelfContainer.Current;
                foreach (var img in Images.Where(i => i.Version is null))
                {
                    var v = img.ContainerIds
                        .Select(cid => self is not null && DockerHostService.IdMatches(cid, self.ToLowerInvariant()) ? _version.Current
                            : local.FirstOrDefault(i => DockerHostService.IdMatches(cid, i.ContainerId!.ToLowerInvariant()))?.Version)
                        .FirstOrDefault(v => v is not null);
                    if (v is not null) ReportedVersion[img.Id] = v;
                }
            }
        }
        LocalCount = await _db.Instances.CountAsync(i => i.Hosting == InstanceHosting.Local);
    }

    /// <summary>
    /// Asks GHCR which release each unlabelled MatCMS/MatCMS.Cloud image is, by the digest it was pulled as. Answers
    /// are cached per digest (a digest never changes its content; 12 h, 1 h for "not found"), and the whole lookup is
    /// bounded to a few seconds — the page must open when the registry is slow.
    /// </summary>
    private async Task ResolveFromRegistryAsync(List<DockerHostService.ImageInfo> images)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        foreach (var repo in new[] { ReleaseWatcher.Repo, VersionService.Repo })
        {
            var prefix = $"ghcr.io/{ReleaseWatcher.Owner}/{repo}@";
            var byDigest = images
                .SelectMany(i => i.Digests.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(d => (Image: i, Digest: d[prefix.Length..])))
                .ToList();
            var open = new List<string>();
            foreach (var (img, digest) in byDigest)
            {
                if (_cache.TryGetValue("ghcr.digest:" + digest, out string? known))
                { if (known is { Length: > 0 }) RegistryVersion[img.Id] = known; }
                else open.Add(digest);
            }
            if (open.Count == 0) continue;
            Dictionary<string, string> found;
            try { found = await _ghcr.ResolveDigestsAsync(ReleaseWatcher.Owner, repo, open.Distinct().ToList(), ct: cts.Token); }
            catch { continue; }
            foreach (var d in open.Distinct())
                _cache.Set("ghcr.digest:" + d, found.GetValueOrDefault(d) ?? "", found.ContainsKey(d) ? TimeSpan.FromHours(12) : TimeSpan.FromHours(1));
            foreach (var (img, digest) in byDigest)
                if (found.TryGetValue(digest, out var tag)) RegistryVersion[img.Id] = tag;
        }
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
