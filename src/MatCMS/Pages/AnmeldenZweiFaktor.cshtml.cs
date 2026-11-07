using MatCMS.Data;
using MatCMS.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// The second step of a member's sign-in (<c>/anmelden/2fa</c>): the password was right (<see cref="MemberService.SignInOrChallengeAsync"/> left the
/// pending cookie), now the code from the authenticator app — or a recovery code. Five wrong codes lock the account's second step for 15 minutes,
/// counted on the server (a replayed cookie cannot reset it).
/// </summary>
public class AnmeldenZweiFaktorModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MemberService _members;
    private readonly MemberTwoFactorService _twoFactor;
    private readonly IDataProtectionProvider _dp;
    private readonly Localizer T;
    public AnmeldenZweiFaktorModel(AppDbContext db, MemberService members, MemberTwoFactorService twoFactor, IDataProtectionProvider dp, Localizer t)
    { _db = db; _members = members; _twoFactor = twoFactor; _dp = dp; T = t; }

    public string? Error { get; private set; }

    public IActionResult OnGet() => MemberTwoFactorCookie.Read(HttpContext, _dp) is null ? Redirect("/anmelden") : Page();

    public async Task<IActionResult> OnPostAsync(string? code)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        var pending = MemberTwoFactorCookie.Read(HttpContext, _dp);
        if (pending is null) return Redirect("/anmelden");
        var m = await _db.SiteMembers.FindAsync(pending.MemberId);
        if (m is null || !m.IsActive || !m.TwoFactorEnabled) { MemberTwoFactorCookie.Delete(HttpContext); return Redirect("/anmelden"); }
        if (_twoFactor.IsLockedOut(m.Id)) { Error = T["member.2fa.locked"]; return Page(); }
        if (!await _twoFactor.VerifyAsync(m, (code ?? "").Trim()))
        {
            _twoFactor.RegisterFailure(m.Id);
            Error = _twoFactor.IsLockedOut(m.Id) ? T["member.2fa.locked"] : T["member.2fa.wrong"];
            return Page();
        }
        _twoFactor.ClearFailures(m.Id);
        MemberTwoFactorCookie.Delete(HttpContext);
        await _members.SignInAsync(HttpContext, m, pending.Remember);
        return Redirect(Local(pending.ReturnUrl));
    }

    private static string Local(string? url) =>
        url is { Length: > 0 } u && u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal) && !u.StartsWith("/\\", StringComparison.Ordinal) ? u : "/";
}
