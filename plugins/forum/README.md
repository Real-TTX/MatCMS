# Forum (natives Plugin)

Ein Diskussionsforum für eine MatCMS-Seite – die Grundfunktionen eines klassischen Boards (Burning Board & Co.), gebaut nur gegen
`MatCMS.Plugin.Abstractions`. Key `forum`, Adressen unter `/forum`.

**Was es kann**

- **Kategorien und Foren**: Foren mit gleicher Kategorie stehen in der Übersicht zusammen. Je Forum: wer lesen darf (alle /
  angemeldete Mitglieder / Mitglieder mit Rolle / nur Moderatoren), wer schreiben darf, gesperrt (nur Moderatoren schreiben),
  Reihenfolge. Ein neues Forum hat beim ersten Laden eine Kategorie „Allgemein“ mit „Allgemeine Diskussion“.
- **Themen und Beiträge**: BBCode (`[b] [i] [u] [s] [quote] [quote=Name] [code] [url] [img] [list][*]`, Links werden erkannt),
  Zitieren, Bearbeiten (eigene Beiträge, Zeitfenster einstellbar), Seiten, „neu“-Markierung je Mitglied, „Alles gelesen“,
  Sprung zum Beitrag (`/forum/p/{id}`), Aufrufzähler, Suche, „Neueste Beiträge“.
- **Mitglieder** sind die Konten der Website (Gastbereich, `/anmelden`). Eigene Registrierung unter `/forum/registrieren`
  (Einstellung, aus bei Lieferung), Profilseite mit Beitragszahl und Signatur, Abos mit E-Mail bei neuen Antworten.
- **Moderation**: Mitglieder mit der Rolle `forum-moderator` (einstellbar) heften an, schließen, verschieben, benennen um,
  bearbeiten und löschen direkt auf der Website. Im Admin: Übersicht, Foren, Themen, Meldungen (mit Zähler und Eintrag unter
  „Braucht Aufmerksamkeit“), Mitglieder (sperren, Signatur entfernen), Einstellungen. Mitglieder melden Beiträge.
- **Schutz**: Jeder Text wird kodiert, Markup entsteht nur aus dem erlaubten BBCode (Links nur http/https/mailto/lokal, Bilder
  nur https oder `/uploads/`), Antiforgery auf allen Formularen, Mindestabstand zwischen Beiträgen, Honeypot gegen Bots.
- **Einbindung**: Sitemap (nur öffentliche Foren), Link-Auswahl (Foren), Website-Suche/KI-Bot (`site.search`, nur öffentliche
  Foren), Dashboard-Kachel und -Karte, Mailvorlagen (`forum.reply`, `forum.newthread`, `forum.report`), Template-Teile
  (`forum-index.html`, `forum-board.html`, `forum-thread.html`, `forum-post.html`), CSS über die Variablen der Website
  (`--forum-accent` → `--accent` …). Datenschutz: Löschen eines Kontos lässt die Beiträge stehen, nimmt aber den Namen weg
  („Gelöschtes Mitglied“) und löscht Profil, Abos, Gelesen-Stände und Meldungen; der Export liefert Beiträge, Signatur und Abos.

**Bauen und paketieren**: `./pack-native.ps1 -Path ./forum` → `dist/forum-<version>.zip`. Im Admin unter Plugins importieren;
es läuft erst mit „Native Plugins erlauben“. Tests: `dotnet test tests/MatCMS.Plugins.Tests --filter Forum` (lädt die gebaute
DLL in die ganze Anwendung).

**Fallen**

- Eine Weiterleitung eines Plugins darf kein `#` enthalten (der Host macht daraus `/`). Der Sprung zum Beitrag läuft deshalb über
  `?p=12`; die Themenseite setzt `#p12` selbst (ohne neuen Verlaufseintrag).
- Der POST-Zähler des Hosts gilt je Plugin und Adresse über ALLE Routen zusammen – ein kleinerer Wert auf einer Route würde
  greifen, sobald jemand an derselben Adresse anderswo im Forum geschrieben hat. Alle Routen haben deshalb denselben Wert.
- `WebUtility.HtmlEncode` kodiert Umlaute als `&#246;` – in Tests den Text vor dem Vergleich dekodieren.
