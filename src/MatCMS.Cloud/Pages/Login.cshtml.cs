using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCMS.Cloud.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly IDataProtectionProvider _dp;

    public LoginModel(AuthService auth, IDataProtectionProvider dp)
    {
        _auth = auth;
        _dp = dp;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet(string? returnUrl, string? twofa)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));
        ReturnUrl = returnUrl;
        if (twofa == "locked") Error = "Zu viele falsche Codes. Bitte melde dich erneut an.";
        if (twofa == "expired") Error = "Die Anmeldung ist abgelaufen. Bitte melde dich erneut an.";
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

        // Password is right. If this account has a second factor, do NOT sign in yet: park the pending
        // identity in a short-lived encrypted cookie and send the browser to the code challenge. This
        // also gates the OAuth authorize step, which trusts only the finished login cookie.
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
