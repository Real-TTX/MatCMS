using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// A visitor creates their own guest-area account (address + password). Only when the operator switched <c>members.selfRegistration</c> on —
/// until then the page does not exist (404), so a site that never wanted public accounts has no registration form to find.
/// The new account is signed in at once; a mail asks to confirm the address.
/// </summary>
public class RegistrierenModel : PageModel
{
    private readonly MemberService _members;
    public RegistrierenModel(MemberService members) => _members = members;

    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }
    public string Name { get; private set; } = "";
    public string Email { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        if (!await _members.SelfRegistrationEnabledAsync()) return NotFound();
        ReturnUrl = Local(returnUrl);
        if (await MemberService.CurrentAsync(HttpContext) is { Identity.IsAuthenticated: true }) return Redirect(ReturnUrl ?? "/");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? name, string? email, string? password, string? returnUrl, string? website)
    {
        if (!await _members.SelfRegistrationEnabledAsync()) return NotFound();
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        ReturnUrl = Local(returnUrl);
        Name = (name ?? "").Trim(); Email = (email ?? "").Trim();
        // The hidden "website" field is for robots: a person never sees it. They get the form back as if nothing had happened.
        if (!string.IsNullOrEmpty(website)) return Page();

        var (status, member) = await _members.CreateAsync(Email, password ?? "", Name, HttpContext.RequestAborted);
        switch (status)
        {
            case MemberService.CreateStatus.InvalidEmail: Error = "Bitte geben Sie eine gültige E-Mail-Adresse an."; return Page();
            case MemberService.CreateStatus.WeakPassword: Error = $"Das Passwort muss mindestens {MemberService.MinPasswordLength} Zeichen haben."; return Page();
            case MemberService.CreateStatus.EmailTaken: Error = "Mit dieser E-Mail-Adresse gibt es schon ein Konto. Melden Sie sich an oder lassen Sie sich ein neues Passwort schicken."; return Page();
        }
        await _members.SignInAsync(HttpContext, member!, persistent: false);
        return Redirect(ReturnUrl ?? "/");
    }

    private static string? Local(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
        && Uri.IsWellFormedUriString(url, UriKind.Relative) ? url : null;
}
