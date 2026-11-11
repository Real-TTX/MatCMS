using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>A payment the host received (bank transfer, cash, card, through a portal).</summary>
public sealed record Payment(long Id, long BookingId, long Amount, DateOnly Day, string Method, string Note);

/// <summary>
/// What a confirmed booking should be paid: a deposit (a share of the total) due some days after the confirmation and the rest due some days before
/// arrival — or everything at once when the arrival is too close for two steps. <see cref="Paid"/> is what came in so far.
/// </summary>
public sealed record PaymentPlan(long Deposit, DateOnly DepositDue, long Rest, DateOnly RestDue, long Paid, long Total)
{
    public long Open => Math.Max(0, Total - Paid);
    /// <summary>"offen", "angezahlt", "bezahlt" — what the list and the guest see.</summary>
    public string State => Paid <= 0 ? "offen" : Paid >= Total ? "bezahlt" : Paid >= Deposit && Deposit < Total ? "angezahlt" : "teilweise";
    /// <summary>The state in the language of a request.</summary>
    public string StateLabel(Tr t) => State switch
    {
        "offen" => t("booking.pay.state.open"), "bezahlt" => t("booking.pay.state.paid"), "angezahlt" => t("booking.pay.state.deposit"), _ => t("booking.pay.state.partly")
    };
    public UiTone Tone => State switch { "bezahlt" => UiTone.Success, "angezahlt" => UiTone.Info, _ => UiTone.Warning };
    /// <summary>The amount that should be in by <paramref name="day"/> but is not.</summary>
    public long Overdue(DateOnly day) => Math.Max(0, (RestDue <= day ? Total : DepositDue <= day ? Deposit : 0) - Paid);
}

