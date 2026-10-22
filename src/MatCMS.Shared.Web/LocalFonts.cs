using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MatCMS.Shared.Web;

/// <summary>
/// Google Fonts, served from THIS server. A page used to link <c>fonts.googleapis.com</c>, so every visitor's browser contacted Google and
/// handed it the visitor's IP address — which German courts have ruled a data-protection violation without consent. Now the page links
/// <c>/fonts/css2?…</c> (the same query Google takes); the first request fetches the stylesheet and its font files ONCE, server-side, keeps
/// them under <c>{data}/fonts</c>, and every later visitor gets them from here. No visitor ever talks to Google.
/// <para>Template code written by users (an <c>@import</c> or a <c>&lt;link&gt;</c> to Google in a template's CSS or layout) is rewritten
/// the same way when it is rendered (<see cref="Rewrite"/>). If Google cannot be reached on that first request, the answer is an
/// empty stylesheet that is not cached — the page falls back to its system fonts and tries again later, it never links Google instead.</para>
/// </summary>
public static class LocalFonts
{
    private const string Google = "https://fonts.googleapis.com";
    // A current desktop browser: Google answers by user agent, and this one gets woff2 with unicode-range subsets.
    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private static readonly Regex FontUrl = new(@"url\((https://fonts\.gstatic\.com/[^)\s'""]+)\)", RegexOptions.Compiled);
    private static readonly Regex FileName = new(@"^[a-f0-9]{32}\.(woff2|woff|ttf|otf)$", RegexOptions.Compiled);
    private static readonly Regex Preconnect = new(@"<link\b[^>]*\bhref\s*=\s*[""']https?://fonts\.(googleapis|gstatic)\.com/?[""'][^>]*>\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private const int MaxQuery = 1500, MaxFiles = 300, MaxFileBytes = 3 * 1024 * 1024;

    /// <summary>The local address of a Google Fonts stylesheet query (<c>family=Inter:wght@400;700&amp;display=swap</c>).</summary>
    public static string Css(string query) => "/fonts/css2?" + query.TrimStart('?');

    /// <summary>Rewrites links to Google's stylesheets in user-written code to the local ones and drops the preconnects to Google.</summary>
    public static string Rewrite(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.IndexOf("fonts.g", StringComparison.OrdinalIgnoreCase) < 0) return code ?? "";
        code = Preconnect.Replace(code, "");
        foreach (var scheme in new[] { "https:", "http:", "" })
        {
            code = code.Replace(scheme + "//fonts.googleapis.com/css2?", "/fonts/css2?", StringComparison.OrdinalIgnoreCase)
                       .Replace(scheme + "//fonts.googleapis.com/css?", "/fonts/css?", StringComparison.OrdinalIgnoreCase);
        }
        return code;
    }

    /// <summary>Maps <c>/fonts/css2</c>, <c>/fonts/css</c> (Google's older API) and <c>/fonts/f/{file}</c>. <paramref name="dataDir"/> is
    /// the application's persisted data folder — the fonts are fetched once per installation, not once per container start.</summary>
    public static IEndpointRouteBuilder MapLocalFonts(this IEndpointRouteBuilder app, string dataDir)
    {
        var root = Path.Combine(dataDir, "fonts");
        // Two parameters on purpose: a lambda taking ONLY the HttpContext and returning a Task binds to the RequestDelegate overload, which
        // drops the IResult — the stylesheet went out as an empty 200.
        app.MapGet("/fonts/css2", (HttpContext ctx, CancellationToken _) => ServeCssAsync(ctx, root, "/css2"));
        app.MapGet("/fonts/css", (HttpContext ctx, CancellationToken _) => ServeCssAsync(ctx, root, "/css"));
        app.MapGet("/fonts/f/{name}", (HttpContext ctx, string name) =>
        {
            var path = Path.Combine(root, "files", name);
            if (!FileName.IsMatch(name) || !File.Exists(path)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            var type = name.EndsWith(".woff2") ? "font/woff2" : name.EndsWith(".woff") ? "font/woff" : name.EndsWith(".otf") ? "font/otf" : "font/ttf";
            return Results.File(path, type);
        });
        return app;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();

    private static async Task<IResult> ServeCssAsync(HttpContext ctx, string root, string api)
    {
        var query = ctx.Request.QueryString.Value ?? "";
        if (query.Length < 8 || query.Length > MaxQuery || !query.Contains("family=")) return Results.BadRequest();
        var key = Hash(api + query);
        var cssFile = Path.Combine(root, "css", key + ".css");
        if (!File.Exists(cssFile))
        {
            var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ctx.RequestAborted);
            try
            {
                if (!File.Exists(cssFile) && !await FetchAsync(ctx, root, api + query, cssFile))
                {
                    // Google unreachable: no fonts this time, the system fonts take over — and nothing is cached, so the next page asks again.
                    ctx.Response.Headers.CacheControl = "no-store";
                    return Results.Text("/* fonts not available yet */", "text/css");
                }
            }
            finally { gate.Release(); }
        }
        ctx.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(cssFile, "text/css; charset=utf-8");
    }

    private static async Task<bool> FetchAsync(HttpContext ctx, string root, string pathAndQuery, string cssFile)
    {
        try
        {
            var http = ctx.RequestServices.GetService<IHttpClientFactory>()?.CreateClient() ?? new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, Google + pathAndQuery);
            req.Headers.TryAddWithoutValidation("User-Agent", Agent);
            using var res = await http.SendAsync(req, ctx.RequestAborted);
            if (!res.IsSuccessStatusCode) return false;
            var css = await res.Content.ReadAsStringAsync(ctx.RequestAborted);
            var files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(Path.GetDirectoryName(cssFile)!);
            var urls = FontUrl.Matches(css).Select(m => m.Groups[1].Value).Distinct().Take(MaxFiles).ToList();
            foreach (var url in urls)
            {
                var ext = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToLowerInvariant();
                if (ext is not ("woff2" or "woff" or "ttf" or "otf")) ext = "woff2";
                var name = Hash(url) + "." + ext;
                var target = Path.Combine(files, name);
                if (!File.Exists(target))
                {
                    using var fr = await http.GetAsync(url, ctx.RequestAborted);
                    if (!fr.IsSuccessStatusCode) return false;
                    var bytes = await fr.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                    if (bytes.Length > MaxFileBytes) return false;
                    var tmp = target + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes, ctx.RequestAborted);
                    File.Move(tmp, target, overwrite: true);
                }
                css = css.Replace(url, "/fonts/f/" + name);
            }
            // Anything still pointing at Google (an unexpected form) would make the browser call it: refuse rather than serve it.
            if (css.Contains("fonts.gstatic.com", StringComparison.OrdinalIgnoreCase)) return false;
            var tmpCss = cssFile + ".tmp";
            await File.WriteAllTextAsync(tmpCss, css, ctx.RequestAborted);
            File.Move(tmpCss, cssFile, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            return false;
        }
    }
}
