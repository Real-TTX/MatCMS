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

    private static string? Resolve()
    {
        // 1) cgroup v1: lines look like "…:/docker/<64-hex>" (or /kubepods/…/<64-hex>).
        //    cgroup v2 usually has no id here, which is why mountinfo follows.
        foreach (var path in new[] { "/proc/self/cgroup", "/proc/self/mountinfo" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    // mountinfo carries ".../docker/containers/<64-hex>/hostname" for the bind mounts
                    // Docker injects, which is the reliable source under cgroup v2.
                    var m = Sha.Match(line);
                    if (m.Success) return m.Value;
                }
            }
            catch { /* not readable (non-Linux, hardened runtime) → try the next source */ }
        }

        // 2) Fallback: Docker sets the hostname to the SHORT container id unless the operator
        //    overrode it. Only accept it when it actually looks like one, so a real host name
        //    ("web-01") is never mistaken for a container id.
        var host = Environment.MachineName?.Trim().ToLowerInvariant() ?? "";
        return ShortId.IsMatch(host) ? host : null;
    }
}
