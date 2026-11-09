using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// The mails of a booking. They land in Admin → Mailvorlagen the first time the plugin loads (the host never overwrites an edited one), so a house
/// writes them in its own words. One mail per recipient; a mail that cannot be sent (no mail set up) is written into the booking's history, never thrown.
/// </summary>
public static class BookingMail
{
    public const string RequestGuest = "booking.request.guest", RequestHost = "booking.request.host", Offer = "booking.offer", Confirmed = "booking.confirmed",
        Declined = "booking.declined", Expired = "booking.expired", AcceptedHost = "booking.accepted.host", Cancelled = "booking.cancelled", Reminder = "booking.reminder";

    private static MailPlaceholder Ph(string t, string d) => new(t, d);
    private static readonly MailPlaceholder[] Common =
    {
        Ph("name", "Name des Gastes"), Ph("nummer", "Buchungsnummer"), Ph("haus", "Name des Objekts"), Ph("einheit", "Name der Unterkunft"),
        Ph("anreise", "Anreise (TT.MM.JJJJ)"), Ph("abreise", "Abreise"), Ph("naechte", "Anzahl Nächte"), Ph("personen", "z. B. „2 Erwachsene, 1 Kind“"),
        Ph("betrag", "Gesamtbetrag"), Ph("rechnung", "Die Posten der Rechnung, eine Zeile je Posten"), Ph("link", "Die persönliche Seite der Buchung (geheimer Link)"),
        Ph("website", "Name der Website")
    };

