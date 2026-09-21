using System.Security.Cryptography;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The server side of the OAuth 2.0 Device Authorization Grant (RFC 8628): issue a device+user code pair,
/// let the operator approve the user code at <c>/device</c>, and hand the polling client an operator API
/// key once — and only once — after approval.
/// <para>The device code is a high-entropy secret stored as <b>SHA-256 only</b> (like instance tokens and
/// API keys); the user code is short and human-typeable (the join-code alphabet, no 0/O/1/I). On approval
/// no secret is stored: the operator key is minted at redemption via <see cref="ApiKeyService"/>, so the
/// raw value never sits at rest — it is created and returned in the same call, then the grant is consumed.</para>
/// </summary>
public class DeviceCodes
{
    private readonly AppDbContext _db;
    public DeviceCodes(AppDbContext db) => _db = db;

    // No 0/O/1/I: the user reads this off one screen and types it into another — the same reason the
    // profile join code (ProfileService.NewJoinCode) uses this alphabet.
    private const string UserAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int UserCodeLen = 8;   // shown grouped as XXXX-XXXX

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int IntervalSeconds = 5;

    public sealed record Issued(string DeviceCode, string UserCode, int ExpiresIn, int Interval);

    /// <summary>Starts a flow: a hashed device code + a fresh user code, valid for <see cref="Lifetime"/>.</summary>
    public async Task<Issued> IssueAsync(string? clientLabel, CancellationToken ct = default)
    {
        var deviceCode = Base64Url(RandomNumberGenerator.GetBytes(32));

        // Keep the user code unique among the codes that could still be entered, so /device resolves one
        // grant unambiguously. The space is huge; a handful of tries is astronomically enough.
        string userCode;
        var tries = 0;
        do { userCode = NewUserCode(); }
        while (await _db.DeviceCodes.AnyAsync(d => d.UserCode == userCode && d.Status == DeviceCodeStatus.Pending, ct)
               && ++tries < 8);

        _db.DeviceCodes.Add(new DeviceCode
        {
            DeviceCodeHash = Hash(deviceCode),
            UserCode = userCode,
            ClientLabel = Label(clientLabel),
            Status = DeviceCodeStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + Lifetime,
            IntervalSeconds = IntervalSeconds,
        });
        await _db.SaveChangesAsync(ct);
        return new Issued(deviceCode, Group(userCode), (int)Lifetime.TotalSeconds, IntervalSeconds);
    }

    /// <summary>Resolves a user-entered code (case-insensitive, dashes/spaces ignored) to a PENDING grant
    /// that has not expired, for the <c>/device</c> consent page. Null = unknown, already decided, or expired.</summary>
    public async Task<DeviceCode?> FindPendingByUserCodeAsync(string? userCode, CancellationToken ct = default)
    {
        var normalized = Normalize(userCode);
        if (normalized.Length != UserCodeLen) return null;
        var row = await _db.DeviceCodes.FirstOrDefaultAsync(d => d.UserCode == normalized, ct);
        if (row is null || row.Status != DeviceCodeStatus.Pending || DateTime.UtcNow > row.ExpiresAt) return null;
        return row;
    }

