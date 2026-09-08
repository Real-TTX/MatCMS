using MatCMS.Services;
using Microsoft.AspNetCore.Authorization;
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

    public LoginModel(AuthService auth, SiteContext site, CloudService cloud)
    {
        _auth = auth;
        _site = site;
        _cloud = cloud;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    /// <summary>Show the "log in with cloud account" button only when SSO is switched on AND this
    /// instance is actually linked to a cloud.</summary>
    public bool SsoAvailable { get; private set; }

    public async Task<IActionResult> OnGet(string? returnUrl, string? sso)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));
        ReturnUrl = returnUrl;
        if (sso == "failed") Error = "Die Anmeldung mit dem Cloud-Konto ist fehlgeschlagen.";
        var enabled = _site.Get(SettingKeys.SsoEnabled) is "1" or "true" or "on" or "yes";
        SsoAvailable = enabled && await _cloud.GetSsoClientAsync() is not null;
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

        await _auth.SignInAsync(HttpContext, user, RememberMe);
        return Redirect(SafeReturn(returnUrl));
    }

    private string SafeReturn(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl! : "/admin";
}
