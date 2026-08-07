using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Reads image tags from the GitHub Container Registry. This is the whole reason the cloud exists on
/// the update side: ONE poll here serves every connected instance, instead of each instance hammering
/// GHCR for itself.
/// </summary>
public class GhcrClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GhcrClient> _log;

    public GhcrClient(IHttpClientFactory http, ILogger<GhcrClient> log)
    {
        _http = http;
        _log = log;
    }

    public sealed record TagList(IReadOnlyList<string> Tags, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>Lists ALL tags of a public GHCR package. Never throws — failures come back as Error.</summary>
    public async Task<TagList> ListTagsAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Cloud");

            // 1) Anonymous pull token for the (public) package.
            var tokenUrl = $"https://ghcr.io/token?scope=repository:{owner}/{repo}:pull&service=ghcr.io";
            using var tokRes = await client.GetAsync(tokenUrl, ct);
            if (!tokRes.IsSuccessStatusCode)
                return new([], $"Token: HTTP {(int)tokRes.StatusCode}");
            using var tokDoc = JsonDocument.Parse(await tokRes.Content.ReadAsStringAsync(ct));
            var token = tokDoc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;

            // 2) List ALL tags. GHCR returns them in CREATION order and PAGINATES via a
            //    "Link: <…>; rel=\"next\"" header. Without following that header we only ever see the
            //    first (oldest) page, so the computed "latest" is stale and the check wrongly reports
            //    "up to date". Follow the Link header until it's gone (page cap as a safety net).
            var tags = new List<string>();
            var next = $"https://ghcr.io/v2/{owner}/{repo}/tags/list?n=100";
            for (var page = 0; page < 20 && next is not null; page++)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, next);
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new("Bearer", token);
                using var tagRes = await client.SendAsync(req, ct);
                if (!tagRes.IsSuccessStatusCode)
                    return new([], $"Registry: HTTP {(int)tagRes.StatusCode}");

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

            return new(tags, tags.Count == 0 ? "Keine Tags gefunden." : null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GHCR tag listing failed for {Owner}/{Repo}", owner, repo);
            return new([], ex.Message);
        }
    }
}

/// <summary>Comparison of the release tags both MatCMS and this app produce:
/// <c>MAJOR.MINOR.BUILD-yyyyMMddHHmmss</c>. Nightly/local tags have no numeric prefix and are ignored.</summary>
public static class ReleaseVersion
{
    public static (int major, int minor, int build)? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag, @"^(\d+)\.(\d+)\.(\d+)");
        return m.Success
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;
    }

    public static int Compare((int major, int minor, int build) a, (int major, int minor, int build) b)
    {
        if (a.major != b.major) return a.major.CompareTo(b.major);
        if (a.minor != b.minor) return a.minor.CompareTo(b.minor);
        return a.build.CompareTo(b.build);
    }

    /// <summary>Newest tag that carries a numeric version, or null when none does.</summary>
    public static string? Latest(IEnumerable<string> tags) =>
        tags.Select(s => (tag: s, ver: Parse(s)))
            .Where(x => x.ver is not null)
            .OrderByDescending(x => x.ver!.Value, Comparer<(int, int, int)>.Create(Compare))
            .Select(x => x.tag)
            .FirstOrDefault();

    /// <summary>True when <paramref name="latest"/> is a strictly newer release than
    /// <paramref name="current"/>. Unparsable input (nightly/local/unknown) = no update claimed.</summary>
    public static bool IsNewer(string? latest, string? current)
    {
        var l = Parse(latest);
        var c = Parse(current);
        return l is not null && c is not null && Compare(l.Value, c.Value) > 0;
    }
}
