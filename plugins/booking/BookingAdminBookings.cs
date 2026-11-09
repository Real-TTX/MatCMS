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
        .Page("bookings/new", null, NewAsync)
        .Page("bookings/{id}", null, DetailAsync)
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
        return BookingPrint.Result(pdf, "Buchung " + b.Number);
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
            : paid.TryGetValue(b.Id, out var v) && v >= b.Total ? Ui.Badge("bezahlt", UiTone.Success) : v > 0 ? Ui.Badge("angezahlt", UiTone.Info) : Ui.Badge("unbezahlt", UiTone.Warning);
        int Count(string t) => t switch
        {
            "offen" => all.Count(b => b.Status is BookingStatus.Request or BookingStatus.Offer),
            "kommend" => all.Count(b => b.Status == BookingStatus.Confirmed && b.Departure >= today),
            _ => 0
        };
        string TabLabel(string key, string label) => Count(key) is var n and > 0 ? label + " (" + n + ")" : label;
        return PluginResult.Page(Ui.Stack(
            Ui.PageHead("Anfragen über die Website und Buchungen – eine Anfrage hält den Zeitraum für " + BookingPlugin.HoldHours(ctx) + " Stunden", "Anfragen & Buchungen"),
            Ui.Tabs(Ui.Tab(TabLabel("offen", "Offen"), R + "bookings", tab == "offen"), Ui.Tab(TabLabel("kommend", "Kommend"), R + "bookings?tab=kommend", tab == "kommend"),
                Ui.Tab("Vergangen", R + "bookings?tab=vergangen", tab == "vergangen"), Ui.Tab("Alle", R + "bookings?tab=alle", tab == "alle")),
            Ui.Table(new[] { Ui.Column("Nr."), Ui.Column("Gast"), Ui.Column("Unterkunft"), Ui.Column("Zeitraum"), Ui.Column("Betrag", right: true), Ui.Column("Status") },
                list.Select(b => Ui.RowFor(b.Number + " " + b.Name + " " + b.Email, Ui.Link(b.Number, R + "bookings/" + b.Id), b.Name,
                    units.TryGetValue(b.UnitId, out var u) ? u.Name : "–", B.Date(b.Arrival) + " – " + B.Date(b.Departure) + " (" + b.Nights + ")", B.Money(b.Total, cur),
                    Ui.Inline(Ui.Badge(BookingStatus.Label(b.Status), BookingStatus.Tone(b.Status)), PaidBadge(b)))),
                searchPlaceholder: "Name, E-Mail oder Nummer …",
                emptyText: tab == "offen" ? "Keine offenen Anfragen." : "Keine Buchungen.")
                with { Below = new UiNode[] { Ui.Button("Neue Buchung", R + "bookings/new", icon: "ti-plus") } }), "Anfragen & Buchungen");
    }

    // ---- by phone ---------------------------------------------------------------------------------------------------

    private static async Task<PluginResult> NewAsync(IPluginContext ctx, CancellationToken ct)
    {
        var store = new BookingStore(ctx.Data);
        var units = (await store.UnitsAsync(ct)).Where(u => u.Active).ToList();
        if (units.Count == 0) return PluginResult.Redirect(R + "units", "Zuerst eine Unterkunft anlegen.", true);
        if (ctx.Request.IsPost)
        {
            var f = ctx.Request;
            var unit = units.FirstOrDefault(u => B.Num(u.Id) == f.F("unit"));
            if (unit is null || B.Day(f.F("arrival")) is not { } a || B.Day(f.F("departure")) is not { } d || B.Clip(f.F("name"), 120).Length == 0)
                return PluginResult.Redirect(R + "bookings/new", "Bitte Unterkunft, Anreise, Abreise und Name angeben.", true);
            var stay = new Stay(a, d, Math.Max(1, Int(f.F("adults"), 2, 1, 50)), Int(f.F("children"), 0, 0, 50));
            Booking b; string key;
            try
            {
                (b, key) = await BookingFlow.RequestAsync(ctx.Data, new BookingFlow.RequestInput(unit, stay, f.F("name"), f.F("email"), f.F("phone"), f.F("note"), "de", "admin"),
                    BookingPlugin.HoldHours(ctx), BookingPlugin.Currency(ctx), ct, enforceRules: false);
                b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Confirmed, "Direkt bestätigt (im Admin angelegt)", null, ct);
            }
            catch (BookingException ex) { return PluginResult.Redirect(R + "bookings/new", ex.Message, true); }
            if (B.Checked(f.F("notify")) && b.Email.Contains('@'))
                await BookingFlow.NoteAsync(ctx.Data, b.Id, "Bestätigung an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Confirmed, key, null, ct), ct);
            return PluginResult.Redirect(R + "bookings/" + b.Id, "Buchung " + b.Number + " angelegt.");
        }
        var form = Ui.Form(
            Ui.Grid(180,
                Ui.Select("unit", "Unterkunft", units.Any(u => B.Num(u.Id) == ctx.Request.Q("unit")) ? ctx.Request.Q("unit") : B.Num(units[0].Id), units.Select(u => Ui.Option(B.Num(u.Id), u.Name)).ToArray()),
                Ui.Input("arrival", "Anreise", B.Day(ctx.Request.Q("arrival")) is { } qa ? B.Iso(qa) : "", UiInputType.Date),
                Ui.Input("departure", "Abreise", B.Day(ctx.Request.Q("arrival")) is { } qd ? B.Iso(qd.AddDays(7)) : "", UiInputType.Date),
                Ui.Input("adults", "Erwachsene", "2", UiInputType.Number), Ui.Input("children", "Kinder", "0", UiInputType.Number)),
            Ui.Grid(220, Ui.Input("name", "Name"), Ui.Input("email", "E-Mail", "", UiInputType.Email), Ui.Input("phone", "Telefon", "", UiInputType.Tel)),
            Ui.TextArea("note", "Notiz", "", 3),
            Ui.Checkbox("notify", "Buchungsbestätigung an den Gast schicken", true),
            Ui.Muted("Der Preis kommt aus der Preisliste; Mindestnächte und Anreisetag gelten hier nicht (Ausnahme am Telefon). Belegte Nächte gehen nie."),
            Ui.FormActions(Ui.Submit("Buchung anlegen", icon: "ti-check")));
        return PluginResult.Page(Ui.Stack(Ui.PageHead("Eine Buchung per Telefon oder E-Mail – sofort bestätigt", "Neue Buchung",
            Ui.Button("Alle Buchungen", R + "bookings", UiButtonKind.Ghost, "ti-arrow-left", small: true)), Ui.Card(form)), "Neue Buchung");
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
                        b = await BookingFlow.OfferAsync(ctx.Data, b, adj, B.Clip(f.F("adjust_text"), 120), Int(f.F("days"), BookingPlugin.OfferDays(ctx), 1, 30), ct);
                        var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
                        await BookingFlow.NoteAsync(ctx.Data, b.Id, "Angebot an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Offer, key, null, ct), ct);
                        return PluginResult.Redirect(self, "Angebot geschickt.");
                    }
                    case "confirm":
                    {
                        b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Confirmed, "Vom Gastgeber bestätigt", null, ct);
                        if (B.Checked(f.F("notify")))
                        {
                            var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
                            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Bestätigung an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Confirmed, key, null, ct), ct);
                        }
                        return PluginResult.Redirect(self, "Bestätigt.");
                    }
                    case "decline":
                    {
                        var reason = B.Clip(f.F("reason"), 1000);
                        b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Declined, "Abgelehnt" + (reason.Length > 0 ? ": " + reason : ""), null, ct);
                        if (B.Checked(f.F("notify")))
                            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Absage an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Declined, null, reason, ct), ct);
                        return PluginResult.Redirect(self, "Abgelehnt – der Zeitraum ist wieder frei.");
                    }
                    case "cancel":
                    {
                        var reason = B.Clip(f.F("reason"), 1000);
                        b = await BookingFlow.MoveAsync(ctx.Data, b.Id, BookingStatus.Cancelled, "Storniert" + (reason.Length > 0 ? ": " + reason : ""), null, ct);
                        if (B.Checked(f.F("notify")))
                            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Stornierung an den Gast: " + await SendAsync(ctx, store, b, BookingMail.Cancelled, null, reason, ct), ct);
                        return PluginResult.Redirect(self, "Storniert – der Zeitraum ist wieder frei.");
                    }
                    case "pay":
                    {
                        var amount = B.ParseMoney(f.F("amount")) ?? 0;
                        await BookingPayments.AddAsync(ctx.Data, b.Id, amount, B.Day(f.F("day")) ?? B.Today,
                            BookingPayments.Methods.Contains(f.F("method")) ? f.F("method") : "", f.F("pay_note"), ct);
                        return PluginResult.Redirect(self, "Zahlung eingetragen.");
                    }
                    case "unpay":
                        if (long.TryParse(f.F("id"), out var pid)) await BookingPayments.RemoveAsync(ctx.Data, b.Id, pid, ct);
                        return PluginResult.Redirect(self, "Zahlung entfernt.");
                    case "note":
                        await ctx.Data.Table(BookingStore.TBookings).UpdateAsync(b.Id, new Dictionary<string, object?> { ["host_note"] = B.Clip(f.F("host_note"), 4000) }, ct);
                        return PluginResult.Redirect(self, "Notiz gespeichert.");
                }
            }
            catch (BookingException ex) { return PluginResult.Redirect(self, ex.Message, true); }
            return PluginResult.Redirect(self);
        }

        var unit = await store.UnitAsync(b.UnitId, ct);
        var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
        var cur = BookingPlugin.Currency(ctx);
        var open = b.Status is BookingStatus.Request or BookingStatus.Offer;
        var head = Ui.PageHead((unit?.Name ?? "–") + " · " + B.Date(b.Arrival) + " – " + B.Date(b.Departure), "Buchung " + b.Number,
            Ui.Button("Alle Buchungen", R + "bookings", UiButtonKind.Ghost, "ti-arrow-left", small: true));

        var steps = new List<UiNode?>();
        if (open)
        {
            steps.Add(Ui.Card(b.Status == BookingStatus.Offer ? "Angebot erneut schicken" : "Angebot schicken", Ui.Form(
                Ui.Muted("Der Gast bekommt den Preis und einen Link zum verbindlichen Buchen."),
                Ui.Grid(160, Ui.Input("adjust", "Nachlass (−) oder Aufschlag", ""), Ui.Input("adjust_text", "Text dazu", "") with { Help = "z. B. „Stammgast-Rabatt“" },
                    Ui.Input("days", "Gilt Tage", B.Num(BookingPlugin.OfferDays(ctx)), UiInputType.Number)),
                Ui.FormActions(Ui.Submit("Angebot schicken", UiButtonKind.Primary, "action", "offer", icon: "ti-send")))));
            steps.Add(Ui.Card("Direkt bestätigen", Ui.Form(Ui.Checkbox("notify", "Buchungsbestätigung an den Gast schicken", true),
                Ui.FormActions(Ui.Submit("Bestätigen", UiButtonKind.Ghost, "action", "confirm", icon: "ti-check")))));
            steps.Add(Ui.Card("Ablehnen", Ui.Form(Ui.TextArea("reason", "Grund (steht in der Mail)", "", 2), Ui.Checkbox("notify", "Absage an den Gast schicken", true),
                Ui.FormActions(Ui.Submit("Ablehnen", UiButtonKind.Danger, "action", "decline", confirm: "Anfrage ablehnen? Der Zeitraum wird wieder frei.")))));
        }
        else if (b.Status == BookingStatus.Confirmed)
            steps.Add(Ui.Card("Stornieren", Ui.Form(Ui.TextArea("reason", "Grund (steht in der Mail)", "", 2), Ui.Checkbox("notify", "Stornierung an den Gast schicken", true),
                Ui.FormActions(Ui.Submit("Stornieren", UiButtonKind.Danger, "action", "cancel", confirm: "Buchung stornieren? Der Zeitraum wird wieder frei.")))));

        var facts = Ui.Card("Aufenthalt", Ui.Stack(
            Ui.Inline(Ui.Badge(BookingStatus.Label(b.Status), BookingStatus.Tone(b.Status)), b.Source == "admin" ? Ui.Badge("im Admin angelegt") : Ui.Badge("Website")),
            Ui.KeyValue(new[] { ("Unterkunft", (unit?.Name ?? "–") + (house is null ? "" : ", " + house.Name)),
                ("Anreise", B.Weekday(b.Arrival) + " " + B.Date(b.Arrival)), ("Abreise", B.Weekday(b.Departure) + " " + B.Date(b.Departure)),
                ("Nächte", B.Num(b.Nights)), ("Gäste", BookingMail.Persons(b.Adults, b.Children)),
                ("Vorgemerkt bis", b.Status == BookingStatus.Request ? BookingMail.Local(b.HoldUntil) : ""),
                ("Angebot gilt bis", b.Status == BookingStatus.Offer ? BookingMail.Local(b.OfferUntil) : "") }.Where(x => x.Item2.Length > 0).ToArray()),
            Ui.Table(new[] { Ui.Column("Posten"), Ui.Column("Berechnung"), Ui.Column("Betrag", right: true) },
                b.Lines.Select(l => Ui.Row(l.Text, l.Detail.Length > 0 ? Ui.Muted(l.Detail) : null, B.Money(l.Amount, cur)))
                    .Append(Ui.Row(Ui.Text("Gesamt"), null, Ui.Text(B.Money(b.Total, cur)))))));
        var guest = Ui.Card("Gast", Ui.Stack(
            b.GuestId > 0 ? Ui.Button("Gästekarte", R + "guests/" + b.GuestId, UiButtonKind.Ghost, "ti-address-book", small: true) : null,
            Ui.KeyValue(new[] { ("Name", b.Name), ("E-Mail", b.Email), ("Telefon", b.Phone) }.Where(x => x.Item2.Length > 0).ToArray()),
            b.Message.Length > 0 ? Ui.Stack(Ui.Heading("Nachricht", 3), Ui.Text(b.Message)) : null,
            Ui.Form(Ui.TextArea("host_note", "Interne Notiz", (await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct))?.Text("host_note") ?? "", 3),
                Ui.FormActions(Ui.Submit("Notiz speichern", UiButtonKind.Ghost, "action", "note", small: true)))));
        UiNode? payments = null;
        if (b.Status == BookingStatus.Confirmed || (await BookingPayments.ListAsync(ctx.Data, b.Id, ct)).Count > 0)
        {
            var plan = await BookingPayments.PlanAsync(ctx, b, ct);
            var list = await BookingPayments.ListAsync(ctx.Data, b.Id, ct);
            var two = plan.Deposit > 0 && plan.Deposit < plan.Total;
            payments = Ui.Card("Zahlungen", Ui.Stack(
                Ui.Inline(Ui.Badge(plan.State, plan.Tone), Ui.Text(B.Money(plan.Paid, cur) + " von " + B.Money(plan.Total, cur) + " bezahlt")),
                Ui.KeyValue(new[]
                {
                    (two ? "Anzahlung" : "Gesamtbetrag", B.Money(two ? plan.Deposit : plan.Total, cur) + " bis " + B.Date(plan.DepositDue)),
                    (two ? "Rest" : "", two ? B.Money(plan.Rest, cur) + " bis " + B.Date(plan.RestDue) : ""),
                    ("Noch offen", B.Money(plan.Open, cur))
                }.Where(x => x.Item1.Length > 0).ToArray()),
                list.Count == 0 ? null : Ui.Table(new[] { Ui.Column("Tag"), Ui.Column("Art"), Ui.Column("Betrag", right: true), Ui.Column("", actions: true) },
                    list.Select(x => Ui.Row(B.Date(x.Day), x.Method + (x.Note.Length > 0 ? " · " + x.Note : ""), B.Money(x.Amount, cur),
                        Ui.Action("Entfernen", "unpay", B.Num(x.Id), confirm: "Zahlung entfernen?", icon: "ti-trash")))),
                Ui.Form(Ui.Grid(140,
                        Ui.Input("amount", "Betrag", plan.Open > 0 ? B.MoneyField(plan.Paid < plan.Deposit && two ? plan.Deposit - plan.Paid : plan.Open) : ""),
                        Ui.Input("day", "Eingang am", B.Iso(B.Today), UiInputType.Date),
                        Ui.Select("method", "Art", BookingPayments.Methods[0], BookingPayments.Methods.Select(m => Ui.Option(m, m)).ToArray()),
                        Ui.Input("pay_note", "Notiz")),
                    Ui.FormActions(Ui.Submit("Zahlung eintragen", UiButtonKind.Ghost, "action", "pay", icon: "ti-cash"))),
                Ui.Button("Buchungsbestätigung (PDF)", self + "/pdf", UiButtonKind.Ghost, "ti-file-type-pdf", small: true)));
        }
        var history = Ui.Card("Verlauf", Ui.Table(new[] { Ui.Column("Wann"), Ui.Column("Was") },
            b.History.AsEnumerable().Reverse().Select(h => Ui.Row(BookingMail.Local(h.At), h.Text)), emptyText: "–"));
        return PluginResult.Page(Ui.Stack(head, Ui.Columns(new UiNode?[] { facts, guest, history }, new UiNode?[] { payments }.Concat(steps))), "Buchung " + b.Number);
    }
}
