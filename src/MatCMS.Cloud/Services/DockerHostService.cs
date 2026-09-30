using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Everything that talks to the Docker engine. Two jobs:
/// <list type="number">
/// <item>decide whether an instance is <b>local</b> (its container lives on the daemon we can reach)
/// or <b>remote</b>,</item>
/// <item>update a local instance in place: pull the new image and recreate the container with the
/// same config, volumes and networks.</item>
/// </list>
/// <para>Access is OPTIONAL. Without a mounted socket (<c>MatCmsCloud:Docker:Endpoint</c> empty or
/// unreachable) every method degrades gracefully and every instance stays remote — the cloud then
/// only notifies.</para>
/// </summary>
public class DockerHostService : IDisposable
{
    private readonly ILogger<DockerHostService> _log;
    private readonly string _endpoint;
    private DockerClient? _client;
    private bool _failed;

    public DockerHostService(IConfiguration config, ILogger<DockerHostService> log)
    {
        _log = log;
        _endpoint = (config["MatCmsCloud:Docker:Endpoint"] ?? "").Trim();
        // Docker.DotNet 3.125 HANGS on "tcp://host:2375" (the notation every Docker doc uses) — no error, no
        // timeout; found when a node-agent talked to a remote daemon and never beat. "http://" is the same thing
        // without TLS and works, so the familiar form is accepted and translated.
        if (_endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            _endpoint = "http://" + _endpoint[6..];
    }

    public bool Configured => _endpoint.Length > 0;
    /// <summary>The endpoint in use (after the tcp→http translation), for display. Empty when unset.</summary>
    public string Endpoint => _endpoint;

    /// <summary>The cloud keeps one instance for its lifetime; the node-agent uses one per beat and per job, so a
    /// connection pool that got stuck (seen after an archive extract over http) dies with its client.</summary>
    public void Dispose() => _client?.Dispose();

    /// <summary>
    /// The label <see cref="HostingService"/> stamps on every container the cloud creates ITSELF.
    /// <para>It is the only honest answer to "did we build this?". An instance that merely joined
    /// with a code runs on somebody else's machine — or next to ours by coincidence — and finding
    /// its container on our daemon says where it is, not who put it there. Deriving a target from
    /// the display name instead would be a guess, and a guess is how the wrong container gets
    /// removed.</para>
    /// </summary>
    public const string ManagedLabel = "matcmscloud.managed";

    /// <summary>Derselbe Zugang für Dienste, die den Daemon nur LESEN — etwa die Portsuche. Die
    /// Verbindung wird hier einmal aufgebaut und bei einem Fehlschlag nicht wieder versucht; das
    /// gilt dann für alle Nutzer gleichermaßen.</summary>
    public DockerClient? ClientOrNull => Client;

    private DockerClient? Client
    {
        get
        {
            if (_failed || !Configured) return null;
            if (_client is not null) return _client;
            try
            {
                _client = new DockerClientConfiguration(new Uri(_endpoint)).CreateClient();
                return _client;
            }
            catch (Exception ex)
            {
                // A bad endpoint is a config mistake, not a runtime condition — log once and stay off.
                _log.LogWarning(ex, "Docker endpoint '{Endpoint}' is not usable — running notify-only", _endpoint);
                _failed = true;
                return null;
            }
        }
    }

    /// <summary>What the daemon knows about one container. <paramref name="PublishedPort"/> is the
    /// host port its HTTP port is mapped to, or null when nothing is published — that is what lets
    /// the cloud offer a preview URL for an instance that never reported one.</summary>
    /// <param name="CloudManaged">True when the container carries <see cref="ManagedLabel"/>, i.e.
    /// this cloud created it. Read from the SAME listing that decides local/remote, so it costs
    /// nothing extra and can never disagree with it.</param>
    public sealed record ContainerInfo(
        string Id, string Name, string Image, string State, int? PublishedPort, bool CloudManaged = false, string? Status = null);

    /// <summary>True when the daemon answers. Cheap ping, used by the settings/status UI.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return false;
        try
        {
            await client.System.PingAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Docker ping failed");
            return false;
        }
    }

    /// <summary>
    /// Finds the container an instance reported. Ids are matched by PREFIX because an instance reads
    /// its own id from the cgroup/hostname and may only know the short 12-character form.
    /// Returns null when the container is not on this daemon → the instance is remote.
    /// </summary>
    public async Task<ContainerInfo?> FindContainerAsync(string? containerId, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null || string.IsNullOrWhiteSpace(containerId)) return null;

        var id = containerId.Trim().ToLowerInvariant();
        if (id.Length < 12) return null; // too short to identify anything safely

