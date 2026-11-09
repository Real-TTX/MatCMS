using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen (phase B1): houses, units (with their blocks), seasons with the price table of every unit, fees and tourist tax, and "Preis prüfen" —
/// the same bill a guest will get, with the reasons when a stay does not fit. Requests, guests and the occupancy chart come in B2/B3.
/// </summary>
public static class BookingAdmin
{
    public const string Root = "/admin/p/booking/";

    public static void Configure(IPluginBuilder b)
    {
        var section = b.Admin.Section("Buchungen", "ti-calendar-check", AdminGroup.Content, 26)
            .Page("dashboard", null, DashboardAsync)
            .Page("properties", null, PropertiesAsync)
            .Page("properties/{id}", null, PropertyAsync)
            .Page("units", null, UnitsAsync)
            .Page("units/{id}", null, UnitAsync)
            .Page("seasons", null, SeasonsAsync)
            .Page("seasons/{id}", null, SeasonAsync)
            .Page("fees", null, FeesAsync)
            .Page("fees/{id}", null, FeeAsync)
            .Page("check", null, CheckAsync)
            .Link("Übersicht", "dashboard", "ti-layout-dashboard")
            .Link("Anfragen & Buchungen", "bookings", "ti-calendar-event", badge: true, also: new[] { "bookings/" })
            .Link("Belegungsplan", "plan", "ti-layout-kanban")
            .Link("Gäste", "guests", "ti-address-book", also: new[] { "guests/" })
            .Link("Kanäle", "channels", "ti-arrows-exchange")
            .Link("Einheiten", "units", "ti-bed", also: new[] { "units/" })
            .Link("Saisons & Preise", "seasons", "ti-calendar-dollar", also: new[] { "seasons/" })
            .Link("Gebühren & Kurtaxe", "fees", "ti-receipt", also: new[] { "fees/" })
            .Link("Preis prüfen", "check", "ti-calculator")
            .Link("Objekte", "properties", "ti-building-cottage", heading: "Einrichtung", also: new[] { "properties/" })
            .Link("Einstellungen", "_settings", "ti-settings")
            .Badge(async (c, ct) => (await BookingFlow.ListAsync(c.Data, new[] { BookingStatus.Request }, ct)).Count is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);
        BookingAdminBookings.Register(section);
        BookingAdminMore.Register(section);
        BookingAdminChannels.Register(section);
    }

