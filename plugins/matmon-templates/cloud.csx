// The template store lives on Matmon Cloud (cloud.matmon.eu): this site shows its public catalogue and forwards what members submit.
// Reading is anonymous (GET /api/store/templates…); submitting, "my submissions", an update's prefill and erasing an account need the
// store API key the cloud's admin creates (Admin > Template store > API keys), kept here as a secret setting. A member is known to the
// cloud only as "{this site's host}:{member id}" plus the name and address the review mails go to.
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MatCMS.Plugin;

public sealed class MtCloudException : Exception
{
    public int Status { get; }
    public MtCloudException(string message, int status = 0) : base(message) { Status = status; }
}

public static class MtCloud
{
    public const string DefaultUrl = "https://cloud.matmon.eu";

    // The test seam: a handler standing in for the cloud.
    public static Func<HttpMessageHandler>? HandlerFactory;

    // The public catalogue changes rarely and every page view asks: a minute in memory, per address.
    private static readonly ConcurrentDictionary<string, (DateTime At, string Body)> Cache = new();

    public static string BaseUrl(IPluginContext ctx)
    {
        var url = (ctx.Settings.Get("cloudUrl") ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? url : DefaultUrl;
    }

    public static bool HasKey(IPluginContext ctx) => !string.IsNullOrWhiteSpace(ctx.Settings.Get("apiKey"));

    /// <summary>How this site names one of its members to the cloud (only its own updates may go into an entry of that name).</summary>
    public static string AuthorRef(IPluginContext ctx, long memberId)
    {
        var host = Uri.TryCreate(ctx.Site.Origin, UriKind.Absolute, out var o) ? o.Host : "site";
        return host + ":" + memberId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HttpClient Client() =>
        HandlerFactory is { } f ? new HttpClient(f(), true) { Timeout = TimeSpan.FromSeconds(15) } : new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private static HttpRequestMessage Request(IPluginContext ctx, HttpMethod method, string path, bool withKey)
    {
        var request = new HttpRequestMessage(method, BaseUrl(ctx) + path);
        if (withKey)
        {
            var key = ctx.Settings.Get("apiKey");
            if (string.IsNullOrWhiteSpace(key)) throw new MtCloudException(ctx.T("mt.err.noKey"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        }
        return request;
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(IPluginContext ctx, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var http = Client();
            using var response = await http.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            ctx.Log("Template store: the cloud could not be reached: " + ex.Message);
            throw new MtCloudException(ctx.T("mt.err.cloudDown"));
        }
        finally { request.Dispose(); }
    }

    private static JsonNode? Parse(string body)
    {
        try { return JsonNode.Parse(body); } catch (JsonException) { return null; }
    }

    /// <summary>The error the cloud gave in words (it answers <c>{ "error": "…" }</c>), else a general one.</summary>
    private static MtCloudException Failure(IPluginContext ctx, HttpStatusCode status, string body) =>
        new(Parse(body)?["error"]?.GetValue<string>() is { Length: > 0 } words ? words
            : status == HttpStatusCode.Unauthorized ? ctx.T("mt.err.keyRefused") : ctx.T("mt.err.cloudStatus", (int)status), (int)status);

    // ---- public catalogue (cached a minute)

    public static async Task<JsonNode?> GetPublicAsync(IPluginContext ctx, string path, CancellationToken ct)
    {
        var url = BaseUrl(ctx) + path;
        if (Cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(1)) return Parse(hit.Body);
        var (status, body) = await SendAsync(ctx, Request(ctx, HttpMethod.Get, path, false), ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK) throw Failure(ctx, status, body);
        if (Cache.Count > 500) Cache.Clear();
        Cache[url] = (DateTime.UtcNow, body);
        return Parse(body);
    }

    /// <summary>A file of the store, fetched for a visitor's download (the cloud counts it). Null = no such entry or version.</summary>
    public static async Task<(byte[] Bytes, string FileName)?> DownloadAsync(IPluginContext ctx, string slug, string? version, CancellationToken ct)
    {
        var path = "/api/store/templates/" + Uri.EscapeDataString(slug) + "/download" + (string.IsNullOrWhiteSpace(version) ? "" : "?v=" + Uri.EscapeDataString(version));
        var request = Request(ctx, HttpMethod.Get, path, false);
        try
        {
            using var http = Client();
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new MtCloudException(ctx.T("mt.err.cloudStatus", (int)response.StatusCode));
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 2 * 1024 * 1024) throw new MtCloudException(ctx.T("mt.err.tooLarge"));
            var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "";
            // The file name comes from the cloud; only a plain one is passed on.
            if (name.Length == 0 || name.Length > 120 || name.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_'))) name = "matmon-templates-" + slug + ".json";
            return (bytes, name);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MtCloudException(ctx.T("mt.err.cloudDown"));
        }
        finally { request.Dispose(); }
    }

    // ---- with the key

    public sealed record SubmitAnswer(string Slug, string Status, string[] Removed);

    public static async Task<SubmitAnswer> SubmitAsync(IPluginContext ctx, JsonObject body, CancellationToken ct)
    {
        var request = Request(ctx, HttpMethod.Post, "/api/store/submissions", true);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var (status, text) = await SendAsync(ctx, request, ct);
        if (status != HttpStatusCode.OK) throw Failure(ctx, status, text);
        var answer = Parse(text) as JsonObject ?? throw new MtCloudException(ctx.T("mt.err.cloudStatus", (int)status));
        Cache.Clear();
        return new SubmitAnswer(answer["slug"]?.GetValue<string>() ?? "", answer["status"]?.GetValue<string>() ?? "",
            (answer["removed"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>());
    }

    public static async Task<JsonArray> MineAsync(IPluginContext ctx, string authorRef, CancellationToken ct)
    {
        var (status, text) = await SendAsync(ctx, Request(ctx, HttpMethod.Get, "/api/store/submissions?author=" + Uri.EscapeDataString(authorRef), true), ct);
        if (status != HttpStatusCode.OK) throw Failure(ctx, status, text);
        return Parse(text)?["items"] as JsonArray ?? new JsonArray();
    }

    /// <summary>One's own entry (texts to prefill an update), or null when the slug is not the member's.</summary>
    public static async Task<JsonObject?> OwnEntryAsync(IPluginContext ctx, string authorRef, string slug, CancellationToken ct)
    {
        var (status, text) = await SendAsync(ctx, Request(ctx, HttpMethod.Get,
            "/api/store/authors/" + Uri.EscapeDataString(authorRef) + "/entries/" + Uri.EscapeDataString(slug), true), ct);
        if (status == HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK) throw Failure(ctx, status, text);
        return Parse(text) as JsonObject;
    }

    public static async Task EraseAuthorAsync(IPluginContext ctx, string authorRef, CancellationToken ct)
    {
        var (status, text) = await SendAsync(ctx, Request(ctx, HttpMethod.Delete, "/api/store/authors/" + Uri.EscapeDataString(authorRef), true), ct);
        if (status != HttpStatusCode.OK) throw Failure(ctx, status, text);
        Cache.Clear();
    }

    // ---- reading a cloud answer

    public static string S(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public static int I(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    public static bool B(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static string[] A(JsonNode? node, string name) =>
        (node?[name] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).ToArray() ?? Array.Empty<string>();
    public static DateTime? D(JsonNode? node, string name) =>
        DateTimeOffset.TryParse(S(node, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d.UtcDateTime : null;
}
