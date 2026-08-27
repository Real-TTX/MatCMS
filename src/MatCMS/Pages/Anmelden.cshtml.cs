using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// The public "guest area" login. A members-only page redirects unauthenticated visitors here with a
/// <c>returnUrl</c>; the same login form also lives as the <c>memberlogin</c> block so it can sit on a
/// landing page. Entirely separate from the admin <c>/login</c> — different scheme, different cookie.
/// </summary>
public class AnmeldenModel : PageModel
{
    private readonly MemberService _members;
    public AnmeldenModel(MemberService members) => _members = members;

    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }
    public bool LoggedIn { get; private set; }
    public string? MemberName { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, bool denied = false)
    {
        ReturnUrl = Local(returnUrl);
        var member = await MemberService.CurrentAsync(HttpContext);
        LoggedIn = member?.Identity?.IsAuthenticated == true;
        MemberName = member?.FindFirst("DisplayName")?.Value ?? member?.Identity?.Name;

        // A logged-in visitor who was bounced here only because their account lacks the page's role.
        if (denied && LoggedIn)
            Error = "Dieses Konto hat keinen Zugriff auf die angeforderte Seite.";
        // Already logged in and just visiting /anmelden directly → send them on.
        else if (LoggedIn && !denied)
            return Redirect(ReturnUrl ?? "/");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl, bool remember = false)
    {
        ReturnUrl = Local(returnUrl);
        var member = await _members.ValidateAsync(username ?? "", password ?? "");
        if (member is null)
        {
            Error = "Benutzername oder Passwort ist falsch.";
            MemberName = null;
            return Page();
        }

        await _members.SignInAsync(HttpContext, member, remember);
        return Redirect(ReturnUrl ?? "/");
    }

    public async Task<IActionResult> OnPostLogoutAsync(string? returnUrl)
    {
        await _members.SignOutAsync(HttpContext);
        return Redirect(Local(returnUrl) ?? "/");
    }

    /// <summary>Only ever return to a local path ("/…" but not "//…") — never an attacker-supplied
    /// absolute URL.</summary>
    private static string? Local(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//")
        && Uri.IsWellFormedUriString(url, UriKind.Relative) ? url : null;
}
