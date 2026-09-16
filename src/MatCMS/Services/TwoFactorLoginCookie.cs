using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MatCMS.Services;

/// <summary>
/// The short-lived "password OK, second factor still pending" state between <c>/login</c> and
/// <c>/login/2fa</c>. There is NO half-authenticated auth cookie in this app (SignInAsync issues the
/// full session in one step), so the pending identity is carried in its own DataProtection-encrypted
/// cookie instead — exactly the pattern the SSO flow already uses (<c>matcms.ssoflow</c>). It never
/// satisfies any authorization policy, so it cannot stand in for a real login.
/// <para>Scoped to <c>Path=/login</c> (the challenge lives at <c>/login/2fa</c>) and only five minutes
/// long. In the cloud iframe the runtime <c>CrossSite</c> cookie rewrite in Program.cs upgrades it to
/// <c>SameSite=None; Secure</c> alongside the auth cookie, or the challenge POST would appear to do
/// nothing there.</para>
/// </summary>
public static class TwoFactorLoginCookie
{
    public const string Name = "matcms.2fa";
    private const string Purpose = "MatCMS.TwoFactorLogin.v1";
    private const string CookiePath = "/login";

    // NOTE: the wrong-code counter is deliberately NOT here. A count in this client-held cookie can be
    // reset by replaying an older copy, so it is enforced server-side instead (TwoFactorService keyed by
    // user id). This cookie only carries WHO is mid-login and where to go afterwards.
    public sealed record Pending(int UserId, bool Remember, string ReturnUrl);

    public static void Write(HttpContext http, IDataProtectionProvider dp, Pending pending)
    {
        var json = JsonSerializer.Serialize(pending);
        http.Response.Cookies.Append(Name, dp.CreateProtector(Purpose).Protect(json), new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(5),
            Path = CookiePath,
        });
    }

    public static Pending? Read(HttpContext http, IDataProtectionProvider dp)
    {
        var raw = http.Request.Cookies[Name];
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonSerializer.Deserialize<Pending>(dp.CreateProtector(Purpose).Unprotect(raw)); }
        catch { return null; }
    }

    public static void Delete(HttpContext http) =>
        http.Response.Cookies.Delete(Name, new CookieOptions { Path = CookiePath });
}
