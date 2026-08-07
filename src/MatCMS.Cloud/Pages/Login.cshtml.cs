using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCMS.Cloud.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel : PageModel
{
    private readonly AuthService _auth;

    public LoginModel(AuthService auth) => _auth = auth;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public bool RememberMe { get; set; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));
        ReturnUrl = returnUrl;
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
