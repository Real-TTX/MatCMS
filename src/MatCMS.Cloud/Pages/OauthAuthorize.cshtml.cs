using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages;

/// <summary>
/// The SSO consent screen — the front channel of the cloud-as-IdP OAuth flow. A MatCMS instance sends
/// the browser here (<c>/oauth/authorize?…</c>); the cloud shows WHO is asking and only an explicit
/// "Zulassen" mints the authorization code, exactly like a Google consent prompt. It replaces the old
/// endpoint that issued the code automatically, so a live cloud session can never silently federate
/// into an instance the user did not intend.
/// <para>It is a Razor Page (not a minimal-API endpoint) on purpose: that gives the POST antiforgery,
/// the login-card styling and localization for free. [Authorize] sends an unauthenticated visitor to
/// the cloud login and back. The whole security validation (PKCE, instance Approved, redirect_uri match,
/// per-instance access, 2FA mandate) is re-run on POST, so a tampered hidden field cannot grant a code.</para>
/// </summary>
[Authorize]
[EnableRateLimiting("login")]
public class OauthAuthorizeModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly OperatorScope _scope;
    private readonly OAuthCodes _codes;
    private readonly CloudContext _cloud;

    public OauthAuthorizeModel(AppDbContext db, OperatorScope scope, OAuthCodes codes, CloudContext cloud)
    {
        _db = db;
        _scope = scope;
        _codes = codes;
        _cloud = cloud;
    }

    // OAuth request params: bound from the query on GET, carried as hidden fields into the POST.
    [BindProperty(SupportsGet = true)] public string? client_id { get; set; }
    [BindProperty(SupportsGet = true)] public string? redirect_uri { get; set; }
    [BindProperty(SupportsGet = true)] public string? state { get; set; }
    [BindProperty(SupportsGet = true)] public string? code_challenge { get; set; }
    [BindProperty(SupportsGet = true)] public string? code_challenge_method { get; set; }
    [BindProperty(SupportsGet = true)] public string? response_type { get; set; }

    public string InstanceName { get; private set; } = "";
    public string AccountLabel { get; private set; } = "";

    /// <summary>Error code for a malformed/unknown request ("invalid" | "unknown" | "redirect"), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The request is valid but this account is not allowed for the instance.</summary>
    public bool NoAccess { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await GateEnrolmentAsync() is IActionResult gate) return gate;
        var (inst, error) = await ResolveAsync();
        if (error is not null) { Error = error; return Page(); }
        if (inst is null) { NoAccess = true; return Page(); }

        InstanceName = inst.Name;
        AccountLabel = User.FindFirst("DisplayName")?.Value ?? User.Identity?.Name ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? decision)
    {
        if (await GateEnrolmentAsync() is IActionResult gate) return gate;
        var (inst, error) = await ResolveAsync();
        if (error is not null) { Error = error; return Page(); }
        if (inst is null) { NoAccess = true; return Page(); }

        // The user declined → hand the client a standard OAuth error at the (already validated) return
        // address; the instance shows an "abgebrochen" message.
        if (decision != "allow")
            return Redirect(BuildRedirect(redirect_uri!, error: "access_denied"));

        var code = _codes.Issue(new OAuthCodes.Grant(_scope.UserId!.Value, inst.PublicId, redirect_uri!, code_challenge!));
        return Redirect(BuildRedirect(redirect_uri!, code: code));
    }

    /// <summary>The same "2FA required" gate the /admin middleware applies, ported here because this
    /// path is outside /admin: an un-enrolled account must not federate into an instance while the
    /// mandate is on. Returns a redirect to enrolment, or null to continue.</summary>
    private async Task<IActionResult?> GateEnrolmentAsync()
    {
        if (_scope.UserId is int uid && _cloud.Flag(SettingKeys.Require2fa))
        {
            var enrolled = await _db.Users.AsNoTracking()
                .Where(u => u.Id == uid).Select(u => u.TwoFactorEnabled).FirstOrDefaultAsync();
            if (!enrolled) return Redirect("/admin/account/twofactor?enrol=true");
        }
        return null;
    }

    /// <summary>Validates the request and resolves the target instance. (inst, null) = the user may
    /// consent; (null, code) = a malformed/unknown request; (null, null) = valid but this account has no
    /// access. Re-run on POST so a forged hidden field cannot grant a code.</summary>
    private async Task<(Instance? inst, string? error)> ResolveAsync()
    {
        if (response_type != "code" || string.IsNullOrEmpty(code_challenge) || (code_challenge_method ?? "S256") != "S256"
            || string.IsNullOrEmpty(client_id) || string.IsNullOrEmpty(redirect_uri) || string.IsNullOrEmpty(state))
            return (null, "invalid");

        var inst = await _db.Instances.FirstOrDefaultAsync(i => i.PublicId == client_id);
        if (inst is null || inst.Status != InstanceStatus.Approved) return (null, "unknown");
        if (!SsoRedirectAllowed(inst, redirect_uri!)) return (null, "redirect");
        if (!await _scope.CanAccessInstanceAsync(inst.Id)) return (null, null);   // valid, but not allowed
        return (inst, null);
    }

    /// <summary>The redirect_uri must be the instance's own <c>/sso/callback</c> (same authority as its
    /// reported PreviewUrl) — the whole trust of the front channel rests on this check.</summary>
    private static bool SsoRedirectAllowed(Instance inst, string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var ru)) return false;
        if (ru.Scheme != Uri.UriSchemeHttps && ru.Scheme != Uri.UriSchemeHttp) return false;
        if (!ru.AbsolutePath.EndsWith("/sso/callback", StringComparison.OrdinalIgnoreCase)) return false;
        var known = inst.PreviewUrl;
        return !string.IsNullOrWhiteSpace(known) && Uri.TryCreate(known, UriKind.Absolute, out var ku)
            && string.Equals(ru.GetLeftPart(UriPartial.Authority), ku.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    private string BuildRedirect(string uri, string? code = null, string? error = null)
    {
        var sep = uri.Contains('?') ? "&" : "?";
        var qs = code is not null ? $"code={Uri.EscapeDataString(code)}" : $"error={Uri.EscapeDataString(error!)}";
        if (!string.IsNullOrEmpty(state)) qs += $"&state={Uri.EscapeDataString(state)}";
        return uri + sep + qs;
    }
}