    private static long Id(IPluginContext ctx) => long.TryParse(ctx.Request.R("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    private static bool IsNew(IPluginContext ctx) => ctx.Request.R("id") == "new";
    private static int Int(string s, int def, int min, int max) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;
    private static string Cur(IPluginContext ctx) => BookingPlugin.Currency(ctx);

    /// <summary>The house chosen by <c>?objekt=</c>, else the first. Most sites have one, so the choice only shows when there are more.</summary>
    private static Property? Chosen(IPluginContext ctx, List<Property> all) =>
        all.FirstOrDefault(p => B.Num(p.Id) == ctx.Request.Q("objekt")) ?? all.FirstOrDefault();

    private static UiNode? HouseTabs(List<Property> all, Property? current, string page) =>
        all.Count < 2 ? null : Ui.Tabs(all.Select(p => Ui.Tab(p.Name, Root + page + "?objekt=" + p.Id, p.Id == current?.Id)).ToArray());

    private static PluginResult NoHouse(string title) => PluginResult.Page(Ui.Stack(
        Ui.PageHead("Zuerst ein Objekt anlegen – das Haus oder die Villa, zu der die Einheiten gehören", title),
        Ui.Card(Ui.Empty("Noch kein Objekt."), Ui.Button("Objekt anlegen", Root + "properties/new", icon: "ti-plus"))), title);

    // ---- overview ---------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DashboardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var units = await store.UnitsAsync(ct);
        var steps = new List<(string Text, bool Done, string Href)>
        {
            ("Objekt anlegen (Haus, Villa)", houses.Count > 0, Root + "properties"),
            ("Einheiten anlegen (Apartments, Zimmer, ganzes Haus)", units.Count > 0, Root + "units"),
        };
        var all = await BookingFlow.ListAsync(ctx.Data, null, ct);
        var open = all.Count(x => x.Status is BookingStatus.Request or BookingStatus.Offer);
        var coming = all.Count(x => x.Status == BookingStatus.Confirmed && x.Departure >= B.Today);
        var seasons = 0; var fees = 0;
        foreach (var h in houses) { seasons += (await store.SeasonsAsync(h.Id, ct)).Count; fees += (await store.FeesAsync(h.Id, ct)).Count; }
        steps.Add(("Saisons und Preise festlegen", seasons > 0, Root + "seasons"));
        steps.Add(("Gebühren und Kurtaxe (Endreinigung, Bettwäsche …)", fees > 0, Root + "fees"));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Anfragen, Buchungen, Häuser, Saisons und Preise – Gästekartei und Kalender-Abgleich folgen", "Buchungen"),
            Ui.Stats(
                Ui.Stat("Offene Anfragen", B.Num(open), tone: open > 0 ? UiTone.Warning : UiTone.Success, href: Root + "bookings"),
                Ui.Stat("Kommende Buchungen", B.Num(coming), href: Root + "bookings?tab=kommend"),
                Ui.Stat("Objekte", B.Num(houses.Count), href: Root + "properties"),
                Ui.Stat("Einheiten", B.Num(units.Count(u => u.Active)), units.Any(u => !u.Active) ? units.Count(u => !u.Active) + " inaktiv" : null, href: Root + "units"),
                Ui.Stat("Saisons", B.Num(seasons), href: Root + "seasons")),
            Ui.Card("Einrichten", Ui.Stack(steps.Select(s => (UiNode?)Ui.Inline(
                Ui.Badge(s.Done ? "erledigt" : "offen", s.Done ? UiTone.Success : UiTone.Warning), Ui.Link(s.Text, s.Href))).ToArray()),
                Ui.Button("Einen Aufenthalt durchrechnen", Root + "check", UiButtonKind.Ghost, "ti-calculator", small: true))), "Buchungen");
    }

    // ---- houses -----------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> PropertiesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var units = await store.UnitsAsync(ct);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Das Haus oder die Villa: Adresse, Land (für Kurtaxe und Meldedaten), Check-in- und Check-out-Zeit", "Objekte"),
            Ui.Table(new[] { Ui.Column("Objekt"), Ui.Column("Ort"), Ui.Column("Einheiten", right: true) },
                houses.Select(h => Ui.RowFor(h.Name, Ui.Link(h.Name, Root + "properties/" + h.Id), h.Address, B.Num(units.Count(u => u.PropertyId == h.Id)))),
                emptyText: "Noch kein Objekt.") with { Below = new UiNode[] { Ui.Button("Neues Objekt", Root + "properties/new", icon: "ti-plus") } }), "Objekte");
    }

    private static async Task<PluginResult> PropertyAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var isNew = IsNew(ctx);
        var p = isNew ? null : await store.PropertyAsync(Id(ctx), ct);
        if (!isNew && p is null) return PluginResult.NotFound();
        if (ctx.Request.IsPost)
        {
            if (p is not null && ctx.Request.Action == "delete")
            {
                if ((await store.UnitsAsync(ct)).Any(u => u.PropertyId == p.Id)) return PluginResult.Redirect(Root + "properties/" + p.Id, "Zuerst die Einheiten dieses Objekts löschen oder verschieben.", true);
                foreach (var s in await store.SeasonsAsync(p.Id, ct)) await store.DeleteSeasonAsync(s.Id, ct);
                foreach (var f in await store.FeesAsync(p.Id, ct)) await store.Fees.DeleteAsync(f.Id, ct);
                await store.Properties.DeleteAsync(p.Id, ct);
                return PluginResult.Redirect(Root + "properties", "Objekt gelöscht.");
            }
            var name = B.Clip(ctx.Request.F("name"), 120);
            if (name.Length == 0) return PluginResult.Redirect(Root + "properties/" + (p?.Id.ToString(CultureInfo.InvariantCulture) ?? "new"), "Bitte einen Namen eingeben.", true);
            var values = new Dictionary<string, object?>
            {
                ["name"] = name, ["slug"] = await store.FreeSlugAsync(BookingStore.TProperties, p?.Slug ?? name, p?.Id ?? 0, ct),
                ["address"] = B.Clip(ctx.Request.F("address"), 300), ["country"] = B.Clip(ctx.Request.F("country"), 2).ToUpperInvariant(),
                ["check_in"] = B.Clip(ctx.Request.F("check_in"), 5), ["check_out"] = B.Clip(ctx.Request.F("check_out"), 5),
                ["description"] = B.Clip(ctx.Request.F("description"), 4000), ["sort"] = Int(ctx.Request.F("sort"), 0, -999, 999)
            };
            var id = p?.Id ?? 0;
            if (p is null) id = await store.Properties.InsertAsync(values, ct); else await store.Properties.UpdateAsync(p.Id, values, ct);
            return PluginResult.Redirect(Root + "properties/" + id, "Gespeichert.");
        }
        var form = Ui.Form(
            Ui.Input("name", "Name", p?.Name ?? "") with { Help = "z. B. „Villa Nika“" },
            Ui.TextArea("address", "Adresse", p?.Address ?? "", 2),
            Ui.Grid(160,
                Ui.Select("country", "Land", p?.Country ?? "HR", Countries.Select(c => Ui.Option(c.Code, c.Name)).ToArray()),
                Ui.Input("check_in", "Check-in ab", p?.CheckIn ?? "15:00"), Ui.Input("check_out", "Check-out bis", p?.CheckOut ?? "10:00"),
                Ui.Input("sort", "Reihenfolge", B.Num(p?.Sort ?? 0), UiInputType.Number)),
            Ui.TextArea("description", "Beschreibung (intern, für die Gästemails später)", p?.Description ?? "", 4),
            Ui.FormActions(Ui.Submit("Speichern"), p is null ? null : Ui.Submit("Löschen", UiButtonKind.Danger, "action", "delete", confirm: "Objekt mit Saisons und Gebühren löschen?")));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(null, p?.Name ?? "Neues Objekt", Ui.Button("Alle Objekte", Root + "properties", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form)), p?.Name ?? "Neues Objekt");
    }

    internal static readonly (string Code, string Name)[] Countries =
    {
        ("HR", "Kroatien"), ("DE", "Deutschland"), ("AT", "Österreich"), ("IT", "Italien"), ("ES", "Spanien"), ("PT", "Portugal"), ("FR", "Frankreich"),
        ("GR", "Griechenland"), ("SI", "Slowenien"), ("ME", "Montenegro"), ("CH", "Schweiz"), ("NL", "Niederlande"), ("DK", "Dänemark")
    };

    // ---- units ------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> UnitsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        if (houses.Count == 0) return NoHouse("Einheiten");
        var units = await store.UnitsAsync(ct);
        string House(long id) => houses.FirstOrDefault(h => h.Id == id)?.Name ?? "–";
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Was gebucht wird: ein Apartment, ein Zimmer oder das ganze Haus", "Einheiten"),
            Ui.Table(new[] { Ui.Column("Einheit"), Ui.Column("Objekt"), Ui.Column("Personen"), Ui.Column("Grundpreis", right: true), Ui.Column("Status") },
                units.Select(u => Ui.RowFor(u.Name + " " + House(u.PropertyId), Ui.Inline(Ui.Link(u.Name, Root + "units/" + u.Id), u.Contains.Count > 0 ? Ui.Badge("ganzes Haus", UiTone.Info) : null),
                    House(u.PropertyId), u.GuestsStd == u.GuestsMax ? B.Num(u.GuestsMax) : u.GuestsStd + "–" + u.GuestsMax, B.Money(u.BasePrice, Cur(ctx)) + " / Nacht",
                    u.Active ? Ui.Badge("buchbar", UiTone.Success) : Ui.Badge("inaktiv"))),
                emptyText: "Noch keine Einheit.") with { Below = new UiNode[] { Ui.Button("Neue Einheit", Root + "units/new", icon: "ti-plus") } }), "Einheiten");
    }

    private static async Task<PluginResult> UnitAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        if (houses.Count == 0) return NoHouse("Einheit");
        var isNew = IsNew(ctx);
        var all = await store.UnitsAsync(ct);
        var u = isNew ? null : all.FirstOrDefault(x => x.Id == Id(ctx));
        if (!isNew && u is null) return PluginResult.NotFound();
        var self = Root + "units/" + (u?.Id.ToString(CultureInfo.InvariantCulture) ?? "new");

        if (ctx.Request.IsPost)
        {
            switch (ctx.Request.Action)
            {
                case "delete" when u is not null:
                    if ((await store.NightsAsync(new[] { u.Id }, DateOnly.MinValue, DateOnly.MaxValue, ct)).Count > 0)
                        return PluginResult.Redirect(self, "Die Einheit hat noch belegte Nächte – zuerst die Sperren entfernen (oder die Einheit auf inaktiv setzen).", true);
                    await store.Units.DeleteAsync(u.Id, ct);
                    foreach (var other in all.Where(o => o.Contains.Contains(u.Id)))
                        await store.Units.UpdateAsync(other.Id, new Dictionary<string, object?> { ["contains"] = string.Join(",", other.Contains.Where(c => c != u.Id)) }, ct);
                    return PluginResult.Redirect(Root + "units", "Einheit gelöscht.");
                case "block" when u is not null:
                    if (B.Day(ctx.Request.F("from")) is not { } bf || B.Day(ctx.Request.F("to")) is not { } bt || bt <= bf)
                        return PluginResult.Redirect(self, "Bitte Von und Bis angeben (Bis = der Tag, ab dem wieder frei ist).", true);
                    try { await store.AddBlockAsync(u, bf, bt, ctx.Request.F("reason"), "manual", ct); }
                    catch (UnavailableException ex) { return PluginResult.Redirect(self, ex.Message, true); }
                    return PluginResult.Redirect(self, "Gesperrt.");
                case "unblock" when u is not null:
                    if (long.TryParse(ctx.Request.F("id"), out var blockId) && (await store.BlocksAsync(u.Id, ct)).Any(x => x.Id == blockId)) await store.RemoveBlockAsync(blockId, ct);
                    return PluginResult.Redirect(self, "Sperre entfernt.");
            }
            var name = B.Clip(ctx.Request.F("name"), 120);
            if (name.Length == 0) return PluginResult.Redirect(self, "Bitte einen Namen eingeben.", true);
            var house = houses.FirstOrDefault(h => B.Num(h.Id) == ctx.Request.F("property_id")) ?? houses[0];
            var std = Int(ctx.Request.F("guests_std"), 2, 1, 50);
            var max = Math.Max(std, Int(ctx.Request.F("guests_max"), std, 1, 50));
            // only units of the same house, never itself, never a "whole house" (one level)
            var contains = all.Where(o => o.Id != u?.Id && o.PropertyId == house.Id && o.Contains.Count == 0 && B.Checked(ctx.Request.F("contains_" + o.Id))).Select(o => B.Num(o.Id));
            var values = new Dictionary<string, object?>
            {
                ["property_id"] = house.Id, ["name"] = name, ["slug"] = await store.FreeSlugAsync(BookingStore.TUnits, ctx.Request.F("slug") is { Length: > 0 } sl ? sl : name, u?.Id ?? 0, ct),
                ["description"] = B.Clip(ctx.Request.F("description"), 4000), ["image"] = B.Clip(ctx.Request.F("image"), 500),
                ["bedrooms"] = Int(ctx.Request.F("bedrooms"), 0, 0, 50), ["guests_std"] = std, ["guests_max"] = max,
                ["contains"] = string.Join(",", contains), ["buffer_days"] = Int(ctx.Request.F("buffer_days"), 0, 0, 14),
                ["base_price"] = Math.Max(0, B.ParseMoney(ctx.Request.F("base_price")) ?? 0), ["base_min_nights"] = Int(ctx.Request.F("base_min_nights"), 1, 1, 60),
                ["extra_person"] = Math.Max(0, B.ParseMoney(ctx.Request.F("extra_person")) ?? 0), ["sort"] = Int(ctx.Request.F("sort"), 0, -999, 999),
                ["active"] = B.Checked(ctx.Request.F("active"))
            };
            var id = u?.Id ?? 0;
            if (u is null) id = await store.Units.InsertAsync(values, ct); else await store.Units.UpdateAsync(u.Id, values, ct);
            return PluginResult.Redirect(Root + "units/" + id, "Gespeichert.");
        }

        var houseId = u?.PropertyId ?? houses[0].Id;
        var siblings = all.Where(o => o.Id != u?.Id && o.PropertyId == houseId && o.Contains.Count == 0).ToList();
        var cur = Cur(ctx);
        var form = Ui.Form(
            Ui.Grid(220,
                Ui.Input("name", "Name", u?.Name ?? "") with { Help = "z. B. „Apartment Meerblick“ oder „Ganze Villa“" },
                houses.Count > 1 ? Ui.Select("property_id", "Objekt", B.Num(houseId), houses.Select(h => Ui.Option(B.Num(h.Id), h.Name)).ToArray()) : Ui.Hidden("property_id", B.Num(houseId)),
                Ui.Input("slug", "Adresse (Slug)", u?.Slug ?? "") with { Help = "Leer = aus dem Namen." }),
            Ui.Image("image", "Titelbild", u?.Image ?? ""),
            Ui.TextArea("description", "Beschreibung", u?.Description ?? "", 5),
            Ui.Grid(160,
                Ui.Input("bedrooms", "Schlafzimmer", B.Num(u?.Bedrooms ?? 1), UiInputType.Number),
                Ui.Input("guests_std", "Personen im Preis", B.Num(u?.GuestsStd ?? 2), UiInputType.Number),
                Ui.Input("guests_max", "Personen höchstens", B.Num(u?.GuestsMax ?? 4), UiInputType.Number),
                Ui.Input("buffer_days", "Puffertage", B.Num(u?.BufferDays ?? 0), UiInputType.Number) with { Help = "Freie Tage zwischen zwei Gästen (Reinigung). 0 = Abreise und Anreise am selben Tag." }),
            Ui.Heading("Grundpreis (außerhalb jeder Saison)", 3),
            Ui.Grid(160,
                Ui.Input("base_price", "Preis je Nacht (" + B.Symbol(cur) + ")", u is null ? "" : B.MoneyField(u.BasePrice)),
                Ui.Input("base_min_nights", "Mindestnächte", B.Num(u?.BaseMinNights ?? 1), UiInputType.Number),
                Ui.Input("extra_person", "Aufschlag je weitere Person und Nacht", u is null ? "" : B.MoneyField(u.ExtraPerson))),
            siblings.Count == 0 ? null : Ui.Stack(
                Ui.Heading("Ganzes Haus", 3),
                Ui.Muted("Ist diese Einheit das ganze Haus, die Einheiten ankreuzen, aus denen es besteht: ist eine davon belegt, ist das Haus belegt – und umgekehrt."),
                Ui.Grid(220, siblings.Select(o => (UiNode?)Ui.Checkbox("contains_" + o.Id, o.Name, u?.Contains.Contains(o.Id) ?? false)).ToArray())),
            Ui.Grid(160, Ui.Input("sort", "Reihenfolge", B.Num(u?.Sort ?? 0), UiInputType.Number)),
            Ui.Checkbox("active", "Buchbar (auf der Website sichtbar)", u?.Active ?? true),
            Ui.FormActions(Ui.Submit("Speichern"), u is null ? null : Ui.Submit("Löschen", UiButtonKind.Danger, "action", "delete", confirm: "Einheit löschen?")));

        UiNode? blocks = null;
        if (u is not null)
        {
            var list = await store.BlocksAsync(u.Id, ct);
            blocks = Ui.Card("Sperren (Eigennutzung, Renovierung)", Ui.Stack(
                Ui.Table(new[] { Ui.Column("Von"), Ui.Column("Bis (wieder frei)"), Ui.Column("Nächte", right: true), Ui.Column("Grund"), Ui.Column("", actions: true) },
                    list.Select(x => Ui.Row(B.Weekday(x.From) + " " + B.Date(x.From), B.Weekday(x.To) + " " + B.Date(x.To), B.Num(x.Nights), x.Reason,
                        Ui.Action("Entfernen", "unblock", B.Num(x.Id), confirm: "Sperre entfernen?", icon: "ti-trash"))),
                    emptyText: "Keine Sperren."),
                Ui.Form(Ui.Grid(160, Ui.Input("from", "Von", "", UiInputType.Date), Ui.Input("to", "Bis (wieder frei)", "", UiInputType.Date), Ui.Input("reason", "Grund")),
                    Ui.FormActions(Ui.Submit("Sperren", UiButtonKind.Ghost, "action", "block", icon: "ti-lock")))));
        }
        return PluginResult.Page(Ui.Stack(Ui.PageHead(null, u?.Name ?? "Neue Einheit", Ui.Button("Alle Einheiten", Root + "units", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form), blocks), u?.Name ?? "Neue Einheit");
    }

    // ---- seasons and prices -----------------------------------------------------------------------------------------

    internal static readonly (string Value, string Label)[] ArrivalChoices =
        { ("", "beliebig"), ("6", "nur Samstag"), ("7", "nur Sonntag"), ("6,7", "Samstag oder Sonntag"), ("5", "nur Freitag"), ("1", "nur Montag"), ("5,6,7", "Fr, Sa oder So") };

    private static async Task<PluginResult> SeasonsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var house = Chosen(ctx, houses);
        if (house is null) return NoHouse("Saisons & Preise");
        if (ctx.Request.IsPost && ctx.Request.Action == "copy" && int.TryParse(ctx.Request.F("year"), out var year))
        {
            var n = await store.CopySeasonsAsync(house.Id, year, ct);
            return PluginResult.Redirect(Root + "seasons?objekt=" + house.Id, n == 0 ? "In " + year + " gibt es keine Saison zum Kopieren." : n + " Saison(s) mit Preisen nach " + (year + 1) + " kopiert.");
        }
        var seasons = await store.SeasonsAsync(house.Id, ct);
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var rates = await store.RatesAsync(units.Select(u => u.Id), ct);
        var cur = Cur(ctx);
        string Range(Season s) => units.Count == 0 ? "–" : rates.Where(r => r.SeasonId == s.Id).Select(r => r.Price).DefaultIfEmpty().ToList() is var ps && ps.All(x => x == 0)
            ? "keine Preise" : B.Money(ps.Where(x => x > 0).Min(), cur) + (ps.Max() != ps.Where(x => x > 0).Min() ? " – " + B.Money(ps.Max(), cur) : "");
        var years = seasons.Select(s => s.From.Year).Distinct().OrderBy(y => y).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Zeiträume mit eigenen Preisen. Überschneiden sich zwei, gilt die mit dem höheren Rang (Silvester in der Nebensaison).", "Saisons & Preise"),
            HouseTabs(houses, house, "seasons"),
            Ui.Table(new[] { Ui.Column("Saison"), Ui.Column("Zeitraum"), Ui.Column("Rang", right: true), Ui.Column("Preis je Nacht", right: true) },
                seasons.Select(s => Ui.RowFor(s.Name, Ui.Link(s.Name, Root + "seasons/" + s.Id), B.Date(s.From) + " – " + B.Date(s.To), B.Num(s.Rank), Range(s))),
                emptyText: "Noch keine Saison – bis dahin gilt überall der Grundpreis der Einheit.")
                with { Below = new UiNode[] { Ui.Button("Neue Saison", Root + "seasons/new?objekt=" + house.Id, icon: "ti-plus") } },
            years.Count == 0 ? null : Ui.Card("Nächstes Jahr", Ui.Form(
                Ui.Muted("Kopiert alle Saisons eines Jahres mit ihren Preisen ein Jahr weiter – danach nur noch die Daten anpassen (Ostern wandert)."),
                Ui.Inline(Ui.Select("year", "Jahr", B.Num(years[^1]), years.Select(y => Ui.Option(B.Num(y), y + " → " + (y + 1))).ToArray()),
                    Ui.Submit("Kopieren", UiButtonKind.Ghost, "action", "copy", icon: "ti-copy"))))), "Saisons & Preise");
    }

    private static async Task<PluginResult> SeasonAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var isNew = IsNew(ctx);
        var s = isNew ? null : await store.SeasonAsync(Id(ctx), ct);
        if (!isNew && s is null) return PluginResult.NotFound();
        var house = s is null ? Chosen(ctx, houses) : houses.FirstOrDefault(h => h.Id == s.PropertyId);
        if (house is null) return NoHouse("Saison");
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var self = Root + "seasons/" + (s?.Id.ToString(CultureInfo.InvariantCulture) ?? "new?objekt=" + house.Id);

        if (ctx.Request.IsPost)
        {
            if (s is not null && ctx.Request.Action == "delete")
            {
                await store.DeleteSeasonAsync(s.Id, ct);
                return PluginResult.Redirect(Root + "seasons?objekt=" + house.Id, "Saison gelöscht.");
            }
            var name = B.Clip(ctx.Request.F("name"), 80);
            if (name.Length == 0 || B.Day(ctx.Request.F("from")) is not { } from || B.Day(ctx.Request.F("to")) is not { } to)
                return PluginResult.Redirect(self, "Bitte Name, Von und Bis angeben.", true);
            if (to < from) (from, to) = (to, from);
            var values = new Dictionary<string, object?>
            {
                ["property_id"] = house.Id, ["name"] = name, ["from_day"] = B.Iso(from), ["to_day"] = B.Iso(to),
                ["rank"] = Int(ctx.Request.F("rank"), 1, 0, 99), ["color"] = B.Clip(ctx.Request.F("color"), 7)
            };
            var id = s?.Id ?? 0;
            if (s is null) id = await store.Seasons.InsertAsync(values, ct); else await store.Seasons.UpdateAsync(s.Id, values, ct);
            foreach (var u in units)
            {
                var price = B.ParseMoney(ctx.Request.F("price_" + u.Id));
                await store.SetRateAsync(u.Id, id, price, Int(ctx.Request.F("min_" + u.Id), 1, 1, 60),
                    ctx.Request.F("days_" + u.Id).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var d) ? d : 0),
                    B.ParseMoney(ctx.Request.F("week_" + u.Id)) ?? 0, B.ParseMoney(ctx.Request.F("extra_" + u.Id)) ?? 0, ct);
            }
            return PluginResult.Redirect(Root + "seasons/" + id, "Gespeichert.");
        }

        var rates = s is null ? new List<Rate>() : await store.RatesAsync(units.Select(u => u.Id), ct);
        var cur = B.Symbol(Cur(ctx));
        UiNode RateRow(Unit u)
        {
            var r = rates.FirstOrDefault(x => x.UnitId == u.Id && x.SeasonId == s?.Id);
            var days = r is null ? "" : string.Join(",", r.ArrivalDays);
            return Ui.Card(u.Name, Ui.Grid(130,
                Ui.Input("price_" + u.Id, "Preis je Nacht (" + cur + ")", r is null ? "" : B.MoneyField(r.Price)) with { Help = "Leer = Grundpreis " + B.Money(u.BasePrice, Cur(ctx)) },
                Ui.Input("min_" + u.Id, "Mindestnächte", B.Num(r?.MinNights ?? u.BaseMinNights), UiInputType.Number),
                Ui.Select("days_" + u.Id, "Anreise", ArrivalChoices.Any(c => c.Value == days) ? days : "",
                    ArrivalChoices.Select(c => Ui.Option(c.Value, c.Label)).ToArray()),
                Ui.Input("week_" + u.Id, "Wochenpreis (7 Nächte, " + cur + ")", r is null || r.WeekPrice == 0 ? "" : B.MoneyField(r.WeekPrice)) with { Help = "Leer = 7 × Nachtpreis" },
                Ui.Input("extra_" + u.Id, "Aufschlag je weitere Person/Nacht", r is null ? (u.ExtraPerson == 0 ? "" : B.MoneyField(u.ExtraPerson)) : r.ExtraPerson == 0 ? "" : B.MoneyField(r.ExtraPerson))
                    with { Help = "ab der " + (u.GuestsStd + 1) + ". Person" }));
        }
        var form = Ui.Form(
            Ui.Grid(180,
                Ui.Input("name", "Name", s?.Name ?? "") with { Help = "z. B. Hauptsaison, Weihnachten, Silvester" },
                Ui.Input("from", "Erste Nacht", s is null ? "" : B.Iso(s.From), UiInputType.Date),
                Ui.Input("to", "Letzte Nacht", s is null ? "" : B.Iso(s.To), UiInputType.Date),
                Ui.Input("rank", "Rang", B.Num(s?.Rank ?? 1), UiInputType.Number) with { Help = "Höher gewinnt bei Überschneidung (z. B. Neben 1, Haupt 2, Silvester 5)." },
                Ui.Input("color", "Farbe", s?.Color ?? "#2563eb", UiInputType.Color)),
            units.Count == 0 ? Ui.Muted("Noch keine Einheit in diesem Objekt – die Preise kommen hierher, sobald es eine gibt.") : Ui.Heading("Preise je Einheit", 3),
            Ui.Stack(units.Select(u => (UiNode?)RateRow(u)).ToArray()),
            Ui.FormActions(Ui.Submit("Speichern"), s is null ? null : Ui.Submit("Löschen", UiButtonKind.Danger, "action", "delete", confirm: "Saison mit ihren Preisen löschen?")));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(house.Name, s?.Name ?? "Neue Saison", Ui.Button("Alle Saisons", Root + "seasons?objekt=" + house.Id, UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form)), s?.Name ?? "Neue Saison");
    }

    // ---- fees -------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> FeesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var house = Chosen(ctx, houses);
        if (house is null) return NoHouse("Gebühren & Kurtaxe");
        var fees = await store.FeesAsync(house.Id, ct);
        var units = await store.UnitsAsync(ct);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Was zum Übernachtungspreis dazukommt – Endreinigung, Bettwäsche, Haustier – und die Kurtaxe, die eigens ausgewiesen wird", "Gebühren & Kurtaxe"),
            HouseTabs(houses, house, "fees"),
            Ui.Table(new[] { Ui.Column("Gebühr"), Ui.Column("Betrag", right: true), Ui.Column("Gilt für"), Ui.Column("Art") },
                fees.Select(f => Ui.RowFor(f.Name, Ui.Link(f.Name, Root + "fees/" + f.Id), B.Money(f.Amount, Cur(ctx)) + " " + FeePer.Label(f.Per),
                    f.UnitId == 0 ? "alle Einheiten" : units.FirstOrDefault(u => u.Id == f.UnitId)?.Name ?? "–",
                    Ui.Inline(f.TouristTax ? Ui.Badge("Kurtaxe", UiTone.Info) : null, f.Optional ? Ui.Badge("wählbar") : Ui.Badge("Pflicht")))),
                emptyText: "Keine Gebühren.") with { Below = new UiNode[] { Ui.Button("Neue Gebühr", Root + "fees/new?objekt=" + house.Id, icon: "ti-plus") } }), "Gebühren & Kurtaxe");
    }

    private static async Task<PluginResult> FeeAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var isNew = IsNew(ctx);
        var row = isNew ? null : await store.Fees.GetAsync(Id(ctx), ct);
        if (!isNew && row is null) return PluginResult.NotFound();
        var f = row is null ? null : (await store.FeesAsync(row.Int("property_id"), ct)).FirstOrDefault(x => x.Id == row.Id);
        var house = f is null ? Chosen(ctx, houses) : houses.FirstOrDefault(h => h.Id == f.PropertyId);
        if (house is null) return NoHouse("Gebühr");
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var self = Root + "fees/" + (f?.Id.ToString(CultureInfo.InvariantCulture) ?? "new?objekt=" + house.Id);
        if (ctx.Request.IsPost)
        {
            if (f is not null && ctx.Request.Action == "delete")
            {
                await store.Fees.DeleteAsync(f.Id, ct);
                return PluginResult.Redirect(Root + "fees?objekt=" + house.Id, "Gebühr gelöscht.");
            }
            var name = B.Clip(ctx.Request.F("name"), 120);
            if (name.Length == 0 || B.ParseMoney(ctx.Request.F("amount")) is not { } amount) return PluginResult.Redirect(self, "Bitte Name und Betrag angeben.", true);
            var per = FeePer.All.Any(p => p.Key == ctx.Request.F("per")) ? ctx.Request.F("per") : FeePer.Stay;
            var unitId = units.FirstOrDefault(u => B.Num(u.Id) == ctx.Request.F("unit_id"))?.Id ?? 0;
            var values = new Dictionary<string, object?>
            {
                ["property_id"] = house.Id, ["unit_id"] = unitId, ["name"] = name, ["amount"] = Math.Max(0, amount), ["per"] = per,
                ["optional"] = B.Checked(ctx.Request.F("optional")), ["tourist_tax"] = B.Checked(ctx.Request.F("tourist_tax")),
                ["children"] = ctx.Request.F("children") is "free" or "half" or "full" ? ctx.Request.F("children") : "full", ["sort"] = Int(ctx.Request.F("sort"), 0, -999, 999)
            };
            var id = f?.Id ?? 0;
            if (f is null) id = await store.Fees.InsertAsync(values, ct); else await store.Fees.UpdateAsync(f.Id, values, ct);
            return PluginResult.Redirect(Root + "fees/" + id, "Gespeichert.");
        }
        var form = Ui.Form(
            Ui.Grid(200,
                Ui.Input("name", "Name", f?.Name ?? "") with { Help = "z. B. Endreinigung, Bettwäsche, Haustier, Kurtaxe" },
                Ui.Input("amount", "Betrag (" + B.Symbol(Cur(ctx)) + ")", f is null ? "" : B.MoneyField(f.Amount)),
                Ui.Select("per", "Berechnung", f?.Per ?? FeePer.Stay, FeePer.All.Select(p => Ui.Option(p.Key, p.Label)).ToArray()),
                Ui.Select("unit_id", "Gilt für", B.Num(f?.UnitId ?? 0), new[] { Ui.Option("0", "alle Einheiten") }.Concat(units.Select(u => Ui.Option(B.Num(u.Id), u.Name))).ToArray())),
            Ui.Checkbox("optional", "Wählbar (nur wenn der Gast es dazubucht)", f?.Optional ?? false),
            Ui.Checkbox("tourist_tax", "Das ist die Kurtaxe (eigene Zeile auf der Rechnung)", f?.TouristTax ?? false),
            Ui.Select("children", "Kinder bei der Kurtaxe", f?.Children ?? "full", Ui.Option("full", "zahlen voll"), Ui.Option("half", "zahlen die Hälfte"), Ui.Option("free", "sind frei")),
            Ui.Grid(160, Ui.Input("sort", "Reihenfolge", B.Num(f?.Sort ?? 0), UiInputType.Number)),
            Ui.FormActions(Ui.Submit("Speichern"), f is null ? null : Ui.Submit("Löschen", UiButtonKind.Danger, "action", "delete", confirm: "Gebühr löschen?")));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(house.Name, f?.Name ?? "Neue Gebühr", Ui.Button("Alle Gebühren", Root + "fees?objekt=" + house.Id, UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form)), f?.Name ?? "Neue Gebühr");
    }

    // ---- price check ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CheckAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        if (units.Count == 0) return PluginResult.Page(Ui.Stack(Ui.PageHead(null, "Preis prüfen"), Ui.Card(Ui.Empty("Noch keine buchbare Einheit."),
            Ui.Button("Einheit anlegen", Root + "units/new", icon: "ti-plus"))), "Preis prüfen");
        string P(string k) => ctx.Request.IsPost ? ctx.Request.F(k) : ctx.Request.Q(k);
        var unit = units.FirstOrDefault(u => B.Num(u.Id) == P("unit")) ?? units[0];
        var today = B.Today;
        var arrival = B.Day(P("arrival")) ?? today.AddDays(30);
        var departure = B.Day(P("departure")) ?? arrival.AddDays(7);
        var adults = Int(P("adults"), 2, 0, 50);
        var children = Int(P("children"), 0, 0, 50);
        var fees = await store.FeesAsync(unit.PropertyId, ct);
        var optional = fees.Where(f => f.Optional && B.Checked(P("fee_" + f.Id))).Select(f => f.Id).ToList();
        var stay = new Stay(arrival, departure, adults, children, optional);
        var quote = await store.QuoteAsync(unit.Id, stay, Cur(ctx), ct);
        var free = stay.Nights > 0 && await store.IsFreeAsync(unit, arrival, departure, ct);
        var cur = Cur(ctx);

        var form = Ui.Form(
            Ui.Grid(160,
                Ui.Select("unit", "Einheit", B.Num(unit.Id), units.Select(u => Ui.Option(B.Num(u.Id), u.Name)).ToArray()),
                Ui.Input("arrival", "Anreise", B.Iso(arrival), UiInputType.Date), Ui.Input("departure", "Abreise", B.Iso(departure), UiInputType.Date),
                Ui.Input("adults", "Erwachsene", B.Num(adults), UiInputType.Number), Ui.Input("children", "Kinder", B.Num(children), UiInputType.Number)),
            fees.Any(f => f.Optional && (f.UnitId == 0 || f.UnitId == unit.Id))
                ? Ui.Grid(220, fees.Where(f => f.Optional && (f.UnitId == 0 || f.UnitId == unit.Id)).Select(f => (UiNode?)Ui.Checkbox("fee_" + f.Id, f.Name + " (" + B.Money(f.Amount, cur) + " " + FeePer.Label(f.Per) + ")", optional.Contains(f.Id))).ToArray())
                : null,
            Ui.FormActions(Ui.Submit("Berechnen", icon: "ti-calculator")));
        UiNode result = quote is null ? Ui.Empty("Diese Einheit ist nicht buchbar.") : Ui.Stack(
            Ui.Inline(
                Ui.Badge(stay.Nights + (stay.Nights == 1 ? " Nacht" : " Nächte"), UiTone.Info),
                free ? Ui.Badge("frei", UiTone.Success) : Ui.Badge("belegt", UiTone.Error),
                quote.Ok ? Ui.Badge("buchbar", UiTone.Success) : Ui.Badge("passt nicht", UiTone.Warning)),
            quote.Problems.Count == 0 ? null : Ui.Alert(string.Join(" ", quote.Problems), UiTone.Warning),
            Ui.Table(new[] { Ui.Column("Posten"), Ui.Column("Berechnung"), Ui.Column("Betrag", right: true) },
                quote.Lines.Select(l => Ui.Row(l.Text, l.Detail.Length > 0 ? Ui.Muted(l.Detail) : l.Kind == "nights" && l.Quantity > 0 ? Ui.Muted(B.Money(l.Unit, cur) + (l.Text.Contains("Woche") ? " je Woche" : " je Nacht")) : null, B.Money(l.Amount, cur)))
                    .Append(Ui.Row(Ui.Text("Gesamt"), null, Ui.Text(B.Money(quote.Total, cur))))));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Die Rechnung, die ein Gast für diesen Aufenthalt bekäme – mit dem Grund, wenn er nicht passt", "Preis prüfen"),
            Ui.Card("Aufenthalt", form), Ui.Card(unit.Name, result)), "Preis prüfen");
    }
}
