using MatCMS.Data;
using MatCMS.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCMS.Pages;

/// <summary>
/// The back-office sign-in. Without MatIdentity it is the site's own form, as always. With <c>MATIDENTITY_AUTHORITY</c> a visitor goes
/// straight on to MatIdentity and comes back signed in; the site's own form stays at <c>/login?local=1</c> — the way in when MatIdentity
/// is down, and after <c>?signedOut=1</c> / <c>?ssoError=1</c> / <c>?denied=1</c>, so nobody is ever locked out by MatIdentity. When
/// MatIdentity's person matches an existing account by its address, the account is linked only after it signs in here with its own
/// password (and second factor): <c>?local=1&amp;link=1</c>, <see cref="MatIdentityPendingLink"/>.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly SiteContext _site;
    private readonly CloudService _cloud;
    private readonly IDataProtectionProvider _dp;
    private readonly MatPlatformSettings _platform;
    private readonly AppDbContext _db;
    private readonly Localizer _t;
    private readonly ILogger<LoginModel> _log;

    public LoginModel(AuthService auth, SiteContext site, CloudService cloud, IDataProtectionProvider dp, MatPlatformSettings platform,
        AppDbContext db, Localizer t, ILogger<LoginModel> log)
    {
        _auth = auth;
        _site = site;
        _cloud = cloud;
        _dp = dp;
        _platform = platform;
        _db = db;
        _t = t;
        _log = log;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    /// <summary>Show the "log in with cloud account" button only when SSO is switched on AND this
    /// instance is actually linked to a cloud.</summary>
    public bool SsoAvailable { get; private set; }

    /// <summary>The sign-in through MatIdentity is configured (<c>MATIDENTITY_AUTHORITY</c>).</summary>
    public bool IdentityEnabled => _platform.IdentityEnabled;

    /// <summary>A pending link of MatIdentity to an account of this address (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    /// <summary>The address that starts the sign-in at MatIdentity again.</summary>
    public string IdentitySignInUrl => string.IsNullOrEmpty(ReturnUrl) ? "/login" : "/login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? sso, string? twofa)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));
        ReturnUrl = returnUrl;
        Prepare();
        if (sso == "failed") Error = "Die Anmeldung mit dem Cloud-Konto ist fehlgeschlagen.";
        if (sso == "cancelled") Error = "Die Anmeldung mit dem Cloud-Konto wurde abgebrochen.";
        if (twofa == "locked") Error = "Zu viele falsche Codes. Bitte melde dich erneut an.";
        if (twofa == "expired") Error = "Die Anmeldung ist abgelaufen. Bitte melde dich erneut an.";
        if (Flag("ssoError")) Error = _t["login.identityFailed"];
        if (Flag("denied")) Error = _t["login.identityDenied"];
        var enabled = _site.Get(SettingKeys.SsoEnabled) is "1" or "true" or "on" or "yes";
        SsoAvailable = enabled && await _cloud.GetSsoClientAsync() is not null;

        // With MatIdentity: straight on to it, no local form in the way — unless the local form was asked for, or this is the answer
        // of a sign-in that just went wrong (a loop back to MatIdentity would hide what happened).
        if (IdentityEnabled && !Flag("local") && !Flag("signedOut") && !Flag("ssoError") && !Flag("denied") && !Flag("link")
            && twofa is null && sso is null)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) });
                return new EmptyResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                _log.LogWarning(ex, "MatIdentity is not reachable, showing the local sign-in");
                Error = _t["login.identityDown"];
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        ReturnUrl = returnUrl;
        Prepare();

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
        // real session cookie is only issued once /login/2fa succeeds. A pending link of MatIdentity stays
        // where it is: the code page takes it after the second factor.
        if (user.TwoFactorEnabled)
        {
            TwoFactorLoginCookie.Write(HttpContext, _dp,
                new TwoFactorLoginCookie.Pending(user.Id, RememberMe, SafeReturn(returnUrl)));
            return Redirect("/login/2fa");
        }

        var identity = await SiteIdentityUsers.TakeLinkAsync(HttpContext, _db, user, _log);
        await _auth.SignInAsync(HttpContext, user, RememberMe, identity: identity);
        return Redirect(SafeReturn(returnUrl));
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink(string? returnUrl)
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect(string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl) ? "/login?local=1" : "/login?local=1&returnUrl=" + Uri.EscapeDataString(returnUrl));
    }

    private void Prepare()
    {
        PendingLink = IdentityEnabled ? MatIdentityPendingLink.Peek(HttpContext) : null;
        LinkExpired = IdentityEnabled && PendingLink is null && Flag("link");
    }

    private bool Flag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";

    private string SafeReturn(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin";
}