        try
        {
            var list = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true }, ct);

            var match = list.FirstOrDefault(c => IdMatches(c.ID, id));
            return match is null ? null : ToInfo(match);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing containers failed");
            return null;
        }
    }

    /// <summary>Prefix match in both directions — an instance may only know the short 12-character id.</summary>
    public static bool IdMatches(string fullId, string reported) =>
        reported.Length >= 12 &&
        (fullId.StartsWith(reported, StringComparison.OrdinalIgnoreCase) ||
         reported.StartsWith(fullId, StringComparison.OrdinalIgnoreCase));

    private static ContainerInfo ToInfo(ContainerListResponse c)
    {
        var name = (c.Names?.FirstOrDefault() ?? "").TrimStart('/');

        // Prefer the mapping of the container's own HTTP port (8080 in the MatCMS image); fall
        // back to any published TCP port. IPv6 duplicates of the same mapping are ignored.
        var published = (c.Ports ?? new List<Port>())
            .Where(p => p.PublicPort > 0 && (p.Type is null || p.Type == "tcp"))
            .OrderByDescending(p => p.PrivatePort == 8080)
            .Select(p => (int?)p.PublicPort)
            .FirstOrDefault();

        var managed = c.Labels is not null
            && c.Labels.TryGetValue(ManagedLabel, out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

        return new ContainerInfo(c.ID, name, c.Image ?? "", c.State ?? "", published, managed, c.Status);
    }

    // ---- Node engine (Hosting increment 4) ----------------------------------------------------------
    //
    // What a node-agent needs beyond the actions above. Deliberately HERE and not in the agent: the cloud's
    // own host and every node run the same code, so a guard added once holds everywhere.

    /// <summary>The MatCMS containers on this daemon — what a node reports as its inventory. Null = no daemon.
    /// Filtered by the same <see cref="LooksLikeMatCms"/> guard as every action: the cloud has no business
    /// learning about the other containers on somebody's host.</summary>
    public async Task<List<ContainerInfo>?> ListMatCmsContainersAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return null;
        var list = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
        // The agent itself and a cloud on the same host are "matcms-cloud" images — infrastructure, not sites.
        return list.Where(c => LooksLikeMatCms(c.Image ?? "", c.Labels)
                               && !(c.Image ?? "").Contains("matcms-cloud", StringComparison.OrdinalIgnoreCase)
                               && !(c.Labels?.ContainsKey(UpdaterLabel) ?? false)
                               && !(c.Labels?.ContainsKey(AgentUpdaterLabel) ?? false))
            .Select(ToInfo).ToList();
    }

    /// <summary>What a container uses right now. <paramref name="MemLimit"/> is the container's limit, which
    /// without a limit set is the host's whole memory.</summary>
    public sealed record ContainerStats(double CpuPercent, long MemBytes, long MemLimit);

    /// <summary>
    /// CPU and memory of the given containers, sampled in parallel. Docker needs about a second per sample
    /// (CPU is a delta between two readings), so this is for pages that show usage, never for a hot path.
    /// Stopped containers and ones that do not answer within the bound are simply missing from the result.
    /// </summary>
    public async Task<Dictionary<string, ContainerStats>> StatsAsync(IEnumerable<string> containerIds, CancellationToken ct = default)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, ContainerStats>();
        var client = Client;
        if (client is null) return new();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        await Task.WhenAll(containerIds.Distinct().Select(async id =>
        {
            try
            {
                var sink = new LastValue<ContainerStatsResponse>();
                // Stream=false: ONE reading — which the daemon takes a second apart from its previous one, so
                // PreCPUStats is filled and the CPU share can be computed. (OneShot would skip that wait and
                // leave PreCPUStats empty.)
                await client.Containers.GetContainerStatsAsync(id, new ContainerStatsParameters { Stream = false }, sink, cts.Token);
                if (sink.Value is { } s && Compute(s) is { } st) result[id] = st;
            }
            catch { /* stopped, gone or slow — no number rather than a wrong one */ }
        }));
        return new(result);
    }

    private static ContainerStats? Compute(ContainerStatsResponse s)
    {
        if (s.CPUStats?.CPUUsage is null || s.MemoryStats is null) return null;
        var cpuDelta = (double)s.CPUStats.CPUUsage.TotalUsage - (s.PreCPUStats?.CPUUsage?.TotalUsage ?? 0);
        var sysDelta = (double)s.CPUStats.SystemUsage - (s.PreCPUStats?.SystemUsage ?? 0);
        var cpus = s.CPUStats.OnlineCPUs > 0 ? s.CPUStats.OnlineCPUs : (uint)(s.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1);
        var cpu = cpuDelta > 0 && sysDelta > 0 ? cpuDelta / sysDelta * cpus * 100.0 : 0;
        // Page cache is not "used" memory — docker stats subtracts it the same way (cgroup v2: inactive_file,
        // v1: cache).
        var stats = s.MemoryStats.Stats;
        ulong cache = 0;
        if (stats is not null && (stats.TryGetValue("inactive_file", out cache) || stats.TryGetValue("cache", out cache))) { }
        var used = (long)(s.MemoryStats.Usage > cache ? s.MemoryStats.Usage - cache : s.MemoryStats.Usage);
        return new ContainerStats(Math.Round(cpu, 1), used, (long)s.MemoryStats.Limit);
    }

    /// <summary>IProgress that keeps the last value synchronously — <see cref="Progress{T}"/> posts to the thread
    /// pool and could still be pending when the call returns.</summary>
    private sealed class LastValue<T> : IProgress<T> { public T? Value; public void Report(T value) => Value = value; }

    /// <summary>The host's size: CPU count and total memory. Null when the daemon cannot be asked.</summary>
    public async Task<(int Cpus, long MemTotal, string? Os)?> HostResourcesAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return null;
        try { var i = await client.System.GetSystemInfoAsync(ct); return ((int)i.NCPU, i.MemTotal, i.OperatingSystem); }
        catch { return null; }
    }

    /// <summary>One MatCMS image on this daemon. <paramref name="Dangling"/> = untagged, left behind when a pull
    /// re-pointed its tag — what <see cref="PruneMatCmsImagesAsync"/> removes when no container uses it.</summary>
    /// <param name="Tags">Every tag the image carries here — a pulled image usually has only the one it was pulled by.</param>
    /// <param name="Version">The build it is, from the <c>matcms.version</c> label our Dockerfiles bake in. Null for
    /// an image built before that label existed; the page then falls back to the version a container on it reports.
    /// Deliberately NOT <c>org.opencontainers.image.version</c> alone: an older image inherits the base image's
    /// ("24.04").</param>
    /// <param name="ContainerIds">The containers using it (running or not).</param>
    /// <param name="Digests">The registry digests it was pulled as (<c>repo@sha256:…</c>) — what lets the cloud ask
    /// the registry which release tag it is.</param>
    public sealed record ImageInfo(string Id, List<string> Tags, string Repo, long Size, DateTime Created, int InUse, bool Dangling,
        string? Version, List<string> ContainerIds, List<string> Digests)
    {
        public string Tag => Tags.FirstOrDefault() ?? Repo + ":<none>";

        /// <summary>Never came from a registry: no digest names a registry host ("ghcr.io/…"). Docker Desktop gives
        /// local builds a digest too ("matcms@sha256:…"), so "has a digest" alone does not decide it.</summary>
        public bool LocalBuild => !Digests.Any(d => d.Split('/')[0] is var host && d.Contains('/') && (host.Contains('.') || host.Contains(':')));
    }

    /// <summary>The MatCMS images on this daemon (instances and the cloud itself), newest first. Same attribution
    /// rule as the prune: by tag or repo digest naming MatCMS — another app's images are never listed.</summary>
    public async Task<List<ImageInfo>?> ListMatCmsImagesAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return null;
        try
        {
            var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            var use = containers.GroupBy(c => c.ImageID ?? "").ToDictionary(g => g.Key, g => g.Select(c => c.ID).ToList());
            return images
                .Select(img =>
                {
                    var tags = (img.RepoTags ?? new List<string>()).Where(t => !string.IsNullOrEmpty(t) && t != "<none>:<none>").ToList();
                    var digests = img.RepoDigests ?? new List<string>();
                    var isMatCms = tags.Any(t => t.Contains("matcms", StringComparison.OrdinalIgnoreCase))
                                   || digests.Any(d => d.Contains("matcms", StringComparison.OrdinalIgnoreCase));
                    if (!isMatCms) return null;
                    var repo = tags.Count > 0 ? SplitImage(tags[0]).repo : (digests.FirstOrDefault()?.Split('@')[0] ?? "");
                    string? version = null;
                    if (img.Labels is not null && img.Labels.TryGetValue("matcms.version", out var v) && !string.IsNullOrWhiteSpace(v)) version = v;
                    var users = use.GetValueOrDefault(img.ID) ?? new List<string>();
                    return new ImageInfo(img.ID, tags, repo, img.Size, img.Created, users.Count, tags.Count == 0, version, users, digests.ToList());
                })
                .Where(i => i is not null).Select(i => i!)
                .OrderByDescending(i => i.Created).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing MatCMS images failed");
            return null;
        }
    }

    /// <summary>Daemon version ("27.3.1") and the HOST's name (not the container's), or the error when the
    /// daemon cannot be reached.</summary>
    public async Task<(string? Version, string? HostName, string? Error)> DaemonInfoAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return (null, null, Configured ? "Docker-Endpunkt nicht nutzbar." : "Kein Docker-Zugriff konfiguriert.");
        try { var i = await client.System.GetSystemInfoAsync(ct); return (i.ServerVersion, i.Name, null); }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    /// <summary>All published host ports on this daemon (stopped containers included — they claim theirs again
    /// on start), or null when it cannot be asked. Null and empty are two different answers.</summary>
    public async Task<HashSet<int>?> UsedPortsAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return null;
        try
        {
            var list = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
            return list.SelectMany(c => c.Ports ?? new List<Port>())
                .Where(p => p.PublicPort > 0).Select(p => (int)p.PublicPort).ToHashSet();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Belegte Ports konnten nicht ermittelt werden.");
            return null;
        }
    }

    /// <summary>The first free port in [from, to], or null (none free, or no daemon — never a guess, which
    /// would only fail later when the container refuses to start).</summary>
    public async Task<int?> NextFreePortAsync(int from, int to, CancellationToken ct = default)
    {
        if (from > to) (from, to) = (to, from);
        var used = await UsedPortsAsync(ct);
        if (used is null) return null;
        for (var port = from; port <= to; port++)
            if (!used.Contains(port)) return port;
        return null;
    }

    // ---- Moving an instance between hosts (Hosting increment 5) -----------------------------------------
    //
    // A move copies the DATA VOLUME 1:1 rather than going through backup/restore: a backup restored into a
    // fresh container keeps the FRESH container's cloud link (ContentTransferService preserves cloud.*), so the
    // site would come back as a new instance. The volume carries its own link, so the same instance simply beats
    // from the new host and the cloud reclassifies it. The source is stopped before the export, which makes the
    // copy consistent (SQLite) and guarantees the identity never runs twice.

    /// <summary>What the target needs to rebuild the container: taken from the SOURCE container itself.</summary>
    /// <summary>Label on the helper that updates a node-agent — kept apart from the cloud's own updater label, so a
    /// node on the cloud's host never mistakes one for the other.</summary>
    public const string AgentUpdaterLabel = "matcmscloud.agentupdater";

    /// <summary>
    /// Updates the node-agent from OUTSIDE: like the cloud, an agent cannot replace the container it runs in (the
    /// process dies half-way). So it starts a one-shot helper — its own image, its daemon access (socket bind and/or
    /// the endpoint variable), its networks — which runs <c>--update-container &lt;id&gt;</c>: pull, recreate, and roll
    /// back if the new agent does not keep running (<see cref="UpdateContainerAsync"/> with a run check).
    /// </summary>
    public async Task<SpawnResult> SpawnContainerUpdateHelperAsync(string targetId, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return new(false, "Kein Docker-Zugriff konfiguriert.");
        ContainerInspectResponse self;
        try { self = await client.Containers.InspectContainerAsync(targetId, ct); }
        catch (Exception ex) { return new(false, $"Eigener Container nicht gefunden: {ex.Message}"); }
        if (!LooksLikeMatCms(self.Config?.Image ?? "", self.Config?.Labels))
            return new(false, $"Abgelehnt: '{self.Config?.Image}' sieht nicht nach MatCMS aus.");

        var helpers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["label"] = new Dictionary<string, bool> { [AgentUpdaterLabel + "=true"] = true } }
        }, ct);
        foreach (var h in helpers)
        {
            if (string.Equals(h.State, "running", StringComparison.OrdinalIgnoreCase)) return new(false, "Ein Agent-Update läuft bereits.");
            try { await client.Containers.RemoveContainerAsync(h.ID, new ContainerRemoveParameters { Force = true }, ct); } catch { }
        }

        // The image the agent RUNS (by id) — its updater is known to work; the tag when containerd dropped the record.
        var helperImage = self.Image;
        try { await client.Images.InspectImageAsync(self.Image, ct); }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { helperImage = self.Config?.Image ?? ""; }

        // Daemon access exactly as the agent has it: the socket bind and the endpoint variable, nothing else of its
        // configuration (the node token stays in the agent).
        var binds = (self.HostConfig?.Binds ?? new List<string>()).Where(b => b.Contains("docker.sock")).ToList();
        var env = (self.Config?.Env ?? new List<string>()).Where(e => e.StartsWith("MatCmsCloud__Docker__", StringComparison.Ordinal)).ToList();
        if (!env.Any(e => e.StartsWith("MatCmsCloud__Docker__Endpoint=", StringComparison.Ordinal)))
            env.Add("MatCmsCloud__Docker__Endpoint=unix:///var/run/docker.sock");

        var name = (self.Name ?? "").TrimStart('/');
        try
        {
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = $"{name}-updater",
                Image = helperImage,
                Cmd = new List<string> { "dotnet", "MatCMS.Cloud.dll", "--update-container", self.ID },
                Env = env,
                Labels = new Dictionary<string, string> { [AgentUpdaterLabel] = "true" },
                HostConfig = new HostConfig { Binds = binds, RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No } },
                // Same networks: a daemon reached over TCP (a dind/remote endpoint) must be reachable for the helper too.
                NetworkingConfig = new NetworkingConfig
                {
                    EndpointsConfig = (self.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                        .ToDictionary(kv => kv.Key, _ => new EndpointSettings())
                },
            }, ct);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            return new(true, "Agent-Update gestartet — der Agent wird in etwa einer Minute neu gestartet und meldet dann seine neue Version.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Starting the agent update helper failed");
            return new(false, $"Helfer konnte nicht gestartet werden: {ex.Message}");
        }
    }

    public sealed record ExportInfo(string Image, List<string> Env, string Name, string Volume, long Bytes);

    /// <summary>Writes a tar of the instance's <c>/app/appdata</c> to <paramref name="sink"/>. The container must be
    /// stopped (refused otherwise — a running SQLite database would be copied mid-write) and must be ours: only
    /// a container carrying <see cref="ManagedLabel"/> is moved, because the move retires or removes it.</summary>
    public async Task<(bool Ok, string Message, ExportInfo? Info)> ExportDataAsync(string containerId, Func<Stream, CancellationToken, Task<long>> sink, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return (false, "Kein Docker-Zugriff konfiguriert.", null);
        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(containerId, ct); }
        catch (Exception ex) { return (false, $"Container nicht gefunden: {ex.Message}", null); }
        var labels = insp.Config?.Labels;
        if (!LooksLikeMatCms(insp.Config?.Image ?? "", labels))
            return (false, $"Abgelehnt: '{insp.Config?.Image}' sieht nicht nach einer MatCMS-Instanz aus.", null);
        if (labels is null || !labels.TryGetValue(ManagedLabel, out var flag) || !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            return (false, "Dieser Container wurde nicht von dieser Cloud angelegt und wird nicht umgezogen.", null);
        if (insp.State?.Running == true)
            return (false, "Der Container läuft noch — er muss für den Umzug gestoppt sein.", null);
        var volume = insp.Mounts?.FirstOrDefault(m => m.Destination == ContainerDataDir && string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase))?.Name;
        if (string.IsNullOrEmpty(volume))
            return (false, $"Unter {ContainerDataDir} hängt kein benannter Datenträger — nichts, was sich umziehen ließe.", null);

        try
        {
            var archive = await client.Containers.GetArchiveFromContainerAsync(insp.ID,
                new GetArchiveFromContainerParameters { Path = ContainerDataDir }, false, ct);
            await using var tar = archive.Stream;
            var bytes = await sink(tar, ct);
            return (true, $"Daten exportiert ({(bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024} KB")}).",
                new ExportInfo(insp.Config?.Image ?? "", insp.Config?.Env?.ToList() ?? new(), (insp.Name ?? "").TrimStart('/'), volume, bytes));
        }
        catch (Exception ex) { return (false, "Export fehlgeschlagen: " + ex.Message, null); }
    }

    /// <summary>
    /// Retires the old copy after a move: renamed (<c>&lt;name&gt;-moved-&lt;date&gt;</c>, frees the name) and its
    /// restart policy set to "no", so neither a host reboot nor Docker ever starts it again — two containers
    /// with the same cloud identity would fight over the instance record. Its volume stays: nothing is deleted
    /// that the operator did not ask to delete.
    /// </summary>
    public async Task<ContainerActionResult> RetireContainerAsync(string containerId, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return new(false, "Kein Docker-Zugriff konfiguriert.");
        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(containerId, ct); }
        catch (Exception ex) { return new(false, $"Container nicht gefunden: {ex.Message}"); }
        if (!LooksLikeMatCms(insp.Config?.Image ?? "", insp.Config?.Labels))
            return new(false, $"Abgelehnt: '{insp.Config?.Image}' sieht nicht nach einer MatCMS-Instanz aus.");
        try
        {
            if (insp.State?.Running == true)
                await client.Containers.StopContainerAsync(insp.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 15 }, ct);
            await client.Containers.UpdateContainerAsync(insp.ID, new ContainerUpdateParameters { RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No } }, ct);
            var newName = $"{(insp.Name ?? "").TrimStart('/')}-moved-{DateTime.UtcNow:yyyyMMddHHmm}";
            await client.Containers.RenameContainerAsync(insp.ID, new ContainerRenameParameters { NewName = newName }, ct);
            return new(true, $"Alte Kopie stillgelegt als „{newName}“ (gestoppt, startet nicht mehr von selbst; Datenträger bleibt).");
        }
        catch (Exception ex) { return new(false, "Stilllegen fehlgeschlagen: " + ex.Message); }
    }

    /// <summary>What a new instance container is made of. The name is decided by the cloud (its naming pattern);
    /// labels and the port are decided HERE, on the host that owns them.</summary>
    public sealed record InstanceContainerSpec(string ContainerName, string VolumeName, string Image, List<string> Env,
        int PortFrom, int PortTo);

    public sealed record CreateContainerResult(bool Ok, string? Error, string? ContainerId, int? Port, string? ContainerName);

    /// <summary>
    /// Pulls the image, creates the instance container (stamped <see cref="ManagedLabel"/> + the labels compose
    /// writes) and starts it. A container that fails after creation is removed again — a stopped leftover with
    /// the right name would block the next attempt. The volume is deliberately kept: it may hold site data
    /// from the first second, and deleting data is no job for an error path.
    /// </summary>
    public async Task<CreateContainerResult> CreateInstanceContainerAsync(InstanceContainerSpec spec, CancellationToken ct = default,
        Func<CancellationToken, Task<Stream>>? seed = null)
    {
        var client = Client;
        if (client is null) return new(false, "Docker ist nicht erreichbar.", null, null, null);
        if (!LooksLikeMatCms(spec.Image, null))
            return new(false, $"Abgelehnt: '{spec.Image}' ist kein MatCMS-Image.", null, null, null);
        // A move seeds the volume with the old site's data. Into an EXISTING volume that would lay one site's
        // files over another's — so a seeded create insists on a volume that does not exist yet.
        if (seed is not null)
        {
            try
            {
                await client.Volumes.InspectAsync(spec.VolumeName, ct);
                return new(false, $"Der Datenträger „{spec.VolumeName}“ existiert auf diesem Host schon (eine alte Kopie?) — erst entfernen.", null, null, null);
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }

        var port = await NextFreePortAsync(spec.PortFrom, spec.PortTo, ct);
        if (port is null) return new(false, $"Kein freier Port zwischen {spec.PortFrom} und {spec.PortTo}.", null, null, null);

        string? createdId = null;
        try
        {
            // Pull first — otherwise creating fails with a message that reads like a bad call, not a missing image.
            // A moved site keeps its image, which may be a tag no registry knows (a local build) — then an image
            // already present on this host is good enough.
            try { await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = spec.Image }, null, new Progress<JSONMessage>(), ct); }
            catch (Exception) when (seed is not null)
            {
                if (!await ImageExistsAsync(client, spec.Image, ct)) throw;
            }

            var labels = new Dictionary<string, string>
            {
                [ManagedLabel] = "true",
                // The labels compose writes itself: Docker Desktop, Dockhand, Portainer group by them without
                // knowing anything about us. working_dir/config_files stay OUT — there is no file behind this.
                ["com.docker.compose.project"] = spec.ContainerName,
                ["com.docker.compose.service"] = "web",
                ["com.docker.compose.container-number"] = "1",
                ["com.docker.compose.oneoff"] = "False",
            };
            // No matcad.* labels: routes are created through Matcad's REST API (ProxyService). With label
            // discovery on, Matcad would otherwise build a SECOND route for the same host that cannot be
            // changed or removed without recreating the container.

            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = spec.ContainerName,
                Image = spec.Image,
                Labels = labels,
                Env = spec.Env,
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        ["8080/tcp"] = new List<PortBinding> { new() { HostPort = port.Value.ToString(CultureInfo.InvariantCulture) } }
                    },
                    Binds = new List<string> { spec.VolumeName + ":/app/appdata" },
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                },
            }, ct);
            createdId = created.ID;
            if (seed is not null)
            {
                // The archive of /app/appdata starts with "appdata/", so it is unpacked into /app — into the
                // created, not yet started container, whose volume is mounted there already.
                await using var tar = await seed(ct);
                await client.Containers.ExtractArchiveToContainerAsync(createdId, new ContainerPathStatParameters { Path = "/app" }, tar, ct);
            }
            await client.Containers.StartContainerAsync(createdId, new ContainerStartParameters(), ct);
            _log.LogInformation("Instanz {Name} angelegt: Container {Id} auf Port {Port}.", spec.ContainerName, createdId, port);
            return new(true, null, createdId, port, spec.ContainerName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Anlegen von {Name} fehlgeschlagen.", spec.ContainerName);
            if (createdId is not null)
            {
                try { await client.Containers.RemoveContainerAsync(createdId, new ContainerRemoveParameters { Force = true }, CancellationToken.None); }
                catch (Exception cleanup) { _log.LogWarning(cleanup, "Der halb gebaute Container {Id} blieb stehen.", createdId); }
                // The one exception to "an error path deletes no data": a SEEDED volume was created by this very
                // call (it was checked not to exist) and holds only a COPY — the original is untouched on the
                // source. Left behind it would block every retry of the move.
                if (seed is not null)
                    try { await client.Volumes.RemoveAsync(spec.VolumeName, force: false, CancellationToken.None); }
                    catch (Exception cleanup) { _log.LogWarning(cleanup, "Der Datenträger {Volume} der fehlgeschlagenen Übernahme blieb stehen.", spec.VolumeName); }
            }
            return new(false, ex.Message, null, null, null);
        }
    }

    public sealed record UpdateResult(bool Ok, string Message);

    /// <summary>
    /// Pulls the image the container is configured with and recreates the container from its own
    /// inspected config (same name, env, volumes, ports, labels, networks). Rolls back to the old
    /// container if the new one cannot be created or started.
    /// <para><b>Guard:</b> refuses to touch a container whose image does not look like a MatCMS image.
    /// The cloud has root-equivalent power through the socket, so it must only ever act on the
    /// containers it positively identified.</para>
    /// <para>Note: a compose-managed container keeps its <c>com.docker.compose.*</c> labels (they live
    /// in the container config we copy), so <c>docker compose</c> still recognises it afterwards.</para>
    /// </summary>
    /// <param name="mustStayUp">Optional run check: the new container must keep running (no restart) for this long,
    /// otherwise it is rolled back like a failed start. Used for the node-agent — an agent that does not come up
    /// takes its whole node out of reach, and it has no HTTP port to health-check.</param>
    public async Task<UpdateResult> UpdateContainerAsync(string containerId, CancellationToken ct = default, TimeSpan? mustStayUp = null)
    {
        var client = Client;
        if (client is null) return new(false, "Kein Docker-Zugriff konfiguriert.");

        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(containerId, ct); }
        catch (Exception ex) { return new(false, $"Container nicht gefunden: {ex.Message}"); }

        var image = insp.Config?.Image ?? "";
        if (!LooksLikeMatCms(image, insp.Config?.Labels))
            return new(false, $"Abgelehnt: '{image}' sieht nicht nach einer MatCMS-Instanz aus.");

        var name = (insp.Name ?? "").TrimStart('/');
        var oldId = insp.ID;
        var (repo, tag) = SplitImage(image);

        try
        {
            // 1) Pull. A no-op when the digest is already local, so this is safe to run repeatedly. A tag no registry
            //    knows (a local build) cannot be pulled — then a DIFFERENT local image under that tag is the update,
            //    exactly as for the cloud's own self-update.
            try
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = repo, Tag = tag },
                    null,
                    new Progress<JSONMessage>(),
                    ct);
            }
            catch (Exception pullEx) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(pullEx, "Pull of {Image} failed — trying the local image under that tag", image);
            }

            var pulled = await client.Images.InspectImageAsync(image, ct);
            if (pulled.ID == insp.Image)
                return new(true, "Bereits aktuell — das gezogene Image ist identisch.");

            // 2) Park the old container under a temporary name so the new one can take the real one.
            var parked = $"{name}-matcmscloud-old";
            await client.Containers.StopContainerAsync(oldId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct);
            await client.Containers.RenameContainerAsync(oldId,
                new ContainerRenameParameters { NewName = parked }, ct);

            string? newId = null;
            try
            {
                var created = await client.Containers.CreateContainerAsync(RecreateParams(insp, name), ct);
                newId = created.ID;
                await client.Containers.StartContainerAsync(newId, new ContainerStartParameters(), ct);
                if (mustStayUp is { } window && await StaysUpAsync(client, newId, window, ct) is { } why)
                    throw new InvalidOperationException("Der neue Container lief nicht stabil: " + why);
            }
            catch (Exception ex)
            {
                // 3) Roll back: drop the half-built container, give the old one its name back and
                //    start it again. An instance must never be left down by a failed update.
                _log.LogError(ex, "Update of {Name} failed — rolling back", name);
                if (newId is not null)
                    try { await client.Containers.RemoveContainerAsync(newId, new ContainerRemoveParameters { Force = true }, ct); } catch { }
                try
                {
                    await client.Containers.RenameContainerAsync(oldId, new ContainerRenameParameters { NewName = name }, ct);
                    await client.Containers.StartContainerAsync(oldId, new ContainerStartParameters(), ct);
                }
                catch (Exception rollbackEx)
                {
                    _log.LogError(rollbackEx, "Rollback of {Name} failed — container {Id} needs manual attention", name, oldId);
                    return new(false, $"Update fehlgeschlagen UND Rollback fehlgeschlagen: {ex.Message} / {rollbackEx.Message}");
                }
                return new(false, $"Update fehlgeschlagen, alter Container läuft wieder: {ex.Message}");
            }

            // 4) Success — the old container is no longer needed.
            try { await client.Containers.RemoveContainerAsync(oldId, new ContainerRemoveParameters { Force = true }, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Old container {Id} could not be removed", oldId); }

            // 5) The old image is now dangling (the new pull re-pointed the tag). Prune the OLD MatCMS images
            //    it left behind so the host disk does not fill over successive updates. Best effort.
            try
            {
                var pr = await PruneMatCmsImagesAsync(ct);
                if (pr.Removed > 0) _log.LogInformation("Pruned {N} old MatCMS image(s) after updating {Name}.", pr.Removed, name);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Post-update image prune failed for {Name}", name); }

            return new(true, $"Container '{name}' wurde auf das neue Image aktualisiert.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update of {Name} failed", name);
            return new(false, ex.Message);
        }
    }

    public sealed record PruneResult(int Removed, long BytesReclaimed);

    /// <summary>
    /// Removes OLD MatCMS images the host no longer needs — the untagged ("dangling") images an update
    /// leaves behind when a new pull re-points the tag. Deliberately narrow:
    /// <list type="bullet">
    /// <item>only UNTAGGED images (a tagged image is either in use or intentionally kept);</item>
    /// <item>only ones attributable to MATCMS via their repo digest — another app's dangling layers are
    ///       never touched, which is exactly what "nur MatCMS-Images" asks for;</item>
    /// <item>delete with <c>Force=false</c>, so the daemon refuses (and we skip) any image a container
    ///       still uses — an image can never be pulled out from under a running site.</item>
    /// </list>
    /// The reclaimed byte count is approximate (shared layers can overstate it).
    /// </summary>
    public async Task<PruneResult> PruneMatCmsImagesAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return new(0, 0);

        int removed = 0;
        long bytes = 0;
        try
        {
            var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = false }, ct);
            foreach (var img in images)
            {
                var tags = img.RepoTags ?? new List<string>();
                var isTagged = tags.Any(t => !string.IsNullOrEmpty(t) && t != "<none>:<none>");
                if (isTagged) continue;   // keep tagged images

                var digests = img.RepoDigests ?? new List<string>();
                var isMatCms = digests.Any(d => d.Contains("matcms", StringComparison.OrdinalIgnoreCase));
                if (!isMatCms) continue;   // never touch another app's dangling layers

                try
                {
                    await client.Images.DeleteImageAsync(img.ID, new ImageDeleteParameters { Force = false }, ct);
                    removed++;
                    bytes += img.Size;
                }
                catch { /* still used by a container → skip, safe */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MatCMS image prune failed");
        }
        return new(removed, bytes);
    }

    /// <summary>
    /// The create parameters that rebuild a container from its own inspected config — same name, env,
    /// volumes, ports, labels, restart policy, networks. Shared by the instance update and the cloud
    /// self-update so both recreate identically.
    /// <para>Two deliberate edits to the copy: networks carry only membership + aliases (the old
    /// EndpointSettings would re-assert the previous IP/MAC and can be rejected), and a hostname that
    /// Docker AUTO-assigned (= the old short id) is dropped so the new container gets its own — otherwise
    /// the new container would report the OLD id as its hostname, and anything falling back to the
    /// hostname to identify itself would name a container that no longer exists. A hostname the operator
    /// set explicitly (compose <c>hostname:</c>) is kept.</para>
    /// </summary>
    private static CreateContainerParameters RecreateParams(ContainerInspectResponse insp, string name)
    {
        var cfg = insp.Config;
        if (cfg is not null && !string.IsNullOrEmpty(cfg.Hostname) && !string.IsNullOrEmpty(insp.ID)
            && insp.ID.StartsWith(cfg.Hostname, StringComparison.OrdinalIgnoreCase))
            cfg.Hostname = "";

        return new CreateContainerParameters(cfg)
        {
            Name = name,
            HostConfig = insp.HostConfig,
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = (insp.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                    .ToDictionary(kv => kv.Key, kv => new EndpointSettings { Aliases = kv.Value?.Aliases })
            }
        };
    }

    /// <summary>Splits "ghcr.io/real-ttx/matcms:latest" into repo + tag (default "latest"). A digest
    /// reference has no mutable tag to pull, so it is treated as repo-only.</summary>
    private static (string repo, string tag) SplitImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return ("", "latest");
        var at = image.IndexOf('@');
        if (at > 0) return (image[..at], "latest");
        // Only a colon AFTER the last slash is a tag — "host:5000/repo" has one before it.
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        return colon > slash && colon > 0
            ? (image[..colon], image[(colon + 1)..])
            : (image, "latest");
    }

    /// <param name="Volumes">The named volumes the container actually had, as the daemon reported
    /// them — never a name rebuilt from the instance's display name.</param>
    public sealed record TeardownTarget(
        string Id, string Name, string Image, IReadOnlyList<string> Volumes, bool CloudManaged);

    /// <summary>
    /// Inspects the container an instance reported and returns exactly what a teardown would touch.
    ///
    /// <para>This exists so the confirmation the operator sees is built from the DAEMON's answer and
    /// nothing else. The volume name is derivable on paper — <c>HostingService</c> builds it as
    /// <c>&lt;stack&gt;-data</c> from the display name — but the display name is editable on the
    /// instance's own page, so re-deriving it later can name a volume that belongs to something
    /// else entirely. Reading the mounts off the container we are about to remove cannot.</para>
    ///
    /// <para>Returns null when there is no such container here, which is also the honest answer for
    /// a remote instance: nothing on this host to tear down.</para>
    /// </summary>
    public sealed record ContainerActionResult(bool Ok, string Message);
    public enum PowerAction { Start, Stop, Restart }

    /// <summary>Starts a stopped instance container. Guarded like every other socket action: only a container
    /// that positively looks like a MatCMS image, so the cloud's root-equivalent socket power can never touch
    /// something it did not identify. Reversible, so this is the lighter guard (image), not the managed-label
    /// one the destructive removal uses.</summary>
    public Task<ContainerActionResult> StartContainerAsync(string containerId, CancellationToken ct = default)
        => PowerAsync(containerId, PowerAction.Start, ct);

    /// <summary>Stops a running instance container (graceful, 15 s before kill). Same guard as start.</summary>
    public Task<ContainerActionResult> StopContainerAsync(string containerId, CancellationToken ct = default)
        => PowerAsync(containerId, PowerAction.Stop, ct);

    /// <summary>Restarts an instance container (graceful, 15 s before kill). Same guard as start.</summary>
    public Task<ContainerActionResult> RestartContainerAsync(string containerId, CancellationToken ct = default)
        => PowerAsync(containerId, PowerAction.Restart, ct);

    private async Task<ContainerActionResult> PowerAsync(string containerId, PowerAction action, CancellationToken ct)
    {
        var client = Client;
        if (client is null) return new(false, "Kein Docker-Zugriff konfiguriert.");

        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(containerId, ct); }
        catch (Exception ex) { return new(false, $"Container nicht gefunden: {ex.Message}"); }

        var image = insp.Config?.Image ?? "";
        if (!LooksLikeMatCms(image, insp.Config?.Labels))
            return new(false, $"Abgelehnt: '{image}' sieht nicht nach einer MatCMS-Instanz aus.");

        try
        {
            switch (action)
            {
                case PowerAction.Start:
                    var started = await client.Containers.StartContainerAsync(insp.ID, new ContainerStartParameters(), ct);
                    return new(true, started ? "Instanz gestartet." : "Instanz lief bereits.");
                case PowerAction.Stop:
                    var stopped = await client.Containers.StopContainerAsync(insp.ID,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 15 }, ct);
                    return new(true, stopped ? "Instanz gestoppt." : "Instanz war bereits gestoppt.");
                default: // Restart
                    await client.Containers.RestartContainerAsync(insp.ID,
                        new ContainerRestartParameters { WaitBeforeKillSeconds = 15 }, ct);
                    return new(true, "Instanz neu gestartet.");
            }
        }
        catch (Exception ex)
        {
            var verb = action switch { PowerAction.Start => "Start", PowerAction.Stop => "Stopp", _ => "Neustart" };
            return new(false, $"{verb} fehlgeschlagen: {ex.Message}");
        }
    }

    public async Task<TeardownTarget?> InspectTeardownAsync(string? containerId, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return null;

        var found = await FindContainerAsync(containerId, ct);
        if (found is null) return null;

        try
        {
            var info = await client.Containers.InspectContainerAsync(found.Id, ct);

            // Only NAMED volumes. A bind mount belongs to the host's file system and is not ours to
            // delete; an anonymous volume has no name to offer and goes with the container anyway.
            var volumes = (info.Mounts ?? [])
                .Where(m => string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var managed = info.Config?.Labels is { } labels
                          && labels.TryGetValue(ManagedLabel, out var flag)
                          && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

            return new TeardownTarget(info.ID, (info.Name ?? "").TrimStart('/'),
                info.Config?.Image ?? found.Image, volumes, managed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Inspecting container {Id} failed", found.Id);
            return null;
        }
    }

    public sealed record TeardownResult(bool Ok, string Message, IReadOnlyList<string> RemovedVolumes);

    /// <summary>
    /// Removes a container the cloud created, and — only if asked — its named volumes with it.
    ///
    /// <para><b>Three guards, and none of them is optional.</b> The container must still be the one
    /// the caller inspected (<paramref name="expectedId"/> is the full id from
    /// <see cref="InspectTeardownAsync"/>, not a name), it must carry <see cref="ManagedLabel"/>, and
    /// it must still look like MatCMS. A container this cloud did not create is refused outright —
    /// an instance that only joined with a code runs on somebody else's machine, and the cloud may
    /// forget it but must never reach into it.</para>
    ///
    /// <para><b>The trap: <c>ContainerRemoveParameters.RemoveVolumes</c> does NOT remove named
    /// volumes.</b> It is `docker rm --volumes`, which only clears ANONYMOUS ones — and the instance's
    /// data volume is named (<c>&lt;stack&gt;-data</c>). Setting that flag and calling it done would
    /// report "everything removed" while the customer's database sat there forever. The named volumes
    /// are therefore removed one by one, explicitly, after the container is gone.</para>
    ///
    /// <para>The volumes are removed AFTER the container, because a volume still in use cannot be
    /// removed and the daemon would refuse. A volume that fails anyway is reported by name rather
    /// than swallowed: an operator who chose "remove everything" needs to know what stayed.</para>
    /// </summary>
    public async Task<TeardownResult> RemoveInstanceContainerAsync(
        string expectedId, bool removeVolumes, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return new(false, "Kein Zugriff auf den Docker-Daemon.", []);
        if (string.IsNullOrWhiteSpace(expectedId) || expectedId.Length < 12)
            return new(false, "Kein gültiges Ziel angegeben.", []);

        ContainerInspectResponse info;
        try
        {
            info = await client.Containers.InspectContainerAsync(expectedId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Container {Id} could not be inspected for teardown", expectedId);
            return new(false, "Der Container wurde auf dem Daemon nicht gefunden.", []);
        }

        var labels = info.Config?.Labels;
        var image = info.Config?.Image ?? "";

        if (labels is null || !labels.TryGetValue(ManagedLabel, out var flag)
            || !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            return new(false, "Dieser Container wurde nicht von dieser Cloud angelegt und wird nicht angefasst.", []);

        if (!LooksLikeMatCms(image, labels))
            return new(false, "Dieser Container sieht nicht nach MatCMS aus und wird nicht angefasst.", []);

        var volumes = removeVolumes
            ? (info.Mounts ?? [])
                .Where(m => string.Equals(m.Type, "volume", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => m.Name).Distinct(StringComparer.Ordinal).ToList()
            : [];

        try
        {
            // Stopping first is politeness, not a requirement — Force would kill it anyway. A
            // container that is already stopped makes this throw, which is not a failure.
            try { await client.Containers.StopContainerAsync(info.ID, new ContainerStopParameters(), ct); }
            catch (Exception ex) { _log.LogDebug(ex, "Container {Id} was not running", info.ID); }

            await client.Containers.RemoveContainerAsync(
                info.ID, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Removing container {Id} failed", info.ID);
            return new(false, "Der Container konnte nicht entfernt werden: " + ex.Message, []);
        }

        var removed = new List<string>();
        var failed = new List<string>();
        foreach (var volume in volumes)
        {
            try
            {
                await client.Volumes.RemoveAsync(volume, force: false, ct);
                removed.Add(volume);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Removing volume {Volume} failed", volume);
                failed.Add(volume);
            }
        }

        // The container is gone either way, so this is a partial success and has to read as one:
        // saying "removed" while the data volume is still on disk is the report that gets believed.
        if (failed.Count > 0)
            return new(true, $"Container entfernt. Diese Datenträger blieben stehen: {string.Join(", ", failed)}.", removed);

        return new(true, removeVolumes && removed.Count > 0
            ? $"Container und Datenträger entfernt ({string.Join(", ", removed)})."
            : "Container entfernt.", removed);
    }

    // ---- Container inspection + logs (Hosting tab) --------------------------------------------------

    /// <summary>What the Hosting tab shows about a container, read live from the daemon.</summary>
    public sealed record ContainerDetails(
        string Id, string Name, string Image, string ImageId, string State, DateTime? StartedAt,
        long RestartCount, int? PublishedPort, bool CloudManaged, string? Health);

    /// <summary>Live details of an instance's container, or null when it is not on this daemon.</summary>
    public async Task<ContainerDetails?> GetContainerDetailsAsync(string? containerId, CancellationToken ct = default)
    {
        var client = Client;
        var found = await FindContainerAsync(containerId, ct);
        if (client is null || found is null) return null;
        try
        {
            var i = await client.Containers.InspectContainerAsync(found.Id, ct);
            // StartedAt is a string in some Docker.DotNet versions and a DateTime in others; going through
            // Convert.ToString keeps this independent of which one is referenced.
            DateTime? started = DateTime.TryParse(Convert.ToString(i.State?.StartedAt, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var s)
                && s.Year > 1 ? s : null;
            return new ContainerDetails(i.ID, (i.Name ?? "").TrimStart('/'), i.Config?.Image ?? found.Image,
                i.Image ?? "", i.State?.Status ?? found.State, started, i.RestartCount,
                found.PublishedPort, found.CloudManaged, i.State?.Health?.Status);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Inspecting container {Id} failed", found.Id);
            return null;
        }
    }

    /// <summary>
    /// The last <paramref name="tail"/> lines of a container's stdout + stderr, with timestamps. Read frame
    /// by frame from the multiplexed stream, so both streams stay in the order the container wrote them
    /// (reading them separately and concatenating would put every error after every normal line). Same
    /// "looks like MatCMS" guard as the power actions — the socket must never be used to read an
    /// unrelated container.
    /// </summary>
    public async Task<(bool Ok, string Text)> GetContainerLogsAsync(string containerId, int tail, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return (false, "Kein Docker-Zugriff konfiguriert.");

        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(containerId, ct); }
        catch (Exception ex) { return (false, $"Container nicht gefunden: {ex.Message}"); }
        if (!LooksLikeMatCms(insp.Config?.Image ?? "", insp.Config?.Labels))
            return (false, $"Abgelehnt: '{insp.Config?.Image}' sieht nicht nach einer MatCMS-Instanz aus.");

        tail = Math.Clamp(tail, 1, 5000);
        try
        {
            using var stream = await client.Containers.GetContainerLogsAsync(insp.ID, insp.Config?.Tty ?? false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Timestamps = true, Tail = tail.ToString(CultureInfo.InvariantCulture) }, ct);

            var sb = new StringBuilder();
            var decoder = Encoding.UTF8.GetDecoder();   // a multi-byte char may straddle two frames
            var buf = new byte[16 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(buf.Length)];
            while (true)
            {
                var r = await stream.ReadOutputAsync(buf, 0, buf.Length, ct);
                if (r.EOF) break;
                var n = decoder.GetChars(buf, 0, r.Count, chars, 0);
                sb.Append(chars, 0, n);
            }
            return (true, sb.ToString());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reading logs of {Id} failed", insp.ID);
            return (false, $"Logs nicht lesbar: {ex.Message}");
        }
    }

    // ---- Reverse-proxy reachability (Hosting increment 3) ------------------------------------------

    /// <summary>Whether a Docker network of that name exists on this daemon.</summary>
    public async Task<bool> NetworkExistsAsync(string network, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null || string.IsNullOrWhiteSpace(network)) return false;
        try { await client.Networks.InspectNetworkAsync(network, ct); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Connects an instance container to the proxy's network, LIVE — no recreate, the site keeps running. A
    /// proxy that addresses containers by name (Caddy, Matcad's Caddy) can only resolve them on a network it
    /// shares with them, and a provisioned instance starts on the default bridge. Already connected = done.
    /// Same "looks like MatCMS" guard as every other socket action.
    /// </summary>
    public async Task<(bool Ok, string Message, string? ContainerName)> ConnectToNetworkAsync(string containerId, string network, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return (false, "Kein Docker-Zugriff konfiguriert.", null);

        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(containerId, ct); }
        catch (Exception ex) { return (false, $"Container nicht gefunden: {ex.Message}", null); }
        if (!LooksLikeMatCms(insp.Config?.Image ?? "", insp.Config?.Labels))
            return (false, $"Abgelehnt: '{insp.Config?.Image}' sieht nicht nach einer MatCMS-Instanz aus.", null);

        var name = (insp.Name ?? "").TrimStart('/');
        if (insp.NetworkSettings?.Networks?.ContainsKey(network) == true)
            return (true, "Bereits im Netz.", name);
        try
        {
            await client.Networks.ConnectNetworkAsync(network, new NetworkConnectParameters { Container = insp.ID }, ct);
            return (true, $"Mit Netz '{network}' verbunden.", name);
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (false, $"Docker-Netz '{network}' existiert nicht.", name);
        }
        catch (Exception ex)
        {
            return (false, $"Verbinden mit '{network}' fehlgeschlagen: {ex.Message}", name);
        }
    }

    // ---- Cloud self-update --------------------------------------------------------------------------
    //
    // A process cannot replace the container it is running in: the moment it stops "itself" it is gone,
    // half-way through. So the cloud starts a short-lived HELPER container (same image, "--self-update"
    // mode, socket + data volume mounted) and the helper does the swap from outside — the instance
    // update's pull → park → recreate → rollback, plus two things an instance update does not need:
    // a real HEALTH CHECK (the cloud migrates its database on start; "the container started" is not
    // "the cloud works"), and a DATABASE SNAPSHOT taken after the old cloud stopped, put back on
    // rollback, so a new version that migrated the schema and then failed never leaves the old code
    // facing a newer schema.

    /// <summary>Label on the helper container, so a running update is recognisable and never doubled.</summary>
    public const string UpdaterLabel = "matcmscloud.updater";

    /// <summary>Where the data volume sits inside both the cloud and the helper container.</summary>
    public const string ContainerDataDir = "/app/appdata";

    public sealed record SpawnResult(bool Ok, string Message);

    /// <summary>
    /// Starts the helper that will update the cloud container <paramref name="selfContainerId"/>. Returns as
    /// soon as the helper runs — the swap itself happens after this request has answered.
    /// <para>Refuses when the data directory is not a mount: recreating a container whose data lives in its
    /// own writable layer would silently throw every instance link away.</para>
    /// </summary>
    public async Task<SpawnResult> SpawnSelfUpdateHelperAsync(string selfContainerId, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return new(false, "Kein Docker-Zugriff konfiguriert.");

        ContainerInspectResponse self;
        try { self = await client.Containers.InspectContainerAsync(selfContainerId, ct); }
        catch (Exception ex) { return new(false, $"Eigener Container nicht gefunden: {ex.Message}"); }
        if (!LooksLikeMatCms(self.Config?.Image ?? "", self.Config?.Labels))
            return new(false, $"Abgelehnt: '{self.Config?.Image}' sieht nicht nach MatCMS aus.");

        var sock = self.Mounts?.FirstOrDefault(m => m.Destination == "/var/run/docker.sock");
        if (sock is null || string.IsNullOrWhiteSpace(sock.Source))
            return new(false, "Der Docker-Socket ist nicht in den Cloud-Container gemountet.");
        var data = self.Mounts?.FirstOrDefault(m => m.Destination == ContainerDataDir);
        if (data is null)
            return new(false, $"{ContainerDataDir} ist kein Volume/Mount — ein Neuaufbau würde alle Daten verlieren. Abgebrochen.");

        // One update at a time. A finished helper from an earlier run is cleared away; a running one wins.
        var helpers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [UpdaterLabel + "=true"] = true }
            }
        }, ct);
        foreach (var h in helpers)
        {
            if (string.Equals(h.State, "running", StringComparison.OrdinalIgnoreCase))
                return new(false, "Ein Cloud-Update läuft bereits.");
            try { await client.Containers.RemoveContainerAsync(h.ID, new ContainerRemoveParameters { Force = true }, ct); } catch { }
        }

        // Which image the helper runs. Preferably the one the cloud RUNS (by id): its updater is the code
        // that is known to work right now. But with the containerd image store (the default in newer
        // Docker), moving the tag to a newer image — a `docker compose pull` done before clicking update,
        // or a local rebuild — drops the old image's record: the container keeps running, yet its image id
        // can no longer be referenced. Then fall back to the TAG, i.e. the new image, which carries the
        // updater too. (Found by the local test, not by reasoning.)
        var helperImage = self.Image;
        try { await client.Images.InspectImageAsync(self.Image, ct); }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            helperImage = self.Config?.Image ?? "";
            try { await client.Images.InspectImageAsync(helperImage, ct); }
            catch (Exception) { return new(false, $"Weder das laufende Image noch '{helperImage}' ist lokal vorhanden."); }
        }

        var name = (self.Name ?? "").TrimStart('/');
        var binds = new List<string>
        {
            $"{sock.Source}:/var/run/docker.sock",
            string.Equals(data.Type, "volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(data.Name)
                ? $"{data.Name}:{ContainerDataDir}"
                : $"{data.Source}:{ContainerDataDir}",
        };

        try
        {
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = $"{name}-updater",
                Image = helperImage,
                Cmd = new List<string> { "dotnet", "MatCMS.Cloud.dll", "--self-update", self.ID },
                Env = new List<string> { "MatCmsCloud__Docker__Endpoint=unix:///var/run/docker.sock" },
                Labels = new Dictionary<string, string> { [UpdaterLabel] = "true" },
                HostConfig = new HostConfig
                {
                    Binds = binds,
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
                },
                // Same networks as the cloud, so the helper can health-check the new cloud by its address.
                NetworkingConfig = new NetworkingConfig
                {
                    EndpointsConfig = (self.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                        .ToDictionary(kv => kv.Key, _ => new EndpointSettings())
                },
            }, ct);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            return new(true, "Update gestartet — die Cloud wird in 1–2 Minuten neu gestartet.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Starting the self-update helper failed");
            return new(false, $"Helper konnte nicht gestartet werden: {ex.Message}");
        }
    }

    /// <summary>The newest self-update helper container, if any: whether it still runs, and its exit code.
    /// Lets the status tell "the helper is working" apart from "the helper died without a result" — e.g. a
    /// new image that cannot even start its runtime, which must read as failed, not as forever running.</summary>
    public async Task<(bool Exists, bool Running, long ExitCode)> GetUpdaterHelperStateAsync(CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return (false, false, 0);
        try
        {
            var list = await client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [UpdaterLabel + "=true"] = true }
                }
            }, ct);
            var h = list.OrderByDescending(c => c.Created).FirstOrDefault();
            if (h is null) return (false, false, 0);
            var running = string.Equals(h.State, "running", StringComparison.OrdinalIgnoreCase);
            if (running) return (true, true, 0);
            var i = await client.Containers.InspectContainerAsync(h.ID, ct);
            return (true, false, i.State?.ExitCode ?? 0);
        }
        catch { return (false, false, 0); }
    }

    public sealed record SelfUpdateResult(bool Ok, string State, string Message, string? ToImage = null);

    /// <summary>Data directory as the HELPER sees it (its working dir is the image's /app).</summary>
    private static string HelperDataDir => Path.Combine(Directory.GetCurrentDirectory(), "appdata");

    /// <summary>
    /// Runs INSIDE the helper container: replaces the cloud container <paramref name="targetId"/> with one on
    /// the newest image, health-checks it, and rolls back — container AND database — if it does not come up.
    /// <paramref name="log"/> receives every step (the runner mirrors it into <see cref="SelfUpdateState"/>).
    /// </summary>
    public async Task<SelfUpdateResult> SelfUpdateAsync(string targetId, Action<string> log, CancellationToken ct = default)
    {
        var client = Client;
        if (client is null) return new(false, "failed", "Kein Docker-Zugriff im Helper.");

        // Let the request that started us deliver its response before the cloud goes down.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        ContainerInspectResponse insp;
        try { insp = await client.Containers.InspectContainerAsync(targetId, ct); }
        catch (Exception ex) { return new(false, "failed", $"Cloud-Container nicht gefunden: {ex.Message}"); }

        var image = insp.Config?.Image ?? "";
        if (!LooksLikeMatCms(image, insp.Config?.Labels))
            return new(false, "failed", $"Abgelehnt: '{image}' sieht nicht nach MatCMS aus.");

        var name = (insp.Name ?? "").TrimStart('/');
        var oldId = insp.ID;
        var (repo, tag) = SplitImage(image);
        log($"Ziel: {name} ({Short(oldId)}), Image {image}.");

        // 1) Pull. A cloud built locally (dev compose: matcms-cloud:latest) has no registry behind its tag —
        //    then the tag may already point at a newer LOCAL build, which is just as valid a target.
        try
        {
            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repo, Tag = tag }, null, new Progress<JSONMessage>(), ct);
            log("Image gezogen.");
        }
        catch (Exception ex) { log($"Pull nicht möglich ({ex.Message}) — verwende das lokale Image."); }

        ImageInspectResponse target;
        try { target = await client.Images.InspectImageAsync(image, ct); }
        catch (Exception ex) { return new(false, "failed", $"Image '{image}' nicht vorhanden: {ex.Message}"); }
        if (target.ID == insp.Image)
            return new(true, "current", "Bereits aktuell — kein neueres Image vorhanden. Nichts verändert.", target.ID);
        log($"Neues Image {Short(target.ID)} (bisher {Short(insp.Image)}).");

        // 2) Stop the old cloud gracefully, THEN snapshot its database — a stopped process has flushed and
        //    released it, so the copy is consistent.
        try { await client.Containers.StopContainerAsync(oldId, new ContainerStopParameters { WaitBeforeKillSeconds = 30 }, ct); }
        catch (Exception ex) { return new(false, "failed", $"Alte Cloud ließ sich nicht stoppen: {ex.Message}"); }
        log("Alte Cloud gestoppt.");
        var (backupDir, dbPath) = BackupDatabase(insp, log);

        var parked = $"{name}-matcmscloud-old";
        string? newId = null;
        try
        {
            await RemoveStaleParkedAsync(client, parked, oldId, log, ct);
            await client.Containers.RenameContainerAsync(oldId, new ContainerRenameParameters { NewName = parked }, ct);

            var created = await client.Containers.CreateContainerAsync(RecreateParams(insp, name), ct);
            newId = created.ID;
            await client.Containers.StartContainerAsync(newId, new ContainerStartParameters(), ct);
            log($"Neue Cloud gestartet ({Short(newId)}) — warte auf den Health-Check …");

            var (healthy, why) = await WaitHealthyAsync(client, newId, TimeSpan.FromSeconds(120), ct);
            if (!healthy) throw new InvalidOperationException(why);
            log("Health-Check bestanden.");
        }
        catch (Exception ex)
        {
            log($"FEHLER: {ex.Message} — Rollback.");
            var ok = await RollbackSelfAsync(client, oldId, newId, name, backupDir, dbPath, log, ct);
            return new(false, ok ? "rolled-back" : "failed",
                ok ? $"Update fehlgeschlagen ({ex.Message}); die alte Cloud läuft wieder."
                   : $"Update UND Rollback fehlgeschlagen ({ex.Message}) — Container {Short(oldId)} manuell prüfen!",
                target.ID);
        }

        // 3) Success — the old container, old images and all but the newest DB snapshots can go.
        try { await client.Containers.RemoveContainerAsync(oldId, new ContainerRemoveParameters { Force = true }, ct); }
        catch (Exception ex) { log($"Alter Container nicht entfernt: {ex.Message}"); }
        try { var pr = await PruneMatCmsImagesAsync(ct); if (pr.Removed > 0) log($"{pr.Removed} alte(s) Image(s) entfernt."); }
        catch { /* best effort */ }
        PruneDbBackups(log);
        return new(true, "succeeded", $"Cloud auf Image {Short(target.ID)} aktualisiert.", target.ID);
    }

    // Redirects are NOT followed: a cloud with "HTTPS erzwingen" answers the plain-http probe with a redirect,
    // which is a perfectly healthy answer — following it into TLS on the container IP would fail and roll
    // back a working update.
    private static readonly HttpClient Probe = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Healthy = the container is running AND answers HTTP on its app port (8080) with anything
    /// below 500. A container that exits, or never answers within <paramref name="timeout"/>, is not.</summary>
    private static async Task<(bool Ok, string Why)> WaitHealthyAsync(DockerClient client, string id, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        var last = "keine Antwort";
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            ContainerInspectResponse i;
            try { i = await client.Containers.InspectContainerAsync(id, ct); }
            catch (Exception ex) { last = ex.Message; continue; }

            // A crashing cloud does not stay "exited": it inherits the old container's restart policy
            // (unless-stopped), so Docker keeps restarting it and it never even gets an address. Waiting for
            // "exited" therefore sat out the whole timeout (found by the local rollback test: 2 min down
            // instead of seconds). Any restart of a container that is seconds old means it crashed.
            if (i.RestartCount > 0 || i.State is { Restarting: true }
                || (i.State is { Running: false } && i.State.Status is "exited" or "dead" or "restarting"))
                return (false, $"Der neue Container startet nicht (Status {i.State?.Status}, Exit-Code {i.State?.ExitCode}, Neustarts {i.RestartCount}).");

            var ip = i.NetworkSettings?.Networks?.Values
                .Select(n => n?.IPAddress).FirstOrDefault(a => !string.IsNullOrEmpty(a));
            if (string.IsNullOrEmpty(ip)) { last = "noch keine Netzwerkadresse"; continue; }

            try
            {
                using var resp = await Probe.GetAsync($"http://{ip}:8080/login", ct);
                if ((int)resp.StatusCode < 500) return (true, "");
                last = $"HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex) { last = ex.Message; }
        }
        return (false, $"Health-Check nach {timeout.TotalSeconds:0} s nicht bestanden ({last}).");
    }

    private async Task<bool> RollbackSelfAsync(DockerClient client, string oldId, string? newId, string name,
        string? backupDir, string? dbPath, Action<string> log, CancellationToken ct)
    {
        if (newId is not null)
        {
            try { await client.Containers.RemoveContainerAsync(newId, new ContainerRemoveParameters { Force = true }, ct); log("Neuer Container entfernt."); }
            catch (Exception ex) { log($"Neuer Container nicht entfernbar: {ex.Message}"); }

            // The new version may already have migrated the schema — the OLD code must get the database
            // back exactly as it left it. Only needed once a new container existed; before that nothing
            // could have touched the file.
            if (backupDir is not null && dbPath is not null) RestoreDatabase(backupDir, dbPath, log);
        }
        try
        {
            var info = await client.Containers.InspectContainerAsync(oldId, ct);
            if ((info.Name ?? "").TrimStart('/') != name)
                await client.Containers.RenameContainerAsync(oldId, new ContainerRenameParameters { NewName = name }, ct);
            await client.Containers.StartContainerAsync(oldId, new ContainerStartParameters(), ct);
            log("Alte Cloud wieder gestartet.");
            return true;
        }
        catch (Exception ex)
        {
            log($"Rollback fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    /// <summary>A parked container left by an earlier interrupted run would block the rename. It is stale by
    /// definition — a cloud (<paramref name="keepId"/>) is running and asked for this update — but it is only
    /// removed when it positively is a MatCMS container.</summary>
    private async Task RemoveStaleParkedAsync(DockerClient client, string parked, string keepId, Action<string> log, CancellationToken ct)
    {
        try
        {
            var i = await client.Containers.InspectContainerAsync(parked, ct);
            if (i.ID == keepId) return;
            if (!LooksLikeMatCms(i.Config?.Image ?? "", i.Config?.Labels))
                throw new InvalidOperationException($"'{parked}' existiert und ist kein MatCMS-Container.");
            await client.Containers.RemoveContainerAsync(i.ID, new ContainerRemoveParameters { Force = true }, ct);
            log($"Verwaisten Container '{parked}' entfernt.");
        }
        catch (DockerContainerNotFoundException) { /* the normal case */ }
    }

    /// <summary>The cloud's SQLite file as the helper sees it — only when it lives on the data volume
    /// (the helper mounts nothing else). Read from the cloud container's own connection string.</summary>
    private static string? ResolveDbPath(ContainerInspectResponse insp)
    {
        const string key = "ConnectionStrings__Default=";
        var cs = insp.Config?.Env?.FirstOrDefault(e => e.StartsWith(key, StringComparison.OrdinalIgnoreCase))?[key.Length..]
                 ?? "Data Source=appdata/matcmscloud.db";
        var m = Regex.Match(cs, @"Data Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var p = m.Groups[1].Value.Trim();
        var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), p));
        var data = Path.GetFullPath(HelperDataDir).TrimEnd('/') + "/";
        return full.StartsWith(data, StringComparison.Ordinal) ? full : null;
    }

    private static (string? Dir, string? Db) BackupDatabase(ContainerInspectResponse insp, Action<string> log)
    {
        var db = ResolveDbPath(insp);
        if (db is null) { log("WARNUNG: Datenbank liegt nicht im Datenvolume — keine DB-Sicherung vor dem Update."); return (null, null); }
        try
        {
            var dir = Path.Combine(HelperDataDir, "self-update", "pre-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var n = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = db + suffix;
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true);
                n++;
            }
            log($"Datenbank gesichert ({n} Datei(en)).");
            return (dir, db);
        }
        catch (Exception ex)
        {
            log($"WARNUNG: DB-Sicherung fehlgeschlagen: {ex.Message}");
            return (null, null);
        }
    }

    private static void RestoreDatabase(string backupDir, string dbPath, Action<string> log)
    {
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var dst = dbPath + suffix;
                var src = Path.Combine(backupDir, Path.GetFileName(dst));
                if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
                // A WAL/SHM the NEW version wrote must not be replayed by the old one. The main file itself
                // is never deleted — only replaced.
                else if (suffix.Length > 0 && File.Exists(dst)) File.Delete(dst);
            }
            log("Datenbank auf den Stand vor dem Update zurückgesetzt.");
        }
        catch (Exception ex) { log($"WARNUNG: DB-Rücksicherung fehlgeschlagen: {ex.Message}"); }
    }

    /// <summary>Keeps the three newest pre-update snapshots; older ones only cost disk.</summary>
    private static void PruneDbBackups(Action<string> log)
    {
        try
        {
            var root = Path.Combine(HelperDataDir, "self-update");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root, "pre-*").OrderByDescending(x => x, StringComparer.Ordinal).Skip(3))
                try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch (Exception ex) { log($"Alte DB-Sicherungen nicht aufgeräumt: {ex.Message}"); }
    }

    private static string Short(string? id)
    {
        var s = (id ?? "").Replace("sha256:", "", StringComparison.OrdinalIgnoreCase);
        return s.Length > 12 ? s[..12] : s;
    }

    /// <summary>Null = it kept running for the whole window; otherwise why not. Any restart counts — a crash-looping
    /// container under "unless-stopped" never shows "exited", only restarts (learned from the cloud self-update).</summary>
    private static async Task<string?> StaysUpAsync(DockerClient client, string id, TimeSpan window, CancellationToken ct)
    {
        var until = DateTime.UtcNow + window;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var i = await client.Containers.InspectContainerAsync(id, ct);
            if (i.RestartCount > 0 || i.State?.Restarting == true) return "er startet immer wieder neu.";
            if (i.State?.Running != true) return $"er hat sich beendet (Exit-Code {i.State?.ExitCode}).";
        }
        return null;
    }

    private static async Task<bool> ImageExistsAsync(DockerClient client, string image, CancellationToken ct)
    {
        try { await client.Images.InspectImageAsync(image, ct); return true; } catch { return false; }
    }

    /// <summary>Safety guard for the destructive path: the image name (or a compose service label)
    /// must mention MatCMS.</summary>
    private static bool LooksLikeMatCms(string image, IDictionary<string, string>? labels)
    {
        if (image.Contains("matcms", StringComparison.OrdinalIgnoreCase)) return true;
        if (labels is null) return false;
        return labels.TryGetValue("com.docker.compose.project", out var project)
               && project.Contains("matcms", StringComparison.OrdinalIgnoreCase);
    }
}
