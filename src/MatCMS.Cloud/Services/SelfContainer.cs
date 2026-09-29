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

        foreach (var path in new[] { "/proc/self/cgroup", "/proc/self/mountinfo" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    // mountinfo carries ".../docker/containers/<64-hex>/hostname" for Docker's injected
                    // bind mounts — the reliable source under cgroup v2.
                    var m = Sha.Match(line);
                    if (m.Success) return m.Value;
                }
            }
            catch { /* not readable → next source */ }
        }

        var host = Environment.MachineName?.Trim().ToLowerInvariant() ?? "";
        return ShortId.IsMatch(host) ? host : null;
    }
}