    public static readonly MailTemplateSpec[] Specs =
    {
        new(RequestGuest, "Buchungen: Anfrage erhalten (an den Gast)", "Geht an den Gast, sobald seine Anfrage eingegangen ist.",
            "Ihre Anfrage {{nummer}} – {{einheit}}",
            "Hallo {{name}},\n\nvielen Dank für Ihre Anfrage! Wir haben den Zeitraum für Sie vorgemerkt und melden uns so schnell wie möglich.\n\n{{einheit}}, {{haus}}\n{{anreise}} – {{abreise}} ({{naechte}} Nächte), {{personen}}\n\n{{rechnung}}\nGesamt: {{betrag}}\n\nDer Stand Ihrer Anfrage:\n{{link}}\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common },
        new(RequestHost, "Buchungen: neue Anfrage (an den Gastgeber)", "Geht an die Adresse aus den Einstellungen, sobald eine Anfrage eingeht.",
            "Neue Anfrage {{nummer}}: {{einheit}}, {{anreise}} – {{abreise}}",
            "Neue Anfrage über die Website:\n\n{{name}} <{{email}}> {{telefon}}\n{{einheit}}, {{anreise}} – {{abreise}} ({{naechte}} Nächte), {{personen}}\nBetrag laut Preisliste: {{betrag}}\n\nNachricht:\n{{nachricht}}\n\nDie Nächte sind vorgemerkt bis {{frist}}. Annehmen, Angebot schicken oder ablehnen:\n{{admin_link}}\n")
        { Placeholders = Common.Concat(new[] { Ph("email", "E-Mail des Gastes"), Ph("telefon", "Telefon des Gastes"), Ph("nachricht", "Nachricht des Gastes"), Ph("frist", "Bis wann die Nächte vorgemerkt sind"), Ph("admin_link", "Die Buchung im Admin") }).ToArray() },
        new(Offer, "Buchungen: Angebot (an den Gast)", "Geht an den Gast, wenn im Admin „Angebot schicken“ geklickt wird.",
            "Unser Angebot {{nummer}} – {{einheit}}",
            "Hallo {{name}},\n\ngerne können Sie bei uns wohnen. Hier unser Angebot:\n\n{{einheit}}, {{haus}}\n{{anreise}} – {{abreise}} ({{naechte}} Nächte), {{personen}}\n\n{{rechnung}}\nGesamt: {{betrag}}\n\nDas Angebot gilt bis {{frist}}. Verbindlich buchen können Sie hier:\n{{link}}\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("frist", "Bis wann das Angebot gilt") }).ToArray() },
        new(Confirmed, "Buchungen: Buchungsbestätigung (an den Gast)", "Geht an den Gast, wenn die Buchung bestätigt ist (Gast hat angenommen oder Gastgeber hat direkt bestätigt).",
            "Buchungsbestätigung {{nummer}} – {{einheit}}",
            "Hallo {{name}},\n\nIhre Buchung ist bestätigt – wir freuen uns auf Sie!\n\n{{einheit}}, {{haus}}\n{{anreise}} – {{abreise}} ({{naechte}} Nächte), {{personen}}\nCheck-in ab {{checkin}}, Check-out bis {{checkout}}\n\n{{rechnung}}\nGesamt: {{betrag}}\n\n{{zahlung}}\n\nIhre Buchung jederzeit hier:\n{{link}}\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("checkin", "Check-in ab"), Ph("checkout", "Check-out bis"), Ph("zahlung", "Zahlungshinweis aus den Einstellungen (Bankverbindung)") }).ToArray() },
        new(Declined, "Buchungen: Absage (an den Gast)", "Geht an den Gast, wenn eine Anfrage abgelehnt wird.",
            "Ihre Anfrage {{nummer}} – {{einheit}}",
            "Hallo {{name}},\n\nleider können wir Ihre Anfrage für {{anreise}} – {{abreise}} nicht annehmen.\n\n{{grund}}\n\nVielleicht passt ein anderer Zeitraum? Wir würden uns freuen.\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("grund", "Der Text, den der Gastgeber beim Ablehnen geschrieben hat") }).ToArray() },
        new(Expired, "Buchungen: abgelaufen (an den Gast)", "Geht an den Gast, wenn eine Anfrage oder ein Angebot ohne Antwort abgelaufen ist.",
            "Ihre Anfrage {{nummer}} ist abgelaufen",
            "Hallo {{name}},\n\nIhre Anfrage für {{einheit}}, {{anreise}} – {{abreise}}, ist abgelaufen und der Zeitraum wieder frei. Wenn Sie noch kommen möchten, fragen Sie gerne erneut an.\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common },
        new(Cancelled, "Buchungen: Stornierung (an den Gast)", "Geht an den Gast, wenn eine Buchung im Admin storniert wird.",
            "Stornierung {{nummer}} – {{einheit}}",
            "Hallo {{name}},\n\nIhre Buchung {{nummer}} ({{einheit}}, {{anreise}} – {{abreise}}) ist storniert.\n\n{{grund}}\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("grund", "Der Text, den der Gastgeber beim Stornieren geschrieben hat") }).ToArray() },
        new(Reminder, "Buchungen: Zahlungserinnerung (an den Gast)", "Geht einmal je Fälligkeit an den Gast, wenn Anzahlung oder Rest zwei Tage danach noch nicht eingetragen ist (abschaltbar in den Einstellungen).",
            "Erinnerung: {{was}} für Ihre Buchung {{nummer}}",
            "Hallo {{name}},\n\nwir freuen uns auf Ihren Aufenthalt vom {{anreise}} bis {{abreise}} in {{einheit}}. Bei uns ist {{was}} noch nicht eingegangen – offen sind {{faellig}}.\n\n{{zahlung}}\nVerwendungszweck: {{nummer}}\n\nFalls Sie schon überwiesen haben: vielen Dank, dann hat sich diese Mail überschnitten.\n\nIhre Buchung: {{link}}\n\nViele Grüße\n{{website}}\n")
        { Placeholders = Common.Concat(new[] { Ph("was", "„die Anzahlung“ oder „den Restbetrag“"), Ph("faellig", "Der offene, fällige Betrag"), Ph("zahlung", "Zahlungshinweis aus den Einstellungen") }).ToArray() },
        new(AcceptedHost, "Buchungen: Angebot angenommen (an den Gastgeber)", "Geht an die Adresse aus den Einstellungen, wenn ein Gast ein Angebot annimmt.",
            "Gebucht: {{nummer}} {{einheit}}, {{anreise}} – {{abreise}}",
            "{{name}} hat das Angebot angenommen – die Buchung ist bestätigt.\n\n{{einheit}}, {{anreise}} – {{abreise}} ({{naechte}} Nächte), {{personen}}\nGesamt: {{betrag}}\n\n{{admin_link}}\n")
        { Placeholders = Common.Concat(new[] { Ph("admin_link", "Die Buchung im Admin") }).ToArray() },
    };

    public static string Persons(int adults, int children) =>
        adults + (adults == 1 ? " Erwachsener" : " Erwachsene") + (children > 0 ? ", " + children + (children == 1 ? " Kind" : " Kinder") : "");

    /// <summary>The values every booking mail knows.</summary>
    public static Dictionary<string, string> Values(IPluginContext ctx, Booking b, Unit? unit, Property? house, string? key)
    {
        var cur = BookingPlugin.Currency(ctx);
        var origin = (ctx.Site.Origin ?? "").TrimEnd('/');
        return new Dictionary<string, string>
        {
            ["name"] = b.Name, ["email"] = b.Email, ["telefon"] = b.Phone, ["nummer"] = b.Number, ["haus"] = house?.Name ?? "", ["einheit"] = unit?.Name ?? "",
            ["anreise"] = B.Date(b.Arrival), ["abreise"] = B.Date(b.Departure), ["naechte"] = B.Num(b.Nights), ["personen"] = Persons(b.Adults, b.Children),
            ["betrag"] = B.Money(b.Total, cur),
            ["rechnung"] = string.Join("\n", b.Lines.Select(l => l.Text + ": " + B.Money(l.Amount, cur))),
            ["link"] = key is null ? "" : origin + BookingFlow.GuestPath + key, ["website"] = ctx.Site.Name,
            ["nachricht"] = b.Message.Length > 0 ? b.Message : "–",
            ["frist"] = Local(b.Status == BookingStatus.Offer ? b.OfferUntil : b.HoldUntil),
            ["admin_link"] = origin + BookingAdmin.Root + "bookings/" + b.Id,
            ["checkin"] = house?.CheckIn ?? "", ["checkout"] = house?.CheckOut ?? "",
            ["zahlung"] = ctx.Settings.Get(BookingPlugin.PaymentInfoKey) ?? ""
        };
    }

    /// <summary>A UTC moment as the site's local date and time — Central European for now (all houses so far are in CET/CEST countries).</summary>
    public static string Local(DateTime? utc)
    {
        if (utc is null) return "";
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " Uhr"; }
        catch (TimeZoneNotFoundException) { return utc.Value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC"; }
    }

    /// <summary>Sends one mail and says how it went (the caller writes it into the history). Never throws.</summary>
    public static async Task<string> SendAsync(IPluginContext ctx, string template, string to, Dictionary<string, string> values, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to) || !to.Contains('@')) return "keine Adresse";
        try
        {
            var r = await ctx.Mail.SendAsync(template, new[] { to.Trim() }, values, null, null, ct);
            return r.Ok ? "verschickt an " + to : "nicht verschickt (" + (r.Error ?? "Mail nicht eingerichtet") + ")";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return "nicht verschickt (" + ex.Message + ")"; }
    }
}
