using System.Security.Cryptography;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

internal sealed partial class ShopStore
{
    /// <summary>A cart somebody asked to be reminded about.</summary>
    public sealed record ReminderCart(long Id, string Token, string Email, string RestoreKey, IReadOnlyList<CartLine> Lines, DateTime UpdatedUtc);

    /// <summary>Remembers (or, with <paramref name="remind"/> false, forgets) that the owner of this cart wants one reminder mail. Asking again re-arms a reminder that was already sent.</summary>
    public async Task<bool> SetCartReminderAsync(string token, string email, bool remind, CancellationToken ct = default)
    {
        var existing = await T("carts").FindByAsync("token", token, ct);
        if (existing is null) return false;
        var values = new Dictionary<string, object?> { ["remind"] = remind, ["email"] = remind ? email : "" };
        if (remind)
        {
            values["reminded"] = null;
            if (existing.Text("restore_key").Length == 0) values["restore_key"] = NewRestoreKey();
        }
        await T("carts").UpdateAsync(existing.Id, values, ct);
        return true;
    }

    public static string NewRestoreKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>The carts that are due: reminder wanted, not yet sent, quiet since <paramref name="idleBefore"/>, and not older than <paramref name="notOlderThan"/> (nobody wants a mail about a cart from last month).</summary>
    public async Task<IReadOnlyList<ReminderCart>> CartsDueForReminderAsync(DateTime idleBefore, DateTime notOlderThan, int take, CancellationToken ct = default)
    {
        var rows = await T("carts").QueryAsync(DataQuery.Everything.Eq("remind", true).Where("updated", DataOp.Lt, idleBefore).Where("updated", DataOp.Gt, notOlderThan).Page(0, take), ct);
        return rows.Where(r => r.Timestamp("reminded") is null && r.Text("email").Length > 0 && r.Text("restore_key").Length > 0)
            .Select(r => new ReminderCart(r.Id, r.Text("token"), r.Text("email"), r.Text("restore_key"), CartOf(r).Lines, r.Timestamp("updated") ?? DateTime.UtcNow)).ToList();
    }

    public Task MarkCartRemindedAsync(long cartId, CancellationToken ct = default) =>
        T("carts").UpdateAsync(cartId, new Dictionary<string, object?> { ["reminded"] = DateTime.UtcNow }, ct);

    public async Task<CartRow?> CartByRestoreKeyAsync(string key, CancellationToken ct = default) =>
        key.Length < 32 ? null : await T("carts").FindAsync(DataQuery.Everything.Eq("restore_key", key).Where("expires", DataOp.Gt, DateTime.UtcNow), ct) is { } r ? CartOf(r) : null;

    /// <summary>Has this address ordered since the given moment (then a reminder about the cart is pointless)?</summary>
    public async Task<bool> HasOrderSinceAsync(string email, DateTime sinceUtc, CancellationToken ct = default) =>
        await T("orders").CountAsync(DataQuery.Everything.Eq("email", email).Where("created", DataOp.Gt, sinceUtc), ct) > 0;

    public Task<int> DeleteCartsOfEmailAsync(string email, CancellationToken ct = default) => T("carts").DeleteWhereAsync(DataQuery.Everything.Eq("email", email), ct);
}
