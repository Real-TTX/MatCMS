using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>Sets a new password through the link of a reset mail. The link works once.</summary>
public class AnmeldenZuruecksetzenModel : PageModel
{
    private readonly MemberService _members;
    private readonly Localizer T;
    public AnmeldenZuruecksetzenModel(MemberService members, Localizer t) { _members = members; T = t; }

    public string Token { get; private set; } = "";
    public bool TokenValid { get; private set; }
    public bool Done { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(string? token)
    {
        Token = token ?? "";
        TokenValid = await _members.FindByResetTokenAsync(token, HttpContext.RequestAborted) is not null;
    }

    public async Task<IActionResult> OnPostAsync(string? token, string? password)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        Token = token ?? "";
        TokenValid = await _members.FindByResetTokenAsync(token, HttpContext.RequestAborted) is not null;
        if (!TokenValid) return Page();
        if ((password ?? "").Length < MemberService.MinPasswordLength)
        {
            Error = T["member.err.passwordShort", MemberService.MinPasswordLength];
            return Page();
        }
        Done = await _members.ResetPasswordAsync(token, password!, HttpContext.RequestAborted);
        if (!Done) TokenValid = false;
        return Page();
    }
}
