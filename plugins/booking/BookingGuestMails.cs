using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The mails around a stay (phase B5), sent by an hourly job to confirmed bookings with an e-mail address — each at most ONCE per booking (the keys
/// sent are kept in the booking's <c>mails</c> column, written BEFORE the mail goes out, so a crash or a second runner never sends twice):
/// the check-in link some days before arrival (with directions and house rules), the way in on the day before (code, key box, Wi-Fi — never earlier),
/// the departure notes on the morning of departure, and a thank-you two days after (with the review link when there is one). Each is a setting.
/// </summary>
public static class BookingGuestMails
{
    public const string CheckinMail = "booking.checkin", ArrivalMail = "booking.arrival", DepartureMail = "booking.departure", ThanksMail = "booking.thanks";
    public const string OnCheckin = "mailCheckin", OnArrival = "mailArrival", OnDeparture = "mailDeparture", OnThanks = "mailThanks", ReviewKey = "reviewUrl";

    private static MailPlaceholder Ph(string t, string d) => new(t, d);
    private static readonly MailPlaceholder[] Common =
    {
        Ph("name", "Name des Gastes"), Ph("nummer", "Buchungsnummer"), Ph("haus", "Name des Objekts"), Ph("einheit", "Name der Unterkunft"),
        Ph("anreise", "Anreise"), Ph("abreise", "Abreise"), Ph("checkin", "Check-in ab"), Ph("checkout", "Check-out bis"), Ph("link", "Die Seite der Buchung"), Ph("website", "Name der Website")
    };

    public static readonly MailTemplateSpec[] Specs =
    {
        new(CheckinMail, "Buchungen: Check-in-Link (an den Gast)", "Einige Tage vor der Anreise (Einstellung): der Link zum Online-Check-in, die Anfahrt und die Hausregeln.",
            "Bald geht's los: Ihr Online-Check-in für {{einheit}}",
            "Hallo {{name}},\n\nin wenigen Tagen ist es so weit – wir freuen uns auf Sie! Bitte tragen Sie vorab die Angaben aller Mitreisenden ein (wir müssen unsere Gäste melden):\n{{checkin_link}}\n\nAnreise: {{anreise}}, Check-in ab {{checkin}} Uhr\n\nAnfahrt:\n{{anfahrt}}\n\nHausregeln:\n{{hausregeln}}\n\nDen Zugang (Schlüssel/Code) schicken wir Ihnen am Tag vor der Anreise.\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("checkin_link", "Link zum Online-Check-in"), Ph("anfahrt", "Anfahrt aus dem Objekt"), Ph("hausregeln", "Hausregeln aus dem Objekt") }).ToArray() },
        new(ArrivalMail, "Buchungen: Zugang am Tag vor der Anreise (an den Gast)", "Am Tag vor der Anreise: Zugang (Schlüssel, Türcode) und WLAN aus der Einheit.",
            "Morgen ist Anreise: so kommen Sie in {{einheit}}",
            "Hallo {{name}},\n\nmorgen ist Ihr Anreisetag. Check-in ab {{checkin}} Uhr.\n\nZugang:\n{{zugang}}\n\nWLAN: {{wlan}}\n\nAlles zu Ihrer Buchung: {{link}}\n\nGute Reise!\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("zugang", "Zugang aus der Einheit (Schlüssel, Code)"), Ph("wlan", "WLAN aus der Einheit") }).ToArray() },
        new(DepartureMail, "Buchungen: Abreise (an den Gast)", "Am Morgen des Abreisetags: die Hinweise zur Abreise aus dem Objekt.",
            "Heute ist Abreise – ein paar Hinweise",
            "Hallo {{name}},\n\nwir hoffen, Sie hatten eine schöne Zeit! Bitte denken Sie an den Check-out bis {{checkout}} Uhr.\n\n{{abreise_info}}\n\nGute Heimreise!\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("abreise_info", "Hinweise zur Abreise aus dem Objekt") }).ToArray() },
        new(ThanksMail, "Buchungen: Danke nach dem Aufenthalt (an den Gast)", "Zwei Tage nach der Abreise: Dank und – wenn eingestellt – die Bitte um eine Bewertung.",
            "Danke für Ihren Besuch in {{einheit}}",
            "Hallo {{name}},\n\nvielen Dank, dass Sie bei uns zu Gast waren! Wir würden uns freuen, Sie wieder begrüßen zu dürfen.\n\n{{bewertung}}\n\nHerzliche Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("bewertung", "Die Bitte um eine Bewertung mit Link – leer, wenn kein Link eingestellt ist") }).ToArray() },
    };

    public static void Declare(ISettingsBuilder s) => s
        .Add(new SettingSpec(OnCheckin, "Mail mit Check-in-Link", SettingKind.Bool) { Default = "1" })
        .Add(new SettingSpec(OnArrival, "Mail mit Zugang am Tag vor der Anreise", SettingKind.Bool) { Default = "1" })
        .Add(new SettingSpec(OnDeparture, "Mail am Abreisetag", SettingKind.Bool) { Default = "1" })
        .Add(new SettingSpec(OnThanks, "Danke-Mail nach der Abreise", SettingKind.Bool) { Default = "1" })
        .Add(new SettingSpec(ReviewKey, "Link zur Bewertung", SettingKind.Text) { Help = "z. B. Ihr Google-Profil. Steht in der Danke-Mail; leer = ohne Bitte um Bewertung." });

    private static bool On(IPluginContext ctx, string key) => ctx.Settings.Get(key) is not ("0" or "false");

    /// <summary>Which mail is due for a booking today — pure, so the rules are testable. Null = none.</summary>
    public static string? Due(DateOnly arrival, DateOnly departure, DateOnly today, int localHour, int checkinDays, bool checkedIn, IReadOnlySet<string> sent)
    {
        if (!sent.Contains(CheckinMail) && !checkedIn && today >= arrival.AddDays(-checkinDays) && today < arrival.AddDays(-1)) return CheckinMail;
        if (!sent.Contains(ArrivalMail) && today >= arrival.AddDays(-1) && today <= arrival) return ArrivalMail;
        if (!sent.Contains(DepartureMail) && today == departure && localHour >= 7) return DepartureMail;
        if (!sent.Contains(ThanksMail) && today >= departure.AddDays(2) && today <= departure.AddDays(14)) return ThanksMail;
        return null;
    }

    public static async Task RunAsync(IPluginContext ctx, CancellationToken ct)
    {
        var today = B.Today;
        int hour;
        try { hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).Hour; } catch (TimeZoneNotFoundException) { hour = DateTime.UtcNow.Hour; }
        var days = double.TryParse(ctx.Settings.Get(BookingCheckin.DaysKey), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 1 ? (int)d : 7;
        var store = new BookingStore(ctx.Data);
        foreach (var b in await BookingFlow.ListAsync(ctx.Data, new[] { BookingStatus.Confirmed }, ct))
        {
            if (!b.Email.Contains('@') || b.Departure < today.AddDays(-14) || b.Arrival > today.AddDays(days)) continue;
            var row = await ctx.Data.Table(BookingStore.TBookings).GetAsync(b.Id, ct);
            if (row is null) continue;
            var sent = row.Text("mails").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var due = Due(b.Arrival, b.Departure, today, hour, days, row.Timestamp("checkin_at") is not null, sent);
            if (due is null) continue;
            sent.Add(due);
            await ctx.Data.Table(BookingStore.TBookings).UpdateAsync(b.Id, new Dictionary<string, object?> { ["mails"] = string.Join(",", sent) }, ct);
            var on = due switch { CheckinMail => On(ctx, OnCheckin), ArrivalMail => On(ctx, OnArrival), DepartureMail => On(ctx, OnDeparture), _ => On(ctx, OnThanks) };
            if (!on) continue;                                                    // switched off: marked as done so switching on later sends no stale mail
            var unit = await store.UnitAsync(b.UnitId, ct);
            var house = unit is null ? null : await store.PropertyAsync(unit.PropertyId, ct);
            var key = await BookingFlow.NewKeyAsync(ctx.Data, b.Id, ct);
            var values = BookingMail.Values(ctx, b, unit, house, key);
            values["checkin_link"] = values["link"] + "/checkin";
            values["anfahrt"] = house?.Directions is { Length: > 0 } dir ? dir : "–";
            values["hausregeln"] = house?.HouseRules is { Length: > 0 } hr ? hr : "–";
            values["zugang"] = unit?.AccessInfo is { Length: > 0 } acc ? acc : "Wir melden uns persönlich bei Ihnen.";
            values["wlan"] = unit?.Wifi is { Length: > 0 } w ? w : "–";
            values["abreise_info"] = house?.CheckoutInfo ?? "";
            values["bewertung"] = ctx.Settings.Get(ReviewKey) is { Length: > 0 } rv ? "Wenn es Ihnen gefallen hat, freuen wir uns sehr über eine Bewertung:\n" + rv : "";
            var label = due switch { CheckinMail => "Check-in-Link", ArrivalMail => "Zugang", DepartureMail => "Abreise", _ => "Danke" };
            await BookingFlow.NoteAsync(ctx.Data, b.Id, "Mail „" + label + "“ an den Gast: " + await BookingMail.SendAsync(ctx, due, b.Email, values, ct), ct);
        }
    }
}
