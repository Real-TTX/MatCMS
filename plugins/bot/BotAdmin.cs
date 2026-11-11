using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Bot;

/// <summary>The bot's back office: what it costs and whether it can answer at all (overview), and the stored conversations when the operator keeps them.
/// Every screen is a tree of library components.</summary>
internal static class BotAdmin
{
    public const string Section = "/admin/p/bot";

    // Thousands written by hand: the image runs with invariant globalization, where "de-DE" silently gives "2,000,000".
    private static string N(IPluginContext ctx, long v)
    {
        var s = v.ToString("N0", CultureInfo.InvariantCulture);
        return ctx.Locale is { } l && l.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? s : s.Replace(',', '.');
    }

    public static async Task<PluginResult> OverviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = BotConfig.From(ctx.Settings);
        var store = new BotStore(ctx.Data);
        var (todayAsked, todayTokens) = await store.ReadAsync(BotStore.Today, BotStore.SiteKey, ct);
        var (_, monthTokens) = await store.ReadAsync(BotStore.Month, BotStore.SiteKey, ct);
        var monthHint = cfg.PerMonthTokens > 0 ? ctx.T("bot.ov.ofLimit", N(ctx, cfg.PerMonthTokens)) : ctx.T("bot.ov.noLimit");
        var monthTone = cfg.PerMonthTokens > 0 && monthTokens >= cfg.PerMonthTokens ? UiTone.Error
            : cfg.PerMonthTokens > 0 && monthTokens >= cfg.PerMonthTokens * 0.8 ? UiTone.Warning : UiTone.Default;

