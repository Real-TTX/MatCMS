using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MatCMS.Services;

/// <summary>
/// Makes the cookie handler's redirects (to /login, /anmelden, access denied) RELATIVE. ASP.NET builds them absolute from
/// the request's Host — and behind the cloud's edge that Host is the node's host address, not the domain the browser asked
/// for: matcms.de/admin sent a signed-out visitor to matcms.cloud2.matthix.de/login, where the session then lived on the
/// wrong address. A relative Location is resolved by the browser against the address it actually used.
/// </summary>
public static class RelativeAuthRedirects
{
    public static void Apply(CookieAuthenticationOptions options)
    {
        options.Events.OnRedirectToLogin = ctx => Redirect(ctx, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = ctx => Redirect(ctx, StatusCodes.Status403Forbidden);
        options.Events.OnRedirectToLogout = ctx => Redirect(ctx, null);
        options.Events.OnRedirectToReturnUrl = ctx => Redirect(ctx, null);
    }

    /// <summary>The path + query of the handler's (possibly absolute) target; a script request keeps getting a status code as before.</summary>
    internal static string ToRelative(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.PathAndQuery
            : uri;

    private static Task Redirect(RedirectContext<CookieAuthenticationOptions> ctx, int? ajaxStatus)
    {
        if (ajaxStatus is { } status && IsAjax(ctx.Request))
        {
            ctx.Response.Headers.Location = ToRelative(ctx.RedirectUri);
            ctx.Response.StatusCode = status;
        }
        else ctx.Response.Redirect(ToRelative(ctx.RedirectUri));
        return Task.CompletedTask;
    }

    // Same test as the framework's own default handler.
    private static bool IsAjax(HttpRequest r) =>
        string.Equals(r.Query["X-Requested-With"], "XMLHttpRequest", StringComparison.Ordinal) ||
        string.Equals(r.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.Ordinal);
}
