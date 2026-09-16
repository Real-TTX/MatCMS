using System.Security.Cryptography;
using System.Text;

namespace MatCMS.Shared;

/// <summary>
/// RFC 6238 TOTP + recovery-code helpers, shared by the CMS instance and the cloud so the second
/// factor is implemented <b>once</b>. Deliberately dependency-free (BCL only), like the rest of this
/// library: the algorithm is standard (HMAC-SHA1 over a 30-second counter) and the codebase already
/// hand-rolls its PKCE verify the same way — constant-time compare via
/// <see cref="CryptographicOperations.FixedTimeEquals"/>.
/// <para>What each application owns instead: <b>where</b> the secret is stored (encrypted at rest) and
/// the login/enrolment plumbing. This class never touches EF, HTTP or DataProtection.</para>
/// </summary>
public static class TwoFactor
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int Period = 30;   // seconds per TOTP step (RFC 6238 default)
    private const int Digits = 6;    // authenticator apps show six digits

    // --- Secret ---------------------------------------------------------------

    /// <summary>A fresh random TOTP secret, Base32-encoded (what an authenticator app expects). 20
    /// bytes = 160 bits, the RFC 4226 recommendation.</summary>
    public static string GenerateSecret(int bytes = 20) =>
        Base32Encode(RandomNumberGenerator.GetBytes(bytes));

    // --- Verification ---------------------------------------------------------

    /// <summary>Checks a user-entered 6-digit code against the secret. Accepts codes from the current
    /// 30-second step and <paramref name="window"/> steps on either side (clock drift). The whole
    /// window is always walked and each candidate compared in constant time, so neither a match nor its
    /// position leaks through timing. <paramref name="unixTimeSeconds"/> is injectable for tests; null
    /// = now.</summary>
    public static bool VerifyTotp(string? secretBase32, string? code, int window = 1, long? unixTimeSeconds = null) =>
        TryVerifyTotp(secretBase32, code, out _, window, unixTimeSeconds);

    /// <summary>As <see cref="VerifyTotp"/>, but also reports the 30-second step the code matched
    /// (<paramref name="matchedStep"/>, -1 when none). The caller persists the highest accepted step per
    /// user and rejects codes at or below it, giving RFC 6238 §5.2 one-time use — a code stays valid for
    /// ~90 s otherwise, long enough for an observed code to be replayed. The whole window is still walked;
    /// only a single assignment depends on the (already constant-time) comparison result.</summary>
    public static bool TryVerifyTotp(string? secretBase32, string? code, out long matchedStep, int window = 1, long? unixTimeSeconds = null)
    {
        matchedStep = -1;
        if (string.IsNullOrWhiteSpace(secretBase32) || string.IsNullOrWhiteSpace(code)) return false;
        var entered = new string(code.Where(char.IsDigit).ToArray());
        if (entered.Length != Digits) return false;

        byte[] key;
        try { key = Base32Decode(secretBase32); }
        catch { return false; }
        if (key.Length == 0) return false;

        var now = unixTimeSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counter = now / Period;

        var enteredBytes = Encoding.ASCII.GetBytes(entered);
        var ok = false;
        for (var i = -window; i <= window; i++)
        {
            var candidate = Encoding.ASCII.GetBytes(ComputeTotp(key, counter + i));
            // OR the constant-time result in so the loop count — and thus the timing — is independent
            // of where, or whether, a match occurs.
            var equal = CryptographicOperations.FixedTimeEquals(enteredBytes, candidate);
            if (equal) matchedStep = counter + i;
            ok |= equal;
        }
        return ok;
    }

    private static string ComputeTotp(byte[] key, long counter)
    {
        var counterBytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        var hash = HMACSHA1.HashData(key, counterBytes);
        // Dynamic truncation (RFC 4226 §5.3): the low nibble of the last byte picks a 4-byte window.
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);
        var otp = binary % (int)Math.Pow(10, Digits);
        return otp.ToString().PadLeft(Digits, '0');
    }

    /// <summary>The <c>otpauth://totp/…</c> URI an authenticator app imports (usually by scanning a QR
    /// of it). Issuer and account are shown to the user in the app; both are percent-encoded.</summary>
    public static string BuildOtpauthUri(string issuer, string account, string secretBase32)
    {
        var label = $"{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}";
        return $"otpauth://totp/{label}"
             + $"?secret={secretBase32}"
             + $"&issuer={Uri.EscapeDataString(issuer)}"
             + $"&algorithm=SHA1&digits={Digits}&period={Period}";
    }

    // --- Recovery codes -------------------------------------------------------

    /// <summary>Fresh single-use recovery codes in PLAINTEXT — show them to the user <b>once</b>, then
    /// store only their <see cref="HashRecoveryCode"/> hashes. Format: two groups of five Base32 chars
    /// (e.g. <c>7K2QF-9HM4T</c>), unambiguous and easy to type.</summary>
    public static List<string> GenerateRecoveryCodes(int count = 10)
    {
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = RandomBase32(10);
            codes.Add($"{raw[..5]}-{raw[5..]}");
        }
        return codes;
    }

    /// <summary>Normalises a typed recovery code (strip separators/spaces, upper-case) so display
    /// formatting and case never affect the match.</summary>
    public static string NormalizeRecoveryCode(string? code) =>
        new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>SHA-256 (hex) of the normalised code — the only form ever stored, like the API-key and
    /// instance-token hashes elsewhere. Single use is enforced by the caller removing a matched hash.</summary>
    public static string HashRecoveryCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeRecoveryCode(code)))).ToLowerInvariant();

    private static string RandomBase32(int chars)
    {
        // One random byte per output char, folded into the 32-char alphabet. 256 is a multiple of 32,
        // so `b & 31` is unbiased; plenty of entropy for a one-time code and no base32 padding maths.
        var bytes = RandomNumberGenerator.GetBytes(chars);
        var sb = new StringBuilder(chars);
        foreach (var b in bytes) sb.Append(Base32Alphabet[b & 31]);
        return sb.ToString();
    }

    // --- Base32 (RFC 4648, no padding) ----------------------------------------

    public static string Base32Encode(byte[] data)
    {
        if (data.Length == 0) return "";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 31]);
            }
            buffer &= (1 << bitsLeft) - 1;   // keep only the undrained low bits so `buffer` can't overflow
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string input)
    {
        var clean = (input ?? "").Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        if (clean.Length == 0) return Array.Empty<byte>();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in clean)
        {
            var val = Base32Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"Invalid base32 character '{c}'.");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
            buffer &= (1 << bitsLeft) - 1;
        }
        return output.ToArray();
    }
}
