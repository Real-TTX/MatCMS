using MatCMS.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>The link of the confirmation mail. Opening it proves the address belongs to the account's owner.</summary>
public class AnmeldenBestaetigenModel : PageModel
{
    private readonly MemberService _members;
    public AnmeldenBestaetigenModel(MemberService members) => _members = members;

    public bool Confirmed { get; private set; }

    public async Task OnGetAsync(string? token) =>
        Confirmed = await _members.ConfirmAsync(token, HttpContext.RequestAborted) is not null;
}
