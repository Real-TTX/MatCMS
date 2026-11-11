using MatCMS.Plugin;

namespace MatCMS.Plugin.Shop;

/// <summary>The withdrawals' table (see <see cref="Withdrawal"/>). A row is written once when a customer confirms; afterwards only its status and the operator's note change —
/// what the customer declared stays as he declared it.</summary>
internal sealed partial class ShopStore
{
    private static Withdrawal WithdrawalOf(DataRow r) => new()
    {
        Id = r.Id, OrderId = r.Int("order_id"), OrderNumber = r.Text("order_number"), MemberId = r.Int("member_id"), Name = r.Text("name"), Email = r.Text("email"),
        ItemsJson = r.Text("items") is { Length: > 0 } j ? j : "[]", ItemsText = r.Text("items_text"), What = r.Text("what"), Message = r.Text("message"),
        Status = r.Text("status") is { Length: > 0 } s ? s : ShopWithdrawals.Received, Note = r.Text("note"),
        CreatedUtc = r.Timestamp("created") ?? DateTime.UtcNow, UpdatedUtc = r.Timestamp("updated") ?? DateTime.UtcNow
    };

    public Task<long> AddWithdrawalAsync(Withdrawal w, CancellationToken ct = default) =>
        T("withdrawals").InsertAsync(new Dictionary<string, object?>
        {
            ["order_id"] = w.OrderId, ["order_number"] = w.OrderNumber, ["member_id"] = w.MemberId, ["name"] = w.Name, ["email"] = w.Email, ["items"] = w.ItemsJson,
            ["items_text"] = w.ItemsText, ["what"] = w.What, ["message"] = w.Message, ["status"] = ShopWithdrawals.Received, ["note"] = "", ["created"] = w.CreatedUtc, ["updated"] = w.CreatedUtc
        }, ct);

    public async Task<IReadOnlyList<Withdrawal>> WithdrawalsAsync(CancellationToken ct = default) =>
        (await T("withdrawals").QueryAsync(DataQuery.Everything.OrderBy("created", true).OrderBy("id", true).Page(0, 5000), ct)).Select(WithdrawalOf).ToList();

    public async Task<Withdrawal?> WithdrawalAsync(long id, CancellationToken ct = default) =>
        id > 0 && await T("withdrawals").GetAsync(id, ct) is { } r ? WithdrawalOf(r) : null;

    public async Task<IReadOnlyList<Withdrawal>> WithdrawalsOfOrderAsync(long orderId, CancellationToken ct = default) =>
        (await T("withdrawals").QueryAsync(DataQuery.Everything.Eq("order_id", orderId).OrderBy("created"), ct)).Select(WithdrawalOf).ToList();

    public Task<long> CountWithdrawalsAsync(string status, CancellationToken ct = default) =>
        T("withdrawals").CountAsync(DataQuery.Everything.Eq("status", status), ct);

    /// <summary>The operator's part: where the return stands and his note. The declaration itself is never rewritten.</summary>
    public Task<int> SetWithdrawalAsync(long id, string status, string note, CancellationToken ct = default) =>
        T("withdrawals").UpdateAsync(id, new Dictionary<string, object?> { ["status"] = status, ["note"] = note, ["updated"] = DateTime.UtcNow }, ct);

    /// <summary>Unlinks an erased account's withdrawals (they stay — proof of a legal declaration — but no longer point at the account).</summary>
    public Task<int> UnlinkWithdrawalsOfMemberAsync(long memberId, CancellationToken ct = default) =>
        T("withdrawals").UpdateWhereAsync(DataQuery.Everything.Eq("member_id", memberId), new Dictionary<string, object?> { ["member_id"] = 0 }, ct);
}
