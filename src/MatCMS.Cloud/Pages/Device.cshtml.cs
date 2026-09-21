using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages;

/// <summary>
/// The verification page of the OAuth 2.0 Device Authorization Grant (RFC 8628). A browserless client
/// (a CLI, an agent, or ChatGPT) directs the operator here with a short <c>user_code</c>; the operator —
/// already signed into the cloud — confirms it, which authorises the pending grant. The client, polling
/// <c>/oauth/token</c>, then receives a full-access operator API key.
/// <para>Modelled on <see cref="OauthAuthorizeModel"/>: <c>[Authorize]</c> does the login bounce, the same
/// 2FA mandate gate applies (this path is outside <c>/admin</c>), and it reuses the login-card view. Unlike
/// SSO there is NO silent auto-approve — granting a device full operator access is always an explicit click,
/// and the card states plainly what is being granted so an operator cannot be walked into approving a code
/// they did not start.</para>
/// </summary>
// Admin-only: authorising a device grant mints a FULL-ACCESS operator API key (AllInstances + CanRestore),
// which is an Admin capability — API-key creation is Admin-only (AuthorizeFolder "/Admin/ApiKeys"), and a
// scoped Operator is deliberately barred from fleet-wide/destructive reach. A bare [Authorize] would let
// any Operator escalate to a fleet-wide restore-capable key via this page.
[Authorize(Policy = "Admin")]
[EnableRateLimiting("login")]
public class DeviceModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly OperatorScope _scope;
    private readonly DeviceCodes _devices;
    private readonly CloudContext _cloud;

    public DeviceModel(AppDbContext db, OperatorScope scope, DeviceCodes devices, CloudContext cloud)
    {
        _db = db;
        _scope = scope;
        _devices = devices;
        _cloud = cloud;
    }

    /// <summary>The user code, prefilled from <c>?code=</c> (the verification_uri_complete a client shows).</summary>
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }

    public bool Found { get; private set; }        // a pending grant resolved → show the consent card
    public bool Unknown { get; private set; }      // a code was entered but is unknown/expired/decided
    public string? Result { get; private set; }    // "approved" | "denied" after the decision
    public string ClientLabel { get; private set; } = "";
    public string AccountLabel { get; private set; } = "";

    public async Task<IActionResult> OnGetAsync()
    {
        if (await GateEnrolmentAsync() is IActionResult gate) return gate;
        AccountLabel = User.FindFirst("DisplayName")?.Value ?? User.Identity?.Name ?? "";

        if (!string.IsNullOrWhiteSpace(Code))
        {
            var row = await _devices.FindPendingByUserCodeAsync(Code, HttpContext.RequestAborted);
            if (row is null) { Unknown = true; return Page(); }
            ClientLabel = row.ClientLabel;
            Found = true;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? userCode, string? decision)
    {
        if (await GateEnrolmentAsync() is IActionResult gate) return gate;
        // Defence in depth behind the [Authorize(Policy="Admin")] attribute: approving a grant mints an
        // Admin-level key, so refuse it server-side too even if the attribute is ever loosened.
        if (!_scope.IsAdmin) return Forbid();
        AccountLabel = User.FindFirst("DisplayName")?.Value ?? User.Identity?.Name ?? "";

        // Step 1 — the operator just entered a code (no decision yet): PRG to the consent card so a reload
        // does not re-post, and the resolved grant is shown for an explicit allow/deny.
        if (string.IsNullOrEmpty(decision))
            return RedirectToPage(new { code = userCode });

        // Step 2 — the decision. Re-resolve the code from scratch (a stale/forged hidden field cannot
        // approve a grant that is not pending) and act.
        var row = await _devices.FindPendingByUserCodeAsync(userCode, HttpContext.RequestAborted);
        if (row is null) { Unknown = true; Code = userCode; return Page(); }

        ClientLabel = row.ClientLabel;
        if (decision == "allow")
        {
            await _devices.ApproveAsync(row, _scope.UserId!.Value, HttpContext.RequestAborted);
            Result = "approved";
        }
        else
        {
            await _devices.DenyAsync(row, _scope.UserId!.Value, HttpContext.RequestAborted);
            Result = "denied";
        }
        return Page();
    }

    /// <summary>The same "2FA required" gate the /admin middleware and /oauth/authorize apply: an
    /// un-enrolled account must not authorise a device while the mandate is on. Redirect to enrolment or null.</summary>
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
}
