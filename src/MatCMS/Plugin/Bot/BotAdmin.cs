using System.Globalization;
using System.Text.Json.Nodes;
using MatCMS.Plugin;

namespace MatCMS.Plugin.Bot;

/// <summary>The bot's back office: what it costs and whether it can answer at all (overview), and the stored conversations when the operator keeps them.
/// Every screen is a tree of library components.</summary>
internal static class BotAdmin
{
    public const string Section = "/admin/p/bot";

    private static string N(long v) => v.ToString("N0", CultureInfo.GetCultureInfo("de-DE"));

    public static async Task<PluginResult> OverviewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var cfg = BotConfig.From(ctx.Settings);
        var store = new BotStore(ctx.Data);
        var (todayAsked, todayTokens) = await store.ReadAsync(BotStore.Today, BotStore.SiteKey, ct);
        var (_, monthTokens) = await store.ReadAsync(BotStore.Month, BotStore.SiteKey, ct);
        var monthHint = cfg.PerMonthTokens > 0 ? ctx.T("bot.ov.ofLimit", N(cfg.PerMonthTokens)) : ctx.T("bot.ov.noLimit");
        var monthTone = cfg.PerMonthTokens > 0 && monthTokens >= cfg.PerMonthTokens ? UiTone.Error
            : cfg.PerMonthTokens > 0 && monthTokens >= cfg.PerMonthTokens * 0.8 ? UiTone.Warning : UiTone.Default;

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(ctx.T("bot.ov.sub"), ctx.T("bot.ov.title"),
                Ui.Button(ctx.T("bot.ov.settings"), Section + "/_settings", UiButtonKind.Ghost, "ti-settings", small: true),
                Ui.Button(ctx.T("bot.ov.try"), "/", UiButtonKind.Ghost, "ti-external-link", small: true) with { NewTab = true }),
            ctx.Site.AiEnabled ? Ui.Alert(ctx.T("bot.ov.aiOn"), UiTone.Success) : Ui.Alert(ctx.T("bot.ov.aiOff"), UiTone.Warning),
            Ui.Stats(
                Ui.Stat(ctx.T("bot.ov.today"), N(todayAsked)),
                Ui.Stat(ctx.T("bot.ov.tokensToday"), N(todayTokens)),
                Ui.Stat(ctx.T("bot.ov.month"), N(monthTokens), monthHint, monthTone)),
            Ui.Alert(cfg.MemberData ? ctx.T("bot.ov.memberOn") : ctx.T("bot.ov.memberOff"), cfg.MemberData ? UiTone.Info : UiTone.Default),
            Ui.Alert(cfg.Consent ? ctx.T("bot.ov.consentOn") : ctx.T("bot.ov.consentOff"), cfg.Consent ? UiTone.Success : UiTone.Default),
            cfg.Bubble ? null : Ui.Alert(ctx.T("bot.ov.bubbleOff"), UiTone.Info)), ctx.T("bot.ov.title"));
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

        var table = Ui.Table(
            new[] { Ui.Column(ctx.T("bot.chats.when")), Ui.Column(ctx.T("bot.chats.who")), Ui.Column(ctx.T("bot.chats.question")), Ui.Column(ctx.T("bot.chats.turns"), right: true), Ui.Column("", actions: true) },
            sessions.Select(s => Ui.RowFor(s.First.Text("question"),
                s.Last.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                s.Member > 0 ? ctx.T("bot.chats.member", s.Member) : ctx.T("bot.chats.guest"),
                Cut(s.First.Text("question"), 120),
                s.Count.ToString(CultureInfo.InvariantCulture),
                Ui.Button(ctx.T("bot.chats.open"), Section + "/chats/" + s.Session, UiButtonKind.Ghost, small: true))),
            searchPlaceholder: ctx.T("action.search"), pageSize: 30, emptyText: ctx.T("bot.chats.none"));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("bot.tab.chats"),
                sessions.Count > 0 ? Ui.Action(ctx.T("bot.chats.deleteAll"), "delete-all", "", UiButtonKind.Danger, confirm: ctx.T("bot.chats.confirmDeleteAll"), icon: "ti-trash") : null),
            cfg.KeepDays > 0 ? Ui.Alert(ctx.T("bot.chats.kept", cfg.KeepDays), UiTone.Info) : Ui.Alert(ctx.T("bot.chats.off"), UiTone.Default),
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
                (r.Timestamp("created") ?? DateTime.UtcNow).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " · " + ctx.T("bot.chats.tokens", N(r.Int("tokens"))),
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
