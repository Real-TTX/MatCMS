using MatCMS.Plugin;
using MatCMS.Shared.Web;
using Microsoft.AspNetCore.Mvc;

namespace MatCMS.Services.Plugins;

/// <summary>Turns a <see cref="PluginResult"/> into an HTTP answer. Everything except a laid-out page is
/// answered here; a page is drawn by the Razor page that called the handler, inside its own layout.</summary>
internal static class PluginResults
{
    /// <summary>A redirect target is only ever a local path: "/…", not "//…" (protocol-relative) and not
    /// "/\…" (which some browsers read as the same thing). Anything else becomes the home page.</summary>
    public static string SafeLocal(string? target)
    {
        var t = (target ?? "").Trim();
        return t.StartsWith('/') && !t.StartsWith("//") && !t.StartsWith("/\\") && Uri.IsWellFormedUriString(t, UriKind.Relative)
            ? t : "/";
    }

    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Set-Cookie", "Content-Length", "Transfer-Encoding", "Content-Encoding", "Connection", "Upgrade", "Host", "Location", "Content-Type"
    };

    /// <summary>
    /// Puts the cookies and headers a handler asked for on the response. A cookie gets the plugin's prefix on the wire and is always
    /// <c>HttpOnly</c>, <c>SameSite=Lax</c>, <c>Secure</c> on https, path "/": a plugin cannot loosen any of that, and cannot touch the site's login
    /// or another plugin's cookies. Headers that frame the body (or set cookies) are refused.
    /// </summary>
    public static void Apply(PluginResult r, HttpResponse response, string pluginKey, bool isHttps)
    {
        foreach (var c in r.Cookies)
        {
            var name = PluginRequestContext.CookiePrefix(pluginKey) + c.Name;
            if (c.Delete) { response.Cookies.Delete(name, new CookieOptions { Path = "/" }); continue; }
            response.Cookies.Append(name, c.Value, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = isHttps, Path = "/", IsEssential = true,
                MaxAge = c.MaxAge is { } age && age > TimeSpan.Zero ? age : null
            });
        }
        foreach (var h in r.Headers)
        {
            if (ForbiddenHeaders.Contains(h.Key) || HasLineBreak(h.Key) || HasLineBreak(h.Value)) continue;
            response.Headers[h.Key] = h.Value;
        }
    }

    // A header value with a line break would let a plugin (or a visitor's text a plugin passed on) add headers of its own.
    private static bool HasLineBreak(string s) => s.Any(ch => ch == (char)13 || ch == (char)10);

    /// <summary>The HTML of a page or fragment result: the component tree drawn by the library's renderer when the
    /// handler built one, otherwise the raw HTML it answered with.</summary>
    public static string Html(PluginResult r, IServiceProvider services, UiTarget target, string antiforgery = "")
    {
        if (r.Tree is null) return r.Content;
        var loc = services.GetService(typeof(Localizer)) as Localizer;
        return UiRenderer.Render(r.Tree, new UiRenderOptions
        {
            Target = target, Antiforgery = antiforgery,
            T = loc is null ? k => k : k => loc[k]
        });
    }

    /// <summary>The answer for every kind but <see cref="PluginResult.ResultKind.Page"/>; null for a page.
    /// <paramref name="target"/> is where a fragment of components is drawn (the back office or the site).</summary>
    public static IActionResult? ToActionResult(PluginResult r, IServiceProvider services, UiTarget target, string antiforgery = "")
    {
        switch (r.Kind)
        {
            case PluginResult.ResultKind.Page:
                return null;
            case PluginResult.ResultKind.Fragment:
                return new ContentResult { Content = Html(r, services, target, antiforgery), ContentType = "text/html; charset=utf-8", StatusCode = r.StatusCode };
            case PluginResult.ResultKind.Redirect:
                if (r.External)
                    return Uri.TryCreate(r.Target, UriKind.Absolute, out var away) && away.Scheme == Uri.UriSchemeHttps && away.UserInfo.Length == 0
                        ? new RedirectResult(away.AbsoluteUri) : new LocalRedirectResult("/");
                return new LocalRedirectResult(SafeLocal(r.Target), permanent: r.StatusCode == 301);
            case PluginResult.ResultKind.Json:
                return new ContentResult { Content = r.Body?.ToJsonString() ?? "null", ContentType = r.ContentType, StatusCode = r.StatusCode };
            case PluginResult.ResultKind.Text:
                return new ContentResult { Content = r.Content, ContentType = r.ContentType, StatusCode = r.StatusCode };
            case PluginResult.ResultKind.File:
                if (r.Stream is not null)
                    return new FileStreamResult(r.Stream, r.ContentType) { EnableRangeProcessing = true, FileDownloadName = string.IsNullOrEmpty(r.Target) ? null : r.Target };
                return string.IsNullOrEmpty(r.Target)
                    ? new FileContentResult(r.Bytes, r.ContentType)
                    : new FileContentResult(r.Bytes, r.ContentType) { FileDownloadName = r.Target };
            default:
                return new StatusCodeResult(r.StatusCode);
        }
    }
}
