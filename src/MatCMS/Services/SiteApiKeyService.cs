using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// The site's API keys (<see cref="SiteApiKey"/>): made, checked, revoked. A key is <c>msk_</c> + 40 random characters; only its SHA-256 is stored,
/// so a lost key is made anew, never shown again. Rights are per plugin and per kind (<see cref="ApiAccess"/>).
/// </summary>
public sealed class SiteApiKeyService
{
    public const string KeyStart = "msk_";
    private readonly AppDbContext _db;
    public SiteApiKeyService(AppDbContext db) => _db = db;

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>The word a right is stored as.</summary>
    public static string Word(ApiAccess a) => a switch { ApiAccess.Write => "write", ApiAccess.PersonalData => "personal", _ => "read" };

    /// <summary>The rights of a key, cleaned: <c>{plugin}:{read|write|personal}</c>, lower case, no duplicates, nothing else.</summary>
    public static string CleanRights(IEnumerable<string> rights) => string.Join(",", rights
        .Select(r => r.Trim().ToLowerInvariant())
        .Where(r => System.Text.RegularExpressions.Regex.IsMatch(r, "^[a-z][a-z0-9_-]{0,63}:(read|write|personal)$"))
        .Distinct().OrderBy(r => r, StringComparer.Ordinal));

    /// <summary>Whether a key's rights allow <paramref name="access"/> on <paramref name="plugin"/>. Write includes read; personal stands alone.</summary>
    public static bool Allows(string rights, string plugin, ApiAccess access)
    {
        var set = rights.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return access switch
        {
            ApiAccess.Read => set.Contains(plugin + ":read") || set.Contains(plugin + ":write"),
            ApiAccess.Write => set.Contains(plugin + ":write"),
            _ => set.Contains(plugin + ":personal")
        };
    }

    /// <summary>Makes a key. Returns the row and the key in clear — the only moment it exists in clear.</summary>
    public async Task<(SiteApiKey Row, string Key)> CreateAsync(string name, IEnumerable<string> rights, DateTime? expiresAt, CancellationToken ct = default)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var key = KeyStart + RandomNumberGenerator.GetString(alphabet, 40);
        var row = new SiteApiKey
        {
            Name = (name ?? "").Trim() is { Length: > 0 } n ? (n.Length > 80 ? n[..80] : n) : "API-Schlüssel",
            Prefix = key[..10], Hash = Hash(key), Rights = CleanRights(rights), ExpiresAt = expiresAt
        };
        _db.SiteApiKeys.Add(row);
        await _db.SaveChangesAsync(ct);
        return (row, key);
    }

    /// <summary>The usable key behind a presented one, or null (unknown, revoked, expired). Compared by hash: the table holds nothing to compare in clear.</summary>
    public async Task<SiteApiKey?> FindAsync(string presented, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(KeyStart, StringComparison.Ordinal) || presented.Length > 100) return null;
        var hash = Hash(presented.Trim());
        var row = await _db.SiteApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Hash == hash, ct);
        return row is not null && row.IsUsable(DateTime.UtcNow) ? row : null;
    }

    // "Last used" and the count are written at most once a minute per key: an ERP that syncs thousands of lines must not turn every call into a write.
    private static readonly ConcurrentDictionary<int, (DateTime At, long Pending)> Touches = new();

    public async Task TouchAsync(int id, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var entry = Touches.AddOrUpdate(id, _ => (DateTime.MinValue, 1), (_, e) => (e.At, e.Pending + 1));
        if (now - entry.At < TimeSpan.FromMinutes(1)) return;
        Touches[id] = (now, 0);
        await _db.SiteApiKeys.Where(k => k.Id == id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now).SetProperty(k => k.Uses, k => k.Uses + entry.Pending), ct);
    }

    public Task<List<SiteApiKey>> ListAsync(CancellationToken ct = default) =>
        _db.SiteApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);

    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default) =>
        await _db.SiteApiKeys.Where(k => k.Id == id && k.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct) > 0;

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default) =>
        await _db.SiteApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct) > 0;
}
