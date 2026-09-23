using System.Security.Cryptography;
using System.Text;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Registers and authenticates OAuth clients for the Authorization Code connector flow. Same "a secret the
/// cloud hands out is stored as its SHA-256, compared in constant time" rule as <see cref="ApiKeyService"/>
/// and instance tokens — one idea of a cloud-issued secret, not three.
/// </summary>
public class OAuthClientService
{
    private readonly AppDbContext _db;
    public OAuthClientService(AppDbContext db) => _db = db;

    private const string IdTag = "mcc_";
    private const string SecretTag = "mcs_";

    public sealed record Created(OAuthClient Client, string ClientSecret);

    /// <summary>Creates a client and returns its secret ONCE. Only the hash + a short prefix are stored.</summary>
    public async Task<Created> CreateAsync(string name, string redirectUris, CancellationToken ct = default)
    {
        var secret = SecretTag + Base64Url(RandomNumberGenerator.GetBytes(32));
        var client = new OAuthClient
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Connector" : name.Trim(),
            ClientId = IdTag + Base64Url(RandomNumberGenerator.GetBytes(16)),
            ClientSecretHash = Hash(secret),
            SecretPrefix = secret[..Math.Min(12, secret.Length)],
            RedirectUris = NormalizeUris(redirectUris),
        };
        _db.OAuthClients.Add(client);
        await _db.SaveChangesAsync(ct);
        return new Created(client, secret);
    }

    /// <summary>Updates an existing client's label and redirect allowlist. The client id and secret are never
    /// touched — a ChatGPT connector typically needs its callback URL added AFTER creation, once ChatGPT has
    /// shown it, and re-creating the client would rotate the secret the operator already pasted in.</summary>
    public async Task<bool> UpdateAsync(int id, string name, string redirectUris, CancellationToken ct = default)
    {
        var client = await _db.OAuthClients.FindAsync(new object?[] { id }, ct);
        if (client is null) return false;
        if (!string.IsNullOrWhiteSpace(name)) client.Name = name.Trim();
        client.RedirectUris = NormalizeUris(redirectUris);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<OAuthClient?> FindByClientIdAsync(string? clientId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(clientId)
            ? Task.FromResult<OAuthClient?>(null)
            : _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);

    /// <summary>Validates client_id + client_secret for the token exchange. Null = unknown, revoked, or
    /// wrong secret (compared in length-constant time).</summary>
    public async Task<OAuthClient?> AuthenticateAsync(string? clientId, string? clientSecret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrEmpty(clientSecret)) return null;
        var client = await _db.OAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId, ct);
        if (client is null || client.Revoked) return null;

        var expected = Encoding.UTF8.GetBytes(client.ClientSecretHash);
        var actual = Encoding.UTF8.GetBytes(Hash(clientSecret));
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return null;

        if (client.LastUsedAt is null || DateTime.UtcNow - client.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            client.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return client;
    }

    /// <summary>The redirect_uri must be EXACTLY one of the client's registered URIs.</summary>
    public static bool RedirectAllowed(OAuthClient client, string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri)) return false;
        foreach (var u in client.RedirectUris.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(u, redirectUri, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string NormalizeUris(string? raw) =>
        string.Join('\n', (raw ?? "")
            .Split(new[] { '\n', '\r', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == Uri.UriSchemeHttps || x.Scheme == Uri.UriSchemeHttp))
            .Distinct());

    public static string Hash(string raw) => ApiKeyService.Hash(raw);
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
