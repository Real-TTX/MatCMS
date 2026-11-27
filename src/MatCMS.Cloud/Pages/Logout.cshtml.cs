using MatCMS.Cloud.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages;

/// <summary>
/// Signs out. After a sign-in through MatIdentity the session there ends too (its end-session endpoint, then back to
/// <c>/signout-callback-oidc</c> and <c>/login?signedOut=1</c>); with MatIdentity configured a local session lands on <c>?signedOut=1</c>
/// as well, so the sign-in page does not sign in again at once.
/// </summary>
[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly AuthService _auth;
    private readonly MatPlatformSettings _platform;

    public LogoutModel(AuthService auth, MatPlatformSettings platform)
    {
        _auth = auth;
        _platform = platform;
    }

    // GET is accepted too so a bookmarked /logout works; both end at the login page.
    public Task<IActionResult> OnGetAsync() => SignOutAsync();

    public Task<IActionResult> OnPostAsync() => SignOutAsync();

    private async Task<IActionResult> SignOutAsync()
    {
        // Read before the local sign-out ends the session: only a sign-in through MatIdentity carries it.
        var idToken = _platform.IdentityEnabled ? AuthService.IdentityTokenOf(User) : null;
        await _auth.SignOutAsync(HttpContext);
        if (!string.IsNullOrEmpty(idToken))
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, idToken);
        return Redirect(_platform.IdentityEnabled ? "/login?signedOut=1" : "/login");
    }
}
