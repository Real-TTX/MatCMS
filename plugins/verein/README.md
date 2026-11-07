# Verein (natives Plugin)

Vereinsverwaltung für eine MatCMS-Seite, gebaut nur gegen `MatCMS.Plugin.Abstractions`. Key `verein`, Mitgliederbereich unter `/mitglied`,
Admin unter Admin → Verein. Erster Einsatz: Schalksteinnarren Besigheim e.V.

**Was es kann**

- **Mitglieder und Rollen**: Die Mitglieder sind Konten der Website; der Verein führt dazu seinen eigenen Eintrag (Name, Telefon, Rollen,
  „Mitglied seit“, Notiz, aktiv). Rollen sind frei einstellbar (eine pro Zeile), welche davon zum Vorstand zählen ebenso. Neues Mitglied im Admin
  = **Einladung** (`ctx.Members.InviteAsync`): eine einzige Mail mit einem Link, Passwort zweimal eingeben, danach ist die Adresse bestätigt und das
  Mitglied angemeldet in `/mitglied` (keine Bestätigungs-Mail der Website, kein „Passwort vergessen“). Ein bestätigtes Konto wird nur verknüpft
  (Mail mit Anmelde-Link). Der Vorstand kann ein **Startpasswort** setzen (beim Anlegen oder später „Passwort setzen“, nie per Mail). Braucht MatCMS ab 1.13.361.
- **Persönliche Angaben**: Geburtsdatum, Adresse und **eigene Felder** (Einstellung „Eigene Felder“, eine Zeile je Feld, z. B. `Hexe-Nr. | alle`, `Narr-Nr. | vorstand` – wer es im Mitgliederbereich sieht). Geburtsdatum und Adresse: nur Vorstand oder per Einstellung alle. Mitglieder pflegen Telefon und Adresse selbst. **Kinder** ohne Konto/E-Mail, an ein oder mehrere Mitglieder gebunden. Mitgliederliste als CSV.
- **Menü**: Angemeldete Mitglieder bekommen im Hauptmenü „Mitglieder“ mit Unterpunkten (Übersicht, Dokumente mit Zähler, Termine, Bilder, Liste, Vorstand, Profil).
- **Kalender (iCal)**: `/verein/termine.ics` (öffentliche Termine, Link unter dem Termin-Block) und je Mitglied ein geheimer Link `/verein/kalender/{token}.ics` (auch interne Termine und eigene Sitzungen; im Profil erzeugen/erneuern).
- **Mitgliederbereich `/mitglied`**: Die Anmeldung steht auf `/mitglied` selbst (das Formular schickt an die Anmeldung der Website, die nach
  `/mitglied` zurückführt). Alle Unterseiten schicken Nicht-Angemeldete zurück nach `/mitglied`, nie zur allgemeinen Anmeldeseite. Hinein kommt nur,
  wer im Verein eingetragen und aktiv ist; andere Konten sehen „Kein Zugang“. Seiten: Übersicht, Dokumente, Termine & Sitzungen, Bilder & Material,
  Mitglieder (Vorstand für alle, alle Mitglieder nur für den Vorstand oder per Einstellung), Vorstand (Übersicht, wer was beantwortet hat), Profil
  (Telefon, Passwort, eigene Bestätigungen).
- **Dokumente** zum *Bestätigen* (Haken), *Unterschreiben* (Haken + vollständigen Namen eintippen, muss zum eingetragenen Namen passen) oder *Lesen* —
  z. B. Datenschutz-Einwilligung, Bildfreigabe. Für alle, nur den Vorstand oder eine Rolle; Frist, Datei-Anhang. Jede Antwort wird **einmal**
  gespeichert (Nachweis: Zeitpunkt, getippter Name, Adresse des Geräts); eine spätere Änderung geht über den Vorstand. Mail an alle Betroffenen beim
  Veröffentlichen, „Offene erinnern“, CSV-Liste (mit Schutz vor Formeln), Archiv. Ein Dokument mit Antworten lässt sich nicht löschen.
- **Sitzungen**: Art, Termin, Ort, Eingeladene, Tagesordnung (aus Vorlage), Einladung per Mail, Zu-/Absagen im Mitgliederbereich (Vorstand sieht
  wer kommt), Protokoll als Entwurf (nur Admin) oder veröffentlicht (für die Eingeladenen; Mail beim ersten Veröffentlichen).
