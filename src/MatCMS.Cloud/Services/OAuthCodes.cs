using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Short-lived authorization codes for the SSO flow (a MatCMS instance logging a user in with their
/// cloud account). A code is issued at <c>/oauth/authorize</c> after the cloud user is authenticated
/// and authorised for the target instance, and redeemed exactly once at <c>/oauth/token</c> over the
/// instance-token-authenticated back-channel.
/// <para>Kept in memory: codes live ~2 minutes, the cloud is a single process, and a restart during a
/// pending login simply means "try again" — there is nothing here worth a table or a schema change.
/// PKCE (S256) binds the code to the client that started the flow, so a leaked code is useless without
/// the matching verifier.</para>
/// </summary>
public class OAuthCodes
{
    private readonly IMemoryCache _cache;
    public OAuthCodes(IMemoryCache cache) => _cache = cache;

    public sealed record Grant(int UserId, string InstancePublicId, string RedirectUri, string CodeChallenge);

    public string Issue(Grant grant)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _cache.Set("oauth:" + code, grant, TimeSpan.FromMinutes(2));
        return code;
    }

    /// <summary>One-time redemption: a code is removed the moment it is read, so it cannot be replayed.</summary>
    public Grant? Redeem(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var key = "oauth:" + code;
        if (_cache.TryGetValue(key, out Grant? g)) { _cache.Remove(key); return g; }
        return null;
    }

    /// <summary>PKCE S256: base64url(SHA-256(verifier)) must equal the challenge stored with the code.</summary>
    public static bool VerifyPkce(string? challenge, string? verifier)
    {
        if (string.IsNullOrEmpty(challenge) || string.IsNullOrEmpty(verifier)) return false;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var computed = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var a = Encoding.ASCII.GetBytes(computed);
        var b = Encoding.ASCII.GetBytes(challenge);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
