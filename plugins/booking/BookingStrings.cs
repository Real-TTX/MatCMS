using System.Globalization;

namespace MatCMS.Plugin.Booking;

/// <summary>
/// A text lookup: the plugin context's <c>T</c> (the language of the current request — the admin's UI language in the back office, the page's language
/// on a public route) or <see cref="BookingStrings.German"/> where no context is at hand (pure functions called by tests, background work).
/// </summary>
public delegate string Tr(string key, params object[] args);

/// <summary>
/// Every text of the booking plugin as (key, German, English), registered as the plugin's two string tables. The rows are split over several files
/// (<c>BookingStrings.*.cs</c>) by area; a test checks that each key the code uses is here with both languages. Keys start with "booking." (the host's
/// Localizer falls back to plugin tables only for dotted keys). Texts that take arguments ({0}) must not contain any other brace.
/// </summary>
internal static partial class BookingStrings
{
    public static void Register(IPluginBuilder b)
    {
        b.Strings("de", All.ToDictionary(r => r.Key, r => r.De));
        b.Strings("en", All.ToDictionary(r => r.Key, r => r.En));
    }

    internal static (string Key, string De, string En)[] All =>
        _all ??= Core.Concat(AdminRows).Concat(BookingsRows).Concat(PublicRows).Concat(ServiceRows).ToArray();
    private static (string Key, string De, string En)[]? _all;

    private static Dictionary<string, string>? _de;

    /// <summary>The German text of a key (the key itself when there is none), formatted with <paramref name="args"/> — the default of every <see cref="Tr"/>.</summary>
    public static string German(string key, params object[] args)
    {
        var table = _de ??= All.ToDictionary(r => r.Key, r => r.De);
        if (!table.TryGetValue(key, out var text)) return key;
        if (args is not { Length: > 0 }) return text;
        try { return string.Format(CultureInfo.InvariantCulture, text, args); }
        catch (FormatException) { return text; }
    }

    /// <summary>Whether <paramref name="key"/> is a key of the tables (an exception may also carry a finished text).</summary>
    public static bool Has(string key) => (_de ??= All.ToDictionary(r => r.Key, r => r.De)).ContainsKey(key);

    /// <summary>The lookup of a context, or German without one.</summary>
    public static Tr Of(IPluginContext? ctx) => ctx is null ? German : ctx.T;

