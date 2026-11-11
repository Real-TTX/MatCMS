using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen (phase B1): houses, units (with their blocks), seasons with the price table of every unit, fees and tourist tax, and "Preis prüfen" —
/// the same bill a guest will get, with the reasons when a stay does not fit. The overview shows today's arrivals and departures; the set-up list only while something is missing.
/// </summary>
public static class BookingAdmin
{
    public const string Root = "/admin/p/booking/";

    public static void Configure(IPluginBuilder b)
    {
        var section = b.Admin.Section("booking.admin.nav", "ti-calendar-check", AdminGroup.Content, 26)
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
            .Link("booking.admin.navOverview", "dashboard", "ti-layout-dashboard")
            .Link("booking.admin.navBookings", "bookings", "ti-calendar-event", badge: true, also: new[] { "bookings/" })
            .Link("booking.admin.navPlan", "plan", "ti-layout-kanban")
            .Link("booking.admin.navGuests", "guests", "ti-address-book", also: new[] { "guests/" })
            .Link("booking.admin.navChannels", "channels", "ti-arrows-exchange")
            .Link("booking.admin.navCheckins", "checkins", "ti-id", also: new[] { "checkins/" })
            .Link("booking.admin.navCleaning", "cleaning", "ti-wash-machine", also: new[] { "cleaning/" })
            .Link("booking.admin.navFederation", "federation", "ti-affiliate", also: new[] { "federation/" })
            .Link("booking.admin.navProperties", "properties", "ti-building-cottage", heading: "booking.admin.navSetup", also: new[] { "properties/" })
            .Link("booking.admin.navUnits", "units", "ti-bed", also: new[] { "units/" })
            .Link("booking.admin.navSeasons", "seasons", "ti-calendar-dollar", also: new[] { "seasons/" })
            .Link("booking.admin.navFees", "fees", "ti-receipt", also: new[] { "fees/" })
            .Link("booking.admin.navCheck", "check", "ti-calculator")
            .Link("booking.admin.navSettings", "_settings", "ti-settings")
            .Badge(async (c, ct) => (await BookingFlow.ListAsync(c.Data, new[] { BookingStatus.Request }, ct)).Count is var n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : null);
        BookingAdminBookings.Register(section);
        BookingAdminMore.Register(section);
        BookingAdminChannels.Register(section);
        BookingAdminCheckins.Register(section);
        BookingCleaning.Register(section);
        BookingAdminFederation.Register(section);
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

    private static PluginResult NoHouse(IPluginContext ctx, string title) => PluginResult.Page(Ui.Stack(
        Ui.PageHead(null, title),
        Ui.Card(Ui.Empty(ctx.T("booking.admin.noHouse")), Ui.Button(ctx.T("booking.admin.addHouse"), Root + "properties/new", icon: "ti-plus"))), title);

    // ---- overview ---------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> DashboardAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var units = await store.UnitsAsync(ct);
        var steps = new List<(string Text, bool Done, string Href)>
        {
            (ctx.T("booking.admin.stepHouse"), houses.Count > 0, Root + "properties"),
            (ctx.T("booking.admin.stepUnits"), units.Count > 0, Root + "units"),
        };
        var all = await BookingFlow.ListAsync(ctx.Data, null, ct);
        var open = all.Count(x => x.Status is BookingStatus.Request or BookingStatus.Offer);
        var coming = all.Count(x => x.Status == BookingStatus.Confirmed && x.Departure >= B.Today);
        var seasons = 0; var fees = 0;
        foreach (var h in houses) { seasons += (await store.SeasonsAsync(h.Id, ct)).Count; fees += (await store.FeesAsync(h.Id, ct)).Count; }
        steps.Add((ctx.T("booking.admin.stepSeasons"), seasons > 0, Root + "seasons"));
        steps.Add((ctx.T("booking.admin.stepFees"), fees > 0, Root + "fees"));
        var today = B.Today;
        var paid = await BookingPayments.PaidAsync(ctx.Data, ct);
        var unpaid = all.Count(x => x.Status == BookingStatus.Confirmed && x.Departure >= today && !(paid.TryGetValue(x.Id, out var v) && v > 0));
        var names = units.ToDictionary(u => u.Id, u => u.Name);
        UiNode Today(string title, IEnumerable<Booking> list) => Ui.Card(title, Ui.Table(
            new[] { Ui.Column(ctx.T("booking.admin.colStay")), Ui.Column(ctx.T("booking.admin.colGuest")), Ui.Column(ctx.T("booking.admin.colPersons"), right: true) },
            list.Select(x => Ui.Row(names.GetValueOrDefault(x.UnitId, "–"), Ui.Link(x.Name.Length > 0 ? x.Name : x.Number, Root + "bookings/" + x.Id), B.Num(x.Adults + x.Children))),
            emptyText: ctx.T("booking.admin.nobody")));
        var confirmed = all.Where(x => x.Status == BookingStatus.Confirmed).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.Stats(
                Ui.Stat(ctx.T("booking.admin.statOpen"), B.Num(open), tone: open > 0 ? UiTone.Warning : UiTone.Success, href: Root + "bookings"),
                Ui.Stat(ctx.T("booking.admin.statComing"), B.Num(coming), href: Root + "bookings?tab=kommend"),
                Ui.Stat(ctx.T("booking.admin.statUnpaid"), B.Num(unpaid), tone: unpaid > 0 ? UiTone.Warning : UiTone.Default, href: Root + "bookings?tab=kommend"),
                Ui.Stat(ctx.T("booking.admin.properties"), B.Num(houses.Count), href: Root + "properties"),
                Ui.Stat(ctx.T("booking.admin.units"), B.Num(units.Count(u => u.Active)), units.Any(u => !u.Active) ? ctx.T("booking.admin.nInactive", units.Count(u => !u.Active)) : null, href: Root + "units")),
            Ui.Grid(320, Today(ctx.T("booking.admin.arrivalsToday"), confirmed.Where(x => x.Arrival == today)), Today(ctx.T("booking.admin.departuresToday"), confirmed.Where(x => x.Departure == today))),
            // the set-up list only while something is missing — once everything is there it is noise
            steps.All(s => s.Done) ? null : Ui.Card(ctx.T("booking.admin.setUp"), Ui.Stack(steps.Select(s => (UiNode?)Ui.Inline(
                Ui.Badge(s.Done ? ctx.T("booking.admin.done") : ctx.T("booking.admin.open"), s.Done ? UiTone.Success : UiTone.Warning), Ui.Link(s.Text, s.Href))).ToArray()))), ctx.T("booking.admin.nav"));
    }

    // ---- houses -----------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> PropertiesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var units = await store.UnitsAsync(ct);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.properties")),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.property")), Ui.Column(ctx.T("booking.admin.colPlace")), Ui.Column(ctx.T("booking.admin.units"), right: true) },
                houses.Select(h => Ui.RowFor(h.Name, Ui.Link(h.Name, Root + "properties/" + h.Id), h.Address, B.Num(units.Count(u => u.PropertyId == h.Id)))),
                emptyText: ctx.T("booking.admin.noHouse")) with { Below = new UiNode[] { Ui.Button(ctx.T("booking.admin.newHouse"), Root + "properties/new", icon: "ti-plus") } }), ctx.T("booking.admin.properties"));
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
                if ((await store.UnitsAsync(ct)).Any(u => u.PropertyId == p.Id)) return PluginResult.Redirect(Root + "properties/" + p.Id, ctx.T("booking.admin.houseHasUnits"), true);
                foreach (var s in await store.SeasonsAsync(p.Id, ct)) await store.DeleteSeasonAsync(s.Id, ct);
                foreach (var f in await store.FeesAsync(p.Id, ct)) await store.Fees.DeleteAsync(f.Id, ct);
                await store.Properties.DeleteAsync(p.Id, ct);
                return PluginResult.Redirect(Root + "properties", ctx.T("booking.admin.houseDeleted"));
            }
            var name = B.Clip(ctx.Request.F("name"), 120);
            if (name.Length == 0) return PluginResult.Redirect(Root + "properties/" + (p?.Id.ToString(CultureInfo.InvariantCulture) ?? "new"), ctx.T("booking.admin.nameNeeded"), true);
            var values = new Dictionary<string, object?>
            {
                ["name"] = name, ["slug"] = await store.FreeSlugAsync(BookingStore.TProperties, p?.Slug ?? name, p?.Id ?? 0, ct),
                ["address"] = B.Clip(ctx.Request.F("address"), 300), ["country"] = B.Clip(ctx.Request.F("country"), 2).ToUpperInvariant(),
                ["check_in"] = B.Clip(ctx.Request.F("check_in"), 5), ["check_out"] = B.Clip(ctx.Request.F("check_out"), 5),
                ["description"] = B.Clip(ctx.Request.F("description"), 4000), ["sort"] = Int(ctx.Request.F("sort"), 0, -999, 999),
                ["directions"] = B.Clip(ctx.Request.F("directions"), 4000), ["house_rules"] = B.Clip(ctx.Request.F("house_rules"), 4000), ["checkout_info"] = B.Clip(ctx.Request.F("checkout_info"), 2000)
            };
            var id = p?.Id ?? 0;
            if (p is null) id = await store.Properties.InsertAsync(values, ct); else await store.Properties.UpdateAsync(p.Id, values, ct);
            return PluginResult.Redirect(Root + "properties/" + id, ctx.T("booking.admin.saved"));
        }
        var form = Ui.Form(
            Ui.Input("name", ctx.T("booking.admin.name"), p?.Name ?? "") with { Help = ctx.T("booking.admin.houseNameHelp") },
            Ui.TextArea("address", ctx.T("booking.admin.address"), p?.Address ?? "", 2),
            Ui.Grid(160,
                Ui.Select("country", ctx.T("booking.admin.country"), p?.Country ?? "HR", Countries.Select(c => Ui.Option(c.Code, ctx.T(c.Name))).ToArray()),
                Ui.Input("check_in", ctx.T("booking.admin.checkIn"), p?.CheckIn ?? "15:00"), Ui.Input("check_out", ctx.T("booking.admin.checkOut"), p?.CheckOut ?? "10:00"),
                Ui.Input("sort", ctx.T("booking.admin.sort"), B.Num(p?.Sort ?? 0), UiInputType.Number)),
            Ui.TextArea("description", ctx.T("booking.admin.descriptionInternal"), p?.Description ?? "", 3),
            Ui.Heading(ctx.T("booking.admin.forGuests"), 3),
            Ui.TextArea("directions", ctx.T("booking.admin.directions"), p?.Directions ?? "", 4) with { Help = ctx.T("booking.admin.directionsHelp") },
            Ui.TextArea("house_rules", ctx.T("booking.admin.houseRules"), p?.HouseRules ?? "", 4),
            Ui.TextArea("checkout_info", ctx.T("booking.admin.checkoutInfo"), p?.CheckoutInfo ?? "", 3) with { Help = ctx.T("booking.admin.checkoutInfoHelp") },
            Ui.FormActions(Ui.Submit(ctx.T("booking.admin.save")), p is null ? null : Ui.Submit(ctx.T("booking.admin.delete"), UiButtonKind.Danger, "action", "delete", confirm: ctx.T("booking.admin.confirmDeleteHouse"))));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(null, p?.Name ?? ctx.T("booking.admin.newHouse"), Ui.Button(ctx.T("booking.admin.allHouses"), Root + "properties", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form)), p?.Name ?? ctx.T("booking.admin.newHouse"));
    }

    internal static readonly (string Code, string Name)[] Countries =
    {
        // names are text keys (booking.admin.country.XX)
        ("HR", "booking.admin.country.HR"), ("DE", "booking.admin.country.DE"), ("AT", "booking.admin.country.AT"), ("IT", "booking.admin.country.IT"),
        ("ES", "booking.admin.country.ES"), ("PT", "booking.admin.country.PT"), ("FR", "booking.admin.country.FR"), ("GR", "booking.admin.country.GR"),
        ("SI", "booking.admin.country.SI"), ("ME", "booking.admin.country.ME"), ("CH", "booking.admin.country.CH"), ("NL", "booking.admin.country.NL"),
        ("DK", "booking.admin.country.DK")
    };

    // ---- units ------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> UnitsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        if (houses.Count == 0) return NoHouse(ctx, ctx.T("booking.admin.units"));
        var units = await store.UnitsAsync(ct);
        string House(long id) => houses.FirstOrDefault(h => h.Id == id)?.Name ?? "–";
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.units")),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.unit")), Ui.Column(ctx.T("booking.admin.property")), Ui.Column(ctx.T("booking.admin.colPersons")), Ui.Column(ctx.T("booking.admin.colBasePrice"), right: true), Ui.Column(ctx.T("booking.admin.colStatus")) },
                units.Select(u => Ui.RowFor(u.Name + " " + House(u.PropertyId), Ui.Inline(Ui.Link(u.Name, Root + "units/" + u.Id), u.Contains.Count > 0 ? Ui.Badge(ctx.T("booking.admin.wholeHouse"), UiTone.Info) : null),
                    House(u.PropertyId), u.GuestsStd == u.GuestsMax ? B.Num(u.GuestsMax) : u.GuestsStd + "–" + u.GuestsMax, B.Money(u.BasePrice, Cur(ctx)) + ctx.T("booking.admin.perNightSlash"),
                    u.Active ? Ui.Badge(ctx.T("booking.admin.bookable"), UiTone.Success) : Ui.Badge(ctx.T("booking.admin.inactive")))),
                emptyText: ctx.T("booking.admin.noUnit")) with { Below = new UiNode[] { Ui.Button(ctx.T("booking.admin.newUnit"), Root + "units/new", icon: "ti-plus") } }), ctx.T("booking.admin.units"));
    }

    private static async Task<PluginResult> UnitAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        if (houses.Count == 0) return NoHouse(ctx, ctx.T("booking.admin.unit"));
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
                        return PluginResult.Redirect(self, ctx.T("booking.admin.unitHasNights"), true);
                    await store.Units.DeleteAsync(u.Id, ct);
                    foreach (var other in all.Where(o => o.Contains.Contains(u.Id)))
                        await store.Units.UpdateAsync(other.Id, new Dictionary<string, object?> { ["contains"] = string.Join(",", other.Contains.Where(c => c != u.Id)) }, ct);
                    return PluginResult.Redirect(Root + "units", ctx.T("booking.admin.unitDeleted"));
                case "block" when u is not null:
                    if (B.Day(ctx.Request.F("from")) is not { } bf || B.Day(ctx.Request.F("to")) is not { } bt || bt <= bf)
                        return PluginResult.Redirect(self, ctx.T("booking.admin.blockDatesNeeded"), true);
                    try { await store.AddBlockAsync(u, bf, bt, ctx.Request.F("reason"), "manual", ct); }
                    catch (UnavailableException ex) { return PluginResult.Redirect(self, ex.Text(ctx), true); }
                    return PluginResult.Redirect(self, ctx.T("booking.admin.blocked"));
                case "unblock" when u is not null:
                    if (long.TryParse(ctx.Request.F("id"), out var blockId) && (await store.BlocksAsync(u.Id, ct)).Any(x => x.Id == blockId)) await store.RemoveBlockAsync(blockId, ct);
                    return PluginResult.Redirect(self, ctx.T("booking.admin.blockRemoved"));
            }
            var name = B.Clip(ctx.Request.F("name"), 120);
            if (name.Length == 0) return PluginResult.Redirect(self, ctx.T("booking.admin.nameNeeded"), true);
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
                ["active"] = B.Checked(ctx.Request.F("active")),
                ["access_info"] = B.Clip(ctx.Request.F("access_info"), 2000), ["wifi"] = B.Clip(ctx.Request.F("wifi"), 300), ["checklist"] = B.Clip(ctx.Request.F("checklist"), 4000)
            };
            var id = u?.Id ?? 0;
            if (u is null) id = await store.Units.InsertAsync(values, ct); else await store.Units.UpdateAsync(u.Id, values, ct);
            return PluginResult.Redirect(Root + "units/" + id, ctx.T("booking.admin.saved"));
        }

        var houseId = u?.PropertyId ?? houses[0].Id;
        var siblings = all.Where(o => o.Id != u?.Id && o.PropertyId == houseId && o.Contains.Count == 0).ToList();
        var cur = Cur(ctx);
        var form = Ui.Form(
            Ui.Grid(220,
                Ui.Input("name", ctx.T("booking.admin.name"), u?.Name ?? "") with { Help = ctx.T("booking.admin.unitNameHelp") },
                houses.Count > 1 ? Ui.Select("property_id", ctx.T("booking.admin.property"), B.Num(houseId), houses.Select(h => Ui.Option(B.Num(h.Id), h.Name)).ToArray()) : Ui.Hidden("property_id", B.Num(houseId)),
                Ui.Input("slug", ctx.T("booking.admin.slug"), u?.Slug ?? "") with { Help = ctx.T("booking.admin.slugHelp") }),
            Ui.Image("image", ctx.T("booking.admin.image"), u?.Image ?? ""),
            Ui.TextArea("description", ctx.T("booking.admin.description"), u?.Description ?? "", 5),
            Ui.Grid(160,
                Ui.Input("bedrooms", ctx.T("booking.admin.bedrooms"), B.Num(u?.Bedrooms ?? 1), UiInputType.Number),
                Ui.Input("guests_std", ctx.T("booking.admin.guestsStd"), B.Num(u?.GuestsStd ?? 2), UiInputType.Number),
                Ui.Input("guests_max", ctx.T("booking.admin.guestsMax"), B.Num(u?.GuestsMax ?? 4), UiInputType.Number),
                Ui.Input("buffer_days", ctx.T("booking.admin.bufferDays"), B.Num(u?.BufferDays ?? 0), UiInputType.Number) with { Help = ctx.T("booking.admin.bufferDaysHelp") },
                Ui.Input("sort", ctx.T("booking.admin.sort"), B.Num(u?.Sort ?? 0), UiInputType.Number)),
            Ui.Heading(ctx.T("booking.admin.basePriceHeading"), 3),
            Ui.Grid(160,
                Ui.Input("base_price", ctx.T("booking.admin.pricePerNight", B.Symbol(cur)), u is null ? "" : B.MoneyField(u.BasePrice)),
                Ui.Input("base_min_nights", ctx.T("booking.admin.minNights"), B.Num(u?.BaseMinNights ?? 1), UiInputType.Number),
                Ui.Input("extra_person", ctx.T("booking.admin.extraPersonNight", B.Symbol(cur)), u is null ? "" : B.MoneyField(u.ExtraPerson))),
            siblings.Count == 0 ? null : Ui.Stack(
                Ui.Heading(ctx.T("booking.admin.consistsOf"), 3),
                Ui.Grid(220, siblings.Select(o => (UiNode?)Ui.Checkbox("contains_" + o.Id, o.Name, u?.Contains.Contains(o.Id) ?? false)).ToArray())),
            Ui.Heading(ctx.T("booking.admin.arrivalCleaning"), 3),
            Ui.TextArea("access_info", ctx.T("booking.admin.access"), u?.AccessInfo ?? "", 3) with { Help = ctx.T("booking.admin.accessHelp") },
            Ui.Input("wifi", ctx.T("booking.admin.wifi"), u?.Wifi ?? ""),
            Ui.TextArea("checklist", ctx.T("booking.admin.checklist"), u?.Checklist ?? "", 4),
            Ui.Checkbox("active", ctx.T("booking.admin.active"), u?.Active ?? true),
            Ui.FormActions(Ui.Submit(ctx.T("booking.admin.save")), u is null ? null : Ui.Submit(ctx.T("booking.admin.delete"), UiButtonKind.Danger, "action", "delete", confirm: ctx.T("booking.admin.confirmDeleteUnit"))));

        UiNode? blocks = null;
        if (u is not null)
        {
            var list = await store.BlocksAsync(u.Id, ct);
            blocks = Ui.Card(ctx.T("booking.admin.blocks"), Ui.Stack(
                Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.from")), Ui.Column(ctx.T("booking.admin.toFree")), Ui.Column(ctx.T("booking.admin.colNights"), right: true), Ui.Column(ctx.T("booking.admin.reason")), Ui.Column("", actions: true) },
                    list.Select(x => Ui.Row(B.Weekday(ctx.T, x.From) + " " + B.Date(x.From), B.Weekday(ctx.T, x.To) + " " + B.Date(x.To), B.Num(x.Nights), x.Reason,
                        Ui.Action(ctx.T("booking.admin.remove"), "unblock", B.Num(x.Id), confirm: ctx.T("booking.admin.confirmRemoveBlock"), icon: "ti-trash"))),
                    emptyText: ctx.T("booking.admin.noBlocks")),
                Ui.Form(Ui.Grid(160, Ui.Input("from", ctx.T("booking.admin.from"), "", UiInputType.Date), Ui.Input("to", ctx.T("booking.admin.toFree"), "", UiInputType.Date), Ui.Input("reason", ctx.T("booking.admin.reason"))),
                    Ui.FormActions(Ui.Submit(ctx.T("booking.admin.block"), UiButtonKind.Ghost, "action", "block", icon: "ti-lock")))));
        }
        return PluginResult.Page(Ui.Stack(Ui.PageHead(null, u?.Name ?? ctx.T("booking.admin.newUnit"), Ui.Button(ctx.T("booking.admin.allUnits"), Root + "units", UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form), blocks), u?.Name ?? ctx.T("booking.admin.newUnit"));
    }

    // ---- seasons and prices -----------------------------------------------------------------------------------------

    internal static readonly (string Value, string Label)[] ArrivalChoices =
        { ("", "booking.admin.arrAny"), ("6", "booking.admin.arrSat"), ("7", "booking.admin.arrSun"), ("6,7", "booking.admin.arrSatSun"), ("5", "booking.admin.arrFri"), ("1", "booking.admin.arrMon"), ("5,6,7", "booking.admin.arrFriSun") };

    private static async Task<PluginResult> SeasonsAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var house = Chosen(ctx, houses);
        if (house is null) return NoHouse(ctx, ctx.T("booking.admin.seasons"));
        if (ctx.Request.IsPost && ctx.Request.Action == "copy" && int.TryParse(ctx.Request.F("year"), out var year))
        {
            var n = await store.CopySeasonsAsync(house.Id, year, ct);
            return PluginResult.Redirect(Root + "seasons?objekt=" + house.Id, n == 0 ? ctx.T("booking.admin.noSeasonToCopy", year) : ctx.T("booking.admin.seasonsCopied", n, year + 1));
        }
        var seasons = await store.SeasonsAsync(house.Id, ct);
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var rates = await store.RatesAsync(units.Select(u => u.Id), ct);
        var cur = Cur(ctx);
        string Range(Season s) => units.Count == 0 ? "–" : rates.Where(r => r.SeasonId == s.Id).Select(r => r.Price).DefaultIfEmpty().ToList() is var ps && ps.All(x => x == 0)
            ? ctx.T("booking.admin.noPrices") : B.Money(ps.Where(x => x > 0).Min(), cur) + (ps.Max() != ps.Where(x => x > 0).Min() ? " – " + B.Money(ps.Max(), cur) : "");
        var years = seasons.Select(s => s.From.Year).Distinct().OrderBy(y => y).ToList();
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.seasons")),
            HouseTabs(houses, house, "seasons"),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.season")), Ui.Column(ctx.T("booking.admin.colPeriod")), Ui.Column(ctx.T("booking.admin.rank"), right: true), Ui.Column(ctx.T("booking.admin.colPricePerNight"), right: true) },
                seasons.Select(s => Ui.RowFor(s.Name, Ui.Link(s.Name, Root + "seasons/" + s.Id), B.Date(s.From) + " – " + B.Date(s.To), B.Num(s.Rank), Range(s))),
                emptyText: ctx.T("booking.admin.noSeason"))
                with { Below = new UiNode[] { Ui.Button(ctx.T("booking.admin.newSeason"), Root + "seasons/new?objekt=" + house.Id, icon: "ti-plus") } },
            years.Count == 0 ? null : Ui.Card(ctx.T("booking.admin.copySeasons"), Ui.Form(
                Ui.Inline(Ui.Select("year", ctx.T("booking.admin.year"), B.Num(years[^1]), years.Select(y => Ui.Option(B.Num(y), y + " → " + (y + 1))).ToArray()),
                    Ui.Submit(ctx.T("booking.admin.copy"), UiButtonKind.Ghost, "action", "copy", icon: "ti-copy"))))), ctx.T("booking.admin.seasons"));
    }

    private static async Task<PluginResult> SeasonAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var isNew = IsNew(ctx);
        var s = isNew ? null : await store.SeasonAsync(Id(ctx), ct);
        if (!isNew && s is null) return PluginResult.NotFound();
        var house = s is null ? Chosen(ctx, houses) : houses.FirstOrDefault(h => h.Id == s.PropertyId);
        if (house is null) return NoHouse(ctx, ctx.T("booking.admin.season"));
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var self = Root + "seasons/" + (s?.Id.ToString(CultureInfo.InvariantCulture) ?? "new?objekt=" + house.Id);

        if (ctx.Request.IsPost)
        {
            if (s is not null && ctx.Request.Action == "delete")
            {
                await store.DeleteSeasonAsync(s.Id, ct);
                return PluginResult.Redirect(Root + "seasons?objekt=" + house.Id, ctx.T("booking.admin.seasonDeleted"));
            }
            var name = B.Clip(ctx.Request.F("name"), 80);
            if (name.Length == 0 || B.Day(ctx.Request.F("from")) is not { } from || B.Day(ctx.Request.F("to")) is not { } to)
                return PluginResult.Redirect(self, ctx.T("booking.admin.seasonFieldsNeeded"), true);
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
            return PluginResult.Redirect(Root + "seasons/" + id, ctx.T("booking.admin.saved"));
        }

        var rates = s is null ? new List<Rate>() : await store.RatesAsync(units.Select(u => u.Id), ct);
        var cur = B.Symbol(Cur(ctx));
        UiNode RateRow(Unit u)
        {
            var r = rates.FirstOrDefault(x => x.UnitId == u.Id && x.SeasonId == s?.Id);
            var days = r is null ? "" : string.Join(",", r.ArrivalDays);
            // one row of fields per unit under its name (no card per unit), labels short enough to stay on one line so the fields line up
            return Ui.Stack(Ui.Heading(u.Name, 4), Ui.Grid(130,
                Ui.Input("price_" + u.Id, ctx.T("booking.admin.rateNight", cur), r is null ? "" : B.MoneyField(r.Price)) with { Help = r is null ? ctx.T("booking.admin.rateNightHelp", B.Money(u.BasePrice, Cur(ctx))) : null },
                Ui.Input("min_" + u.Id, ctx.T("booking.admin.minNights"), B.Num(r?.MinNights ?? u.BaseMinNights), UiInputType.Number),
                Ui.Select("days_" + u.Id, ctx.T("booking.admin.arrival"), ArrivalChoices.Any(c => c.Value == days) ? days : "",
                    ArrivalChoices.Select(c => Ui.Option(c.Value, ctx.T(c.Label))).ToArray()),
                Ui.Input("week_" + u.Id, ctx.T("booking.admin.rateWeek", cur), r is null || r.WeekPrice == 0 ? "" : B.MoneyField(r.WeekPrice)) with { Help = ctx.T("booking.admin.rateWeekHelp") },
                Ui.Input("extra_" + u.Id, ctx.T("booking.admin.ratePerson", cur), r is null ? (u.ExtraPerson == 0 ? "" : B.MoneyField(u.ExtraPerson)) : r.ExtraPerson == 0 ? "" : B.MoneyField(r.ExtraPerson))
                    with { Help = ctx.T("booking.admin.fromPerson", u.GuestsStd + 1) }));
        }
        var form = Ui.Form(
            Ui.Grid(180,
                Ui.Input("name", ctx.T("booking.admin.name"), s?.Name ?? "") with { Help = ctx.T("booking.admin.seasonNameHelp") },
                Ui.Input("from", ctx.T("booking.admin.firstNight"), s is null ? "" : B.Iso(s.From), UiInputType.Date),
                Ui.Input("to", ctx.T("booking.admin.lastNight"), s is null ? "" : B.Iso(s.To), UiInputType.Date),
                Ui.Input("rank", ctx.T("booking.admin.rank"), B.Num(s?.Rank ?? 1), UiInputType.Number) with { Help = ctx.T("booking.admin.rankHelp") },
                Ui.Input("color", ctx.T("booking.admin.color"), s?.Color ?? "#2563eb", UiInputType.Color)),
            units.Count == 0 ? Ui.Empty(ctx.T("booking.admin.noUnitInHouse")) : Ui.Heading(ctx.T("booking.admin.pricesPerUnit"), 3),
            Ui.Stack(units.Select(u => (UiNode?)RateRow(u)).ToArray()),
            Ui.FormActions(Ui.Submit(ctx.T("booking.admin.save")), s is null ? null : Ui.Submit(ctx.T("booking.admin.delete"), UiButtonKind.Danger, "action", "delete", confirm: ctx.T("booking.admin.confirmDeleteSeason"))));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(house.Name, s?.Name ?? ctx.T("booking.admin.newSeason"), Ui.Button(ctx.T("booking.admin.allSeasons"), Root + "seasons?objekt=" + house.Id, UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form)), s?.Name ?? ctx.T("booking.admin.newSeason"));
    }

    // ---- fees -------------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> FeesAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var houses = await store.PropertiesAsync(ct);
        var house = Chosen(ctx, houses);
        if (house is null) return NoHouse(ctx, ctx.T("booking.admin.fees"));
        var fees = await store.FeesAsync(house.Id, ct);
        var units = await store.UnitsAsync(ct);
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.fees")),
            HouseTabs(houses, house, "fees"),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.fee")), Ui.Column(ctx.T("booking.admin.amount"), right: true), Ui.Column(ctx.T("booking.admin.appliesTo")), Ui.Column(ctx.T("booking.admin.colKind")) },
                fees.Select(f => Ui.RowFor(f.Name, Ui.Link(f.Name, Root + "fees/" + f.Id), B.Money(f.Amount, Cur(ctx)) + " " + FeePer.Label(ctx.T, f.Per),
                    f.UnitId == 0 ? ctx.T("booking.admin.allUnitsLower") : units.FirstOrDefault(u => u.Id == f.UnitId)?.Name ?? "–",
                    Ui.Inline(f.TouristTax ? Ui.Badge(ctx.T("booking.admin.touristTax"), UiTone.Info) : null, f.Optional ? Ui.Badge(ctx.T("booking.admin.optional")) : Ui.Badge(ctx.T("booking.admin.mandatory"))))),
                emptyText: ctx.T("booking.admin.noFees")) with { Below = new UiNode[] { Ui.Button(ctx.T("booking.admin.newFee"), Root + "fees/new?objekt=" + house.Id, icon: "ti-plus") } }), ctx.T("booking.admin.fees"));
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
        if (house is null) return NoHouse(ctx, ctx.T("booking.admin.fee"));
        var units = (await store.UnitsAsync(ct)).Where(u => u.PropertyId == house.Id).ToList();
        var self = Root + "fees/" + (f?.Id.ToString(CultureInfo.InvariantCulture) ?? "new?objekt=" + house.Id);
        if (ctx.Request.IsPost)
        {
            if (f is not null && ctx.Request.Action == "delete")
            {
                await store.Fees.DeleteAsync(f.Id, ct);
                return PluginResult.Redirect(Root + "fees?objekt=" + house.Id, ctx.T("booking.admin.feeDeleted"));
            }
            var name = B.Clip(ctx.Request.F("name"), 120);
            if (name.Length == 0 || B.ParseMoney(ctx.Request.F("amount")) is not { } amount) return PluginResult.Redirect(self, ctx.T("booking.admin.feeFieldsNeeded"), true);
            var per = FeePer.Keys.Contains(ctx.Request.F("per")) ? ctx.Request.F("per") : FeePer.Stay;
            var unitId = units.FirstOrDefault(u => B.Num(u.Id) == ctx.Request.F("unit_id"))?.Id ?? 0;
            var values = new Dictionary<string, object?>
            {
                ["property_id"] = house.Id, ["unit_id"] = unitId, ["name"] = name, ["amount"] = Math.Max(0, amount), ["per"] = per,
                ["optional"] = B.Checked(ctx.Request.F("optional")), ["tourist_tax"] = B.Checked(ctx.Request.F("tourist_tax")),
                ["children"] = ctx.Request.F("children") is "free" or "half" or "full" ? ctx.Request.F("children") : "full", ["sort"] = Int(ctx.Request.F("sort"), 0, -999, 999)
            };
            var id = f?.Id ?? 0;
            if (f is null) id = await store.Fees.InsertAsync(values, ct); else await store.Fees.UpdateAsync(f.Id, values, ct);
            return PluginResult.Redirect(Root + "fees/" + id, ctx.T("booking.admin.saved"));
        }
        var form = Ui.Form(
            Ui.Grid(200,
                Ui.Input("name", ctx.T("booking.admin.name"), f?.Name ?? "") with { Help = ctx.T("booking.admin.feeNameHelp") },
                Ui.Input("amount", ctx.T("booking.admin.amountCur", B.Symbol(Cur(ctx))), f is null ? "" : B.MoneyField(f.Amount)),
                Ui.Select("per", ctx.T("booking.admin.calculation"), f?.Per ?? FeePer.Stay, FeePer.Keys.Select(p => Ui.Option(p, FeePer.Label(ctx.T, p))).ToArray()),
                Ui.Select("unit_id", ctx.T("booking.admin.appliesTo"), B.Num(f?.UnitId ?? 0), new[] { Ui.Option("0", ctx.T("booking.admin.allUnitsLower")) }.Concat(units.Select(u => Ui.Option(B.Num(u.Id), u.Name))).ToArray())),
            Ui.Checkbox("optional", ctx.T("booking.admin.optionalCheck"), f?.Optional ?? false),
            Ui.Checkbox("tourist_tax", ctx.T("booking.admin.touristTaxCheck"), f?.TouristTax ?? false),
            Ui.Select("children", ctx.T("booking.admin.childrenTax"), f?.Children ?? "full", Ui.Option("full", ctx.T("booking.admin.childFull")), Ui.Option("half", ctx.T("booking.admin.childHalf")), Ui.Option("free", ctx.T("booking.admin.childFree"))),
            Ui.Grid(160, Ui.Input("sort", ctx.T("booking.admin.sort"), B.Num(f?.Sort ?? 0), UiInputType.Number)),
            Ui.FormActions(Ui.Submit(ctx.T("booking.admin.save")), f is null ? null : Ui.Submit(ctx.T("booking.admin.delete"), UiButtonKind.Danger, "action", "delete", confirm: ctx.T("booking.admin.confirmDeleteFee"))));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(house.Name, f?.Name ?? ctx.T("booking.admin.newFee"), Ui.Button(ctx.T("booking.admin.allFees"), Root + "fees?objekt=" + house.Id, UiButtonKind.Ghost, "ti-arrow-left", small: true)),
            Ui.Card(form)), f?.Name ?? ctx.T("booking.admin.newFee"));
    }

    // ---- price check ------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> CheckAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        if (units.Count == 0) return PluginResult.Page(Ui.Stack(Ui.PageHead(null, ctx.T("booking.admin.navCheck")), Ui.Card(Ui.Empty(ctx.T("booking.admin.noBookableUnit")),
            Ui.Button(ctx.T("booking.admin.addUnit"), Root + "units/new", icon: "ti-plus"))), ctx.T("booking.admin.navCheck"));
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
        var quote = await store.QuoteAsync(unit.Id, stay, Cur(ctx), ct, ctx.T);
        var free = stay.Nights > 0 && await store.IsFreeAsync(unit, arrival, departure, ct);
        var cur = Cur(ctx);

        var form = Ui.Form(
            Ui.Grid(140,
                Ui.Select("unit", ctx.T("booking.admin.unit"), B.Num(unit.Id), units.Select(u => Ui.Option(B.Num(u.Id), u.Name)).ToArray()),
                Ui.Input("arrival", ctx.T("booking.admin.arrival"), B.Iso(arrival), UiInputType.Date), Ui.Input("departure", ctx.T("booking.admin.departure"), B.Iso(departure), UiInputType.Date),
                Ui.Input("adults", ctx.T("booking.admin.adults"), B.Num(adults), UiInputType.Number), Ui.Input("children", ctx.T("booking.admin.children"), B.Num(children), UiInputType.Number)),
            fees.Any(f => f.Optional && (f.UnitId == 0 || f.UnitId == unit.Id))
                ? Ui.Grid(220, fees.Where(f => f.Optional && (f.UnitId == 0 || f.UnitId == unit.Id)).Select(f => (UiNode?)Ui.Checkbox("fee_" + f.Id, f.Name + " (" + B.Money(f.Amount, cur) + " " + FeePer.Label(ctx.T, f.Per) + ")", optional.Contains(f.Id))).ToArray())
                : null,
            Ui.FormActions(Ui.Submit(ctx.T("booking.admin.calculate"), icon: "ti-calculator")));
        UiNode result = quote is null ? Ui.Empty(ctx.T("booking.admin.unitNotBookable")) : Ui.Stack(
            Ui.Inline(
                Ui.Badge(stay.Nights == 1 ? ctx.T("booking.price.oneNight") : ctx.T("booking.price.nNights", stay.Nights), UiTone.Info),
                free ? Ui.Badge(ctx.T("booking.admin.free"), UiTone.Success) : Ui.Badge(ctx.T("booking.admin.taken"), UiTone.Error),
                quote.Ok ? Ui.Badge(ctx.T("booking.admin.bookable"), UiTone.Success) : Ui.Badge(ctx.T("booking.admin.doesNotFit"), UiTone.Warning)),
            quote.Problems.Count == 0 ? null : Ui.Alert(string.Join(" ", quote.Problems), UiTone.Warning),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.admin.colItem")), Ui.Column(ctx.T("booking.admin.calculation")), Ui.Column(ctx.T("booking.admin.amount"), right: true) },
                // a week line's unit is the week price (the line's text is "1 Woche …" / "1 week …" in the language it was priced in)
                quote.Lines.Select(l => Ui.Row(l.Text, l.Detail.Length > 0 ? Ui.Muted(l.Detail) : l.Kind == "nights" && l.Quantity > 0 ? Ui.Muted(B.Money(l.Unit, cur) + " " + (l.Text.Contains("Woche") || l.Text.Contains("week") ? ctx.T("booking.admin.perWeek") : ctx.T("booking.per.night"))) : null, B.Money(l.Amount, cur)))
                    .Append(Ui.Row(Ui.Text(ctx.T("booking.admin.total")), null, Ui.Text(B.Money(quote.Total, cur))))));
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.admin.navCheck")),
            Ui.Card(ctx.T("booking.admin.stay"), form), Ui.Card(unit.Name, result)), ctx.T("booking.admin.navCheck"));
    }
}
