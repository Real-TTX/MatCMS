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
                    if (unit is null) return PluginResult.Redirect(self, "Bitte eine Einheit wählen.", true);
                    var name = Portals.Contains(f.F("portal")) && f.F("portal") != "Andere" ? f.F("portal") : B.Clip(f.F("name"), 60) is { Length: > 0 } n ? n : "Kalender";
                    var url = f.F("url").Trim();
                    var text = f.F("ics");
                    if (url.Length == 0 && text.Trim().Length == 0) return PluginResult.Redirect(self, "Bitte die Kalender-Adresse des Portals eingeben (oder einen Kalender einfügen).", true);
                    if (url.Length > 0)
                        try { BookingIcal.Check(url); }
                        catch (HttpRequestException ex) { return PluginResult.Redirect(self, ex.Message, true); }
                    var feedId = await ctx.Data.Table(BookingStore.TFeeds).InsertAsync(new Dictionary<string, object?>
                    {
                        ["unit_id"] = unit.Id, ["name"] = name, ["url"] = B.Clip(url, 1000), ["export_token"] = BookingIcal.NewToken(), ["created"] = DateTime.UtcNow
                    }, ct);
                    var feed = (await BookingIcal.FeedAsync(ctx.Data, feedId, ct))!;
                    await BookingIcal.RunAsync(ctx, feed, ct, url.Length == 0 ? text : null);
                    var after = (await BookingIcal.FeedAsync(ctx.Data, feedId, ct))!;
                    return after.LastError.Length > 0
                        ? PluginResult.Redirect(self, name + " angelegt, aber noch nicht gelesen: " + after.LastError, true)
                        : PluginResult.Redirect(self, name + " gelesen: " + after.Events + " Belegung(en)" + (after.Conflicts.Length > 0 ? " – mit Überschneidungen, siehe unten." : "."));
                }
                case "run" when await BookingIcal.FeedAsync(ctx.Data, id, ct) is { } feed:
                {
                    var text = f.F("ics");
                    if (feed.Url.Length == 0 && text.Trim().Length == 0) return PluginResult.Redirect(self, "Dieser Kalender hat keine Adresse – bitte den Kalender-Inhalt einfügen.", true);
                    await BookingIcal.RunAsync(ctx, feed, ct, feed.Url.Length == 0 ? text : null);
                    var after = (await BookingIcal.FeedAsync(ctx.Data, id, ct))!;
                    return after.LastError.Length > 0 ? PluginResult.Redirect(self, feed.Name + ": " + after.LastError, true) : PluginResult.Redirect(self, feed.Name + " gelesen: " + after.Events + " Belegung(en).");
                }
                case "remove":
                    await BookingIcal.RemoveFeedAsync(ctx.Data, id, ct);
                    return PluginResult.Redirect(self, "Kalender entfernt – seine Belegungen sind wieder frei.");
                case "renew" when units.Any(u => u.Id == id):
                    await BookingIcal.RenewTokensAsync(ctx.Data, id, ct);
                    return PluginResult.Redirect(self, "Neue Adressen erzeugt. Die alten gehen nicht mehr – bitte die neuen bei den Portalen eintragen.");
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
                Ui.KeyValue(("Kalender dieser Einheit", own)),
                mine.Count == 0 ? Ui.Empty("Noch kein Portal verbunden.") : Ui.Table(
                    new[] { Ui.Column("Portal"), Ui.Column("Stand"), Ui.Column("Belegungen", right: true), Ui.Column("Adresse für das Portal"), Ui.Column("", actions: true) },
                    mine.Select(x => Ui.Row(
                        Ui.Stack(Ui.Text(x.Name), x.Url.Length > 0 ? Ui.Muted(x.Url.Length > 60 ? x.Url[..60] + "…" : x.Url) : Ui.Muted("eingefügt, nicht abgerufen"),
                            x.Conflicts.Length > 0 ? Ui.Alert(x.Conflicts.Replace("\n", " · "), UiTone.Error) : null),
                        x.LastError.Length > 0 ? Ui.Badge("Fehler: " + x.LastError, UiTone.Error)
                            : x.LastOk is null ? Ui.Badge("noch nicht gelesen", UiTone.Warning) : Ui.Badge("gelesen " + BookingMail.Local(x.LastOk), UiTone.Success),
                        B.Num(x.Events),
                        Ui.Muted(Url(ctx, x.ExportToken)),
                        Ui.Inline(Ui.Action("Jetzt lesen", "run", B.Num(x.Id), icon: "ti-refresh"),
                            Ui.Action("Entfernen", "remove", B.Num(x.Id), confirm: "Kalender entfernen? Seine Belegungen werden hier wieder frei.", icon: "ti-trash"))))),
                Ui.Action("Neue Adressen erzeugen", "renew", B.Num(u.Id), confirm: "Alle Kalender-Adressen dieser Einheit ändern? Die alten gehen danach nicht mehr – bei jedem Portal neu eintragen.", icon: "ti-key") with { WithLabel = true })));
        }

        var add = units.Count == 0 ? null : Ui.Card("Portal verbinden", Ui.Form(
            Ui.Grid(200,
                Ui.Select("unit", "Einheit", B.Num(units[0].Id), units.Select(x => Ui.Option(B.Num(x.Id), x.Name)).ToArray()),
                Ui.Select("portal", "Portal", Portals[0], Portals.Select(p => Ui.Option(p, p)).ToArray()),
                Ui.Input("name", "Name (bei „Andere“)")),
            Ui.Input("url", "Kalender-Adresse des Portals (https://…/….ics)", "", UiInputType.Url)
                with { Help = "Export-Adresse aus dem Portal (Airbnb: Kalender → Verfügbarkeit). Danach die Adresse aus der Tabelle dort als „Kalender importieren“ eintragen." },
            Ui.TextArea("ics", "… oder einmalig einen Kalender einfügen (Inhalt einer .ics-Datei)", "", 3),
            Ui.FormActions(Ui.Submit("Verbinden und lesen", icon: "ti-plug-connected"))));

        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, "Kanäle"),
            Ui.Alert("Zwischen zwei Abrufen eines Portals ist eine Doppelbuchung möglich – sie wird hier rot gemeldet.", UiTone.Info),
            Ui.Columns(cards, new UiNode?[] { add })), "Kanäle");
    }
}
