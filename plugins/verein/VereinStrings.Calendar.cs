namespace MatCMS.Plugin.Verein;

internal static partial class VereinStrings
{
    // The calendar view (VereinCalendar, /mitglied/kalender, Admin → Kalender) and the member area's block pages (VereinPages).
    private static readonly (string Key, string De, string En)[] Calendar =
    {
        // ---- the calendar ----
        ("verein.cal.title", "Kalender", "Calendar"), ("verein.cal.open", "Kalenderansicht", "Calendar view"),
        ("verein.menu.calendar", "Kalender", "Calendar"), ("verein.admin.nav.calendar", "Kalender", "Calendar"),
        ("verein.cal.type.all", "Alle", "All"), ("verein.cal.type.events", "Termine", "Events"), ("verein.cal.type.meetings", "Sitzungen", "Meetings"),
        ("verein.cal.type.birthdays", "Geburtstage", "Birthdays"), ("verein.cal.type.entries", "Eintritte & Jubiläen", "Joining & anniversaries"),
        ("verein.cal.one.event", "Termin", "Event"), ("verein.cal.one.meeting", "Sitzung", "Meeting"), ("verein.cal.one.birthday", "Geburtstag", "Birthday"),
        ("verein.cal.one.entry", "Mitgliedschaft", "Membership"),
        ("verein.cal.draft", "Entwurf", "Draft"), ("verein.cal.internal", "intern", "internal"), ("verein.cal.cancelled", "abgesagt", "cancelled"),
        ("verein.cal.birthdayOf", "{0} wird {1}", "{0} turns {1}"), ("verein.cal.joined", "{0}: Eintritt in den Verein", "{0}: joined the club"),
        ("verein.cal.yearsIn", "{0}: {1} Jahre im Verein", "{0}: {1} years in the club"),
        ("verein.cal.emptyTitle", "Nichts im Kalender", "Nothing in the calendar"), ("verein.cal.emptyText", "In diesem Zeitraum steht nichts an.", "Nothing is planned in this period."),
        ("verein.cal.emptyMonth", "In diesem Monat steht nichts an.", "Nothing is planned this month."),
        ("verein.cal.feeds", "Kalender-Abo", "Calendar feed"),
        ("verein.cal.feedsHint", "Die öffentliche Adresse enthält nur öffentliche Termine – keine Sitzungen, Geburtstage oder Namen von Mitgliedern. Mit ?art=Umzug nur eine Art. Mitglieder abonnieren im Profil ihren eigenen Kalender (auch interne Termine und ihre Sitzungen).",
            "The public address holds public events only – no meetings, birthdays or names of members. With ?art=Parade only one type. Members subscribe to their own calendar in their profile (internal events and their meetings too)."),
        ("verein.cal.feedPublic", "Öffentliche Termine (iCal)", "Public events (iCal)"), ("verein.cal.feedMore", "Abo-Knopf und Download", "Subscribe button and download"),
        ("verein.cal.newEvent", "Neuer Termin", "New event"),
        ("verein.cal.filter", "Was zeigen", "What to show"), ("verein.cal.prev", "Vorheriger Monat", "Previous month"), ("verein.cal.next", "Nächster Monat", "Next month"),
        ("verein.cal.today", "Heute", "Today"), ("verein.cal.view", "Ansicht", "View"), ("verein.cal.month", "Monat", "Month"), ("verein.cal.list", "Liste", "List"),
        ("verein.cal.myFeed", "Meinen Kalender abonnieren", "Subscribe to my calendar"), ("verein.cal.makeFeed", "Eigenen Kalender-Link im Profil anlegen", "Create your own calendar link in your profile"),
        ("verein.cal.feedNote", "Im Abo stehen Termine und Sitzungen, nie Geburtstage.", "The feed holds events and meetings, never birthdays."),

        // ---- the member area's pages in the block editor ----
        ("verein.page.area", "Verein – Mitgliederbereich", "Club – member area"),
        ("verein.page.areaDesc", "Der Rahmen aller Seiten unter /mitglied nach der Anmeldung (Übersicht, Dokumente, Termine, Kalender, Bilder, Mitglieder, Profil).",
            "The frame of every page under /mitglied after signing in (overview, documents, events, calendar, photos, members, profile)."),
        ("verein.page.signin", "Verein – Anmeldung", "Club – sign-in"),
        ("verein.page.signinDesc", "Die Seite /mitglied für Besucher, die nicht angemeldet sind: Text und Anmeldeformular.", "The /mitglied page for visitors who are not signed in: text and sign-in form."),
        ("verein.page.var.title", "Titel der Seite", "Title of the page"), ("verein.page.var.page", "Die Seite: home, docs, dates, calendar, files, people, board, profile …", "The page: home, docs, dates, calendar, files, people, board, profile …"),
        ("verein.page.var.club", "Name des Vereins", "The club's name"), ("verein.page.var.name", "Name des Mitglieds", "The member's name"),
        ("verein.page.var.firstName", "Vorname des Mitglieds", "The member's first name"), ("verein.page.var.initials", "Initialen des Mitglieds", "The member's initials"),
        ("verein.page.var.board", "Gesetzt, wenn das Mitglied zum Vorstand gehört", "Set when the member is on the board"),
        ("verein.page.var.areaPage", "Die ganze Seite wie bisher (Kopf und Inhalt)", "The whole page as before (head and content)"),
        ("verein.page.var.head", "Kopf: wo man ist, Titel und das eigene Profil", "Head: where you are, title and your own profile"),
        ("verein.page.var.content", "Der Inhalt der Seite", "The page's content"), ("verein.page.var.me", "Das eigene Profil (Bild und Vorname)", "Your own profile (picture and first name)"),
        ("verein.page.var.intro", "Text auf der Anmeldeseite (Einstellungen)", "Text on the sign-in page (settings)"),
        ("verein.page.var.failed", "Gesetzt, wenn die Anmeldung fehlgeschlagen ist", "Set when signing in failed"),
        ("verein.page.var.signinPage", "Die ganze Anmeldeseite wie bisher", "The whole sign-in page as before"),
        ("verein.page.var.form", "Die Anmeldekarte mit dem Formular", "The sign-in card with its form"),
        ("verein.page.slot", "Verein: Baustein", "Club: piece"),
        ("verein.page.slotDesc", "Setzt einen Teil des Mitgliederbereichs (Kopf, Inhalt, Anmeldeformular) auf eine Seite des Vereins im Block-Editor.", "Places a part of the member area (head, content, sign-in form) on one of the club's pages in the block editor."),
        ("verein.page.slotField", "Baustein", "Piece"),
        ("verein.page.slotHelp", "Wirkt nur auf den Seiten „Verein – Mitgliederbereich“ und „Verein – Anmeldung“. Formulare bleiben ganz.", "Only works on the pages “Club – member area” and “Club – sign-in”. Forms stay whole."),
        ("verein.page.sampleName", "Erika Muster", "Erika Example"),
        ("verein.page.sampleText", "Hier steht der Inhalt der jeweiligen Seite – die Übersicht, die Dokumente, die Termine …", "The content of each page goes here – the overview, the documents, the events …"),
    };
}