    public async Task ApproveAsync(DeviceCode row, int userId, CancellationToken ct = default)
    {
        row.Status = DeviceCodeStatus.Approved;
        row.ApprovedByUserId = userId;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DenyAsync(DeviceCode row, int userId, CancellationToken ct = default)
    {
        row.Status = DeviceCodeStatus.Denied;
        row.ApprovedByUserId = userId;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The distinct RFC 8628 token-endpoint outcomes for a device-code poll.</summary>
    public enum PollKind { AuthorizationPending, SlowDown, ExpiredToken, AccessDenied, Ok, InvalidGrant }
    public sealed record PollResult(PollKind Kind, string? AccessToken = null);

    /// <summary>
    /// One poll of <c>/oauth/token</c> for a device code. On the first poll after approval this MINTS the
    /// operator API key (full operator access) and returns its raw value once, then consumes the grant so a
    /// replay gets nothing. Enforces the poll <c>interval</c> (<see cref="PollKind.SlowDown"/>) and expiry.
    /// </summary>
    public async Task<PollResult> RedeemAsync(string? deviceCodeRaw, ApiKeyService apiKeys, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(deviceCodeRaw)) return new(PollKind.InvalidGrant);

        // Read untracked and mutate via atomic conditional UPDATEs. Each poll runs on its own scoped
        // DbContext, so a tracked read-modify-write could let two concurrent post-approval polls both mint
        // (no concurrency token on the row). The Approved→Consumed flip below is a compare-and-set instead.
        var row = await _db.DeviceCodes.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceCodeHash == Hash(deviceCodeRaw), ct);
        // Unknown or already redeemed → an opaque invalid_grant: never reveal that a code once existed.
        if (row is null || row.Status == DeviceCodeStatus.Consumed) return new(PollKind.InvalidGrant);

        if (DateTime.UtcNow > row.ExpiresAt)
        {
            await _db.DeviceCodes.Where(d => d.Id == row.Id && d.Status != DeviceCodeStatus.Consumed)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceCodeStatus.Denied), ct);
            return new(PollKind.ExpiredToken);
        }

        // Polling faster than the advertised interval → slow_down (record the poll time either way so the
        // next call is measured from now).
        var now = DateTime.UtcNow;
        if (row.LastPolledAt is DateTime last && now - last < TimeSpan.FromSeconds(row.IntervalSeconds))
        {
            await _db.DeviceCodes.Where(d => d.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastPolledAt, now), ct);
            return new(PollKind.SlowDown);
        }
        await _db.DeviceCodes.Where(d => d.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastPolledAt, now), ct);

        if (row.Status == DeviceCodeStatus.Denied) return new(PollKind.AccessDenied);
        if (row.Status == DeviceCodeStatus.Pending) return new(PollKind.AuthorizationPending);

        // Approved: atomically CLAIM the grant before minting, so exactly one concurrent poll can win. The
        // conditional UPDATE flips Approved→Consumed for one caller; concurrent losers flip 0 rows and get
        // invalid_grant. Claiming BEFORE the mint means a crash between the two loses the grant (a harmless
        // retry) rather than leaking a key that was minted but never handed back.
        var claimed = await _db.DeviceCodes
            .Where(d => d.Id == row.Id && d.Status == DeviceCodeStatus.Approved)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeviceCodeStatus.Consumed), ct);
        if (claimed == 0) return new(PollKind.InvalidGrant);

        var created = await apiKeys.CreateAsync(
            name: $"Gerät: {row.ClientLabel} · {DateTime.UtcNow:yyyy-MM-dd}",
            canRestore: true, allInstances: true, instanceIds: Array.Empty<int>(), ct);
        await _db.DeviceCodes.Where(d => d.Id == row.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.MintedApiKeyId, created.Key.Id), ct);
        return new(PollKind.Ok, created.RawKey);
    }

    /// <summary>Removes grants that expired or were consumed a while ago, so the table does not grow.</summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        return await _db.DeviceCodes
            .Where(d => d.ExpiresAt < cutoff || (d.Status == DeviceCodeStatus.Consumed && d.CreatedAt < cutoff))
            .ExecuteDeleteAsync(ct);
    }

    private static string NewUserCode()
    {
        var chars = new char[UserCodeLen];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = UserAlphabet[RandomNumberGenerator.GetInt32(UserAlphabet.Length)];
        return new string(chars);
    }

    private static string Group(string code) => code.Length == UserCodeLen ? code[..4] + "-" + code[4..] : code;
    private static string Normalize(string? code) =>
        new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string Label(string? raw)
    {
        var s = new string((raw ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ').Take(40).ToArray()).Trim();
        return s.Length == 0 ? "Gerät" : s;
    }

    private static string Hash(string raw) => ApiKeyService.Hash(raw);   // one idea of "hash a handed-out secret"
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
