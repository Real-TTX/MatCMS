using System.Text.RegularExpressions;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The id of the container the CLOUD itself runs in — needed so the self-updater knows which container
/// to replace. Same detection as the CMS's <c>ContainerIdentity</c> (64-hex from <c>/proc/self/cgroup</c>
/// or <c>/proc/self/mountinfo</c>, falling back to the hostname only when it looks like Docker's 12-hex
/// short id), plus an explicit override for setups where neither works.
/// <para>Null outside a container (<c>dotnet run</c>) — the self-updater is then simply unavailable,
/// which is the honest answer: there is no container to replace.</para>
/// </summary>
public static class SelfContainer
{
    /// <summary>Environment override (<c>MatCmsCloud__SelfContainerId</c>) for a runtime where the id
    /// cannot be read from /proc and the hostname was set to something else.</summary>
    public const string OverrideEnv = "MatCmsCloud__SelfContainerId";

    private static readonly Regex Sha = new("[0-9a-f]{64}", RegexOptions.Compiled);
    private static readonly Regex ShortId = new("^[0-9a-f]{12}$", RegexOptions.Compiled);
    private static readonly Regex ContainersPath = new("/containers/([0-9a-f]{64})/", RegexOptions.Compiled);

    private static string? _cached;
    private static bool _resolved;

    public static string? Current
    {
        get
        {
            if (_resolved) return _cached;
            _cached = Resolve();
            _resolved = true;
            return _cached;
        }
    }

    private static string? Resolve()
    {
        var forced = Environment.GetEnvironmentVariable(OverrideEnv)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(forced)) return forced;

        // Same resolution as the CMS's ContainerIdentity: ids from Docker's ".../containers/<64-hex>/..." bind mounts
        // first, any other 64-hex after, the hostname (Docker's short id) deciding between candidates. NOT the first
        // 64-hex in the file — under nested Docker that is an OUTER volume id (found with a dind node).
        var host = Environment.MachineName?.Trim().ToLowerInvariant() ?? "";
        var shortId = ShortId.IsMatch(host) ? host : null;
        var preferred = new List<string>();
        var other = new List<string>();
        foreach (var path in new[] { "/proc/self/mountinfo", "/proc/self/cgroup" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    foreach (Match m in ContainersPath.Matches(line)) preferred.Add(m.Groups[1].Value);
                    foreach (Match m in Sha.Matches(line)) other.Add(m.Value);
                }
            }
            catch { /* not readable → next source */ }
        }
        var candidates = preferred.Concat(other).Distinct().ToList();
        if (shortId is not null && candidates.FirstOrDefault(c => c.StartsWith(shortId, StringComparison.Ordinal)) is { } agreed)
            return agreed;
        return candidates.FirstOrDefault() ?? shortId;
    }
}
