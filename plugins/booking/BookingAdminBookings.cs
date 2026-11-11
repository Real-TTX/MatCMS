using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// Admin → Buchungen → Anfragen &amp; Buchungen: the list (open, coming, past, all) and one booking with its next steps as buttons — send an offer
/// (with a discount or surcharge), confirm, decline, cancel — its bill, the guest's contact and message, and the history (every move and every mail,
/// sent or not). "Neue Buchung" is the booking by phone: it may break the house's rules and is confirmed at once.
/// </summary>
public static class BookingAdminBookings
{
    private static string R => BookingAdmin.Root;

    public static IAdminSection Register(IAdminSection s) => s
        .Page("bookings", null, ListAsync)
        .Page("bookings/new", null, BookingFederation.Pushing(NewAsync))
        .Page("bookings/{id}", null, BookingFederation.Pushing(DetailAsync))
        .Page("bookings/{id}/pdf", null, PdfAsync);

    private static async Task<PluginResult> PdfAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id) || await BookingFlow.GetAsync(ctx.Data, id, ct) is not { } b) return PluginResult.NotFound();
        return await ConfirmationPdfAsync(ctx, b, ct);
    }

    /// <summary>The confirmation PDF of a booking — the admin's link and the guest's page use this one.</summary>
    internal static async Task<PluginResult> ConfirmationPdfAsync(IPluginContext ctx, Booking b, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var pdf = BookingPrint.Confirmation(await BookingPrint.LetterheadAsync(ctx, ct), b, unit, house, await BookingPayments.PlanAsync(ctx, b, ct),
            ctx.Settings.Get(BookingPlugin.PaymentInfoKey) ?? "", BookingPlugin.Currency(ctx));
        return BookingPrint.Result(pdf, ctx.T("booking.bookings.title", b.Number));
    }

    private static int Int(string s, int def, int min, int max) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;

    private static async Task<PluginResult> ListAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).ToDictionary(u => u.Id);
        var tab = ctx.Request.Q("tab") is "kommend" or "vergangen" or "alle" ? ctx.Request.Q("tab") : "offen";
        var today = B.Today;
        var all = await BookingFlow.ListAsync(ctx.Data, null, ct);
        var list = tab switch
        {
            "kommend" => all.Where(b => b.Status == BookingStatus.Confirmed && b.Departure >= today).ToList(),
            "vergangen" => all.Where(b => b.Departure < today).OrderByDescending(b => b.Arrival).ToList(),
            "alle" => all.OrderByDescending(b => b.Created).ToList(),
            _ => all.Where(b => b.Status is BookingStatus.Request or BookingStatus.Offer).OrderBy(b => b.Created).ToList()
        };
        var cur = BookingPlugin.Currency(ctx);
        var paid = await BookingPayments.PaidAsync(ctx.Data, ct);
        UiNode? PaidBadge(Booking b) => b.Status != BookingStatus.Confirmed ? null
            : paid.TryGetValue(b.Id, out var v) && v >= b.Total ? Ui.Badge(ctx.T("booking.bookings.paid"), UiTone.Success) : v > 0 ? Ui.Badge(ctx.T("booking.bookings.partPaid"), UiTone.Info) : Ui.Badge(ctx.T("booking.bookings.unpaid"), UiTone.Warning);
        int Count(string t) => t switch
        {
            "offen" => all.Count(b => b.Status is BookingStatus.Request or BookingStatus.Offer),
            "kommend" => all.Count(b => b.Status == BookingStatus.Confirmed && b.Departure >= today),
            _ => 0
        };
        string TabLabel(string key, string label) => Count(key) is var n and > 0 ? label + " (" + n + ")" : label;
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead(null, ctx.T("booking.bookings.listTitle")),
            Ui.Tabs(Ui.Tab(TabLabel("offen", ctx.T("booking.bookings.tabOpen")), R + "bookings", tab == "offen"), Ui.Tab(TabLabel("kommend", ctx.T("booking.bookings.tabComing")), R + "bookings?tab=kommend", tab == "kommend"),
                Ui.Tab(ctx.T("booking.bookings.tabPast"), R + "bookings?tab=vergangen", tab == "vergangen"), Ui.Tab(ctx.T("booking.bookings.tabAll"), R + "bookings?tab=alle", tab == "alle")),
            // status and payment in two columns: two badges in one cell wrapped and made every row twice as high
            Ui.Table(new[] { Ui.Column(ctx.T("booking.bookings.colNumber")), Ui.Column(ctx.T("booking.bookings.colGuest")), Ui.Column(ctx.T("booking.bookings.colUnit")), Ui.Column(ctx.T("booking.bookings.colPeriod")),
                    Ui.Column(ctx.T("booking.bookings.colAmount"), right: true), Ui.Column(ctx.T("booking.bookings.colStatus")), Ui.Column(ctx.T("booking.bookings.colPayment")) },
                list.Select(b => Ui.RowFor(b.Number + " " + b.Name + " " + b.Email, Ui.Link(b.Number, R + "bookings/" + b.Id), b.Name,
                    units.TryGetValue(b.UnitId, out var u) ? u.Name : "–", B.Date(b.Arrival) + " – " + B.Date(b.Departure) + " (" + b.Nights + ")", B.Money(b.Total, cur),
                    Ui.Badge(BookingStatus.Label(ctx.T, b.Status), BookingStatus.Tone(b.Status)), PaidBadge(b))),
                searchPlaceholder: ctx.T("booking.bookings.search"),
                emptyText: tab == "offen" ? ctx.T("booking.bookings.noOpen") : ctx.T("booking.bookings.none"))
                with { Below = new UiNode[] { Ui.Button(ctx.T("booking.bookings.new"), R + "bookings/new", icon: "ti-plus") } }), ctx.T("booking.bookings.listTitle"));
    }

    // ---- by phone ---------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> NewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        if (units.Count == 0) return PluginResult.Redirect(R + "units", ctx.T("booking.bookings.unitFirst"), true);
        if (ctx.Request.IsPost)
        {
            var f = ctx.Request;
            var unit = units.FirstOrDefault(u => B.Num(u.Id) == f.F("unit"));
            if (unit is null || B.Day(f.F("arrival")) is not { } a || B.Day(f.F("departure")) is not { } d || B.Clip(f.F("name"), 120).Length == 0)
                return PluginResult.Redirect(R + "bookings/new", ctx.T("booking.bookings.fillIn"), true);
            var stay = new Stay(a, d, Math.Max(1, Int(f.F("adults"), 2, 1, 50)), Int(f.F("children"), 0, 0, 50));
            Booking b; string key;
            try
            {
                (b, key) = await BookingFlow.RequestAsync(ctx.Data, new BookingFlow.RequestInput(unit, stay, f.F("name"), f.F("email"), f.F("phone"), f.F("note"), "de", "admin"),
                    BookingPlugin.HoldHours(ctx), BookingPlugin.Currency(ctx), ct, enforceRules: false, tr: ctx.T);
                b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Confirmed, "Direkt bestätigt (im Admin angelegt)", null, ct);
            }
            catch (BookingException ex) { return PluginResult.Redirect(R + "bookings/new", ex.Text(ctx), true); }
            if (B.Checked(f.F("notify")) && b.Email.Contains('@'))
                await BookingFlow.NoteAsync(ctx.Data, b.Id, "Bestätigung an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Confirmed, key, null, ct), ct);
            return PluginResult.Redirect(R + "bookings/" + b.Id, ctx.T("booking.bookings.created", b.Number));
        }
        var form = Ui.Form(
            Ui.Grid(180,
                Ui.Select("unit", ctx.T("booking.bookings.colUnit"), units.Any(u => B.Num(u.Id) == ctx.Request.Q("unit")) ? ctx.Request.Q("unit") : B.Num(units[0].Id), units.Select(u => Ui.Option(B.Num(u.Id), u.Name)).ToArray()),
                Ui.Input("arrival", ctx.T("booking.bookings.arrival"), B.Day(ctx.Request.Q("arrival")) is { } qa ? B.Iso(qa) : "", UiInputType.Date),
                Ui.Input("departure", ctx.T("booking.bookings.departure"), B.Day(ctx.Request.Q("arrival")) is { } qd ? B.Iso(qd.AddDays(7)) : "", UiInputType.Date),
                Ui.Input("adults", ctx.T("booking.bookings.adults"), "2", UiInputType.Number), Ui.Input("children", ctx.T("booking.bookings.children"), "0", UiInputType.Number)),
            Ui.Grid(220, Ui.Input("name", ctx.T("booking.bookings.name")), Ui.Input("email", ctx.T("booking.bookings.email"), "", UiInputType.Email), Ui.Input("phone", ctx.T("booking.bookings.phone"), "", UiInputType.Tel)),
            Ui.TextArea("note", ctx.T("booking.bookings.note"), "", 3),
            Ui.Checkbox("notify", ctx.T("booking.bookings.sendConfirmation"), true),
            Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.create"), icon: "ti-check")));
        return PluginResult.Page(Ui.Stack(Ui.PageHead(null, ctx.T("booking.bookings.new"),
            Ui.Button(ctx.T("booking.bookings.all"), R + "bookings", UiButtonKind.Ghost, "ti-arrow-left", small: true)), Ui.Card(form)), ctx.T("booking.bookings.new"));
    }

    // ---- one booking ----------------------------------------------------------------------------------------------

    private static async Task<string> SendAsync(IPluginContext ctx, BookingStore store, Booking b, string template, string? key, string? reason, CancellationToken ct)
    {
        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var values = BookingMail.Values(ctx, b, unit, house, key);
        if (reason is not null) values["grund"] = reason;
        return await BookingMail.SendAsync(ctx, template, b.Email, values, ct);
    }

    private static async Task<PluginResult> DetailAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (!long.TryParse(ctx.Request.R("id"), out var id)) return PluginResult.NotFound();
        var store = new BookingStore(ctx.Data);
        var b = await BookingFlow.GetAsync(ctx.Data, id, ct);
        if (b is null) return PluginResult.NotFound();
        var self = R + "bookings/" + b.Id;
        var f = ctx.Request;

        if (f.IsPost)
        {
            try
            {
                switch (f.Action)
                {
                    case "offer":
                    {
                        var adj = B.ParseMoney(f.F("adjust")) ?? 0;
                        b = await BookingFlow.OfferAsync(ctx.Data, b, adj, B.Clip(f.F("adjust_text"), 120), Int(f.F("days"), BookingPlugin.OfferDays(ctx), 1, 30), ct, ctx.T);
                        var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
                        await BookingFlow.NoteAsync(ctx.Data, b.Id, "Angebot an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Offer, key, null, ct), ct);
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.offerSent"));
                    }
                    case "confirm":
                    {
                        b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Confirmed, "Vom Gastgeber bestätigt", null, ct);
                        if (B.Checked(f.F("notify")))
                        {
                            var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
                            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Bestätigung an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Confirmed, key, null, ct), ct);
                        }
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.confirmed"));
                    }
                    case "decline":
                    {
                        var reason = B.Clip(f.F("reason"), 1000);
                        b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Declined, "Abgelehnt" + (reason.Length > 0 ? ": " + reason : ""), null, ct);
                        if (B.Checked(f.F("notify")))
                            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Absage an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Declined, null, reason, ct), ct);
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.declined"));
                    }
                    case "cancel":
                    {
                        var reason = B.Clip(f.F("reason"), 1000);
                        b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Cancelled, "Storniert" + (reason.Length > 0 ? ": " + reason : ""), null, ct);
                        if (B.Checked(f.F("notify")))
                            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Stornierung an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Cancelled, null, reason, ct), ct);
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.cancelled"));
                    }
                    case "pay":
                    {
                        var amount = B.ParseMoney(f.F("amount")) ?? 0;
                        await BookingPayments.AddAsync(ctx.Data, b.Id, amount, B.Day(f.F("day")) ?? B.Today,
                            BookingPayments.Methods.Contains(f.F("method")) ? f.F("method") : "", f.F("pay_note"), ct);
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.paymentAdded"));
                    }
                    case "unpay":
                        if (long.TryParse(f.F("id"), out var pid)) await BookingPayments.RemoveAsync(ctx.Data, b.Id, pid, ct);
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.paymentRemoved"));
                    case "note":
                        await ctx.Data.Table(BookingStore.TBookings).UpdateAsync(b.Id, new Dictionary<string, object?> { ["host_note"] = B.Clip(f.F("host_note"), 4000) }, ct);
                        return PluginResult.Redirect(self, ctx.T("booking.bookings.noteSaved"));
                }
            }
            catch (BookingException ex) { return PluginResult.Redirect(self, ex.Text(ctx), true); }
            return PluginResult.Redirect(self);
        }

        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var cur = BookingPlugin.Currency(ctx);
        var open = b.Status is BookingStatus.Request or BookingStatus.Offer;
        var hasPayments = b.Status == BookingStatus.Confirmed || (await BookingPayments.ListAsync(ctx.Data, b.Id, ct)).Count > 0;

        // the next steps are buttons in the page's head (as an order in the shop): on a phone they are at the top, not under the whole booking.
        // Each opens a dialog with the form it always had — same fields, same actions.
        var steps = new List<UiNode?>();
        var dialogs = new List<UiNode?>();
        if (open)
        {
            var offerTitle = b.Status == BookingStatus.Offer ? ctx.T("booking.bookings.offerAgain") : ctx.T("booking.bookings.sendOffer");
            steps.Add(Ui.Submit(offerTitle, UiButtonKind.Primary, icon: "ti-send", small: true) with { Opens = "offer-dialog" });
            steps.Add(Ui.Submit(ctx.T("booking.bookings.confirm"), UiButtonKind.Ghost, icon: "ti-check", small: true) with { Opens = "confirm-dialog" });
            steps.Add(Ui.Submit(ctx.T("booking.bookings.decline"), UiButtonKind.Danger, icon: "ti-x", small: true) with { Opens = "decline-dialog" });
            dialogs.Add(Ui.Dialog("offer-dialog", offerTitle, Ui.Form(
                Ui.Grid(160, Ui.Input("adjust", ctx.T("booking.bookings.adjust"), ""), Ui.Input("adjust_text", ctx.T("booking.bookings.adjustText"), "") with { Help = ctx.T("booking.bookings.adjustHelp") },
                    Ui.Input("days", ctx.T("booking.bookings.validDays"), B.Num(BookingPlugin.OfferDays(ctx)), UiInputType.Number)),
                Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.sendOffer"), UiButtonKind.Primary, "action", "offer", icon: "ti-send")))));
            dialogs.Add(Ui.Dialog("confirm-dialog", ctx.T("booking.bookings.confirmDirect"), Ui.Form(Ui.Checkbox("notify", ctx.T("booking.bookings.sendConfirmation"), true),
                Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.confirm"), UiButtonKind.Primary, "action", "confirm", icon: "ti-check")))));
            dialogs.Add(Ui.Dialog("decline-dialog", ctx.T("booking.bookings.decline"), Ui.Form(Ui.TextArea("reason", ctx.T("booking.bookings.reason"), "", 2), Ui.Checkbox("notify", ctx.T("booking.bookings.sendDecline"), true),
                Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.decline"), UiButtonKind.Danger, "action", "decline", confirm: ctx.T("booking.bookings.confirmDecline"))))));
        }
        else if (b.Status == BookingStatus.Confirmed)
        {
            steps.Add(Ui.Submit(ctx.T("booking.bookings.cancel"), UiButtonKind.Danger, icon: "ti-x", small: true) with { Opens = "cancel-dialog" });
            dialogs.Add(Ui.Dialog("cancel-dialog", ctx.T("booking.bookings.cancel"), Ui.Form(Ui.TextArea("reason", ctx.T("booking.bookings.reason"), "", 2), Ui.Checkbox("notify", ctx.T("booking.bookings.sendCancel"), true),
                Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.cancel"), UiButtonKind.Danger, "action", "cancel", confirm: ctx.T("booking.bookings.confirmCancel"))))));
        }
        var headActions = new List<UiNode?> { Ui.Button(ctx.T("booking.bookings.all"), R + "bookings", UiButtonKind.Ghost, "ti-arrow-left", small: true) };
        if (hasPayments) headActions.Add(Ui.Button(ctx.T("booking.bookings.pdf"), self + "/pdf", UiButtonKind.Ghost, "ti-file-type-pdf", small: true));
        headActions.AddRange(steps);
        var head = Ui.PageHead((unit?.Name ?? "–") + " · " + B.Date(b.Arrival) + " – " + B.Date(b.Departure), ctx.T("booking.bookings.title", b.Number), headActions.ToArray());

        var facts = Ui.Card(ctx.T("booking.bookings.stay"), Ui.Stack(
            Ui.Inline(Ui.Badge(BookingStatus.Label(ctx.T, b.Status), BookingStatus.Tone(b.Status)), b.Source == "admin" ? Ui.Badge(ctx.T("booking.bookings.byAdmin")) : Ui.Badge(ctx.T("booking.bookings.website"))),
            Ui.KeyValue(new[] { (ctx.T("booking.bookings.colUnit"), (unit?.Name ?? "–") + (house is null ? "" : ", " + house.Name)),
                (ctx.T("booking.bookings.arrival"), B.Weekday(ctx.T, b.Arrival) + " " + B.Date(b.Arrival)), (ctx.T("booking.bookings.departure"), B.Weekday(ctx.T, b.Departure) + " " + B.Date(b.Departure)),
                (ctx.T("booking.bookings.nights"), B.Num(b.Nights)), (ctx.T("booking.bookings.guests"), BookingMail.Persons(b.Adults, b.Children)),
                (ctx.T("booking.bookings.heldUntil"), b.Status == BookingStatus.Request ? BookingMail.Local(b.HoldUntil) : ""),
                (ctx.T("booking.bookings.offerUntil"), b.Status == BookingStatus.Offer ? BookingMail.Local(b.OfferUntil) : "") }.Where(x => x.Item2.Length > 0).ToArray()),
            Ui.Table(new[] { Ui.Column(ctx.T("booking.bookings.colItem")), Ui.Column(ctx.T("booking.bookings.colCalc")), Ui.Column(ctx.T("booking.bookings.colAmount"), right: true) },
                b.Lines.Select(l => Ui.Row(l.Text, l.Detail.Length > 0 ? Ui.Muted(l.Detail) : null, B.Money(l.Amount, cur)))
                    .Append(Ui.Row(Ui.Text(ctx.T("booking.bookings.total")), null, Ui.Text(B.Money(b.Total, cur)))))));
        var guest = Ui.Card(ctx.T("booking.bookings.colGuest"), Ui.Stack(
            Ui.KeyValue(new[] { (ctx.T("booking.bookings.name"), b.Name), (ctx.T("booking.bookings.email"), b.Email), (ctx.T("booking.bookings.phone"), b.Phone) }.Where(x => x.Item2.Length > 0).ToArray()),
            b.GuestId > 0 ? Ui.Link(ctx.T("booking.bookings.guestCard"), R + "guests/" + b.GuestId) : null,
            b.Message.Length > 0 ? Ui.Stack(Ui.Heading(ctx.T("booking.bookings.message"), 3), Ui.Text(b.Message)) : null,
            Ui.Form(Ui.TextArea("host_note", ctx.T("booking.bookings.hostNote"), (await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct))?.Text("host_note") ?? "", 3),
                Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.saveNote"), UiButtonKind.Ghost, "action", "note", small: true)))));
        UiNode? payments = null;
        if (hasPayments)
        {
            var plan = await BookingPayments.PlanAsync(ctx, b, ct);
            var list = await BookingPayments.ListAsync(ctx.Data, b.Id, ct);
            var two = plan.Deposit > 0 && plan.Deposit < plan.Total;
            payments = Ui.Card(ctx.T("booking.bookings.payments"), Ui.Stack(
                Ui.Inline(Ui.Badge(plan.StateLabel(ctx.T), plan.Tone), Ui.Text(ctx.T("booking.bookings.paidOf", B.Money(plan.Paid, cur), B.Money(plan.Total, cur)))),
                Ui.KeyValue(new[]
                {
                    (two ? ctx.T("booking.bookings.deposit") : ctx.T("booking.bookings.totalAmount"), ctx.T("booking.bookings.dueBy", B.Money(two ? plan.Deposit : plan.Total, cur), B.Date(plan.DepositDue))),
                    (two ? ctx.T("booking.bookings.rest") : "", two ? ctx.T("booking.bookings.dueBy", B.Money(plan.Rest, cur), B.Date(plan.RestDue)) : "")
                }.Where(x => x.Item1.Length > 0).ToArray()),
                list.Count == 0 ? null : Ui.Table(new[] { Ui.Column(ctx.T("booking.bookings.colDay")), Ui.Column(ctx.T("booking.bookings.colMethod")), Ui.Column(ctx.T("booking.bookings.colAmount"), right: true), Ui.Column("", actions: true) },
                    list.Select(x => Ui.Row(B.Date(x.Day), BookingPayments.MethodLabel(ctx.T, x.Method) + (x.Note.Length > 0 ? " · " + x.Note : ""), B.Money(x.Amount, cur),
                        Ui.Action(ctx.T("booking.bookings.remove"), "unpay", B.Num(x.Id), confirm: ctx.T("booking.bookings.confirmRemovePayment"), icon: "ti-trash")))),
                Ui.Form(Ui.Grid(140,
                        Ui.Input("amount", ctx.T("booking.bookings.amount"), plan.Open > 0 ? B.MoneyField(plan.Paid < plan.Deposit && two ? plan.Deposit - plan.Paid : plan.Open) : ""),
                        Ui.Input("day", ctx.T("booking.bookings.receivedOn"), B.Iso(B.Today), UiInputType.Date),
                        Ui.Select("method", ctx.T("booking.bookings.colMethod"), BookingPayments.Methods[0], BookingPayments.Methods.Select(m => Ui.Option(m, BookingPayments.MethodLabel(ctx.T, m))).ToArray()),
                        Ui.Input("pay_note", ctx.T("booking.bookings.note"))),
                    Ui.FormActions(Ui.Submit(ctx.T("booking.bookings.addPayment"), UiButtonKind.Ghost, "action", "pay", icon: "ti-cash")))));
        }
        var checkin = await BookingAdminCheckins.CardAsync(ctx, b, ct);
        // what happened comes first and takes the width; the time is a narrow column at the right
        var history = Ui.Card(ctx.T("booking.bookings.history"), Ui.Table(new[] { Ui.Column(ctx.T("booking.bookings.colWhat")), Ui.Column(ctx.T("booking.bookings.colWhen"), right: true) },
            b.History.AsEnumerable().Reverse().Select(h => Ui.Row(h.Text, Ui.Muted(BookingMail.Local(h.At)))), emptyText: "–"));
        var page = new List<UiNode?> { head };
        page.AddRange(dialogs);
        page.Add(Ui.Columns(new UiNode?[] { facts, checkin, history }, new UiNode?[] { payments, guest }));
        return PluginResult.Page(Ui.Stack(page.ToArray()), ctx.T("booking.bookings.title", b.Number));
    }
}
