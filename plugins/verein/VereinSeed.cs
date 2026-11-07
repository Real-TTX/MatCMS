namespace MatCMS.Plugin.Verein;

/// <summary>The templates a new club starts with. Starting points only — the back office says so; nothing here is legal advice.</summary>
public static class VereinSeed
{
    public static readonly (string Name, string Kind, string Title, string Body)[] Templates =
    {
        ("Einwilligung Datenschutz (Mitgliederdaten)", VereinStore.TplDocument, "Einwilligung in die Verarbeitung meiner Daten",
            """
            Liebe Mitglieder,

            für die Vereinsarbeit verarbeitet {{verein}} personenbezogene Daten seiner Mitglieder: Name, Anschrift, Geburtsdatum, Telefonnummer, E-Mail-Adresse, Eintrittsdatum und Funktionen im Verein. Die Daten werden nur für Zwecke des Vereins genutzt (Mitgliederverwaltung, Einladungen, Beiträge, Organisation von Veranstaltungen) und nicht an Dritte weitergegeben, außer wenn das für die Vereinsarbeit nötig ist (z. B. an einen Dachverband) oder gesetzlich vorgeschrieben ist.

            Im Mitgliederbereich der Website sehen die anderen Mitglieder deinen Namen und deine Rolle; der Vorstand sieht zusätzlich deine Kontaktdaten und deine Bestätigungen.

            Du kannst jederzeit Auskunft über deine gespeicherten Daten verlangen sowie ihre Berichtigung oder Löschung, soweit keine Aufbewahrungspflichten entgegenstehen. Eine Einwilligung kannst du jederzeit für die Zukunft widerrufen – eine kurze Nachricht an den Vorstand genügt.

            Mit meiner Bestätigung stimme ich der Verarbeitung meiner Daten wie beschrieben zu.

            (Vorlage – bitte vor dem Veröffentlichen an euren Verein anpassen und prüfen.)
            """),
        ("Einwilligung Fotos und Videos", VereinStore.TplDocument, "Einwilligung zur Veröffentlichung von Fotos und Videos",
            """
            {{verein}} möchte das Vereinsleben zeigen – auf der Website, in sozialen Netzwerken (z. B. Instagram, Facebook), in der Presse und in Vereinsdrucksachen.

            Ich bin damit einverstanden, dass Fotos und Videos, auf denen ich (im Häs oder ohne) zu erkennen bin und die bei Veranstaltungen, Umzügen und Vereinsterminen entstehen, für diese Zwecke veröffentlicht werden dürfen. Mein Name wird dabei nur genannt, wenn ich zustimme.

            Mir ist bewusst, dass im Internet veröffentlichte Bilder weltweit abrufbar sind und von Dritten kopiert werden können. Ich kann diese Einwilligung jederzeit für die Zukunft widerrufen; Bilder auf der Website und in den Kanälen des Vereins werden dann entfernt, bereits gedruckte Veröffentlichungen bleiben unberührt.

            Für Kinder und Jugendliche unter 16 Jahren bestätigt ein Erziehungsberechtigter.

            (Vorlage – bitte vor dem Veröffentlichen an euren Verein anpassen und prüfen.)
            """),
        ("Tagesordnung Vorstandssitzung", VereinStore.TplAgenda, "Vorstandssitzung",
            """
            1. Begrüßung und Feststellung der Beschlussfähigkeit
            2. Protokoll der letzten Sitzung
            3. Berichte (Vorsitz, Kasse, Schriftführung)
            4. Termine und Veranstaltungen
            5. Anträge und Beschlüsse
            6. Verschiedenes
            """),
        ("Tagesordnung Mitgliederversammlung", VereinStore.TplAgenda, "Mitgliederversammlung {{jahr}}",
            """
            1. Begrüßung und Feststellung der ordnungsgemäßen Einladung und Beschlussfähigkeit
            2. Genehmigung der Tagesordnung
            3. Bericht des Vorstands
            4. Bericht der Kasse
            5. Bericht der Kassenprüfer
            6. Entlastung des Vorstands
            7. Wahlen
            8. Anträge
            9. Termine und Ausblick
            10. Verschiedenes
            """),
        ("Protokoll", VereinStore.TplMinutes, "Protokoll",
            """
            Protokoll: {{titel}}
            Datum und Uhrzeit: {{termin}}
            Ort: {{ort}}

            Anwesend:
            Entschuldigt:
            Sitzungsleitung:
            Protokoll:

            Tagesordnung:
            {{tagesordnung}}

            Verlauf und Beschlüsse:
            TOP 1:
            TOP 2:

            Ende der Sitzung:

            ______________________          ______________________
            Sitzungsleitung                 Protokoll
            """),
    };
}
