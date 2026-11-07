using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// The link of an invitation (<see cref="MemberService.InviteAsync"/>): the person sets a password once — that confirms the address — and is signed in
/// at once and led to where the inviter wanted (a local path, e.g. a club's /mitglied). One step, no "forgot password", no second mail.
/// </summary>
public class AnmeldenWillkommenModel : PageModel
{
    private readonly MemberService _members;
    private readonly Localizer T;
    public AnmeldenWillkommenModel(MemberService members, Localizer t) { _members = members; T = t; }

    public string Token { get; private set; } = "";
    public string ReturnUrl { get; private set; } = "/";
    public string Email { get; private set; } = "";
    public bool TokenValid { get; private set; }
    public string? Error { get; private set; }

    private static string Local(string? url) =>
        url is { Length: > 0 } u && u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal) && !u.StartsWith("/\\", StringComparison.Ordinal) ? u : "/";

    public async Task OnGetAsync(string? token, string? returnUrl)
    {
        Token = token ?? "";
        ReturnUrl = Local(returnUrl);
        var m = await _members.FindByResetTokenAsync(token, HttpContext.RequestAborted);
        TokenValid = m is not null;
        Email = m?.Email ?? "";
    }

    public async Task<IActionResult> OnPostAsync(string? token, string? returnUrl, string? password, string? repeat)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        Token = token ?? "";
        ReturnUrl = Local(returnUrl);
        var m = await _members.FindByResetTokenAsync(token, HttpContext.RequestAborted);
        TokenValid = m is not null;
        Email = m?.Email ?? "";
        if (m is null) return Page();
        if ((password ?? "").Length < MemberService.MinPasswordLength) { Error = T["member.err.passwordShort", MemberService.MinPasswordLength]; return Page(); }
        if (password != repeat) { Error = T["member.welcome.mismatch"]; return Page(); }
        if (!await _members.ResetPasswordAsync(token, password!, HttpContext.RequestAborted)) { TokenValid = false; return Page(); }
        // setting the password confirmed the address (ResetPasswordAsync); now the person is in — the reason for this page
        // (an account that already turned on a second factor still has to give its code)
        return Redirect(await _members.SignInOrChallengeAsync(HttpContext, m, persistent: false, ReturnUrl));
    }
}
