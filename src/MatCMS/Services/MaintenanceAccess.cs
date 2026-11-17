using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace MatCMS.Services;

/// <summary>
/// The way past the maintenance page without an admin account: the operator sets a PIN or password
/// (Settings → Wartung, <see cref="SettingKeys.MaintenancePin"/>, stored as a PBKDF2 hash only), and whoever
/// enters it on the maintenance page gets a DataProtection-encrypted, HttpOnly pass cookie for
/// <see cref="PassLifetime"/>. The pass names a fingerprint of the stored hash, so a new or removed PIN
/// ends every pass handed out before. After <see cref="MaxFailures"/> wrong entries from one address that
/// address is locked out for <see cref="LockDuration"/> — the correct PIN included — held in memory like
/// the other throttles (a restart forgets it, which costs an attacker a restart they cannot cause).
/// </summary>
public sealed class MaintenanceAccess
{
    public const string CookieName = "matcms.maint";
    /// <summary>The form field the maintenance page posts; a POST without it is an ordinary blocked request.</summary>
    public const string PinField = "matcms_maint_pin";
    public const int MaxFailures = 3;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PassLifetime = TimeSpan.FromHours(12);
    /// <summary>Shortest password; a PIN is exactly one of <see cref="PinLengths"/> digits instead.</summary>
    public const int MinLength = 4;
    public const int MaxLength = 128;
    /// <summary>The lengths a PIN may have; the maintenance page draws one box per digit.</summary>
    public static readonly int[] PinLengths = [4, 6, 8];

    private const int Iterations = 100_000;
    private const string Scheme = "pbkdf2-sha256";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Until)> _failures = new();

    public MaintenanceAccess(IDataProtectionProvider dp) =>
        _protector = dp.CreateProtector("MatCMS.MaintenanceAccess").ToTimeLimitedDataProtector();

    /// <summary>Test seam: the clock the lock-out is measured on.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    // --- the stored PIN ------------------------------------------------------

    // A PIN is stored as "pin{n}:" + the hash, a password as the bare hash: the kind travels WITH the hash in one row, so the
    // page can never draw boxes for a password (and both are equally protected and kept out of every backup).

    /// <summary>The digits of the stored PIN, or 0 for a password (and for nothing stored).</summary>
    public static int PinDigits(string? stored)
    {
        var s = (stored ?? "").Trim();
        if (!s.StartsWith("pin", StringComparison.Ordinal)) return 0;
        var colon = s.IndexOf(':');
        return colon > 3 && int.TryParse(s.AsSpan(3, colon - 3), out var n) && PinLengths.Contains(n) ? n : 0;
    }

    /// <summary>Whether <paramref name="secret"/> is a valid new value: exactly <paramref name="digits"/> digits, or a
    /// password of <see cref="MinLength"/>…<see cref="MaxLength"/> characters when <paramref name="digits"/> is 0.</summary>
    public static bool IsValid(string secret, int digits) =>
        digits > 0 ? PinLengths.Contains(digits) && secret.Length == digits && secret.All(char.IsAsciiDigit)
                   : secret.Length >= MinLength && secret.Length <= MaxLength;

    public static string Hash(string pin, int digits = 0)
    {
        if (digits > 0 && !PinLengths.Contains(digits)) throw new ArgumentOutOfRangeException(nameof(digits));
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return (digits > 0 ? $"pin{digits}:" : "") + $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string? stored)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrWhiteSpace(stored)) return false;
        var body = stored.Trim();
        var digits = PinDigits(body);
        if (digits > 0)
        {
            if (!IsValid(pin, digits)) return false;   // not even the shape of the PIN
            body = body[(body.IndexOf(':') + 1)..];
        }
        var parts = body.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Names the stored hash without revealing it: a pass is valid only for the PIN it was granted under.</summary>
    private static string Fingerprint(string stored) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored.Trim())))[..32];

    // --- the pass cookie -----------------------------------------------------

    public bool HasPass(HttpContext ctx, string stored)
    {
        var raw = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return false;
        try { return _protector.Unprotect(raw) == Fingerprint(stored); }
        catch (CryptographicException) { return false; }   // expired, tampered, or from other keys
    }

    public void Grant(HttpContext ctx, string stored)
    {
        ctx.Response.Cookies.Append(CookieName, _protector.Protect(Fingerprint(stored), PassLifetime), new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = PassLifetime,
            IsEssential = true,
        });
    }

    // --- the lock-out --------------------------------------------------------

    public static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public bool IsLocked(string client) =>
        _failures.TryGetValue(client, out var f) && f.Count >= MaxFailures && f.Until > Now();

    /// <summary>Counts a wrong entry; returns true when this one locked the address. Failures are forgotten
    /// <see cref="LockDuration"/> after the last one, so the lock lasts that long from the third.</summary>
    public bool Fail(string client)
    {
        var now = Now();
        if (_failures.Count > 5000)
            foreach (var kv in _failures.Where(kv => kv.Value.Until <= now).ToList()) _failures.TryRemove(kv.Key, out _);
        var next = _failures.AddOrUpdate(client, _ => (1, now + LockDuration),
            (_, cur) => cur.Until <= now ? (1, now + LockDuration) : (cur.Count + 1, now + LockDuration));
        return next.Count >= MaxFailures;
    }

    public void Succeeded(string client) => _failures.TryRemove(client, out _);

    /// <summary>Test seam: forget every counted failure.</summary>
    public void Reset() => _failures.Clear();
}
