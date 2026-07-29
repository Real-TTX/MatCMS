using Microsoft.AspNetCore.Localization;

namespace MatCMS.Services;

/// <summary>
/// Determines the UI culture per request so the two areas behave independently:
/// <list type="bullet">
///   <item>ADMIN (and /login, /logout): defaults to <b>English</b>; an explicit choice via the
///   language switch (the culture cookie) overrides it. So the back-office is English out of the box
///   but every admin can switch to any language that ships a <c>Resources/&lt;code&gt;.json</c>.</item>
///   <item>PUBLIC site: follows the <b>content locale taken from the URL</b> (the first path segment
///   when it's a routable culture, else the site's root language) — a German page keeps German chrome
///   regardless of any cookie, and vice-versa.</item>
/// </list>
/// Registered first in the request-culture provider chain; it always returns a result.
/// </summary>
public sealed class MatCmsCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value ?? "";
        var isAdmin = path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith("/logout", StringComparison.OrdinalIgnoreCase);

        string culture;
        if (isAdmin)
        {
            // Explicit admin choice (culture cookie, UI-culture part) wins; otherwise English.
            var pref = CookieUiCulture(httpContext.Request.Cookies[CookieRequestCultureProvider.DefaultCookieName]);
            culture = Localizer.IsSupported(pref) ? pref! : "en";
        }
        else
        {
            // Public: the content locale is the first URL segment when it's a routable culture,
            // otherwise the site's root language.
            var seg = path.Trim('/');
            var slash = seg.IndexOf('/');
            if (slash >= 0) seg = seg[..slash];
            seg = seg.ToLowerInvariant();
            culture = Localizer.IsSupported(seg) ? seg : Localizer.DefaultCulture;
        }

        return Task.FromResult<ProviderCultureResult?>(new ProviderCultureResult(culture, culture));
    }

    /// <summary>Extracts the UI-culture ("uic=") part from the ASP.NET culture cookie ("c=de|uic=de").</summary>
    private static string? CookieUiCulture(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        foreach (var part in raw.Split('|'))
            if (part.StartsWith("uic=", StringComparison.Ordinal))
                return part[4..];
        return null;
    }
}
