namespace MatCMS.Plugin.Booking;

internal static partial class BookingStrings
{
    // cleaning, guest-mail settings, iCal, mail helpers, the printouts (BookingCleaning, BookingGuestMails, BookingIcal, BookingMail, BookingPrint)
    private static readonly (string Key, string De, string En)[] ServiceRows =
    [
        ("booking.clean.set.group", "Reinigung", "Cleaning"),
        ("booking.clean.set.cleaners", "Reinigung: Zuständige", "Cleaning: staff"),
        ("booking.clean.set.cleanersHelp", "Ein Name je Zeile (z. B. „Ana – 0385 91 123 456“). Erscheint in der Auswahl der Reinigungsliste.", "One name per line (e.g. “Ana – 0385 91 123 456”). Offered in the cleaning list."),
        ("booking.clean.booking", "Buchung {0}", "Booking {0}"), ("booking.clean.stay", "Aufenthalt", "Stay"),
        ("booking.clean.renewed", "Neue Kalender-Adresse erzeugt – die alte geht nicht mehr.", "New calendar address created – the old one no longer works."),
        ("booking.clean.assignee", "Zuständig", "Assigned to"), ("booking.clean.nobody", "– niemand –", "– nobody –"),
        ("booking.clean.done", "Erledigt", "Done"), ("booking.clean.doneBadge", "erledigt", "done"), ("booking.clean.undo", "Zurück", "Undo"),
        ("booking.clean.title", "Reinigung", "Cleaning"), ("booking.clean.asPdf", "Als PDF", "As PDF"),
        ("booking.clean.from", "Von", "From"), ("booking.clean.to", "Bis", "To"), ("booking.clean.show", "Anzeigen", "Show"),
        ("booking.clean.col.day", "Tag", "Day"), ("booking.clean.col.unit", "Einheit", "Unit"), ("booking.clean.col.departure", "Abreise", "Departure"), ("booking.clean.col.next", "Danach", "Next"),
        ("booking.clean.turnover", "Wechsel", "Changeover"), ("booking.clean.turnoverUntil", "Wechsel bis {0} Uhr", "Changeover by {0}"),
        ("booking.clean.turnoverParen", "(Wechsel)", "(changeover)"),
        ("booking.clean.today", "heute", "today"), ("booking.clean.persons", "{0} Pers.", "{0} ppl."),
        ("booking.clean.empty", "In diesem Zeitraum reist niemand ab.", "Nobody leaves in this period."),
        ("booking.clean.calendar", "Kalender für das Reinigungsteam", "Calendar for the cleaning team"), ("booking.clean.address", "Adresse", "Address"),
        ("booking.clean.calendarHelp", "Ohne Namen, zum Abonnieren im Handy-Kalender.", "Without names, to subscribe to in a phone calendar."),
        ("booking.clean.renew", "Neue Adresse erzeugen", "Create new address"),
        ("booking.clean.renewConfirm", "Die bisherige Kalender-Adresse geht danach nicht mehr. Fortfahren?", "The current calendar address will stop working. Continue?"),
        ("booking.clean.pdfTitle", "Reinigung {0} – {1}", "Cleaning {0} – {1}"), ("booking.clean.pdfKicker", "Reinigungsliste · {0} – {1}", "Cleaning list · {0} – {1}"),
        ("booking.clean.yes", "ja", "yes"), ("booking.clean.checklist", "Checkliste {0}", "Checklist {0}"), ("booking.clean.fileName", "Reinigung {0}", "Cleaning {0}"),

        ("booking.gm.set.checkin", "Mail mit Check-in-Link", "Mail with check-in link"),
        ("booking.gm.set.arrival", "Mail mit Zugang am Tag vor der Anreise", "Mail with access details the day before arrival"),
        ("booking.gm.set.departure", "Mail am Abreisetag", "Mail on departure day"),
        ("booking.gm.set.thanks", "Danke-Mail nach der Abreise", "Thank-you mail after departure"),
        ("booking.gm.set.review", "Link zur Bewertung", "Review link"),
        ("booking.gm.set.reviewHelp", "z. B. Ihr Google-Profil. Steht in der Danke-Mail; leer = ohne Bitte um Bewertung.", "e.g. your Google profile. Included in the thank-you mail; empty = no review request."),

        ("booking.ical.unitGone", "Die Einheit gibt es nicht mehr.", "The unit no longer exists."),

        ("booking.mail.adult", "1 Erwachsener", "1 adult"), ("booking.mail.adults", "{0} Erwachsene", "{0} adults"),
        ("booking.mail.time", "{0} Uhr", "{0}"),

        ("booking.print.set.group", "Druckvorlage (PDF)", "Print template (PDF)"),
        ("booking.print.set.logo", "Logo", "Logo"),
        ("booking.print.set.logoHelp", "Oben links auf der Buchungsbestätigung. Leer = das Logo der Website.", "Top left on the booking confirmation. Empty = the website's logo."),
        ("booking.print.set.head", "Briefkopf (rechts oben)", "Letterhead (top right)"), ("booking.print.set.line", "Zeile", "Line"),
        ("booking.print.set.headHelp", "Die erste Zeile fett – z. B. Name, Anschrift, Telefon, E-Mail. Leer = Name der Website und ihre Adresse.", "The first line in bold – e.g. name, address, phone, e-mail. Empty = the website's name and address."),
        ("booking.print.set.foot", "Fußzeile", "Footer"),
        ("booking.print.set.footHelp", "Bis zu drei Zeilen unten, z. B. Bankverbindung, Steuernummer, Website.", "Up to three lines at the bottom, e.g. bank details, tax number, website."),
        ("booking.print.set.color", "Farbe", "Colour"), ("booking.print.set.colorHelp", "Titel, Überschriften und Linien.", "Title, headings and lines."),
        ("booking.print.title", "Buchungsbestätigung {0}", "Booking confirmation {0}"), ("booking.print.kicker", "Buchungsbestätigung · Nr. {0}", "Booking confirmation · No. {0}"),
        ("booking.print.accommodation", "Unterkunft", "Accommodation"), ("booking.print.guest", "Gast", "Guest"), ("booking.print.address", "Adresse", "Address"),
        ("booking.print.arrival", "Anreise", "Arrival"), ("booking.print.departure", "Abreise", "Departure"),
        ("booking.print.from", "ab {0} Uhr", "from {0}"), ("booking.print.until", "bis {0} Uhr", "by {0}"),
        ("booking.print.nights", "Nächte", "Nights"), ("booking.print.guests", "Gäste", "Guests"), ("booking.print.status", "Status", "Status"),
        ("booking.print.price", "Preis", "Price"), ("booking.print.item", "Posten", "Item"), ("booking.print.calc", "Berechnung", "Calculation"), ("booking.print.amount", "Betrag", "Amount"),
        ("booking.print.total", "Gesamt", "Total"), ("booking.print.payment", "Zahlung", "Payment"), ("booking.print.deposit", "Anzahlung", "Deposit"),
        ("booking.print.due", "fällig am {0}", "due on {0}"), ("booking.print.rest", "Restbetrag", "Balance"), ("booking.print.fullAmount", "Gesamtbetrag", "Total amount"),
        ("booking.print.paid", "Bereits bezahlt", "Already paid"), ("booking.print.open", "Noch offen", "Outstanding"),
        ("booking.print.reference", "Verwendungszweck: {0}", "Payment reference: {0}"),
        ("booking.print.asOf", "Stand: {0}", "As of: {0}"), ("booking.print.page", "Seite {0} / {1}", "Page {0} / {1}"),
    ];
}
