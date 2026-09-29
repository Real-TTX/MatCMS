using System.Text.RegularExpressions;

namespace MatCMS.Services;

/// <summary>
/// Works out the id of the container we are running in. The cloud matches this against the
/// containers on its own Docker daemon to decide whether we are a LOCAL instance it may update, or
/// a REMOTE one it can only notify about — so getting it right is the whole point.
/// <para>Returns null outside a container (local <c>dotnet run</c>), which correctly makes us remote.</para>
/// </summary>
public static class ContainerIdentity
{
    private static readonly Regex Sha = new("[0-9a-f]{64}", RegexOptions.Compiled);
    private static readonly Regex ShortId = new("^[0-9a-f]{12}$", RegexOptions.Compiled);

    private static string? _cached;
    private static bool _resolved;

    /// <summary>Container id (64-hex when we can read it, else the 12-char short form), or null.
    /// Resolved once — the id cannot change while the process lives.</summary>
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

    private static readonly Regex ContainersPath = new("/containers/([0-9a-f]{64})/", RegexOptions.Compiled);

    private static string? Resolve()
    {
        // Docker sets the hostname to the SHORT container id unless the operator overrode it. Only
        // accepted when it actually looks like one, so a real host name ("web-01") is never mistaken
        // for a container id.
        var host = Environment.MachineName?.Trim().ToLowerInvariant() ?? "";
        var shortId = ShortId.IsMatch(host) ? host : null;

        // Candidates, best first: ids in Docker's ".../containers/<64-hex>/..." paths (the bind mounts it
        // injects — hostname, hosts, resolv.conf — the reliable source under cgroup v2), then any other
        // 64-hex (cgroup v1 "…:/docker/<64-hex>", /kubepods/…).
        //
        // NOT simply "the first 64-hex in the file": nested (Docker in Docker, some LXC/VM setups) mountinfo
        // also carries the ids of OUTER volumes, and the first one found was exactly such an id — the cloud
        // then looked for a container that does not exist and the site could not be managed.
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
            catch { /* not readable (non-Linux, hardened runtime) → try the next source */ }
        }
        var candidates = preferred.Concat(other).Distinct().ToList();

        // The short id from the hostname decides between several candidates when it can.
        if (shortId is not null && candidates.FirstOrDefault(c => c.StartsWith(shortId, StringComparison.Ordinal)) is { } agreed)
            return agreed;
        return candidates.FirstOrDefault() ?? shortId;
    }
}
