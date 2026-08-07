using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatCMS.Services;

/// <summary>
/// Reports the running application version and (best-effort) checks GHCR for a newer published
/// image. The app cannot update itself — updating means pulling the new image via Docker — so this
/// only surfaces whether an update exists.
/// </summary>
public class VersionService
{
    // GHCR image coordinates (registry API expects the lowercase owner/repo).
    public const string Owner = "real-ttx";
    public const string Repo = "matcms";
    public string ImageRef => $"ghcr.io/{Owner}/{Repo}";
    public string UpdateCommand => "docker compose pull && docker compose up -d";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<VersionService> _log;

    public VersionService(IHttpClientFactory http, ILogger<VersionService> log)
    {
        _http = http;
        _log = log;
    }

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

    // Leading Major.Minor.Build of a tag (e.g. "1.0.42-20260725-1200" or "1.2.3").
    private static (int major, int minor, int build)? ParseVer(string tag)
    {
        var m = Regex.Match(tag, @"^(\d+)\.(\d+)\.(\d+)");
        return m.Success
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;
    }

    private static int Cmp((int major, int minor, int build) a, (int major, int minor, int build) b)
    {
        if (a.major != b.major) return a.major.CompareTo(b.major);
        if (a.minor != b.minor) return a.minor.CompareTo(b.minor);
        return a.build.CompareTo(b.build);
    }

    /// <summary>Queries GHCR for the newest release tag. Never throws — failures come back as Error.</summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var current = Current;
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-UpdateCheck");

            // 1) Anonymous pull token for the (public) package.
            var tokenUrl = $"https://ghcr.io/token?scope=repository:{Owner}/{Repo}:pull&service=ghcr.io";
            using var tokRes = await client.GetAsync(tokenUrl, ct);
            if (!tokRes.IsSuccessStatusCode)
                return new(current, null, false, $"Token: HTTP {(int)tokRes.StatusCode}");
            using var tokDoc = JsonDocument.Parse(await tokRes.Content.ReadAsStringAsync(ct));
            var token = tokDoc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;

            // 2) List ALL image tags. GHCR returns the tag list in creation order and PAGINATES it via a
            //    "Link: <…>; rel=\"next\"" header. Without following that header we only ever see the
            //    first (oldest) page, so the computed "latest" is stale and the check wrongly reports
            //    "up to date". Follow the Link header until it's gone (page cap as a safety net).
            var tags = new List<string>();
            var next = $"https://ghcr.io/v2/{Owner}/{Repo}/tags/list?n=100";
            for (var page = 0; page < 20 && next is not null; page++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, next);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new("Bearer", token);
                using var tagRes = await client.SendAsync(req, ct);
                if (!tagRes.IsSuccessStatusCode)
                    return new(current, null, false, $"Registry: HTTP {(int)tagRes.StatusCode}");

                using var tagDoc = JsonDocument.Parse(await tagRes.Content.ReadAsStringAsync(ct));
                if (tagDoc.RootElement.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                    tags.AddRange(tagsEl.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s))!);

                // Next page: "Link: </v2/…/tags/list?last=…&n=…>; rel=\"next\"" (relative path → prefix host).
                next = null;
                if (tagRes.Headers.TryGetValues("Link", out var links))
                {
                    var m = Regex.Match(string.Join(",", links), @"<([^>]+)>\s*;\s*rel=""next""");
                    if (m.Success)
                    {
                        var u = m.Groups[1].Value;
                        next = u.StartsWith("http") ? u : $"https://ghcr.io{u}";
                    }
                }
            }

            if (tags.Count == 0)
                return new(current, null, false, "Keine Tags gefunden.");

            var versioned = tags
                .Select(s => (tag: s, ver: ParseVer(s)))
                .Where(x => x.ver is not null)
                .Select(x => (x.tag, v: x.ver!.Value))
                .OrderByDescending(x => x.v, Comparer<(int, int, int)>.Create(Cmp))
                .ToList();

            if (versioned.Count == 0)
                return new(current, null, false, "Keine Versions-Tags gefunden.");

            var latest = versioned[0];
            var cur = ParseVer(current);
            var update = cur is not null && Cmp(latest.v, cur.Value) > 0;
            return new(current, latest.tag, update, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
            return new(current, null, false, ex.Message);
        }
    }
}
