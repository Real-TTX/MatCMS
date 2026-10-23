using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Bot;

/// <summary>
/// The bot's two tables. <c>usage</c> counts questions and tokens per day and visitor, per day for the site and per month for the site — the limits are
/// checked against it before the model is asked. A visitor is NOT stored as an address: the key is a hash of address, browser and day, which changes every day
/// and is deleted after two. <c>chats</c> holds conversations only while the operator chose to keep them (<see cref="BotConfig.KeepDays"/>).
/// </summary>
internal sealed class BotStore
{
    private readonly IPluginData _data;
    public BotStore(IPluginData data) => _data = data;

    public static void Declare(IDataBuilder d)
    {
        d.Table("usage", t => t.Text("day", required: true, def: "").Text("key", required: true, def: "")
            .Int("messages", def: "0").Int("tokens", def: "0").UniqueIndex("day", "key"));
        d.Table("chats", t => t.Text("session", required: true, def: "").Int("member_id", def: "0").Text("question").Text("answer")
            .Int("tokens", def: "0").Json("sources", def: "[]").Timestamp("created").Index("session").Index("created"));
    }

    public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Month => DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public const string SiteKey = "site";

    /// <summary>The visitor's key for today: not reversible, not the same tomorrow.</summary>
    public static string VisitorKey(string ip, string userAgent) =>
        "v:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Today + "|" + ip + "|" + userAgent)))[..24].ToLowerInvariant();

    public async Task<(long Messages, long Tokens)> ReadAsync(string day, string key, CancellationToken ct = default)
    {
        var r = await _data.Table("usage").FindAsync(DataQuery.Everything.Eq("day", day).Eq("key", key), ct);
        return r is null ? (0, 0) : (r.Int("messages"), r.Int("tokens"));
    }

    /// <summary>Adds to a counter row, creating it first (inside one transaction, so two questions at the same moment cannot both create it).</summary>
    public Task AddAsync(string day, string key, long messages, long tokens, CancellationToken ct = default) =>
        _data.InTransactionAsync(async tx =>
        {
            var table = tx.Table("usage");
            var row = await table.FindAsync(DataQuery.Everything.Eq("day", day).Eq("key", key), ct);
            var id = row?.Id ?? await table.InsertAsync(new Dictionary<string, object?> { ["day"] = day, ["key"] = key, ["messages"] = 0L, ["tokens"] = 0L }, ct);
            if (messages != 0) await table.IncrementAsync(id, "messages", messages, ct: ct);
            if (tokens != 0) await table.IncrementAsync(id, "tokens", tokens, ct: ct);
        }, ct);

    public Task<long> SaveTurnAsync(string session, long memberId, string question, string answer, long tokens, string sourcesJson, CancellationToken ct = default) =>
        _data.Table("chats").InsertAsync(new Dictionary<string, object?>
        {
            ["session"] = session, ["member_id"] = memberId, ["question"] = question, ["answer"] = answer, ["tokens"] = tokens,
            ["sources"] = sourcesJson, ["created"] = DateTime.UtcNow
        }, ct);

    /// <summary>The daily clean-up: visitor counters older than two days, the site's counters older than 13 months, conversations older than the operator's
    /// retention — all of them when nothing is to be kept.</summary>
    public async Task CleanAsync(int keepDays, CancellationToken ct = default)
    {
        var usage = _data.Table("usage");
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await usage.DeleteWhereAsync(DataQuery.Everything.Where("key", DataOp.Ne, SiteKey).Where("day", DataOp.Lt, twoDaysAgo), ct);
        var yearAgo = DateTime.UtcNow.AddMonths(-13).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        await usage.DeleteWhereAsync(DataQuery.Everything.Eq("key", SiteKey).Where("day", DataOp.Lt, yearAgo), ct);
        var chats = _data.Table("chats");
        if (keepDays <= 0) await chats.DeleteWhereAsync(DataQuery.Everything, ct);
        else await chats.DeleteWhereAsync(DataQuery.Everything.Where("created", DataOp.Lt, DateTime.UtcNow.AddDays(-keepDays)), ct);
    }
}
