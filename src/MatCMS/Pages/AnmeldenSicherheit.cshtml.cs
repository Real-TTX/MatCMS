using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Pages;

/// <summary>
/// A signed-in member's own second factor (<c>/anmelden/sicherheit</c>): set it up (QR + first code → recovery codes shown once), make new recovery
/// codes, switch it off — the last two only with a current code, so a session left open on a shared computer cannot quietly remove it. Plugins
/// link here with a local <c>returnUrl</c> (a club's profile page) and <c>pflicht=1</c> when their area requires it.
/// <para>The secret being set up travels in a DataProtection-encrypted hidden field bound to the member's id, never in the clear and never stored
/// before the first good code.</para>
/// </summary>
public class AnmeldenSicherheitModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly MemberTwoFactorService _twoFactor;
    private readonly ITimeLimitedDataProtector _pendingProtector;
    private readonly SiteContext _site;
    private readonly Localizer T;
    public AnmeldenSicherheitModel(AppDbContext db, MemberTwoFactorService twoFactor, IDataProtectionProvider dp, SiteContext site, Localizer t)
    {
        _db = db; _twoFactor = twoFactor; _site = site; T = t;
        _pendingProtector = dp.CreateProtector("MatCMS.MemberTwoFactor.Pending.v1").ToTimeLimitedDataProtector();
    }

    public SiteMember Member { get; private set; } = null!;
    public string ReturnUrl { get; private set; } = "";
    public bool Required { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    /// <summary>Set up: the encrypted pending secret, the readable secret (to type in by hand) and the QR address.</summary>
    public string PendingToken { get; private set; } = "";
    public string Secret { get; private set; } = "";
    public string OtpauthUri { get; private set; } = "";
    /// <summary>Recovery codes to show exactly once (after setting up or renewing).</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }
    public int RemainingCodes => MemberTwoFactorService.RemainingRecoveryCodes(Member);

    private async Task<SiteMember?> LoadAsync(string? returnUrl, string? pflicht)
    {
        ReturnUrl = Local(returnUrl);
        Required = pflicht is "1" or "true";
        var principal = await MemberService.CurrentAsync(HttpContext);
        if (!int.TryParse(principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)) return null;
        var m = await _db.SiteMembers.FindAsync(id);
        if (m is null || !m.IsActive) return null;
        Member = m;
        return m;
    }

    private IActionResult ToSignIn() => Redirect("/anmelden?returnUrl=" + Uri.EscapeDataString(Request.Path + Request.QueryString));

    private void StartSetup(string? keepSecret = null)
    {
        Secret = keepSecret ?? _twoFactor.NewSecret();
        PendingToken = _pendingProtector.Protect(Member.Id + ":" + Secret, TimeSpan.FromMinutes(20));
        OtpauthUri = MemberTwoFactorService.OtpauthUri(_site.SiteName, Member, Secret);
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? pflicht = null)
    {
        if (await LoadAsync(returnUrl, pflicht) is null) return ToSignIn();
        if (!Member.TwoFactorEnabled) StartSetup();
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(string? returnUrl, string? pflicht, string? pending, string? code)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        if (await LoadAsync(returnUrl, pflicht) is null) return ToSignIn();
        if (Member.TwoFactorEnabled) return Page();
        string? secret = null;
        try
        {
            var raw = _pendingProtector.Unprotect(pending ?? "");
            var cut = raw.IndexOf(':');
            if (cut > 0 && raw[..cut] == Member.Id.ToString()) secret = raw[(cut + 1)..];
        }
        catch { /* expired or tampered: start again */ }
        if (secret is null) { Error = T["member.2fa.setupExpired"]; StartSetup(); return Page(); }
        var codes = await _twoFactor.ConfirmAsync(Member, secret, (code ?? "").Trim());
        if (codes is null) { Error = T["member.2fa.wrong"]; StartSetup(secret); return Page(); }
        RecoveryCodes = codes;
        Notice = T["member.2fa.enabled"];
        return Page();
    }

    public async Task<IActionResult> OnPostRenewAsync(string? returnUrl, string? pflicht, string? code)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        if (await LoadAsync(returnUrl, pflicht) is null) return ToSignIn();
        if (!Member.TwoFactorEnabled) return RedirectToPage(new { returnUrl = ReturnUrl, pflicht = Required ? "1" : null });
        if (!await CheckCodeAsync(code)) return Page();
        RecoveryCodes = await _twoFactor.RegenerateRecoveryCodesAsync(Member);
        Notice = T["member.2fa.renewed"];
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(string? returnUrl, string? pflicht, string? code)
    {
        if (!MemberAuthLimiter.Allow(HttpContext)) return StatusCode(StatusCodes.Status429TooManyRequests);
        if (await LoadAsync(returnUrl, pflicht) is null) return ToSignIn();
        if (!Member.TwoFactorEnabled) return RedirectToPage(new { returnUrl = ReturnUrl, pflicht = Required ? "1" : null });
        if (!await CheckCodeAsync(code)) return Page();
        await _twoFactor.DisableAsync(Member);
        Notice = T["member.2fa.disabled"];
        StartSetup();
        return Page();
    }

    private async Task<bool> CheckCodeAsync(string? code)
    {
        if (_twoFactor.IsLockedOut(Member.Id)) { Error = T["member.2fa.locked"]; return false; }
        if (await _twoFactor.VerifyAsync(Member, (code ?? "").Trim())) { _twoFactor.ClearFailures(Member.Id); return true; }
        _twoFactor.RegisterFailure(Member.Id);
        Error = T["member.2fa.wrong"];
        return false;
    }

    private static string Local(string? url) =>
        url is { Length: > 0 } u && u.StartsWith('/') && !u.StartsWith("//", StringComparison.Ordinal) && !u.StartsWith("/\\", StringComparison.Ordinal) ? u : "";
}
