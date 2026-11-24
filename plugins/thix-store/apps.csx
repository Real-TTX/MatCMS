// The website's own words about the apps, by short name (the launcher's catalog has the rest: name, tagline, description, pictures).
// Order = the order of the tiles; Office = the group "ThixOffice"; New = the badge "Neu"; Color = the app's accent on its page and tile;
// Lead = a shorter first paragraph than the catalog's description (null: the description); Features = the tiles on the app's page;
// Note = HTML under the pictures. An app of the catalog missing here still gets its page and its tile, after these and without features.
// German only, like the catalog's texts.
using System.Collections.Generic;

public sealed record TsFeature(string Icon, string Title, string Text);
public sealed record TsExtra(string Short, string Color, bool Office, bool New, string? Lead, TsFeature[] Features, string? Note);

public static class TsApps
{
    public static readonly TsExtra[] All =
    {
        new TsExtra("clip", "#2F6FED", false, false, "ThixClip merkt sich, was du kopierst – Texte, Links und Bilder samt erkanntem Text – und holt es mit einem Tastenkürzel zurück.",
            new[]
            {
                new TsFeature("ti-keyboard", "Ein Tastenkürzel", "Strg+Umschalt+V unter Windows, ⌃⌥V auf dem Mac – der Verlauf ist sofort da, ein Klick fügt ein."),
                new TsFeature("ti-photo-search", "Bilder mit Text", "Kopierte Bilder werden offline gelesen. So findest du auch den Screenshot mit der Fehlermeldung über die Suche."),
                new TsFeature("ti-pin", "Favoriten und Anheften", "Was du oft brauchst, bleibt oben. Wie lange der Rest bleibt, bestimmst du."),
                new TsFeature("ti-lock", "Diskret", "Was ein Passwortmanager als vertraulich markiert, wird nie aufgezeichnet. Die Aufnahme lässt sich jederzeit pausieren."),
                new TsFeature("ti-cloud", "Auf allen Computern", "Mit der MatCloud ist der Verlauf auf all deinen Computern gleich – und auch im Browser erreichbar."),
            },
            null),
        new TsExtra("note", "#DB4C3F", false, false, "Eine bewusst einfache Notiz-App: Markdown, Text und kennwortgeschützte Notizen als normale Dateien, mit Tabs wie in einem Editor.",
            new[]
            {
                new TsFeature("ti-file-text", "Normale Dateien", "Jede Notiz ist eine .md- oder .txt-Datei in deinem Ordner – lesbar mit jedem Programm, ohne Datenbank."),
                new TsFeature("ti-lock-square-rounded", "Mit Kennwort", "Geheime Notizen werden mit AES-256-GCM verschlüsselt und nur mit deinem Kennwort geöffnet."),
                new TsFeature("ti-layout-columns", "Bibliothek oder Notepad", "Breit eine kleine Notizbibliothek, schmal ein Editor mit Tabs wie Notepad."),
                new TsFeature("ti-markdown", "Markdown-Vorschau", "Überschriften, Listen und Links so, wie sie gemeint sind – ein Klick zwischen Text und Vorschau."),
                new TsFeature("ti-cloud", "Abgleich", "Mit der MatCloud sind deine Notizen auf allen Computern gleich."),
            },
            null),
        new TsExtra("calendar", "#2F6FED", false, false, "Microsoft 365, Google Kalender, ICS-Abos und eigene Kalender in einem Fenster – mit Erinnerungen, die stehen bleiben, bis du sie beantwortest.",
            new[]
            {
                new TsFeature("ti-calendar-stats", "Alles in einem Fenster", "Arbeit aus Microsoft 365, Privates aus Google, Feiertage, Schule und Verein als ICS-Abo, dazu eigene Kalender."),
                new TsFeature("ti-bell-ringing", "Erinnerungen, die bleiben", "Die Erinnerung bleibt über allen Fenstern stehen, bis du sie schließt oder verschiebst – und zum Beginn kommt sie groß in die Mitte."),
                new TsFeature("ti-video", "Mit einem Klick dabei", "Teams, Meet, Zoom und Webex: Der Beitreten-Knopf steht direkt in der Erinnerung."),
                new TsFeature("ti-moon", "Später und Nicht stören", "Erinnerungen verschieben oder für eine Weile ganz ruhig stellen."),
                new TsFeature("ti-cloud", "Auf allen Computern", "Mit der MatCloud sind Konten, eigene Kalender und erledigte Erinnerungen überall gleich."),
            },
            "Was ThixCalendar mit den Daten deines Google Kalenders macht, steht in der <a href=\"https://matthix.de/thixcalendar\">App-Beschreibung</a> und der <a href=\"https://matthix.de/datenschutz\">Datenschutzerklärung</a> auf matthix.de."),
        new TsExtra("fred", "#5B33F0", false, false, "Ein Tastenkürzel, ein Suchfeld: Programme starten und beenden, rechnen, Befehle im Terminal ausführen, im Web suchen.",
            new[]
            {
                new TsFeature("ti-search", "Alt+Leertaste", "Das Suchfeld ist überall sofort da. Tippen, Enter, fertig."),
                new TsFeature("ti-calculator", "Rechnen im Suchfeld", "Einfach die Rechnung eintippen, das Ergebnis steht schon da."),
                new TsFeature("ti-terminal-2", "Terminal inklusive", "Befehle direkt ausführen und die Ausgabe gleich im Fenster sehen. Hängende Programme beendest du mit „kill“."),
                new TsFeature("ti-world-search", "Websuche mit Kürzeln", "Eigene Schlüsselwörter für deine Suchmaschinen und Seiten."),
                new TsFeature("ti-puzzle", "Themes, Plugins, Skripte", "Mit eigenem Plugin-Store und deinen eigenen Skripten wird ThixFred zu deinem Werkzeug."),
            },
            null),
        new TsExtra("tunnel", "#3B8CF0", false, false, "SSH-Profile, mehrere Hops und lokale Portweiterleitungen – ThixTunnel baut Tunnel mit dem OpenSSH deines Systems auf.",
            new[]
            {
                new TsFeature("ti-route", "Mehrere Hops", "Über einen oder mehrere Sprungserver zum Ziel (ssh -J … -L …), ohne Befehle zu tippen."),
                new TsFeature("ti-plug-connected", "Verbindet sich selbst", "Auf Wunsch beim Start automatisch und nach einem Abbruch wieder."),
                new TsFeature("ti-key", "Schlüssel einrichten", "Schlüssel erzeugen und auf dem Server hinterlegen – Schritt für Schritt."),
                new TsFeature("ti-arrows-exchange", "Windows und Mac", "Profile exportieren und auf dem anderen System importieren."),
                new TsFeature("ti-layout-sidebar-right", "Im Infobereich", "ThixTunnel wartet unauffällig im Infobereich bzw. in der Menüleiste."),
            },
            null),
        new TsExtra("shot", "#3F78F4", false, true, null,
            new[]
            {
                new TsFeature("ti-screenshot", "Screenshots", "Bereich aufziehen, loslassen, fertig – oder den ganzen Bildschirm, auf Wunsch mit Zeitverzögerung."),
                new TsFeature("ti-crop", "Zuschneiden und markieren", "Der Ausschnitt öffnet sich gleich im Editor."),
                new TsFeature("ti-video", "Bildschirmvideos", "Bildschirm oder Ausschnitt mit bis zu 30 Bildern pro Sekunde als MP4, mit Mauszeiger und Systemton."),
                new TsFeature("ti-zoom-in", "Zooms nachträglich", "Im Videoeditor Zooms setzen, verschieben, kürzen und verstärken – das Original bleibt erhalten."),
                new TsFeature("ti-layout-navbar", "Die Aufnahmeleiste", "Die schwebende Leiste oben am Bildschirm startet Screenshots und Videos direkt vom Desktop."),
            },
            null),
        new TsExtra("space", "#0E8074", false, true, null,
            new[]
            {
                new TsFeature("ti-layout-grid", "Interaktive Karte", "Jede Datei ist eine Kachel so groß wie ihr Anteil, die Farbe sagt die Art. Ein Doppelklick zoomt in den Ordner."),
                new TsFeature("ti-arrows-diff", "Was ist gewachsen?", "Jede neue Analyse wird mit der vorherigen verglichen: was dazukam, wuchs, schrumpfte oder verschwand."),
                new TsFeature("ti-trash", "Direkt aufräumen", "Öffnen, im Explorer oder Finder zeigen und löschen – lokal in den Papierkorb."),
                new TsFeature("ti-server", "Laufwerke und Freigaben", "Laufwerke, Ordner und SMB-Freigaben – auf Wunsch automatisch täglich, wöchentlich oder monatlich analysiert."),
                new TsFeature("ti-cloud", "Alle Computer im Blick", "Mit der MatCloud siehst du auch die Laufwerke deiner anderen Computer und wie sie wachsen."),
            },
            null),
        new TsExtra("sync", "#347DE6", false, true, null,
            new[]
            {
                new TsFeature("ti-arrows-exchange", "Abgleich, Spiegel, Backup", "Bidirektional, als Spiegel oder als Backup mit Versionen – Konflikte erkennt ThixSync."),
                new TsFeature("ti-eye", "Erst schauen, dann kopieren", "Alle geplanten Änderungen siehst du vorher in einer Vorschau und löst Konflikte einzeln."),
                new TsFeature("ti-cloud-network", "Jeder Speicher", "SMB, NAS, SFTP, FTP, WebDAV, S3, Google Drive, OneDrive, Dropbox, Nextcloud und mehr – jeder als Quelle und als Ziel."),
                new TsFeature("ti-clock", "Zeitpläne", "Von Hand, im Intervall, täglich, wöchentlich oder automatisch, sobald sich etwas ändert."),
                new TsFeature("ti-bolt", "Schnell", "Viele kleine Dateien parallel, große in Stücken – mit Fortschritt in MB/s und Dateien pro Sekunde."),
            },
            null),
        new TsExtra("write", "#185ABD", true, false, "Schreiben wie in Word: Menüband, Seiten wie auf Papier, Formatvorlagen, Tabellen und Export als PDF. Word-Dokumente öffnet und speichert ThixWrite direkt.",
            new[]
            {
                new TsFeature("ti-file-type-docx", "Direkt .docx", "Word-Dokumente öffnen und speichern – was ThixWrite noch nicht anzeigt, bleibt beim Speichern erhalten."),
                new TsFeature("ti-layout", "Seiten wie auf Papier", "Menüband und Backstage, wie man es kennt, und die Seite so, wie sie gedruckt wird."),
                new TsFeature("ti-typography", "Formatvorlagen", "Überschriften, Aufzählungen, Tabellen, Bilder, Fußnoten und Inhaltsverzeichnis."),
                new TsFeature("ti-message", "Kommentare", "Anmerkungen am Rand, Suchen und Ersetzen."),
                new TsFeature("ti-file-type-pdf", "Export als PDF", "Fertig zum Versenden oder Drucken."),
            },
            null),
        new TsExtra("calc", "#107C41", true, false, "Rechnen wie in Excel: Formeln mit 459 Funktionen unter deutschen Namen, Sortieren und Filtern, Diagramme. Excel-Mappen und CSV öffnet und speichert ThixCalc direkt.",
            new[]
            {
                new TsFeature("ti-math-function", "459 Funktionen", "Unter den deutschen Namen, die du kennst – SUMME, SVERWEIS, WENN – samt dynamischer Arrays."),
                new TsFeature("ti-file-spreadsheet", "Direkt .xlsx und CSV", "Excel-Mappen (.xlsx, .xlsm) und CSV-Dateien öffnen und speichern."),
                new TsFeature("ti-chart-bar", "Diagramme", "Aus der Auswahl ein Diagramm, dazu bedingte Formatierung."),
                new TsFeature("ti-filter", "Sortieren und Filtern", "Auch bei 100 000 Zeilen flüssig."),
                new TsFeature("ti-printer", "Drucken und PDF", "Mit Seiteneinrichtung und Export als PDF."),
            },
            null),
        new TsExtra("show", "#C43E1C", true, false, "Präsentieren wie in PowerPoint: Folien mit Layouts und Designs, Formen, Bilder und Diagramme, Übergänge und Animationen.",
            new[]
            {
                new TsFeature("ti-presentation", "Layouts und Designs", "Folien mit fertigen Layouts, Designs für die ganze Präsentation."),
                new TsFeature("ti-shape", "Formen, Bilder, Diagramme", "Alles, was eine Folie braucht."),
                new TsFeature("ti-transition-right", "Übergänge und Animationen", "Damit die Präsentation lebt."),
                new TsFeature("ti-device-desktop", "Referentenansicht", "Bildschirmpräsentation mit Notizen und nächster Folie für dich."),
                new TsFeature("ti-file-type-ppt", "Direkt .pptx", "PowerPoint-Präsentationen (.pptx, .pptm) öffnen und speichern, Export als PDF."),
            },
            null),
        new TsExtra("mail", "#2674BE", true, true, null,
            new[]
            {
                new TsFeature("ti-mail", "IMAP und SMTP", "Name, Adresse und Kennwort genügen meist: ThixMail kennt die gängigen Anbieter wie GMX, WEB.DE, Telekom, Posteo und mailbox.org."),
                new TsFeature("ti-layout-sidebar", "Wie im deutschen Outlook", "Ordnerbereich mit „Alle Posteingänge“, Nachrichtenliste mit Suche, Lesebereich und Unterhaltungen."),
                new TsFeature("ti-wifi-off", "Offline lesen", "Nachrichten liegen auf dem Mailserver und zum Lesen ohne Verbindung auf deinem Computer – nie in der Cloud."),
                new TsFeature("ti-calendar-event", "Kalender und Personen", "Termine, Einladungen zusagen oder absagen und das Adressbuch von ThixContacts im selben Fenster."),
                new TsFeature("ti-shield-lock", "Kennwörter sicher", "Kennwörter liegen nur im Schlüsselspeicher des Systems. Einem unbekannten Zertifikat wird erst vertraut, wenn du es geprüft hast."),
            },
            null),
        new TsExtra("contacts", "#7B5DA9", true, true, null,
            new[]
            {
                new TsFeature("ti-address-book", "Wie Outlooks „Personen“", "Favoriten, Gruppen und Kategorien links, die Liste mit Suche und Alphabet in der Mitte, rechts die Visitenkarte."),
                new TsFeature("ti-users-group", "Kontaktgruppen", "Gruppen anlegen, Mitglieder hinzufügen und mit einem Klick eine E-Mail an alle schreiben."),
                new TsFeature("ti-copy", "Doppelte zusammenführen", "Gleiche Namen, E-Mail-Adressen oder Telefonnummern finden – auch +49 und 0049 – und Feld für Feld zusammenführen."),
                new TsFeature("ti-file-import", "vCard und CSV", "Import und Export als vCard und CSV, beim CSV-Import mit Spaltenzuordnung. Drucken und Export als PDF."),
                new TsFeature("ti-cloud", "Überall gleich", "Mit der MatCloud sind die Kontakte auf all deinen Computern und im Browser gleich – und in ThixMail."),
            },
            null),
    };
}
