using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace MatCMS.Pages;

/// <summary>
/// The second step of a two-factor login: the password was already verified by <see cref="LoginModel"/>,
/// which parked the pending identity in the <see cref="TwoFactorLoginCookie"/> and redirected here.
/// This page turns a valid authenticator code (or a recovery code) into the real session. Anonymous
/// and rate-limited exactly like the password form.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("login")]
public class Login2faModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuthService _auth;
    private readonly TwoFactorService _twoFactor;
    private readonly IDataProtectionProvider _dp;

    public Login2faModel(AppDbContext db, AuthService auth, TwoFactorService twoFactor, IDataProtectionProvider dp)
    {
        _db = db;
        _auth = auth;
        _twoFactor = twoFactor;
        _dp = dp;
    }

    [BindProperty] public string Code { get; set; } = "";
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true) return Redirect("/admin");
        // No pending state → nobody got here through the password step; back to the start.
        if (TwoFactorLoginCookie.Read(HttpContext, _dp) is null) return Redirect("/login?twofa=expired");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var pending = TwoFactorLoginCookie.Read(HttpContext, _dp);
        if (pending is null) return Redirect("/login?twofa=expired");

        var user = await _db.Users.FindAsync(pending.UserId);
        if (user is null || !user.TwoFactorEnabled)
        {
            // The account was deleted or had 2FA turned off between the two steps — start over.
            TwoFactorLoginCookie.Delete(HttpContext);
            return Redirect("/login");
        }

        // Too many wrong codes for this account recently (server-side, so a replayed cookie can't reset
        // it) → back to the password step.
        if (_twoFactor.IsLockedOut(user.Id))
        {
            TwoFactorLoginCookie.Delete(HttpContext);
            return Redirect("/login?twofa=locked");
        }

        if (string.IsNullOrWhiteSpace(Code) || !await _twoFactor.VerifySecondFactorAsync(user, Code))
        {
            _twoFactor.RegisterFailure(user.Id);
            if (_twoFactor.IsLockedOut(user.Id))
            {
                TwoFactorLoginCookie.Delete(HttpContext);
                return Redirect("/login?twofa=locked");
            }
            Error = "Der Code ist falsch oder abgelaufen.";
            return Page();
        }

        _twoFactor.ClearFailures(user.Id);
        TwoFactorLoginCookie.Delete(HttpContext);
        await _auth.SignInAsync(HttpContext, user, pending.Remember);
        return Redirect(Url.IsLocalUrl(pending.ReturnUrl) ? pending.ReturnUrl : "/admin");
    }
}