    private static readonly (string Key, string De, string En)[] Core =
    [
        // weekdays (ISO 1 = Monday) and months, short and long
        ("booking.day.1", "Mo", "Mon"), ("booking.day.2", "Di", "Tue"), ("booking.day.3", "Mi", "Wed"), ("booking.day.4", "Do", "Thu"),
        ("booking.day.5", "Fr", "Fri"), ("booking.day.6", "Sa", "Sat"), ("booking.day.7", "So", "Sun"),
        ("booking.month.1", "Januar", "January"), ("booking.month.2", "Februar", "February"), ("booking.month.3", "März", "March"),
        ("booking.month.4", "April", "April"), ("booking.month.5", "Mai", "May"), ("booking.month.6", "Juni", "June"), ("booking.month.7", "Juli", "July"),
        ("booking.month.8", "August", "August"), ("booking.month.9", "September", "September"), ("booking.month.10", "Oktober", "October"),
        ("booking.month.11", "November", "November"), ("booking.month.12", "Dezember", "December"),

        // states of a booking
        ("booking.status.request", "Anfrage", "Request"), ("booking.status.offer", "Angebot", "Offer"), ("booking.status.confirmed", "bestätigt", "confirmed"),
        ("booking.status.declined", "abgelehnt", "declined"), ("booking.status.expired", "abgelaufen", "expired"), ("booking.status.cancelled", "storniert", "cancelled"),

        // how a fee counts
        ("booking.per.stay", "je Aufenthalt", "per stay"), ("booking.per.night", "je Nacht", "per night"), ("booking.per.person", "je Person", "per person"),
        ("booking.per.person_night", "je Person und Nacht", "per person and night"),

        // the bill (Pricing)
        ("booking.price.departureAfterArrival", "Die Abreise muss nach der Anreise liegen.", "Departure must be after arrival."),
        ("booking.price.maxNights", "Höchstens {0} Nächte am Stück.", "At most {0} nights in a row."),
        ("booking.price.oneAdult", "Mindestens ein Erwachsener.", "At least one adult."),
        ("booking.price.maxGuests", "{0} ist für höchstens {1} Personen.", "{0} sleeps at most {1} people."),
        ("booking.price.minNights", "mindestens {0} Nächte.", "at least {0} nights."),
        ("booking.price.arrival", "Anreise", "Arrival"), ("booking.price.arrivalOnly", "{0} nur {1}.", "{0} only on {1}."),
        ("booking.price.base", "Grundpreis", "Base price"),
        ("booking.price.week", "1 Woche {0}", "1 week {0}"), ("booking.price.weeks", "{0} Wochen {1}", "{0} weeks {1}"),
        ("booking.price.night", "1 Nacht {0}", "1 night {0}"), ("booking.price.nights", "{0} Nächte {1}", "{0} nights {1}"),
        ("booking.price.extraPerson", "1 weitere Person (ab {0}. Person)", "1 extra person (from person {0})"),
        ("booking.price.extraPersons", "{0} weitere Personen (ab {1}. Person)", "{0} extra people (from person {1})"),
        ("booking.price.adults", "{0} Erw.", "{0} ad."),
        ("booking.price.child", "1 Kind", "1 child"), ("booking.price.children", "{0} Kinder", "{0} children"),
        ("booking.price.childHalf", "zur Hälfte", "at half"), ("booking.price.childFree", "frei", "free"),
        ("booking.price.oneNight", "1 Nacht", "1 night"), ("booking.price.nNights", "{0} Nächte", "{0} nights"),
        ("booking.price.discount", "Nachlass", "Discount"), ("booking.price.surcharge", "Aufschlag", "Surcharge"),

        // refusals of the flow and the calendar
        ("booking.err.notBookable", "Diese Unterkunft ist nicht buchbar.", "This accommodation cannot be booked."),
        ("booking.err.noLongerFree", "Der Zeitraum ist leider nicht mehr frei.", "Sorry, these dates are no longer available."),
        ("booking.err.noSuchBooking", "Diese Buchung gibt es nicht.", "This booking does not exist."),
        ("booking.err.cannotMove", "Nicht möglich: die Buchung ist {0}.", "Not possible: the booking is {0}."),
        ("booking.err.negativeTotal", "Der Gesamtbetrag kann nicht unter null liegen.", "The total cannot be below zero."),
        ("booking.err.taken", "Belegt: {0}", "Taken: {0}"),
        ("booking.err.takenJustNow", "Belegt – gerade eben von einer anderen Buchung.", "Taken – just now by another booking."),
        ("booking.err.unknownUnit", "Unbekannte Einheit.", "Unknown unit."),
        ("booking.err.datesNeeded", "Bitte An- und Abreise angeben (JJJJ-MM-TT).", "Please give arrival and departure (YYYY-MM-DD)."),

        // settings of the plugin itself (BookingPlugin)
        ("booking.set.groupGeneral", "Allgemein", "General"),
        ("booking.set.currency", "Währung", "Currency"), ("booking.set.currencyHelp", "Eine Währung für alle Preise dieser Website.", "One currency for every price on this website."),
        ("booking.set.eur", "Euro (€)", "Euro (€)"), ("booking.set.chf", "Schweizer Franken", "Swiss franc"), ("booking.set.gbp", "Pfund (£)", "Pound (£)"), ("booking.set.usd", "US-Dollar ($)", "US dollar ($)"),
        ("booking.set.notify", "Neue Anfragen an", "Send new requests to"),
        ("booking.set.notifyHelp", "E-Mail-Adresse des Gastgebers. Leer = keine Mail, die Anfrage steht trotzdem im Admin.", "The host's e-mail address. Empty = no mail, the request still shows in the admin."),
        ("booking.set.hold", "Anfrage hält den Zeitraum (Stunden)", "A request holds the dates (hours)"),
        ("booking.set.holdHelp", "So lange sind die Nächte nach einer Anfrage für den Gast vorgemerkt – danach sind sie wieder frei, wenn nichts passiert ist.", "How long the nights stay reserved for the guest after a request – after that they are free again unless something happened."),
        ("booking.set.offer", "Angebot gilt (Tage)", "Offer valid (days)"),
        ("booking.set.paymentInfo", "Zahlungshinweis", "Payment note"),
        ("booking.set.paymentInfoHelp", "Steht in der Buchungsbestätigung und auf der Seite des Gastes, z. B. Bankverbindung, Anzahlung und bis wann.", "Shown in the booking confirmation and on the guest's page, e.g. bank details, deposit and due date."),
        ("booking.set.privacy", "Datenschutzerklärung", "Privacy policy"), ("booking.set.privacyHelp", "Die Seite, auf die das Anfrageformular verweist.", "The page the request form links to."),

        // the dashboard's attention notes
        ("booking.attn.oneRequest", "1 neue Buchungsanfrage", "1 new booking request"), ("booking.attn.requests", "{0} neue Buchungsanfragen", "{0} new booking requests"),
        ("booking.attn.requestsText", "Annehmen, Angebot schicken oder ablehnen – sonst läuft die Vormerkung ab.", "Accept, send an offer or decline – otherwise the hold runs out."),
        ("booking.attn.doubleBooking", "Doppelbuchung über {0}", "Double booking via {0}"),
        ("booking.attn.feedNotRead", "Kalender von {0} wird nicht gelesen", "Calendar of {0} is not being read"),
    ];
}
