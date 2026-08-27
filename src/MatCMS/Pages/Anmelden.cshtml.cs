using System.Net;
using MatCMS.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// The public "guest area" login. A members-only page redirects unauthenticated visitors here with a
/// <c>returnUrl</c>; the same login form also lives as the <c>memberlogin</c> block so it can sit on a
/// landing page. Entirely separate from the admin <c>/login</c> — different scheme, different cookie.
/// <para>When the active template carries a <see cref="Models.Template.LoginHtml"/>, this page renders
/// THAT as a full standalone page (no site header/nav), so the login can look like a bespoke landing.</para>
/// </summary>
public class AnmeldenModel : PageModel
{
    private readonly MemberService _members;
    private readonly SiteContext _site;
    public AnmeldenModel(MemberService members, SiteContext site) { _members = members; _site = site; }

    public string? Error { get; private set; }
    public string? ReturnUrl { get; private set; }
    public bool LoggedIn { get; private set; }
    public string? MemberName { get; private set; }

    // --- Custom (template-provided) login page ---
    /// <summary>Non-null when the active template defines a custom login page: the HTML before and
    /// after the {{login_form}} token, plus the template's CSS/JS to inject.</summary>
    public bool HasCustom { get; private set; }
    public string CustomBefore { get; private set; } = "";
    public string CustomAfter { get; private set; } = "";
    public string CustomCss { get; private set; } = "";
    public string CustomJs { get; private set; } = "";
    public string SiteName => _site.SiteName;

    private void ResolveTemplate()
    {
        var tpl = _site.ActiveTemplate;
        if (tpl is null || string.IsNullOrWhiteSpace(tpl.LoginHtml)) return;

        var errorHtml = string.IsNullOrEmpty(Error) ? "" : $"<div class=\"login-error\">{WebUtility.HtmlEncode(Error)}</div>";
        var raw = tpl.LoginHtml
            .Replace("{{error}}", errorHtml)
            .Replace("{{site_name}}", WebUtility.HtmlEncode(_site.SiteName))
            .Replace("{{year}}", DateTime.Now.Year.ToString());
        // Attached files: {{asset:name}} -> /template-assets/{id}/name (same as on the public pages).
        raw = MatCMS.Content.TemplateAssets.Resolve(raw, tpl.Id) ?? raw;

        const string token = "{{login_form}}";
        var idx = raw.IndexOf(token, StringComparison.Ordinal);
        if (idx >= 0) { CustomBefore = raw[..idx]; CustomAfter = raw[(idx + token.Length)..]; }
        else { CustomBefore = raw; CustomAfter = ""; }   // no token: form is appended after
        CustomCss = MatCMS.Content.TemplateAssets.Resolve(tpl.CustomCss, tpl.Id) ?? "";
        CustomJs = MatCMS.Content.TemplateAssets.Resolve(tpl.CustomJs, tpl.Id) ?? "";
        HasCustom = true;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, bool denied = false)
    {
        ReturnUrl = Local(returnUrl);
        var member = await MemberService.CurrentAsync(HttpContext);
        LoggedIn = member?.Identity?.IsAuthenticated == true;
        MemberName = member?.FindFirst("DisplayName")?.Value ?? member?.Identity?.Name;

        if (denied && LoggedIn)
            Error = "Dieses Konto hat keinen Zugriff auf die angeforderte Seite.";
        else if (LoggedIn && !denied)
            return Redirect(ReturnUrl ?? "/");

        ResolveTemplate();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? username, string? password, string? returnUrl, bool remember = false)
    {
        ReturnUrl = Local(returnUrl);
        var member = await _members.ValidateAsync(username ?? "", password ?? "");
        if (member is null)
        {
            Error = "Benutzername oder Passwort ist falsch.";
            MemberName = null;
            ResolveTemplate();
            return Page();
        }

        await _members.SignInAsync(HttpContext, member, remember);
        return Redirect(ReturnUrl ?? "/");
    }

    public async Task<IActionResult> OnPostLogoutAsync(string? returnUrl)
    {
        await _members.SignOutAsync(HttpContext);
        return Redirect(Local(returnUrl) ?? "/");
    }

    private static string? Local(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//")
        && Uri.IsWellFormedUriString(url, UriKind.Relative) ? url : null;
}
