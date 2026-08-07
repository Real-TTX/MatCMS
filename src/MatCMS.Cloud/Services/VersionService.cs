using System.Reflection;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Reports the running version of the CLOUD itself and (best-effort) checks GHCR for a newer image.
/// The app cannot update itself — updating means pulling the new image via Docker — so this only
/// surfaces whether an update exists. For the MANAGED instances see <see cref="ReleaseWatcher"/>.
/// </summary>
public class VersionService
{
    // GHCR image coordinates (registry API expects the lowercase owner/repo).
    public const string Owner = "real-ttx";
    public const string Repo = "matcms-cloud";
    public string ImageRef => $"ghcr.io/{Owner}/{Repo}";
    public string UpdateCommand => "docker compose pull && docker compose up -d";

    private readonly GhcrClient _ghcr;

    public VersionService(GhcrClient ghcr) => _ghcr = ghcr;

    /// <summary>Running app version (InformationalVersion, build metadata after '+' stripped).</summary>
    public string Current
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return "1.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    public record UpdateCheck(string Current, string? Latest, bool UpdateAvailable, string? Error);

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var current = Current;
        var tags = await _ghcr.ListTagsAsync(Owner, Repo, ct);
        if (!tags.Ok) return new(current, null, false, tags.Error);

        var latest = ReleaseVersion.Latest(tags.Tags);
        if (latest is null) return new(current, null, false, "Keine Versions-Tags gefunden.");

        return new(current, latest, ReleaseVersion.IsNewer(latest, current), null);
    }
}
