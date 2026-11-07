# Verein (natives Plugin)

Vereinsverwaltung für eine MatCMS-Seite, gebaut nur gegen `MatCMS.Plugin.Abstractions`. Key `verein`, Mitgliederbereich unter `/mitglied`,
Admin unter Admin → Verein. Erster Einsatz: Schalksteinnarren Besigheim e.V.

**Was es kann**

- **Mitglieder und Rollen**: Die Mitglieder sind Konten der Website; der Verein führt dazu seinen eigenen Eintrag (Name, Telefon, Rollen,
  „Mitglied seit“, Notiz, aktiv). Rollen sind frei einstellbar (eine pro Zeile), welche davon zum Vorstand zählen ebenso. Neues Mitglied im Admin
  = Konto mit Zufallspasswort + Einladung per Mail („Passwort festlegen“ über `/anmelden/vergessen`); eine schon vorhandene Adresse wird verknüpft.
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
- **Termine** (Umzüge, Bälle, Ausflüge …): öffentlich oder intern, abgesagt bleibt durchgestrichen stehen. Block **„Vereinstermine“** für jede Seite:
  nur kommende *öffentliche* Termine, Anzahl, „nur diese Art“ (z. B. `Umzug` → „Nächste Umzüge“), Liste oder Karten.
- **Instagram-Bild** (Admin → Verein → Instagram-Bild): Termine als Bild in zwei Vorlagen – *Terminliste* (die nächsten 3–8, auf Wunsch nur eine Art wie „Umzug“) und *Plakat* (ein Termin groß) –, in drei Formaten (Quadrat 1080², Hochformat 1080×1350, Story 1080×1920), mit Hintergrundfoto aus der Mediathek, Vereinsfarbe, Logo und Hashtag-Zeile aus den Einstellungen. Der Server zeichnet ein SVG (Fotos und Logo als eingebettete JPEGs, Systemschriften), der Browser macht daraus das PNG.
- **Vorlagen** für Dokumente, Tagesordnungen und Protokolle mit Platzhaltern (`{{verein}}`, `{{datum}}`, `{{jahr}}`, im Protokoll `{{titel}}`,
  `{{termin}}`, `{{ort}}`, `{{tagesordnung}}`). Beim ersten Laden kommen fünf Startvorlagen (Datenschutz, Fotos/Videos, zwei Tagesordnungen,
  Protokoll) — Ausgangspunkte, keine Rechtsberatung.
- **Bilder & Material**: Mitglieder laden hoch (mehrere auf einmal, Größe einstellbar bis 25 MB); erlaubt sind Bilder, PDF, Office, Text, ZIP, MP3,
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
