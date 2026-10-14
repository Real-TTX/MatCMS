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
                return new LocalRedirectResult(SafeLocal(r.Target));
            case PluginResult.ResultKind.Json:
                return new ContentResult { Content = r.Body?.ToJsonString() ?? "null", ContentType = r.ContentType, StatusCode = r.StatusCode };
            case PluginResult.ResultKind.Text:
                return new ContentResult { Content = r.Content, ContentType = r.ContentType, StatusCode = r.StatusCode };
            case PluginResult.ResultKind.File:
                return string.IsNullOrEmpty(r.Target)
                    ? new FileContentResult(r.Bytes, r.ContentType)
                    : new FileContentResult(r.Bytes, r.ContentType) { FileDownloadName = r.Target };
            default:
                return new StatusCodeResult(r.StatusCode);
        }
    }
}
