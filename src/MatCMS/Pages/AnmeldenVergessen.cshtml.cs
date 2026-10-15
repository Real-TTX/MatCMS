using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>Asks for a mailed link to choose a new password. The answer is the same whether the address has an account or not.</summary>
public class AnmeldenVergessenModel : PageModel
{
    private readonly MemberService _members;
    public AnmeldenVergessenModel(MemberService members) => _members = members;

    public bool Sent { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? email)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        await _members.RequestResetAsync(email ?? "", HttpContext.RequestAborted);
        Sent = true;
        return Page();
    }
}
