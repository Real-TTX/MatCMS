namespace MatCMS.Cloud.Models;

/// <summary>
/// A further customer domain of an instance, next to its main one (<see cref="Instance.ProxyDomain"/>): typically
/// <c>www.example.de</c> beside <c>example.de</c>, or an old domain that should keep working. It always lives where
/// the main domain lives (edge or host proxy, <see cref="Via"/>) and follows it — a changed main domain, a move to
/// another host, switching the edge — through <c>ProxyService.SyncExtraDomainsAsync</c>; tearing the site down
/// removes it with the other routes. Without a main domain there are no further domains.
/// </summary>
public class InstanceDomain
{
    public int Id { get; set; }

    public int InstanceId { get; set; }
    public Instance? Instance { get; set; }

    /// <summary>Bare host, lowercase. Unique across all instances and against main domains and host addresses.</summary>
    public string Domain { get; set; } = "";

    /// <summary><see cref="InstanceDomainModes"/>: "redirect" (to the main domain — the usual case, one address for
    /// search engines) or "alias" (serves the same site under this name).</summary>
    public string Mode { get; set; } = InstanceDomainModes.Redirect;

    // Where its route lives — the same fields as the main domain's, for the same reason: an unpublish must reach the
    // proxy the route was made at, even after the settings changed.
    public string? Provider { get; set; }
    public string? RouteId { get; set; }
    public string? Via { get; set; }
    public string? Error { get; set; }
    public DateTime? PublishedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class InstanceDomainModes
{
    public const string Redirect = "redirect";
    public const string Alias = "alias";

    public static string Normalise(string? mode) =>
        string.Equals(mode?.Trim(), Alias, StringComparison.OrdinalIgnoreCase) ? Alias : Redirect;
}
