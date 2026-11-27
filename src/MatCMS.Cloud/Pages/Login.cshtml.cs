using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCMS.Cloud.Pages;

/// <summary>
/// The sign-in. With MatIdentity configured (MATIDENTITY_AUTHORITY) a visitor goes straight on to MatIdentity and comes back signed in; the
/// cloud's own form stays at <c>/login?local=1</c> (a way in when MatIdentity is down), and after <c>?signedOut=1</c> / <c>?ssoError=1</c>.
/// When MatIdentity's person matches an existing account by its address, the account is linked only after it signs in here with its own
/// password (and second factor): <c>?local=1&amp;link=1</c>, <see cref="MatIdentityPendingLink"/>.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly IDataProtectionProvider _dp;
    private readonly MatPlatformSettings _platform;
    private readonly AppDbContext _db;
    private readonly ILogger<LoginModel> _log;

    public LoginModel(AuthService auth, IDataProtectionProvider dp, MatPlatformSettings platform, AppDbContext db, ILogger<LoginModel> log)
    {
        _auth = auth;
        _dp = dp;
        _platform = platform;
        _db = db;
        _log = log;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    /// <summary>The sign-in through MatIdentity is configured.</summary>
    public bool IdentityEnabled => _platform.IdentityEnabled;

    /// <summary>A pending link of MatIdentity to an account of this address (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    /// <summary>The address that starts the sign-in at MatIdentity again.</summary>
    public string IdentitySignInUrl => string.IsNullOrEmpty(ReturnUrl) ? "/login" : "/login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? twofa)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));
        ReturnUrl = returnUrl;
        Prepare();
        if (twofa == "locked") Error = "Zu viele falsche Codes. Bitte melde dich erneut an.";
        if (twofa == "expired") Error = "Die Anmeldung ist abgelaufen. Bitte melde dich erneut an.";
        if (Flag("ssoError")) Error = "Die Anmeldung über MatIdentity ist fehlgeschlagen. Du kannst dich hier mit deinem Cloud-Konto anmelden.";
        if (Flag("denied")) Error = "MatIdentity hat dich angemeldet, aber die Cloud hat das Konto abgelehnt.";

        // Auto login: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Flag("local") && !Flag("signedOut") && !Flag("ssoError") && !Flag("denied") && twofa is null)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) });
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                _log.LogWarning(ex, "MatIdentity is not reachable, showing the local sign-in");
                Error = "MatIdentity ist gerade nicht erreichbar. Du kannst dich hier mit deinem Cloud-Konto anmelden.";
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

        // Password is right. If this account has a second factor, do NOT sign in yet: park the pending
        // identity in a short-lived encrypted cookie and send the browser to the code challenge. This
        // also gates the OAuth authorize step, which trusts only the finished login cookie. A pending link of
        // MatIdentity stays where it is: the code page takes it after the second factor.
        if (user.TwoFactorEnabled)
        {
            TwoFactorLoginCookie.Write(HttpContext, _dp,
                new TwoFactorLoginCookie.Pending(user.Id, RememberMe, SafeReturn(returnUrl)));
            return Redirect("/login/2fa");
        }

        var identity = await TakeLinkAsync(HttpContext, _db, user, _log);
        await _auth.SignInAsync(HttpContext, user, RememberMe, identity);
        return Redirect(SafeReturn(returnUrl));
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink(string? returnUrl)
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect(string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl) ? "/login?local=1" : "/login?local=1&returnUrl=" + Uri.EscapeDataString(returnUrl));
    }

    /// <summary>
    /// After the account's own password (and second factor): a pending link of MatIdentity is taken only for exactly this account (and deleted
    /// either way), the account gets the person's subject, and the session keeps MatIdentity's session. Null when there is nothing to link.
    /// </summary>
    public static async Task<AuthService.IdentitySession?> TakeLinkAsync(HttpContext http, AppDbContext db, User user, ILogger log)
    {
        if (MatIdentityPendingLink.TakeFor(http, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) is not { } pending) return null;
        var linked = await IdentityAccounts.LinkAsync(db, user,
            new IdentityAccounts.Person(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName, pending.Role), http.RequestAborted);
        if (linked is null)
        {
            log.LogWarning("User {UserId} signed in, but was not linked with MatIdentity: the person belongs to another account", user.Id);
            return null;
        }

        log.LogInformation("User {UserId} linked with MatIdentity", user.Id);
        return new AuthService.IdentitySession(pending.Subject, pending.SessionId, pending.IdToken);
    }

    private void Prepare()
    {
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && Flag("link");
    }

    private bool Flag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";

    private string SafeReturn(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin";
}
