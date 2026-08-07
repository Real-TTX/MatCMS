using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages;

[AllowAnonymous]
public class ErrorModel : PageModel
{
    public void OnGet() { }
}
