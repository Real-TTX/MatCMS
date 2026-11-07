using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Services;

/// <summary>
/// The second factor (TOTP) of the site's GUEST-AREA accounts (<see cref="SiteMember"/>) — the twin of <see cref="TwoFactorService"/> for the
/// back office, on the same shared algorithm (<see cref="TwoFactor"/>). Kept apart on purpose: another table, another encryption purpose and other
/// cache keys, so a member's code can never count for an administrator of the same number or the other way round.
/// <para>The secret is stored encrypted; recovery codes only as hashes. Enrolment writes nothing until the member proved a code. A secret that cannot
/// be decrypted (keys lost) only disables the TOTP branch — the recovery codes still work.</para>
/// </summary>
public class MemberTwoFactorService
{
    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IMemoryCache _cache;
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ReplayMemory = TimeSpan.FromMinutes(2);

    public MemberTwoFactorService(AppDbContext db, IDataProtectionProvider dp, IMemoryCache cache)
    {
        _db = db;
        _protector = dp.CreateProtector("MatCMS.TwoFactor.MemberSecret.v1");
        _cache = cache;
    }

    public string NewSecret() => TwoFactor.GenerateSecret();

    /// <summary>The <c>otpauth://</c> address for the QR. The issuer is the site's NAME, so a member with several sites tells them apart in the app.</summary>
    public static string OtpauthUri(string siteName, SiteMember m, string secret) =>
        TwoFactor.BuildOtpauthUri(string.IsNullOrWhiteSpace(siteName) ? "MatCMS" : siteName.Trim(), m.Email.Length > 0 ? m.Email : m.Username, secret);

    /// <summary>Turns the second factor on after the first good code; returns the recovery codes to show ONCE, or null on a wrong code (nothing saved).</summary>
    public async Task<IReadOnlyList<string>?> ConfirmAsync(SiteMember m, string pendingSecret, string code)
    {
        if (!TwoFactor.VerifyTotp(pendingSecret, code)) return null;
        var recovery = TwoFactor.GenerateRecoveryCodes();
        m.TotpSecret = _protector.Protect(pendingSecret);
        m.RecoveryCodes = string.Join('\n', recovery.Select(TwoFactor.HashRecoveryCode));
        m.TwoFactorEnabled = true;
        await _db.SaveChangesAsync();
        return recovery;
    }

    /// <summary>A code from the app (each 30-second step only once) or an unused recovery code (spent).</summary>
    public async Task<bool> VerifyAsync(SiteMember m, string code)
    {
        if (!m.TwoFactorEnabled || string.IsNullOrWhiteSpace(m.TotpSecret)) return false;
        string? secret = null;
        try { secret = _protector.Unprotect(m.TotpSecret); } catch { /* keys lost → recovery codes only */ }
        if (secret is not null && TwoFactor.TryVerifyTotp(secret, code, out var step) && step > (_cache.Get<long?>(StepKey(m.Id)) ?? 0))
        {
            _cache.Set(StepKey(m.Id), step, ReplayMemory);
            return true;
        }
        if (TwoFactor.NormalizeRecoveryCode(code).Length == 0) return false;
        var remaining = (m.RecoveryCodes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!remaining.Remove(TwoFactor.HashRecoveryCode(code))) return false;
        m.RecoveryCodes = remaining.Count == 0 ? null : string.Join('\n', remaining);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { return false; }          // spent by a concurrent sign-in: fail closed
        return true;
    }

    private static string StepKey(int id) => $"m2fa-step:{id}";
    private static string FailKey(int id) => $"m2fa-fail:{id}";
    public bool IsLockedOut(int id) => (_cache.Get<int?>(FailKey(id)) ?? 0) >= MaxFailures;
    public void RegisterFailure(int id) => _cache.Set(FailKey(id), (_cache.Get<int?>(FailKey(id)) ?? 0) + 1, LockoutWindow);
    public void ClearFailures(int id) => _cache.Remove(FailKey(id));

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(SiteMember m)
    {
        var recovery = TwoFactor.GenerateRecoveryCodes();
        m.RecoveryCodes = string.Join('\n', recovery.Select(TwoFactor.HashRecoveryCode));
        await _db.SaveChangesAsync();
        return recovery;
    }

    /// <summary>Switches the second factor off (the member with a code, or an administrator / a plugin's back office for a lost phone).</summary>
    public async Task DisableAsync(SiteMember m)
    {
        m.TwoFactorEnabled = false;
        m.TotpSecret = null;
        m.RecoveryCodes = null;
        await _db.SaveChangesAsync();
    }

    public static int RemainingRecoveryCodes(SiteMember m) => (m.RecoveryCodes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// "Password right, code still to come" between <c>/anmelden</c> and <c>/anmelden/2fa</c> — the members' twin of <see cref="TwoFactorLoginCookie"/>:
/// its own name and purpose, path <c>/anmelden</c>, five minutes. It satisfies no policy and is no sign-in.
/// </summary>
public static class MemberTwoFactorCookie
{
    public const string Name = "matcms.m2fa";
    private const string Purpose = "MatCMS.MemberTwoFactorLogin.v1";
    private const string CookiePath = "/anmelden";

    public sealed record Pending(int MemberId, bool Remember, string ReturnUrl);

    public static void Write(HttpContext http, IDataProtectionProvider dp, Pending pending) =>
        http.Response.Cookies.Append(Name, dp.CreateProtector(Purpose).Protect(JsonSerializer.Serialize(pending)), new CookieOptions
        {
            HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(5), Path = CookiePath, IsEssential = true
        });

    public static Pending? Read(HttpContext http, IDataProtectionProvider dp)
    {
        var raw = http.Request.Cookies[Name];
        if (string.IsNullOrEmpty(raw)) return null;
        try { return JsonSerializer.Deserialize<Pending>(dp.CreateProtector(Purpose).Unprotect(raw)); }
        catch { return null; }
    }

    public static void Delete(HttpContext http) => http.Response.Cookies.Delete(Name, new CookieOptions { Path = CookiePath });
}