/// <summary>Payments: the plan (pure), the table, the settings, and the one reminder per due date.</summary>
public static class BookingPayments
{
    public const string DepositKey = "depositPercent", DepositDaysKey = "depositDays", RestDaysKey = "restDays", RemindKey = "remindPayments";
    // the stored values stay German (data); MethodLabel shows them in the request's language
    public static readonly string[] Methods = { "Überweisung", "Bar", "Karte", "PayPal", "über ein Portal" };
    public static string MethodLabel(Tr t, string m) => m switch
    {
        "Überweisung" => t("booking.pay.method.transfer"), "Bar" => t("booking.pay.method.cash"), "Karte" => t("booking.pay.method.card"),
        "PayPal" => t("booking.pay.method.paypal"), "über ein Portal" => t("booking.pay.method.portal"), _ => m
    };

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(DepositKey, "booking.pay.deposit", SettingKind.Number) { Group = "booking.pay.group", Default = "30", Min = 0, Max = 100, Help = "booking.pay.depositHelp" })
        .Add(new SettingSpec(DepositDaysKey, "booking.pay.depositDays", SettingKind.Number) { Default = "7", Min = 0, Max = 60, Help = "booking.pay.depositDaysHelp" })
        .Add(new SettingSpec(RestDaysKey, "booking.pay.restDays", SettingKind.Number) { Default = "30", Min = 0, Max = 180, Help = "booking.pay.restDaysHelp" })
        .Add(new SettingSpec(RemindKey, "booking.pay.remind", SettingKind.Bool) { Default = "1", Help = "booking.pay.remindHelp" });

    private static int N(IPluginContext ctx, string key, int def) =>
        double.TryParse(ctx.Settings.Get(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? (int)v : def;

    /// <summary>The plan of a booking — pure, for the tests and the PDF alike.</summary>
    public static PaymentPlan Plan(long total, DateOnly confirmed, DateOnly arrival, int depositPercent, int depositDays, int restDays, long paid)
    {
        var restDue = arrival.AddDays(-Math.Max(0, restDays));
        var depositDue = confirmed.AddDays(Math.Max(0, depositDays));
        if (depositDue > arrival) depositDue = confirmed;                             // a booking for tomorrow: due now
        // the share in cents, rounded UP, then up to whole currency units (a guest transfers "120 €", not "119,37 €")
        var deposit = depositPercent <= 0 ? 0 : Math.Min(total, ((total * Math.Clamp(depositPercent, 0, 100) + 99) / 100 + 99) / 100 * 100);
        if (depositPercent >= 100 || restDue <= depositDue) return new PaymentPlan(total, depositDue, 0, depositDue, paid, total);   // too close: all at once
        if (deposit <= 0) return new PaymentPlan(0, restDue, total, restDue, paid, total);
        return new PaymentPlan(deposit, depositDue, total - deposit, restDue, paid, total);
    }

    public static async Task<PaymentPlan> PlanAsync(IPluginContext ctx, Booking b, CancellationToken ct)
    {
        var paid = (await ListAsync(ctx.Data, b.Id, ct)).Sum(p => p.Amount);
        var confirmed = b.ConfirmedAt is { } c ? DateOnly.FromDateTime(c) : B.Today;
        return Plan(b.Total, confirmed, b.Arrival, N(ctx, DepositKey, 30), N(ctx, DepositDaysKey, 7), N(ctx, RestDaysKey, 30), paid);
    }

    public static async Task<List<Payment>> ListAsync(IPluginData data, long bookingId, CancellationToken ct = default) =>
        (await data.Table(BookingStore.TPayments).QueryAsync(DataQuery.Everything.Eq("booking_id", bookingId).OrderBy("day"), ct))
            .Select(r => new Payment(r.Id, r.Int("booking_id"), r.Int("amount"), B.Day(r.Text("day")) ?? default, r.Text("method"), r.Text("note"))).ToList();

    /// <summary>Paid sums of many bookings at once (the list).</summary>
    public static async Task<Dictionary<long, long>> PaidAsync(IPluginData data, CancellationToken ct = default) =>
        (await data.Table(BookingStore.TPayments).QueryAsync(DataQuery.Everything, ct)).GroupBy(r => r.Int("booking_id")).ToDictionary(g => g.Key, g => g.Sum(r => r.Int("amount")));

    public static async Task AddAsync(IPluginData data, long bookingId, long amount, DateOnly day, string method, string note, CancellationToken ct = default)
    {
        if (amount == 0) throw new BookingException("booking.pay.amountNeeded");
        await data.Table(BookingStore.TPayments).InsertAsync(new Dictionary<string, object?>
        {
            ["booking_id"] = bookingId, ["amount"] = amount, ["day"] = B.Iso(day), ["method"] = B.Clip(method, 40), ["note"] = B.Clip(note, 300), ["created"] = DateTime.UtcNow
        }, ct);
        await BookingFlow.NoteAsync(data, bookingId, "Zahlung eingetragen: " + B.Money(amount) + " am " + B.Date(day) + (method.Length > 0 ? " (" + method + ")" : ""), ct);
    }

    public static async Task RemoveAsync(IPluginData data, long bookingId, long paymentId, CancellationToken ct = default)
    {
        var p = (await ListAsync(data, bookingId, ct)).FirstOrDefault(x => x.Id == paymentId);
        if (p is null) return;
        await data.Table(BookingStore.TPayments).DeleteAsync(p.Id, ct);
        await BookingFlow.NoteAsync(data, bookingId, "Zahlung entfernt: " + B.Money(p.Amount) + " vom " + B.Date(p.Day), ct);
    }

    /// <summary>
    /// The daily reminder: a confirmed booking whose deposit (or rest) is two days overdue gets ONE mail per due date — remembered in the booking, so a
    /// second run, a restart or a payment entered late never sends it twice.
    /// </summary>
    public static async Task RemindAsync(IPluginContext ctx, CancellationToken ct)
    {
        if (ctx.Settings.Get(RemindKey) is "0" or "false") return;
        var today = B.Today;
        var store = new BookingStore(ctx.Data);
        foreach (var b in await BookingFlow.ListAsync(ctx.Data, new[] { BookingStatus.Confirmed }, ct))
        {
            if (b.Departure < today || !b.Email.Contains('@')) continue;
            var plan = await PlanAsync(ctx, b, ct);
            var row = await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct);
            string? which = null;
            if (plan.Rest > 0 && plan.RestDue.AddDays(2) <= today && plan.Paid < plan.Total && row?.Timestamp("reminded_rest") is null) which = "rest";
            else if (plan.Deposit > 0 && plan.DepositDue.AddDays(2) <= today && plan.Paid < plan.Deposit && row?.Timestamp("reminded_deposit") is null) which = "deposit";
            if (which is null) continue;
            await ctx.Data.Table(BookingStore.TBookings).UpdateAsync(b.Id, new Dictionary<string, object?> { ["reminded_" + which] = DateTime.UtcNow }, ct);
            var unit = await store.UnitAsync(b.UnitId, ct);
            var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
            var values = BookingMail.Values(ctx, b, unit, house, await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct));
            values["faellig"] = B.Money(plan.Overdue(today), BookingPlugin.Currency(ctx));
            values["was"] = which == "rest" ? "den Restbetrag" : "die Anzahlung";
            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Zahlungserinnerung (" + (which == "rest" ? "Rest" : "Anzahlung") + "): " + await BookingMail.SendAsync(ctx, BookingMail.Reminder, b.Email, values, ct), ct);
        }
    }
}
