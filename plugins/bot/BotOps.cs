using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Bot;

/// <summary>
/// The bot for AI agents (the site's assistant, the cloud's MCP, the site's own MCP server): what it used and may use (no personal data — the counters
/// know a visitor only as a hash of the day), and the stored conversations. The conversations are visitors' and members' own words: those reads are
/// <c>PersonalData</c> and never reach the assistant. The bot's settings are settings (the host's settings form, a cloud rollout), not content — no
/// operation writes them, and there is no operation that deletes conversations (the back office does that, irreversibly).
/// </summary>
internal static class BotOps
{
    public const int MaxChats = 200;

    public static readonly PluginOperationSpec UsageRead = new("usage.read",
        "Verbrauch und Grenzen des Bots: Fragen und Tokens heute, Tokens dieses Monats, die Grenzen (pro Besucher und Tag, pro Monat), Tages- und Monatsverlauf, " +
        "ob er antworten kann (KI-Verbindung) und was eingeschaltet ist (Kundendaten, Einwilligung, Chat-Blase, Aufbewahrung der Gespräche in Tagen).",
        """{"days"?: 14}""", Writes: false);
    public static readonly PluginOperationSpec ChatList = new("chat.list",
        "Gespeicherte Gespräche (nur wenn die Aufbewahrung eingeschaltet ist): Gesprächs-id, Zeit, Mitglieds-Nummer oder Gast, erste Frage, Anzahl Fragen.",
        """{"limit"?: 30, "search"?: "text"}""", Writes: false, PersonalData: true);
    public static readonly PluginOperationSpec ChatRead = new("chat.read",
        "Ein gespeichertes Gespräch lesen: jede Frage mit Antwort, Zeit, Tokens und Quellen.", """{"session": "…"}""", Writes: false, PersonalData: true);

    public static void Register(IPluginBuilder b)
    {
        b.Operation(UsageRead, UsageAsync);
        b.Operation(ChatList, ListChatsAsync);
        b.Operation(ChatRead, ReadChatAsync);
    }

    private static JsonObject Answer(string status, string message, JsonNode? data = null)
    {
        var o = new JsonObject { ["status"] = status, ["message"] = message };
        if (data is not null) o["data"] = data;
        return o;
    }

    private static string Str(JsonObject? o, string key) => (o?[key] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : "").Trim();

