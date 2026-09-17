using MatCMS.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCMS.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly SiteContext _site;
    private readonly CloudService _cloud;
    private readonly IDataProtectionProvider _dp;

    public LoginModel(AuthService auth, SiteContext site, CloudService cloud, IDataProtectionProvider dp)
    {
        _auth = auth;
        _site = site;
        _cloud = cloud;
        _dp = dp;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    /// <summary>Show the "log in with cloud account" button only when SSO is switched on AND this
    /// instance is actually linked to a cloud.</summary>
    public bool SsoAvailable { get; private set; }

    /// <summary>When this instance is meant to run inside the cloud's iframe (embedAuth on), the SSO
    /// button must NOT break out to top level — the whole flow stays in the frame. Off = keep the
    /// top-level break-out, which is the robust default outside an embed.</summary>
    public bool EmbedAuth { get; private set; }

    public async Task<IActionResult> OnGet(string? returnUrl, string? sso, string? twofa)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));
        ReturnUrl = returnUrl;
        if (sso == "failed") Error = "Die Anmeldung mit dem Cloud-Konto ist fehlgeschlagen.";
        if (sso == "cancelled") Error = "Die Anmeldung mit dem Cloud-Konto wurde abgebrochen.";
        if (twofa == "locked") Error = "Zu viele falsche Codes. Bitte melde dich erneut an.";
        if (twofa == "expired") Error = "Die Anmeldung ist abgelaufen. Bitte melde dich erneut an.";
        var enabled = _site.Get(SettingKeys.SsoEnabled) is "1" or "true" or "on" or "yes";
        SsoAvailable = enabled && await _cloud.GetSsoClientAsync() is not null;
        EmbedAuth = _site.Get(SettingKeys.EmbedAuth).Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        ReturnUrl = returnUrl;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            Error = "Bitte E-Mail und Passwort eingeben.";
            return Page();
        }

        var user = await _auth.ValidateAsync(Username.Trim(), Password);
        if (user is null)
        {
            Error = "E-Mail oder Passwort ist falsch.";
            return Page();
        }

        // Password is right. If this account has a second factor, do NOT sign in yet: stash the pending
        // identity in a short-lived encrypted cookie and send the browser to the code challenge. The
        // real session cookie is only issued once /login/2fa succeeds.
        if (user.TwoFactorEnabled)
        {
            TwoFactorLoginCookie.Write(HttpContext, _dp,
                new TwoFactorLoginCookie.Pending(user.Id, RememberMe, SafeReturn(returnUrl)));
            return Redirect("/login/2fa");
        }

        await _auth.SignInAsync(HttpContext, user, RememberMe);
        return Redirect(SafeReturn(returnUrl));
    }

    private string SafeReturn(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin";
}
