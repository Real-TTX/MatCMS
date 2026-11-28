using MatCMS.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

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

    public IActionResult OnGet() => Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        // Read before the local sign-out ends the session: only a sign-in through MatIdentity carries it. Such a session is ended at
        // MatIdentity too (its end-session endpoint), and the browser comes back to /login?signedOut=1 — the local form, not straight
        // back into MatIdentity. Every other session ends here as it always did.
        var idToken = _platform.IdentityEnabled ? AuthService.IdentityTokenOf(User) : null;
        await _auth.SignOutAsync(HttpContext);
        if (!string.IsNullOrEmpty(idToken))
            return MatIdentitySignIn.SignOutAtIdentity(HttpContext, idToken);
        return Redirect("/");
    }
}