- **Termine** (Umzüge, Bälle, Ausflüge …): **Entwurf** (nur im Admin) oder veröffentlicht, öffentlich oder intern, abgesagt bleibt durchgestrichen stehen.
  **Planung** für Mitglieder: Ablauf als Zeitplan (Aufstellung, Zunftmeisterempfang …), Treffpunkt, Parken/Anfahrt, Hinweise – sichtbar auf
  `/mitglied/termine/{id}`, nie auf der Website. Block **„Vereinstermine“** für jede Seite:
  nur kommende *öffentliche* Termine, Anzahl, „nur diese Art“ (z. B. `Umzug` → „Nächste Umzüge“), Liste oder Karten.
- **Instagram-Bild** (Admin → Verein → Instagram-Bild): Termine als Bild in drei Vorlagen – *Terminliste* (die nächsten 3–8, auf Wunsch nur eine Art wie „Umzug“), *Saison / Narrenfahrplan* (alle Termine eines Zeitraums, voreingestellt die nächste Fasnet 6.1.–Aschermittwoch, bei vielen Terminen auf mehrere Bilder verteilt) und *Plakat* (ein Termin groß) –, in drei Formaten (Quadrat 1080², Hochformat 1080×1350, Story 1080×1920), mit Hintergrundfoto aus der Mediathek, Vereinsfarbe, Logo und Hashtag-Zeile aus den Einstellungen. Der Server zeichnet ein SVG (Fotos und Logo als eingebettete JPEGs, Systemschriften), der Browser macht daraus das PNG.
- **Vorlagen** für Dokumente, Tagesordnungen und Protokolle mit Platzhaltern (`{{verein}}`, `{{datum}}`, `{{jahr}}`, im Protokoll `{{titel}}`,
  `{{termin}}`, `{{ort}}`, `{{tagesordnung}}`). Beim ersten Laden kommen fünf Startvorlagen (Datenschutz, Fotos/Videos, zwei Tagesordnungen,
  Protokoll) — Ausgangspunkte, keine Rechtsberatung.
- **Bilder & Material**: Mitglieder laden viele Dateien auf einmal hoch (Handy, je Datei eine Anfrage, Fortschrittsbalken); Aufnahmedatum aus dem Foto (EXIF, sonst Hochladedatum), Termin automatisch nach dem Datum oder gewählt; **Freigabe** durch den Vorstand (Einstellung, an bei Lieferung; Zähler im Admin). Mitglieder laden hoch (mehrere auf einmal, Größe einstellbar bis 25 MB); erlaubt sind Bilder, PDF, Office, Text, ZIP, MP3,
  MP4 — nie HTML/SVG/Skripte, Bilder und PDF werden an ihren ersten Bytes geprüft. Ausgeliefert nur an Mitglieder (`nosniff`, CSP mit `sandbox`),
  privat = nur für sich und den Vorstand.
- **Mails** (Admin → Mailvorlagen): `verein.invite`, `verein.document`, `verein.reminder`, `verein.meeting`, `verein.minutes`; Antwortadresse einstellbar.
- **Datenschutz**: Löschen eines Kontos entfernt Eintrag, Rückmeldungen und Uploads; Antworten auf Dokumente bleiben als Zählung, ohne Namen und
  Adresse. Der Export liefert Eintrag, Antworten, Rückmeldungen, Uploads.

**Bauen und paketieren**: `./pack-native.ps1 -Path ./verein` → `dist/verein-<version>.zip`. Im Admin unter Plugins importieren; es läuft erst mit
„Native Plugins erlauben“. Tests: `dotnet test tests/MatCMS.Plugins.Tests --filter Verein` (lädt die gebaute DLL in die ganze Anwendung).

**Fallen und Grenzen**

- Falsches Passwort auf `/mitglied` landet auf der Anmeldeseite der Website (die API hat keine Passwortprüfung für Plugins); von dort führt die
  Anmeldung wieder nach `/mitglied`.
- Bilder werden in Originalgröße ausgeliefert (kein Verkleinern im Plugin) — die Galerie lädt bei vielen großen Handyfotos entsprechend.
- Texte sind deutsch und stehen im Code (kein Sprachumschalter).
- Alle Routen haben denselben POST-Zähler (der Host zählt je Plugin und Adresse über alle Routen).
