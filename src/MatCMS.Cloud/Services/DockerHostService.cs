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
public class DockerHostService
{
    private readonly ILogger<DockerHostService> _log;
    private readonly string _endpoint;
    private DockerClient? _client;
    private bool _failed;

    public DockerHostService(IConfiguration config, ILogger<DockerHostService> log)
    {
        _log = log;
        _endpoint = (config["MatCmsCloud:Docker:Endpoint"] ?? "").Trim();
    }

    public bool Configured => _endpoint.Length > 0;

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
        string Id, string Name, string Image, string State, int? PublishedPort, bool CloudManaged = false);

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

            var match = list.FirstOrDefault(c =>
                c.ID.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(c.ID, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;

            var name = (match.Names?.FirstOrDefault() ?? "").TrimStart('/');

            // Prefer the mapping of the container's own HTTP port (8080 in the MatCMS image); fall
            // back to any published TCP port. IPv6 duplicates of the same mapping are ignored.
            var published = (match.Ports ?? new List<Port>())
                .Where(p => p.PublicPort > 0 && (p.Type is null || p.Type == "tcp"))
                .OrderByDescending(p => p.PrivatePort == 8080)
                .Select(p => (int?)p.PublicPort)
                .FirstOrDefault();

            var managed = match.Labels is not null
                && match.Labels.TryGetValue(ManagedLabel, out var flag)
                && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

            return new ContainerInfo(match.ID, name, match.Image ?? "", match.State ?? "", published, managed);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Listing containers failed");
            return null;
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
    public async Task<UpdateResult> UpdateContainerAsync(string containerId, CancellationToken ct = default)
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
            // 1) Pull. A no-op when the digest is already local, so this is safe to run repeatedly.
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repo, Tag = tag },
                null,
                new Progress<JSONMessage>(),
                ct);

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
                var create = new CreateContainerParameters(insp.Config)
                {
                    Name = name,
                    HostConfig = insp.HostConfig,
                    // Copy only the network membership + aliases. Carrying the old EndpointSettings
                    // wholesale would re-assert the previous IP/MAC and can be rejected by the daemon.
                    NetworkingConfig = new NetworkingConfig
                    {
                        EndpointsConfig = (insp.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                            .ToDictionary(
                                kv => kv.Key,
                                kv => new EndpointSettings { Aliases = kv.Value?.Aliases })
                    }
                };

                var created = await client.Containers.CreateContainerAsync(create, ct);
                newId = created.ID;
                await client.Containers.StartContainerAsync(newId, new ContainerStartParameters(), ct);
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

            return new(true, $"Container '{name}' wurde auf das neue Image aktualisiert.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update of {Name} failed", name);
            return new(false, ex.Message);
        }
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
