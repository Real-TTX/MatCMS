using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen → Kanäle: per unit the calendar addresses to hand out and the portals' calendars to read. A portal gets ITS OWN address (without the
/// nights it sent itself); the unit's own address is for everything else (a phone, a cleaning service). A calendar may also be pasted once instead of
/// fetched (an export from an old system).
/// </summary>
public static class BookingAdminChannels
{
    private static string R => BookingAdmin.Root;
    public static readonly string[] Portals = { "Airbnb", "Booking.com", "Vrbo", "Rentlio", "Google Kalender", "Andere" };

    public static IAdminSection Register(IAdminSection s) => s.Page("channels", null, PageAsync);

    private static string Url(IPluginContext ctx, string token) => (ctx.Site.Origin ?? "").TrimEnd('/') + BookingIcal.ExportPath + token + ".ics";

    private static async Task<PluginResult> PageAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        var self = R + "channels";
        var f = ctx.Request;
        if (f.IsPost)
        {
            long.TryParse(f.F("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id);
            switch (f.Action)
            {
                case "add":
                {
                    var unit = units.FirstOrDefault(u => B.Num(u.Id) == f.F("unit"));
                    if (unit is null) return PluginResult.Redirect(self, ctx.T("booking.admin.chooseUnit"), true);
                    var name = Portals.Contains(f.F("portal")) && f.F("portal") != "Andere" ? f.F("portal") : B.Clip(f.F("name"), 60) is { Length: > 0 } n ? n : "Kalender";
                    var url = f.F("url").Trim();
                    var text = f.F("ics");
                    if (url.Length == 0 && text.Trim().Length == 0) return PluginResult.Redirect(self, ctx.T("booking.admin.feedUrlNeeded"), true);
                    if (url.Length > 0)
                        try { BookingIcal.Check(url); }
                        catch (HttpRequestException ex) { return PluginResult.Redirect(self, BookingStrings.Shown(ctx.T, ex.Message), true); }
                    var feedId = await ctx.Data.Table(BookingStore.TFeeds).InsertAsync(new Dictionary<string, object?>
                    {
                        ["unit_id"] = unit.Id, ["name"] = name, ["url"] = B.Clip(url, 1000), ["export_token"] = BookingIcal.NewToken(), ["created"] = DateTime.UtcNow
                    }, ct);
                    var feed = (await BookingIcal.FeedAsync(ctx.Data, feedId, ct))!;
                    await BookingIcal.RunAsync(ctx, feed, ct, url.Length == 0 ? text : null);
                    var after = (await BookingIcal.FeedAsync(ctx.Data, feedId, ct))!;
                    return after.LastError.Length > 0
                        ? PluginResult.Redirect(self, ctx.T("booking.admin.feedAddedNotRead", name, BookingStrings.Shown(ctx.T, after.LastError)), true)
                        : PluginResult.Redirect(self, ctx.T(after.Conflicts.Length > 0 ? "booking.admin.feedReadConflicts" : "booking.admin.feedRead", name, after.Events));
                }
                case "run" when await BookingIcal.FeedAsync(ctx.Data, id, ct) is { } feed:
                {
                    var text = f.F("ics");
                    if (feed.Url.Length == 0 && text.Trim().Length == 0) return PluginResult.Redirect(self, ctx.T("booking.admin.feedNoUrl"), true);
                    await BookingIcal.RunAsync(ctx, feed, ct, feed.Url.Length == 0 ? text : null);
                    var after = (await BookingIcal.FeedAsync(ctx.Data, id, ct))!;
                    return after.LastError.Length > 0 ? PluginResult.Redirect(self, feed.Name + ": " + BookingStrings.Shown(ctx.T, after.LastError), true) : PluginResult.Redirect(self, ctx.T("booking.admin.feedRead", feed.Name, after.Events));
                }
                case "remove":
                    await BookingIcal.RemoveFeedAsync(ctx.Data, id, ct);
                    return PluginResult.Redirect(self, ctx.T("booking.admin.feedRemoved"));
                case "renew" when units.Any(u => u.Id == id):
                    await BookingIcal.RenewTokensAsync(ctx.Data, id, ct);
                    return PluginResult.Redirect(self, ctx.T("booking.admin.tokensRenewed"));
            }
            return PluginResult.Redirect(self);
        }

        var feeds = await BookingIcal.FeedsAsync(ctx.Data, ct);
        var cards = new List<UiNode?>();
        foreach (var u in units)
        {
            var mine = feeds.Where(x => x.UnitId == u.Id).ToList();
            var own = Url(ctx, await BookingIcal.UnitTokenAsync(ctx.Data, u.Id, ct));
            cards.Add(Ui.Card(u.Name, Ui.Stack(
                Ui.KeyValue((ctx.T("booking.admin.unitCalendar"), own)),
                mine.Count == 0 ? Ui.Empty(ctx.T("booking.admin.noPortal")) : Ui.Table(
                    new[] { Ui.Column(ctx.T("booking.admin.portal")), Ui.Column(ctx.T("booking.admin.colState")), Ui.Column(ctx.T("booking.admin.colOccupations"), right: true), Ui.Column(ctx.T("booking.admin.colPortalUrl")), Ui.Column("", actions: true) },
                    mine.Select(x => Ui.Row(
                        Ui.Stack(Ui.Text(x.Name), x.Url.Length > 0 ? Ui.Muted(x.Url.Length > 60 ? x.Url[..60] + "…" : x.Url) : Ui.Muted(ctx.T("booking.admin.pasted")),
                            x.Conflicts.Length > 0 ? Ui.Alert(BookingIcal.ConflictsText(ctx.T, x.Conflicts), UiTone.Error) : null),
                        x.LastError.Length > 0 ? Ui.Badge(ctx.T("booking.admin.error", BookingStrings.Shown(ctx.T, x.LastError)), UiTone.Error)
                            : x.LastOk is null ? Ui.Badge(ctx.T("booking.admin.notReadYet"), UiTone.Warning) : Ui.Badge(ctx.T("booking.admin.readAt", BookingMail.Local(x.LastOk)), UiTone.Success),
                        B.Num(x.Events),
                        Ui.Muted(Url(ctx, x.ExportToken)),
                        Ui.Inline(Ui.Action(ctx.T("booking.admin.readNow"), "run", B.Num(x.Id), icon: "ti-refresh"),
                            Ui.Action(ctx.T("booking.admin.remove"), "remove", B.Num(x.Id), confirm: ctx.T("booking.admin.confirmRemoveFeed"), icon: "ti-trash"))))),
                Ui.Action(ctx.T("booking.admin.renewTokens"), "renew", B.Num(u.Id), confirm: ctx.T("booking.admin.confirmRenewTokens"), icon: "ti-key") with { WithLabel = true })));
        }

        var add = units.Count == 0 ? null : Ui.Card(ctx.T("booking.admin.connectPortal"), Ui.Form(
            Ui.Grid(200,
                Ui.Select("unit", ctx.T("booking.admin.unit"), B.Num(units[0].Id), units.Select(x => Ui.Option(B.Num(x.Id), x.Name)).ToArray()),
                // the values are stored as the feed's name; only the two German words are shown in the admin's language
                Ui.Select("portal", ctx.T("booking.admin.portal"), Portals[0], Portals.Select(p => Ui.Option(p, p switch
                {
                    "Google Kalender" => ctx.T("booking.admin.portalGoogle"), "Andere" => ctx.T("booking.admin.portalOther"), _ => p
                })).ToArray()),
                Ui.Input("name", ctx.T("booking.admin.portalName"))),
            Ui.Input("url", ctx.T("booking.admin.feedUrl"), "", UiInputType.Url)
                with { Help = ctx.T("booking.admin.feedUrlHelp") },
            Ui.TextArea("ics", ctx.T("booking.admin.pasteIcs"), "", 3),
            Ui.FormActions(Ui.Submit(ctx.T("booking.admin.connectRead"), icon: "ti-plug-connected"))));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.navChannels")),
            Ui.Alert(ctx.T("booking.admin.channelsNote"), UiTone.Info),
            Ui.Columns(cards, new UiNode?[] { add })), ctx.T("booking.admin.navChannels"));
    }
}