        // The switches as one status card — four alert bands read as four warnings, and "on" and "off" looked the same. An alert only where something is missing.
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("bot.ov.title"),
                Ui.Button(ctx.T("bot.ov.try"), "/", UiButtonKind.Ghost, "ti-external-link", small: true) with { NewTab = true }),
            ctx.Site.AiEnabled ? null : Ui.Alert(ctx.T("bot.ov.aiOff"), UiTone.Warning),
            Ui.Stats(
                Ui.Stat(ctx.T("bot.ov.today"), N(ctx, todayAsked)),
                Ui.Stat(ctx.T("bot.ov.tokensToday"), N(ctx, todayTokens)),
                Ui.Stat(ctx.T("bot.ov.month"), N(ctx, monthTokens), monthHint, monthTone)),
            Ui.Card(ctx.T("bot.ov.status"),
                Ui.KeyValue(
                    (ctx.T("bot.ov.kAi"), ctx.T(ctx.Site.AiEnabled ? "bot.ov.aiOn" : "bot.ov.aiNone")),
                    (ctx.T("bot.ov.kMember"), ctx.T(cfg.MemberData ? "bot.ov.memberOn" : "bot.ov.memberOff")),
                    (ctx.T("bot.ov.kConsent"), ctx.T(cfg.Consent ? "bot.ov.consentOn" : "bot.ov.consentOff")),
                    (ctx.T("bot.ov.kBubble"), ctx.T(cfg.Bubble ? "bot.ov.bubbleOn" : "bot.ov.bubbleOff"))))), ctx.T("bot.ov.title"));
    }

    public static async Task<PluginResult> ChatsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = BotConfig.From(ctx.Settings);
        var chats = ctx.Data.Table("chats");
        if (ctx.Request.IsPost && ctx.Request.Action == "delete-all")
        {
            await chats.DeleteWhereAsync(DataQuery.Everything, ct);
            return PluginResult.Redirect(Section + "/chats", ctx.T("bot.chats.deleted"));
        }

        var rows = await chats.QueryAsync(DataQuery.Everything.OrderBy("created", descending: true).Page(0, 3000), ct);
        var sessions = rows.GroupBy(r => r.Text("session"))
            .Select(g => (Session: g.Key, Last: g.Max(r => r.Timestamp("created") ?? DateTime.MinValue), First: g.OrderBy(r => r.Timestamp("created")).First(),
                          Count: g.Count(), Member: g.Max(r => r.Int("member_id"))))
            .OrderByDescending(s => s.Last).ToList();

        var retention = Ui.Button(ctx.T("bot.chats.retention"), Section + "/_settings?group=" + Uri.EscapeDataString("bot.set.gHistory"), UiButtonKind.Ghost, "ti-settings", small: true);
        // Nothing stored and nothing kept: one empty state with the way to switch it on — not an explaining banner above an empty search and table.
        if (cfg.KeepDays <= 0 && sessions.Count == 0)
            return PluginResult.Page(Ui.Card(Ui.Empty(ctx.T("bot.chats.off")), retention), ctx.T("bot.tab.chats"));

        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("bot.chats.when")), Ui.Column(ctx.T("bot.chats.who")), Ui.Column(ctx.T("bot.chats.question")), Ui.Column(ctx.T("bot.chats.turns"), right: true), Ui.Column("", actions: true) },
            sessions.Select(s => Ui.RowFor(s.First.Text("question"),
                s.Last.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                s.Member > 0 ? ctx.T("bot.chats.member", s.Member) : ctx.T("bot.chats.guest"),
                Cut(s.First.Text("question"), 120),
                s.Count.ToString(CultureInfo.InvariantCulture),
                Ui.Button(ctx.T("bot.chats.open"), Section + "/chats/" + s.Session, UiButtonKind.Ghost, small: true))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 30, emptyText: ctx.T("bot.chats.none")) with
        {
            // how long they are kept is a note under the list, not a banner above it
            Below = new UiNode[] { Ui.Muted(cfg.KeepDays > 0 ? ctx.T("bot.chats.kept", cfg.KeepDays) : ctx.T("bot.chats.off")), retention }
        };

        return PluginResult.Page(Ui.Stack(
            sessions.Count > 0 ? Ui.PageHead(null, ctx.T("bot.tab.chats"),
                Ui.Action(ctx.T("bot.chats.deleteAll"), "delete-all", "", UiButtonKind.Danger, confirm: ctx.T("bot.chats.confirmDeleteAll"), icon: "ti-trash")) : null,
            table), ctx.T("bot.tab.chats"));
    }

    public static async Task<PluginResult> ChatAsync(IPluginContext ctx, CancellationToken ct)
    {
        var session = ctx.Request.R("session");
        var chats = ctx.Data.Table("chats");
        if (ctx.Request.IsPost && ctx.Request.Action == "delete")
        {
            await chats.DeleteWhereAsync(DataQuery.Everything.Eq("session", session), ct);
            return PluginResult.Redirect(Section + "/chats", ctx.T("bot.chats.deleted"));
        }
        var rows = await chats.QueryAsync(DataQuery.Everything.Eq("session", session).OrderBy("created").Page(0, 500), ct);
        if (rows.Count == 0) return PluginResult.Redirect(Section + "/chats");

        var turns = rows.Select(r =>
        {
            var sources = (r.Json("sources") as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(s => (Title: s["title"]?.GetValue<string>() ?? "", Url: s["url"]?.GetValue<string>() ?? "")).Where(s => s.Url.StartsWith('/')).ToList();
            return (UiNode)Ui.Card(
                (r.Timestamp("created") ?? DateTime.UtcNow).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " · " + ctx.T("bot.chats.tokens", N(ctx, r.Int("tokens"))),
                Ui.KeyValue((ctx.T("bot.chats.q"), r.Text("question")), (ctx.T("bot.chats.a"), r.Text("answer"))),
                sources.Count == 0 ? null : Ui.Inline(new UiNode?[] { Ui.Muted(ctx.T("bot.chats.sources") + ":") }.Concat(sources.Select(s => (UiNode?)Ui.Link(s.Title, s.Url, newTab: true))).ToArray()));
        }).ToArray();

        return PluginResult.Page(Ui.Stack(new UiNode?[]
        {
            Ui.PageHead(rows[0].Int("member_id") > 0 ? ctx.T("bot.chats.member", rows[0].Int("member_id")) : ctx.T("bot.chats.guest"), ctx.T("bot.tab.chats"),
                Ui.Button(ctx.T("bot.chats.back"), Section + "/chats", UiButtonKind.Ghost, "ti-arrow-left", small: true),
                Ui.Action(ctx.T("bot.chats.delete"), "delete", session, UiButtonKind.Danger, confirm: ctx.T("bot.chats.confirmDelete"), icon: "ti-trash"))
        }.Concat(turns).ToArray()), ctx.T("bot.tab.chats"));
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
