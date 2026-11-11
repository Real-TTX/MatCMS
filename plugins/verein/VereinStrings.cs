namespace MatCMS.Plugin.Verein;

/// <summary>
/// Every text of the club plugin as (key, German, English), registered as the plugin's two string tables and read with <c>ctx.T("verein.…")</c> — the
/// host picks the language of the request (the admin's language in the back office, the page's language on public routes) and falls back to German.
/// The table is split over partial files by area (each area owns its own key prefix, so two files can never define the same key); a test checks that
/// every key the code uses is here with both languages. Keys must contain a dot: the host's Localizer falls back to plugin tables only for dotted keys.
/// </summary>
internal static partial class VereinStrings
{
    public static void Register(IPluginBuilder b)
    {
        b.Strings("de", All.ToDictionary(r => r.Key, r => r.De));
        b.Strings("en", All.ToDictionary(r => r.Key, r => r.En));
    }

    internal static (string Key, string De, string En)[] All => _all ??= Core.Concat(Admin).Concat(Public).Concat(Other).ToArray();
    private static (string Key, string De, string En)[]? _all;

    private static readonly (string Key, string De, string En)[] Core =
    {
        // the language marker: V.En(ctx) asks it, so dates follow exactly the language the texts are in
        ("verein.lang", "de", "en"),

        // ---- settings (VereinSettings.Declare; group names are keys too, so the group pages are /_settings?group=verein.set.group…) ----
        ("verein.set.groupGeneral", "Allgemein", "General"), ("verein.set.groupRoles", "Rollen & Felder", "Roles & fields"),
        ("verein.set.groupArea", "Mitgliederbereich", "Member area"), ("verein.set.groupUploads", "Bilder & Material", "Photos & material"),
        ("verein.set.clubName", "Name des Vereins", "Club name"),
        ("verein.set.clubNameHelp", "Erscheint im Mitgliederbereich, in den E-Mails und auf dem Briefkopf. Leer = Name der Website.", "Shown in the member area, in the e-mails and on the letterhead. Empty = the site's name."),
        ("verein.set.replyTo", "Antwortadresse der E-Mails", "Reply-to address of the e-mails"),
        ("verein.set.replyToHelp", "Wer auf eine E-Mail des Vereins antwortet, schreibt an diese Adresse (z. B. die des Vorstands).", "Replies to the club's e-mails go to this address (e.g. the board's)."),
        ("verein.set.eventsTitle", "Überschrift der Termine", "Heading of the events"),
        ("verein.set.eventsTitleHelp", "Über dem Block „Vereinstermine“ auf der Website, wenn der Block keine eigene hat.", "Above the “Club events” block on the website when the block has none of its own."),
        ("verein.set.askDefault", "Neue Termine fragen die Teilnahme ab", "New events ask who is coming"),
        ("verein.set.askDefaultHelp", "An: Bei jedem neuen Termin ist „Mitglieder sollen zu- oder absagen“ schon angehakt. Eine E-Mail geht erst raus, wenn beim Termin „Abfrage per E-Mail senden“ gedrückt wird.",
            "On: every new event has “Members should reply yes or no” ticked already. No e-mail goes out until “Send request by e-mail” is pressed on the event."),
        ("verein.set.roles", "Rollen", "Roles"), ("verein.set.role", "Rolle", "Role"), ("verein.set.rolePlaceholder", "z. B. Kassierer/in", "e.g. Treasurer"), ("verein.set.board", "Vorstand", "Board"),
        ("verein.set.rolesHelp", "Ein Mitglied kann mehrere Rollen haben. Wer eine Vorstandsrolle hat, sieht im Mitgliederbereich die Übersichten (wer hat bestätigt, wer kommt) und alle Mitglieder.",
            "A member can have several roles. Whoever has a board role sees the overviews in the member area (who confirmed, who is coming) and all members."),
        ("verein.set.fields", "Eigene Felder", "Own fields"), ("verein.set.field", "Feld", "Field"), ("verein.set.fieldPlaceholder", "z. B. Mitgliedsnummer", "e.g. Membership number"),
        ("verein.set.fieldAll", "Alle sehen es", "Everyone sees it"),
        ("verein.set.fieldsHelp", "Zusätzliche Angaben je Mitglied (z. B. Mitgliedsnummer, Instrument, Spielklasse, Häs-Nummer). Ohne Haken sieht ein Feld nur der Vorstand; auf der Website erscheint es nie. Umbenennen beginnt das Feld leer.",
            "Extra details per member (e.g. membership number, instrument, league, costume number). Without the tick only the board sees a field; it never appears on the website. Renaming starts the field empty."),
        ("verein.set.directoryForAll", "Mitgliederliste für alle Mitglieder", "Member list for all members"),
        ("verein.set.directoryForAllHelp", "Aus: Mitglieder sehen nur den Vorstand. An: alle sehen alle Mitglieder (Name, Rolle, Telefon).", "Off: members see only the board. On: everyone sees all members (name, role, phone)."),
        ("verein.set.birthdayForAll", "Geburtsdatum für alle Mitglieder sichtbar", "Date of birth visible to all members"),
        ("verein.set.birthdayForAllHelp", "Aus: nur der Vorstand (und die Person selbst) sieht es.", "Off: only the board (and the person) sees it."),
        ("verein.set.addressForAll", "Adresse für alle Mitglieder sichtbar", "Address visible to all members"),
        ("verein.set.addressForAllHelp", "Aus: nur der Vorstand (und die Person selbst) sieht sie.", "Off: only the board (and the person) sees it."),
        ("verein.set.loginIntro", "Text auf der Anmeldeseite", "Text on the sign-in page"),
        ("verein.set.twoFactor", "Zwei-Faktor-Anmeldung", "Two-factor sign-in"),
        ("verein.set.twoFactorOptional", "freiwillig (jedes Mitglied kann sie im Profil einschalten)", "optional (every member can switch it on in the profile)"),
        ("verein.set.twoFactorBoard", "Pflicht für den Vorstand", "Required for the board"), ("verein.set.twoFactorAll", "Pflicht für alle Mitglieder", "Required for all members"),
        ("verein.set.twoFactorHelp", "Pflicht: Wer sie noch nicht eingerichtet hat, wird beim Öffnen des Mitgliederbereichs zur Einrichtung geschickt (Code aus einer Authenticator-App, z. B. Google oder Microsoft Authenticator).",
            "Required: whoever has not set it up yet is sent to the setup when opening the member area (code from an authenticator app, e.g. Google or Microsoft Authenticator)."),
        ("verein.set.uploadApproval", "Uploads vor dem Veröffentlichen freigeben", "Approve uploads before they are shown"),
        ("verein.set.uploadApprovalHelp", "An: Was ein Mitglied hochlädt, sehen erst der Vorstand und die Person selbst; die anderen Mitglieder erst nach der Freigabe.",
            "On: what a member uploads is seen by the board and the person first; the other members only after approval."),
        ("verein.set.maxUpload", "Größte Datei beim Hochladen (MB)", "Largest file to upload (MB)"),

        // ---- the events block (VereinPlugin) ----
        ("verein.block.name", "Vereinstermine", "Club events"),
        ("verein.block.description", "Die kommenden öffentlichen Termine des Vereins (z. B. die nächsten Umzüge) – gepflegt unter Admin → Verein → Termine.", "The club's upcoming public events (e.g. the next parades) – kept under Admin → Club → Events."),
        ("verein.block.heading", "Überschrift", "Heading"), ("verein.block.headingHelp", "Leer = die Überschrift aus den Einstellungen des Vereins.", "Empty = the heading from the club's settings."),
        ("verein.block.defaultHeading", "Kommende Termine", "Upcoming events"),
        ("verein.block.intro", "Einleitung", "Introduction"), ("verein.block.count", "Wie viele Termine", "How many events"), ("verein.block.countAll", "alle", "all"),
        ("verein.block.kind", "Nur diese Art", "Only this kind"), ("verein.block.kindHelp", "Leer = alle Arten. Sonst nur Termine, deren Art so heißt.", "Empty = all kinds. Otherwise only events of this kind."),
        ("verein.block.layout", "Darstellung", "Layout"), ("verein.block.layoutList", "Liste", "List"), ("verein.block.layoutCards", "Karten", "Cards"),
        ("verein.block.empty", "Text, wenn nichts ansteht", "Text when nothing is coming up"), ("verein.block.emptyText", "Gerade stehen keine Termine an.", "There are no upcoming events right now."),
        ("verein.block.subscribe", "Im eigenen Kalender abonnieren", "Subscribe in your own calendar"),

        // ---- public event pages (VereinEvents) and link targets ----
        ("verein.events.title", "Termine", "Events"), ("verein.events.all", "Alle Termine", "All events"), ("verein.events.event", "Termin", "Event"),
        ("verein.events.subscribeAll", "Alle Termine im eigenen Kalender abonnieren", "Subscribe to all events in your own calendar"),
        ("verein.events.cancelled", "abgesagt", "cancelled"), ("verein.events.when", "Wann", "When"), ("verein.events.where", "Wo", "Where"),
        ("verein.events.toCalendar", "In den Kalender", "Add to calendar"), ("verein.events.map", "Karte", "Map"), ("verein.events.more", "Mehr erfahren", "Learn more"),
        ("verein.events.membersLink", "Ablauf, Treffpunkt und Bilder im Mitgliederbereich", "Schedule, meeting point and photos in the member area"),
        ("verein.links.group", "Verein", "Club"), ("verein.links.events", "Termine (Übersicht)", "Events (overview)"), ("verein.links.memberArea", "Mitgliederbereich", "Member area"),
        ("verein.links.ical", "Termine als Kalender (iCal)", "Events as a calendar (iCal)"), ("verein.links.event", "Termin: {0} ({1})", "Event: {0} ({1})"),

        // ---- the site menu for members (VereinMenu) ----
        ("verein.menu.members", "Mitglieder", "Members"), ("verein.menu.overview", "Übersicht", "Overview"), ("verein.menu.docs", "Dokumente", "Documents"),
        ("verein.menu.events", "Termine & Sitzungen", "Events & meetings"), ("verein.menu.files", "Bilder & Material", "Photos & material"),
        ("verein.menu.directory", "Mitgliederliste", "Member list"), ("verein.menu.board", "Vorstand", "Board"), ("verein.menu.profile", "Mein Profil", "My profile"),
        ("verein.menu.signOut", "Abmelden", "Sign out"),

        // ---- the board's overview and the dashboard card (VereinDashboard) ----
        ("verein.dash.title", "Verein", "Club"), ("verein.dash.today", "heute", "today"), ("verein.dash.tomorrow", "morgen", "tomorrow"),
        ("verein.dash.internal", "intern", "internal"), ("verein.dash.inOpen", "{0} dabei · {1} offen", "{0} coming · {1} open"),
        ("verein.dash.nextEvents", "Nächste Termine", "Next events"), ("verein.dash.nothing", "Gerade steht nichts an.", "Nothing coming up right now."),
        ("verein.dash.col.event", "Termin", "Event"), ("verein.dash.col.whenWhere", "Wann · Wo", "When · Where"), ("verein.dash.col.participation", "Teilnahme", "Attendance"),
        ("verein.dash.allEvents", "Alle Termine", "All events"), ("verein.dash.nextMeeting", "Nächste Sitzung", "Next meeting"),
        ("verein.dash.what", "Was", "What"), ("verein.dash.when", "Wann", "When"), ("verein.dash.where", "Wo", "Where"), ("verein.dash.invitation", "Einladung", "Invitation"),
        ("verein.dash.notSent", "noch nicht verschickt", "not sent yet"), ("verein.dash.sentAt", "verschickt am {0}", "sent on {0}"), ("verein.dash.open", "Öffnen", "Open"),
        ("verein.dash.birthdays", "Geburtstage (30 Tage)", "Birthdays (30 days)"), ("verein.dash.noBirthdays", "Keine – oder es fehlen Geburtsdaten.", "None – or dates of birth are missing."),
        ("verein.dash.col.who", "Wer", "Who"), ("verein.dash.col.when", "Wann", "When"), ("verein.dash.col.age", "Alter", "Age"),
        ("verein.dash.jubilees", "Jubiläen {0}", "Anniversaries {0}"), ("verein.dash.col.years", "Jahre", "Years"), ("verein.dash.col.since", "Seit", "Since"),
        ("verein.dash.newest", "Zuletzt aufgenommen", "Recently joined"), ("verein.dash.child", "Kind", "Child"),
        ("verein.dash.openArea", "Mitgliederbereich öffnen", "Open member area"), ("verein.dash.members", "Mitglieder", "Members"),
        ("verein.dash.children", "{0} Kinder", "{0} children"), ("verein.dash.passive", "{0} passiv", "{0} passive"), ("verein.dash.ofWhich", "davon {0}", "of which {0}"),
        ("verein.dash.noAccess", "Ohne Zugang", "Without access"), ("verein.dash.openConfirmations", "Offene Bestätigungen", "Open confirmations"),
        ("verein.dash.uploadsWaiting", "Uploads zur Freigabe", "Uploads to approve"), ("verein.dash.quickAdd", "Schnell anlegen", "Quick add"),
        ("verein.dash.add.member", "Mitglied", "Member"), ("verein.dash.add.child", "Kind", "Child"), ("verein.dash.add.doc", "Dokument", "Document"),
        ("verein.dash.add.meeting", "Sitzung", "Meeting"), ("verein.dash.add.event", "Termin", "Event"),
        ("verein.dash.turns", "{0} wird {1}", "{0} turns {1}"),
        ("verein.dash.cardTitle", "Verein · Termine & Geburtstage", "Club · Events & birthdays"), ("verein.dash.cardAll", "Zur Vereinsübersicht", "To the club overview"),
    };
}
