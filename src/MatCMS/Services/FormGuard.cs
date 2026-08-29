using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Services;

/// <summary>
/// Progressive, self-hosted anti-spam for public forms — no third party, no env var, no cookie.
/// A configurable ladder read from <c>antispam.level</c> (global default) and overridable per form
/// (<c>Form.SpamLevel</c>, null = inherit):
/// <list type="bullet">
/// <item><b>0</b> off.</item>
/// <item><b>1</b> invisible: a honeypot field, a signed submit-timing token, and a JS-interaction
///   proof. Zero user friction — and fair, because the form already needs JS (datepicker, rich
///   select, conditional fields).</item>
/// <item><b>2</b> adds an invisible proof-of-work the browser solves in the background.</item>
/// <item><b>3</b> adds a self-hosted arithmetic captcha (visible, accessible, no images).</item>
/// </list>
/// It is deliberately <b>stateless</b>: everything a check needs (issue time, the interaction nonce,
/// the PoW challenge, the captcha answer) rides inside a DataProtection-signed token in a hidden
/// field, so there is no server session and nothing to store. The token is also time-limited, so an
/// old one is rejected on its own. A baseline per-IP rate limit is always on while guarding.
/// </summary>
public sealed class FormGuard
{
    // Field names look plausible/innocuous on purpose. They must match the render partial.
    public const string HoneypotField = "url__hp";     // hidden off-screen; a bot fills it, a human never sees it
    public const string TokenField = "__fg";           // the signed payload
    public const string InteractionField = "__fi";     // JS copies the token nonce here on first interaction
    public const string PowField = "__fp";             // client proof-of-work nonce (level >= 2)
    public const string CaptchaField = "__fc";         // the visitor's captcha answer (level >= 3)

    private const int MinSeconds = 2;                  // faster than this after render = a script, not a human
    private const int MaxAgeMinutes = 180;             // a token older than this is stale / replayed
    private const int PowBitsLevel2 = 14;              // ~16k hashes: unmeasurable for one human, a tax on mass spam
    private const int RateLimitPerMinute = 6;          // submits per IP per minute while guarding

    private readonly ITimeLimitedDataProtector _protector;
    private readonly IMemoryCache _cache;

    public FormGuard(IDataProtectionProvider dp, IMemoryCache cache)
    {
        _protector = dp.CreateProtector("MatCMS.FormGuard.v1").ToTimeLimitedDataProtector();
        _cache = cache;
    }

    /// <summary>What the render partial needs to draw the guarded form. The token carries the secrets;
    /// the plain fields (nonce, challenge, operands) are what the client script/markup work with.</summary>
    public sealed record Issued(string Token, string Nonce, int PowBits, string Challenge, int CaptchaA, int CaptchaB);

    public Issued Issue(string slug, int level)
    {
        var nonce = Hex(9);
        var challenge = Hex(12);
        var powBits = level >= 2 ? PowBitsLevel2 : 0;
        int a = 0, b = 0;
        if (level >= 3) { a = RandomNumberGenerator.GetInt32(2, 10); b = RandomNumberGenerator.GetInt32(2, 10); }

        var payload = new Payload
        {
            Iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Slug = slug,
            Nonce = nonce,
            PowBits = powBits,
            Challenge = challenge,
            Sum = a + b,
        };
        var token = _protector.Protect(JsonSerializer.Serialize(payload), TimeSpan.FromMinutes(MaxAgeMinutes));
        return new Issued(token, nonce, powBits, challenge, a, b);
    }

    public enum Verdict
    {
        Ok,
        /// <summary>Treat as spam but show SUCCESS — never tell a bot why it was dropped. Nothing stored/mailed.</summary>
        SpamSilent,
        /// <summary>Show the visitor a message and let them try again (keeps a rare false-positive recoverable).</summary>
        Retry,
    }

    public sealed record Result(Verdict Verdict, string? Message = null);

    /// <summary>Runs the checks for <paramref name="level"/> against a posted form. The antiforgery token
    /// is already validated by the framework before this is called.</summary>
    public Result Validate(HttpRequest req, string slug, int level, string? clientIp)
    {
        if (level <= 0) return new(Verdict.Ok);
        var form = req.Form;

        // Baseline: per-IP rate limit. A sliding one-minute counter in memory — cheap, and it needs no
        // schema. Independent of the level checks so a flood is capped even if a request would pass them.
        if (!string.IsNullOrEmpty(clientIp))
        {
            var k = "fg:rl:" + clientIp;
            var n = (_cache.TryGetValue(k, out int c) ? c : 0) + 1;
            _cache.Set(k, n, TimeSpan.FromMinutes(1));
            if (n > RateLimitPerMinute)
                return new(Verdict.Retry, "Zu viele Versuche. Bitte warte kurz und sende dann erneut.");
        }

        // Honeypot: a real visitor never sees the field, so any value is a bot. Silent — a visible
        // rejection would teach the bot to leave it blank.
        if (!string.IsNullOrWhiteSpace(form[HoneypotField])) return new(Verdict.SpamSilent);

        // Signed token: unprotecting also enforces the max age. Missing/forged/expired → recoverable retry.
        Payload? p = null;
        var tok = form[TokenField].ToString();
        if (!string.IsNullOrEmpty(tok))
        {
            try { p = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(tok)); }
            catch { p = null; }
        }
        if (p is null || !string.Equals(p.Slug, slug, StringComparison.Ordinal))
            return new(Verdict.Retry, "Die Sicherheitsprüfung ist abgelaufen. Bitte lade die Seite neu und sende erneut.");

        // Timing: humans do not submit a form a fraction of a second after it appeared.
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - p.Iat;
        if (elapsed < MinSeconds)
            return new(Verdict.Retry, "Bitte einen Moment warten und dann erneut senden.");

        // JS-interaction proof: the script writes the token nonce here on the first keypress/click. No
        // interaction = no JS = a script. Silent, because the form needs JS to work at all anyway.
        if (!string.Equals(form[InteractionField].ToString(), p.Nonce, StringComparison.Ordinal))
            return new(Verdict.SpamSilent);

        // Level 2: proof-of-work over the token's own challenge (so it cannot be precomputed or reused
        // past the token's short life).
        if (level >= 2 && p.PowBits > 0 && !PowOk(p.Challenge, form[PowField].ToString(), p.PowBits))
            return new(Verdict.Retry, "Die Sicherheitsprüfung ist fehlgeschlagen. Bitte lade die Seite neu.");

        // Level 3: the arithmetic captcha.
        if (level >= 3)
        {
            var ans = form[CaptchaField].ToString().Trim();
            if (!int.TryParse(ans, out var got) || got != p.Sum)
                return new(Verdict.Retry, "Bitte beantworte die Sicherheitsfrage korrekt.");
        }

        return new(Verdict.Ok);
    }

    private static string Hex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static bool PowOk(string challenge, string nonce, int bits)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 32) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(challenge + ":" + nonce));
        return LeadingZeroBits(hash) >= bits;
    }

    private static int LeadingZeroBits(byte[] h)
    {
        var count = 0;
        foreach (var b in h)
        {
            if (b == 0) { count += 8; continue; }
            for (var i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) == 0) count++;
                else return count;
            }
            break;
        }
        return count;
    }

    private sealed class Payload
    {
        public long Iat { get; set; }
        public string Slug { get; set; } = "";
        public string Nonce { get; set; } = "";
        public int PowBits { get; set; }
        public string Challenge { get; set; } = "";
        public int Sum { get; set; }
    }
}
