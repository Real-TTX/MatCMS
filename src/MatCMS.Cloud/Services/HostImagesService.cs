using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The MatCMS images of ONE host — "Dieser Host" (node null, the cloud's own daemon) or a node (through its agent,
/// jobs <c>images.list</c> / <c>images.prune</c>) — with the version each one is: the <c>matcms.version</c> label,
/// else the release tag GHCR has under its digest, else what a container on it reports. One service for the host
/// page's Docker tab, <c>/api/v1/hosting/images</c> and the MCP image tools.
/// </summary>
public sealed class HostImagesService
{
    private readonly AppDbContext _db;
    private readonly DockerHostService _docker;
    private readonly NodeService _nodes;
    private readonly VersionService _version;
    private readonly GhcrClient _ghcr;
    private readonly IMemoryCache _cache;

    public HostImagesService(AppDbContext db, DockerHostService docker, NodeService nodes, VersionService version, GhcrClient ghcr, IMemoryCache cache)
    {
        _db = db; _docker = docker; _nodes = nodes; _version = version; _ghcr = ghcr; _cache = cache;
    }

    /// <param name="Images">Null = could not be listed; <see cref="Error"/> says why.</param>
    /// <param name="Versions">Per image id: the resolved version (label, registry or reported).</param>
    /// <param name="Reported">Image ids whose version is only what a container on it reports.</param>
    public sealed record Result(List<DockerHostService.ImageInfo>? Images, string? Error,
        Dictionary<string, string> Versions, HashSet<string> Reported)
    {
        /// <summary>What the cleanup would remove: untagged and used by no container.</summary>
        public List<DockerHostService.ImageInfo> Prunable => Images?.Where(i => i.Dangling && i.InUse == 0).ToList() ?? new();
    }

    public async Task<Result> ListAsync(Node? node, CancellationToken ct = default)
    {
        List<DockerHostService.ImageInfo>? images;
        if (node is null)
        {
            if (!await _docker.IsReachableAsync(ct)) return new(null, "Docker-Daemon nicht erreichbar.", new(), new());
            images = await _docker.ListMatCmsImagesAsync(ct);
            if (images is null) return new(null, "Docker-Daemon nicht erreichbar.", new(), new());
        }
        else
        {
            var r = await _nodes.RunAsync(node, NodeJobKinds.ImagesList, new { }, null, TimeSpan.FromSeconds(30), ct);
            if (!r.Ok) return new(null, r.Message, new(), new());
            images = NodeJobExecutor.Deserialize<List<DockerHostService.ImageInfo>>(r.ResultJson) ?? new();
        }

        var versions = new Dictionary<string, string>();
        foreach (var i in images) if (i.Version is { } v) versions[i.Id] = v;
        await ResolveFromRegistryAsync(images.Where(i => !versions.ContainsKey(i.Id)).ToList(), versions, ct);

        // Last resort: the version a container on the image reports — an instance's heartbeat, or this cloud itself
        // for its own container.
        var reported = new HashSet<string>();
        var sites = await _db.Instances.AsNoTracking()
            .Where(i => i.ContainerId != null && i.Version != null && (node == null ? i.Hosting == InstanceHosting.Local : i.NodeId == node.Id))
            .Select(i => new { i.ContainerId, i.Version }).ToListAsync(ct);
        var self = node is null ? SelfContainer.Current : null;
        foreach (var img in images.Where(i => !versions.ContainsKey(i.Id)))
        {
            var v = img.ContainerIds
                .Select(cid => self is not null && DockerHostService.IdMatches(cid, self.ToLowerInvariant()) ? _version.Current
                    : sites.FirstOrDefault(s => DockerHostService.IdMatches(cid, s.ContainerId!.ToLowerInvariant()))?.Version)
                .FirstOrDefault(x => x is not null);
            if (v is not null) { versions[img.Id] = v; reported.Add(img.Id); }
        }
        return new(images, null, versions, reported);
    }

    /// <summary>Removes the old, untagged, unused MatCMS images of the host.</summary>
    public async Task<(bool Ok, string Message, DockerHostService.PruneResult? Result)> PruneAsync(Node? node, CancellationToken ct = default)
    {
        if (node is null) { var r = await _docker.PruneMatCmsImagesAsync(ct); return (true, "", r); }
        var o = await _nodes.RunAsync(node, NodeJobKinds.ImagesPrune, new { }, null, TimeSpan.FromMinutes(2), ct);
        return o.Ok ? (true, "", NodeJobExecutor.Deserialize<DockerHostService.PruneResult>(o.ResultJson)) : (false, o.Message, null);
    }

    /// <summary>
    /// Asks GHCR which release each unlabelled MatCMS/MatCMS.Cloud image is, by the digest it was pulled as. Cached per
    /// digest (a digest never changes its content; 12 h, 1 h for "not found"), and bounded to a few seconds — the page
    /// must open when the registry is slow.
    /// </summary>
    private async Task ResolveFromRegistryAsync(List<DockerHostService.ImageInfo> images, Dictionary<string, string> versions, CancellationToken ct)
    {
        if (images.Count == 0) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                { if (known is { Length: > 0 }) versions[img.Id] = known; }
                else open.Add(digest);
            }
            if (open.Count == 0) continue;
            Dictionary<string, string> found;
            try { found = await _ghcr.ResolveDigestsAsync(ReleaseWatcher.Owner, repo, open.Distinct().ToList(), ct: cts.Token); }
            catch { continue; }
            foreach (var d in open.Distinct())
                _cache.Set("ghcr.digest:" + d, found.GetValueOrDefault(d) ?? "", found.ContainsKey(d) ? TimeSpan.FromHours(12) : TimeSpan.FromHours(1));
            foreach (var (img, digest) in byDigest)
                if (found.TryGetValue(digest, out var tag)) versions[img.Id] = tag;
        }
    }
}
