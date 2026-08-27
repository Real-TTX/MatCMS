using System.Security.Cryptography;
using System.Text;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Creates and verifies operator API keys. The same shape as <c>InstanceService</c>'s token helpers —
/// a URL-safe random secret, stored only as its SHA-256, compared in length-constant time — so there
/// is one idea of "a secret the cloud hands out" rather than two.
/// </summary>
public class ApiKeyService
{
    private readonly AppDbContext _db;
    public ApiKeyService(AppDbContext db) => _db = db;

    /// <summary>Marks the value as one of ours in logs and headers, and lets a reader tell an API key
    /// from an instance token at a glance.</summary>
    private const string Tag = "mck_";

    public sealed record Created(ApiKey Key, string RawKey);

    /// <summary>
    /// Creates a key and returns its raw value ONCE. Only the hash and a short prefix are stored, so
    /// the raw value cannot be recovered afterwards — the caller must show it now or never.
    /// </summary>
    public async Task<Created> CreateAsync(string name, bool canRestore, bool allInstances,
        IEnumerable<int> instanceIds, CancellationToken ct = default)
    {
        var raw = Tag + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Name = string.IsNullOrWhiteSpace(name) ? "API-Schlüssel" : name.Trim(),
            KeyHash = Hash(raw),
            Prefix = raw[..Math.Min(12, raw.Length)],
            CanRestore = canRestore,
            AllInstances = allInstances,
        };
        if (!allInstances)
            key.Instances = instanceIds.Distinct()
                .Select(id => new ApiKeyInstance { InstanceId = id }).ToList();

        _db.ApiKeys.Add(key);
        await _db.SaveChangesAsync(ct);
        return new Created(key, raw);
    }

    /// <summary>
    /// Resolves the raw key from an <c>Authorization: Bearer …</c> header to its record, or null when
    /// it is missing, unknown or revoked. The scope rows are loaded with it, because every authorised
    /// call needs them right after. <see cref="ApiKey.LastUsedAt"/> is refreshed at most once a minute.
    /// </summary>
    public async Task<ApiKey?> AuthenticateAsync(string? authorizationHeader, CancellationToken ct = default)
    {
        var raw = ExtractBearer(authorizationHeader);
        if (raw is null) return null;

        var hash = Hash(raw);
        var key = await _db.ApiKeys.Include(k => k.Instances)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (key is null || key.Revoked) return null;

        // Defence in depth against a timing side channel in the lookup path: compare the hashes in
        // length-constant time, exactly as the instance token check does.
        var expected = Encoding.UTF8.GetBytes(key.KeyHash);
        var actual = Encoding.UTF8.GetBytes(hash);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return null;

        if (key.LastUsedAt is null || DateTime.UtcNow - key.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            key.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return key;
    }

    /// <summary>Whether a key may act on a given instance: all-instances keys always, scoped keys only
    /// for an instance in their list.</summary>
    public static bool CanAccess(ApiKey key, Instance instance) =>
        key.AllInstances || key.Instances.Any(s => s.InstanceId == instance.Id);

    private static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var h = header.Trim();
        const string scheme = "Bearer ";
        if (h.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) h = h[scheme.Length..].Trim();
        return h.Length == 0 ? null : h;
    }

    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
