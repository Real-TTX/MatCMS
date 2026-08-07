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
    public sealed record ContainerInfo(string Id, string Name, string Image, string State, int? PublishedPort);

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

            return new ContainerInfo(match.ID, name, match.Image ?? "", match.State ?? "", published);
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
