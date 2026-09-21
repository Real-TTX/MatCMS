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
/// The Authorization Code consent screen for a registered connector client (a native "Sign in", e.g. a
/// ChatGPT custom GPT). The client sends the browser here; an Admin confirms, and a PKCE/secret-bound code
/// is issued to the client's registered redirect_uri, which the client exchanges at <c>/oauth/token</c>.
/// <para>Admin-only and ALWAYS an explicit click (no silent auto-approve like the instance-SSO page):
/// approving mints a full-access operator key, so it must be a deliberate Admin action. The whole
/// validation — response_type, known+unrevoked client, exact redirect_uri allowlist, S256 — is re-run on
/// GET and POST so a forged hidden field cannot grant a code.</para>
/// </summary>
[Authorize(Policy = "Admin")]
[EnableRateLimiting("login")]
public class OauthConnectModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly OperatorScope _scope;
    private readonly OAuthCodes _codes;
    private readonly OAuthClientService _clients;
    private readonly CloudContext _cloud;

    public OauthConnectModel(AppDbContext db, OperatorScope scope, OAuthCodes codes, OAuthClientService clients, CloudContext cloud)
    {
        _db = db;
        _scope = scope;
        _codes = codes;
        _clients = clients;
        _cloud = cloud;
    }

    [BindProperty(SupportsGet = true)] public string? client_id { get; set; }
    [BindProperty(SupportsGet = true)] public string? redirect_uri { get; set; }
    [BindProperty(SupportsGet = true)] public string? state { get; set; }
    [BindProperty(SupportsGet = true)] public string? code_challenge { get; set; }
    [BindProperty(SupportsGet = true)] public string? code_challenge_method { get; set; }
    [BindProperty(SupportsGet = true)] public string? response_type { get; set; }
    [BindProperty(SupportsGet = true)] public string? scope { get; set; }

    public string ClientName { get; private set; } = "";
    public string AccountLabel { get; private set; } = "";
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await GateEnrolmentAsync() is IActionResult gate) return gate;
        if (!_scope.IsAdmin) return Forbid();
        var (client, error) = await ResolveAsync();
        if (error is not null) { Error = error; return Page(); }

        ClientName = client!.Name;
        AccountLabel = User.FindFirst("DisplayName")?.Value ?? User.Identity?.Name ?? "";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? decision)
    {
        if (await GateEnrolmentAsync() is IActionResult gate) return gate;
        if (!_scope.IsAdmin) return Forbid();
        var (client, error) = await ResolveAsync();
        if (error is not null) { Error = error; return Page(); }

        if (decision != "allow")
            return Redirect(BuildRedirect(redirect_uri!, error: "access_denied"));

        var code = _codes.IssueConnector(new OAuthCodes.ConnectorGrant(
            _scope.UserId!.Value, client!.ClientId, redirect_uri!, string.IsNullOrEmpty(code_challenge) ? null : code_challenge));
        return Redirect(BuildRedirect(redirect_uri!, code: code));
    }

    private async Task<(OAuthClient? client, string? error)> ResolveAsync()
    {
        if (response_type != "code" || string.IsNullOrEmpty(client_id) || string.IsNullOrEmpty(redirect_uri))
            return (null, "invalid");
        if (!string.IsNullOrEmpty(code_challenge_method) && code_challenge_method != "S256")
            return (null, "invalid");

        var client = await _clients.FindByClientIdAsync(client_id);
        if (client is null || client.Revoked) return (null, "unknown");
        if (!OAuthClientService.RedirectAllowed(client, redirect_uri)) return (null, "redirect");
        return (client, null);
    }

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

    private string BuildRedirect(string uri, string? code = null, string? error = null)
    {
        var sep = uri.Contains('?') ? "&" : "?";
        var qs = code is not null ? $"code={Uri.EscapeDataString(code)}" : $"error={Uri.EscapeDataString(error!)}";
        if (!string.IsNullOrEmpty(state)) qs += $"&state={Uri.EscapeDataString(state)}";
        return uri + sep + qs;
    }
}
