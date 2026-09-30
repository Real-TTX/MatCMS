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

    /// <summary>
    /// Which release tag a pulled image IS: maps manifest digests (as a local image's RepoDigests carry them,
    /// <c>sha256:…</c>) to the newest release tag with that digest. A locally pulled image only keeps the tag it
    /// was pulled by — ":latest" — so this is the only way to name the build of an image that predates the
    /// <c>matcms.version</c> label. Looks at the newest <paramref name="maxTags"/> releases only (one HEAD request
    /// each); anything older stays unresolved. Never throws.
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveDigestsAsync(string owner, string repo, IReadOnlyCollection<string> digests,
        int maxTags = 30, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (digests.Count == 0) return result;
        try
        {
            var list = await ListTagsAsync(owner, repo, ct);
            if (!list.Ok) return result;
            var releases = list.Tags.Where(t => ReleaseVersion.Parse(t) is not null)
                .OrderByDescending(t => ReleaseVersion.Parse(t)!.Value, Comparer<(int, int, int)>.Create((a, b) => ReleaseVersion.Compare(a, b)))
                .Take(maxTags).ToList();

            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Cloud");
            using var tokRes = await client.GetAsync($"https://ghcr.io/token?scope=repository:{owner}/{repo}:pull&service=ghcr.io", ct);
            if (!tokRes.IsSuccessStatusCode) return result;
            using var tokDoc = JsonDocument.Parse(await tokRes.Content.ReadAsStringAsync(ct));
            var token = tokDoc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;

            var wanted = new HashSet<string>(digests, StringComparer.OrdinalIgnoreCase);
            // In parallel (bounded): thirty sequential round trips to GHCR took the page close to ten seconds.
            var found = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(8);
            await Task.WhenAll(releases.Select(async tag =>
            {
                await gate.WaitAsync(ct);
                try
                {
                var req = new HttpRequestMessage(HttpMethod.Head, $"https://ghcr.io/v2/{owner}/{repo}/manifests/{tag}");
                if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new("Bearer", token);
                // Every manifest kind CI can push (multi-arch index or single manifest, OCI or Docker) — the registry
                // answers with the digest of what it stores, which is exactly what a pull records locally.
                foreach (var mt in new[] { "application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
                                           "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json" })
                    req.Headers.Accept.ParseAdd(mt);
                using var res = await client.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode || !res.Headers.TryGetValues("Docker-Content-Digest", out var dv)) return;
                var digest = dv.FirstOrDefault();
                // Several tags can share a digest (a rebuild of nothing); the NEWEST release name wins.
                if (digest is not null && wanted.Contains(digest))
                    found.AddOrUpdate(digest, tag, (_, old) => ReleaseVersion.IsNewer(tag, old) ? tag : old);
                }
                catch { /* one tag that fails is one tag not resolved */ }
                finally { gate.Release(); }
            }));
            foreach (var kv in found) result[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GHCR digest lookup failed for {Owner}/{Repo}", owner, repo);
        }
        return result;
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
