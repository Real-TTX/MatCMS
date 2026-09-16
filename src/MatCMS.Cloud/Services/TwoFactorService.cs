using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The cloud-side second factor: wraps the shared, dependency-free <see cref="TwoFactor"/> algorithm
/// with the two things it deliberately leaves out — storing the TOTP secret ENCRYPTED at rest
/// (DataProtection, like every other secret here) and persisting enrolment / recovery state on the
/// <see cref="User"/> row. Enrolment writes nothing until the user proves a code, so an abandoned
/// setup leaves no trace. Mirror of the CMS's own service.
/// </summary>
public class TwoFactorService
{
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;

    // The issuer shown in the authenticator app. A STATIC label, not the cloud name, so a rename does
    // not orphan already-enrolled entries — the secret is what matters, not the label.
    private const string Issuer = "MatCMS.Cloud";

    /// <summary>Wrong second-factor codes allowed per account before it is locked out for
    /// <see cref="LockoutWindow"/>. Server-side (memory cache keyed by user id), so — unlike a
    /// client-held cookie counter — replaying an old request cannot reset it.</summary>
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    // Remember a consumed TOTP step a little longer than the code's own ~90 s validity window.
    private static readonly TimeSpan ReplayMemory = TimeSpan.FromMinutes(2);

    public TwoFactorService(AppDbContext db, IDataProtectionProvider dp, IMemoryCache cache)
    {
        _db = db;
        _protector = dp.CreateProtector("MatCMS.Cloud.TwoFactor.Secret.v1");
        _cache = cache;
    }

    // --- Enrolment ------------------------------------------------------------

    /// <summary>A fresh Base32 secret for a not-yet-saved enrolment.</summary>
    public string NewSecret() => TwoFactor.GenerateSecret();

    /// <summary>The <c>otpauth://</c> URI (rendered as a QR) for a pending secret and this user.</summary>
    public string OtpauthUri(User user, string secret)
    {
        var account = string.IsNullOrWhiteSpace(user.Email) ? user.Username : user.Email!;
        return TwoFactor.BuildOtpauthUri(Issuer, account, secret);
    }

    /// <summary>Verifies the first code against the pending secret and, on success, turns 2FA on and
    /// returns the PLAINTEXT recovery codes to show once. Returns null on a wrong code (nothing saved).</summary>
    public async Task<IReadOnlyList<string>?> ConfirmAsync(User user, string pendingSecret, string code)
    {
        if (!TwoFactor.VerifyTotp(pendingSecret, code)) return null;
        var recovery = TwoFactor.GenerateRecoveryCodes();
        user.TotpSecret = _protector.Protect(pendingSecret);
        user.RecoveryCodes = string.Join('\n', recovery.Select(TwoFactor.HashRecoveryCode));
        user.TwoFactorEnabled = true;
        await _db.SaveChangesAsync();
        return recovery;
    }

    // --- Login verification ---------------------------------------------------

    /// <summary>Second-factor check at login: a valid TOTP code (rejected if already consumed), or an
    /// unused recovery code (which is then consumed). A TOTP secret that cannot be decrypted (lost keys)
    /// only disables the TOTP branch — recovery codes are key-independent hashes and must still work, or
    /// a legitimate admin is hard-locked out.</summary>
    public async Task<bool> VerifySecondFactorAsync(User user, string code)
    {
        if (!user.TwoFactorEnabled || string.IsNullOrWhiteSpace(user.TotpSecret)) return false;

        string? secret = null;
        try { secret = _protector.Unprotect(user.TotpSecret); }
        catch { /* keys lost/rotated → TOTP uncheckable; fall through to recovery codes */ }

        if (secret is not null
            && TwoFactor.TryVerifyTotp(secret, code, out var step)
            && step > LastConsumedStep(user.Id))
        {
            // One-time use (RFC 6238 §5.2): remember the step so the same code can't be replayed while
            // it is still inside its ~90 s validity window.
            _cache.Set(StepKey(user.Id), step, ReplayMemory);
            return true;
        }

        return await TryConsumeRecoveryCodeAsync(user, code);
    }

    private long LastConsumedStep(int userId) => _cache.Get<long?>(StepKey(userId)) ?? 0;
    private static string StepKey(int userId) => $"2fa-step:{userId}";

    // --- Server-side failed-attempt lockout -----------------------------------
    // Keyed by user id in the memory cache, so it survives a replayed pending cookie (which a client
    // controls) — unlike a counter carried in that cookie.

    private static string FailKey(int userId) => $"2fa-fail:{userId}";

    /// <summary>True once too many wrong codes have been tried for this account recently.</summary>
    public bool IsLockedOut(int userId) => (_cache.Get<int?>(FailKey(userId)) ?? 0) >= MaxFailures;

    /// <summary>Records one wrong second-factor attempt (sliding <see cref="LockoutWindow"/>).</summary>
    public void RegisterFailure(int userId) =>
        _cache.Set(FailKey(userId), (_cache.Get<int?>(FailKey(userId)) ?? 0) + 1, LockoutWindow);

    /// <summary>Clears the failure count after a successful sign-in.</summary>
    public void ClearFailures(int userId) => _cache.Remove(FailKey(userId));

    private async Task<bool> TryConsumeRecoveryCodeAsync(User user, string code)
    {
        if (TwoFactor.NormalizeRecoveryCode(code).Length == 0) return false;
        var wanted = TwoFactor.HashRecoveryCode(code);
        var remaining = (user.RecoveryCodes ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var idx = remaining.IndexOf(wanted);
        if (idx < 0) return false;
        remaining.RemoveAt(idx);
        user.RecoveryCodes = remaining.Count == 0 ? null : string.Join('\n', remaining);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another login consumed a recovery code on this row concurrently (RecoveryCodes is an
            // optimistic-concurrency token). Fail this attempt closed rather than double-spend a code.
            return false;
        }
        return true;
    }

    // --- Management -----------------------------------------------------------

    /// <summary>Issues a fresh set of recovery codes (invalidating the old ones) and returns them once.</summary>
    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(User user)
    {
        var recovery = TwoFactor.GenerateRecoveryCodes();
        user.RecoveryCodes = string.Join('\n', recovery.Select(TwoFactor.HashRecoveryCode));
        await _db.SaveChangesAsync();
        return recovery;
    }

    /// <summary>Turns 2FA off and clears the secret + recovery codes. Used both by the user (self) and
    /// by an admin resetting a colleague who lost their device.</summary>
    public async Task DisableAsync(User user)
    {
        user.TwoFactorEnabled = false;
        user.TotpSecret = null;
        user.RecoveryCodes = null;
        await _db.SaveChangesAsync();
    }

    /// <summary>How many single-use recovery codes are still unused.</summary>
    public static int RemainingRecoveryCodes(User user) =>
        (user.RecoveryCodes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}