    private static int Int(JsonObject? o, string key, int def, int max)
    {
        if (o?[key] is not JsonValue v) return def;
        long n;
        if (v.TryGetValue<long>(out n)) { }
        else if (v.TryGetValue<int>(out var i)) n = i;
        else if (!(v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n))) return def;
        return (int)Math.Clamp(n, 1, max);
    }

    private static string When(DateTime? d) => (d ?? DateTime.UtcNow).ToString("o", CultureInfo.InvariantCulture);

    public static async Task<JsonNode?> UsageAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var cfg = BotConfig.From(ctx.Settings);
        var store = new BotStore(ctx.Data);
        var (todayAsked, todayTokens) = await store.ReadAsync(BotStore.Today, BotStore.SiteKey, ct);
        var (monthAsked, monthTokens) = await store.ReadAsync(BotStore.Month, BotStore.SiteKey, ct);
        var days = Int(args as JsonObject, "days", 14, 400);
        // The site's own counter rows only ("site"): a day is "yyyy-MM-dd", a month "yyyy-MM". The visitors' rows (a hash per day) are never shown.
        var rows = await ctx.Data.Table("usage").QueryAsync(DataQuery.Everything.Eq("key", BotStore.SiteKey).OrderBy("day", descending: true).Page(0, 1000), ct);
        var history = new JsonArray(rows.Where(r => r.Text("day").Length == 10).Take(days)
            .Select(r => (JsonNode)new JsonObject { ["day"] = r.Text("day"), ["questions"] = r.Int("messages"), ["tokens"] = r.Int("tokens") }).ToArray());
        var months = new JsonArray(rows.Where(r => r.Text("day").Length == 7).Take(13)
            .Select(r => (JsonNode)new JsonObject { ["month"] = r.Text("day"), ["questions"] = r.Int("messages"), ["tokens"] = r.Int("tokens") }).ToArray());
        var data = new JsonObject
        {
            ["today"] = new JsonObject { ["questions"] = todayAsked, ["tokens"] = todayTokens },
            ["month"] = new JsonObject { ["questions"] = monthAsked, ["tokens"] = monthTokens, ["limit"] = cfg.PerMonthTokens, ["left"] = cfg.PerMonthTokens > 0 ? Math.Max(0, cfg.PerMonthTokens - monthTokens) : null },
            ["limits"] = new JsonObject
            {
                ["questionsPerVisitorDay"] = cfg.PerVisitorDay, ["tokensPerVisitorDay"] = cfg.PerVisitorTokens, ["tokensPerMonth"] = cfg.PerMonthTokens,
                ["maxAnswerTokens"] = cfg.MaxAnswerTokens, ["maxQuestionChars"] = cfg.MaxQuestionChars, ["zeroMeans"] = "ohne Grenze",
            },
            ["status"] = new JsonObject
            {
                ["aiConnected"] = ctx.Site.AiEnabled, ["memberData"] = cfg.MemberData, ["consent"] = cfg.Consent, ["bubble"] = cfg.Bubble, ["keepDays"] = cfg.KeepDays,
            },
            ["days"] = history, ["months"] = months,
        };
        var limit = cfg.PerMonthTokens > 0 ? $" von {cfg.PerMonthTokens}" : "";
        return Answer("applied", $"Heute {todayAsked} Fragen, diesen Monat {monthTokens}{limit} Tokens.", data);
    }

    public static async Task<JsonNode?> ListChatsAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var o = args as JsonObject;
        var cfg = BotConfig.From(ctx.Settings);
        var search = Str(o, "search");
        var rows = await ctx.Data.Table("chats").QueryAsync(DataQuery.Everything.OrderBy("created", descending: true).Page(0, 3000), ct);
        var sessions = rows.GroupBy(r => r.Text("session"))
            .Select(g => (Session: g.Key, Last: g.Max(r => r.Timestamp("created") ?? DateTime.MinValue), First: g.OrderBy(r => r.Timestamp("created")).First(),
                          Count: g.Count(), Member: g.Max(r => r.Int("member_id")), All: g.ToList()))
            .Where(s => search.Length == 0 || s.All.Any(r => r.Text("question").Contains(search, StringComparison.OrdinalIgnoreCase) || r.Text("answer").Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.Last).ToList();
        var list = new JsonArray(sessions.Take(Int(o, "limit", 30, MaxChats)).Select(s => (JsonNode)new JsonObject
        {
            ["session"] = s.Session, ["last"] = When(s.Last), ["member"] = s.Member > 0 ? s.Member : null, ["firstQuestion"] = s.First.Text("question"), ["turns"] = s.Count,
        }).ToArray());
        var note = cfg.KeepDays > 0 ? $"Gespräche werden {cfg.KeepDays} Tage aufbewahrt." : "Die Aufbewahrung ist aus – es werden keine Gespräche gespeichert.";
        return Answer("applied", $"{list.Count} von {sessions.Count} Gesprächen. {note}", new JsonObject { ["keepDays"] = cfg.KeepDays, ["chats"] = list });
    }

    public static async Task<JsonNode?> ReadChatAsync(IPluginContext ctx, JsonNode? args, CancellationToken ct)
    {
        var session = Str(args as JsonObject, "session");
        if (session.Length == 0) return Answer("failed", "session fehlt.");
        var rows = await ctx.Data.Table("chats").QueryAsync(DataQuery.Everything.Eq("session", session).OrderBy("created").Page(0, 500), ct);
        if (rows.Count == 0) return Answer("failed", "Kein gespeichertes Gespräch mit dieser id.");
        var turns = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
        {
            ["created"] = When(r.Timestamp("created")), ["member"] = r.Int("member_id") > 0 ? r.Int("member_id") : null, ["question"] = r.Text("question"),
            ["answer"] = r.Text("answer"), ["tokens"] = r.Int("tokens"), ["sources"] = (r.Json("sources") as JsonArray ?? new JsonArray()).DeepClone(),
        }).ToArray());
        return Answer("applied", $"{rows.Count} Frage(n)", new JsonObject { ["session"] = session, ["turns"] = turns });
    }
}
