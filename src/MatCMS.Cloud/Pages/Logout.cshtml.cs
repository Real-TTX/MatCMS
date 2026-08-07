using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages;

[AllowAnonymous]
public class LogoutModel : PageModel
{
    private readonly AuthService _auth;

    public LogoutModel(AuthService auth) => _auth = auth;

    // GET is accepted too so a bookmarked /logout works; both end at the login page.
    public async Task<IActionResult> OnGetAsync()
    {
        await _auth.SignOutAsync(HttpContext);
        return RedirectToPage("/Login");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _auth.SignOutAsync(HttpContext);
        return RedirectToPage("/Login");
    }
}
