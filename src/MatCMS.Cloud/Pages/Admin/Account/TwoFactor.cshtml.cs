using System.Security.Claims;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatCMS.Cloud.Pages.Admin.Account;

/// <summary>
/// Self-service two-factor setup for the LOGGED-IN cloud account (Admin or Operator alike): scan the
/// QR, confirm a code, keep the recovery codes. An admin can only <em>reset</em> another account
/// (Users/Edit), never enrol it — the secret is confirmed by the person holding the device.
/// </summary>
public class TwoFactorModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly TwoFactorService _twoFactor;

    public TwoFactorModel(AppDbContext db, TwoFactorService twoFactor)
    {
        _db = db;
        _twoFactor = twoFactor;
    }

    public bool Enrolled { get; private set; }
    public int RemainingCodes { get; private set; }
    public bool RequiredByPolicy { get; private set; }

    /// <summary>The pending Base32 secret during setup — carried in a hidden field across the confirm
    /// POST so nothing touches the DB until the code is proven. It is displayed anyway (QR + manual
    /// key), so a hidden copy is no extra exposure.</summary>
    [BindProperty] public string PendingSecret { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";

    public string OtpauthUri { get; private set; } = "";
    public string? Error { get; private set; }

    /// <summary>Fresh recovery codes to show exactly once (via TempData after enrol / regenerate).</summary>
    public IReadOnlyList<string> NewRecoveryCodes { get; private set; } = System.Array.Empty<string>();

    private async Task<User?> CurrentUserAsync()
    {
        var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(id, out var uid) ? await _db.Users.FindAsync(uid) : null;
    }

    public async Task<IActionResult> OnGetAsync(bool enrol = false)
    {
        var user = await CurrentUserAsync();
        if (user is null) return Redirect("/login");

        RequiredByPolicy = enrol;
        Enrolled = user.TwoFactorEnabled;
        RemainingCodes = TwoFactorService.RemainingRecoveryCodes(user);

        if (!Enrolled)
        {
            PendingSecret = _twoFactor.NewSecret();
            OtpauthUri = _twoFactor.OtpauthUri(user, PendingSecret);
        }

        if (TempData["RecoveryCodes"] is string codes && !string.IsNullOrEmpty(codes))
            NewRecoveryCodes = codes.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Redirect("/login");
        if (user.TwoFactorEnabled) return RedirectToPage();

        var recovery = await _twoFactor.ConfirmAsync(user, PendingSecret, Code);
        if (recovery is null)
        {
            Enrolled = false;
            OtpauthUri = _twoFactor.OtpauthUri(user, PendingSecret);
            Error = "Der Code ist falsch. Bitte prüfe die Uhrzeit deines Geräts und versuche es erneut.";
            return Page();
        }

        TempData["RecoveryCodes"] = string.Join('\n', recovery);
        TempData["Flash"] = "Zwei-Faktor-Authentifizierung ist aktiviert.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegenerateAsync()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Redirect("/login");
        if (!user.TwoFactorEnabled) return RedirectToPage();

        var recovery = await _twoFactor.RegenerateRecoveryCodesAsync(user);
        TempData["RecoveryCodes"] = string.Join('\n', recovery);
        TempData["Flash"] = "Neue Wiederherstellungscodes erzeugt. Die alten sind ungültig.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDisableAsync()
    {
        var user = await CurrentUserAsync();
        if (user is null) return Redirect("/login");

        await _twoFactor.DisableAsync(user);
        TempData["Flash"] = "Zwei-Faktor-Authentifizierung ist deaktiviert.";
        return RedirectToPage();
    }
}
