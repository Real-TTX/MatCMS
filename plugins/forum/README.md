# Forum (natives Plugin)

Ein Diskussionsforum für eine MatCMS-Seite – die Grundfunktionen eines klassischen Boards (Burning Board & Co.), gebaut nur gegen
`MatCMS.Plugin.Abstractions`. Key `forum`, Adressen unter `/forum`.

**Was es kann**

- **Kategorien und Foren**: Foren mit gleicher Kategorie stehen in der Übersicht zusammen. Je Forum: wer lesen darf (alle /
  angemeldete Mitglieder / Mitglieder mit Rolle / nur Moderatoren), wer schreiben darf, gesperrt (nur Moderatoren schreiben),
  Reihenfolge (Pfeile in der Liste). Ein neues Forum hat beim ersten Laden eine Kategorie „Allgemein“ mit „Allgemeine Diskussion“.
- **Themen und Beiträge**: BBCode (`[b] [i] [u] [s] [quote] [quote=Name] [code] [url] [img] [list][*]`, Links werden erkannt),
  Zitieren, Bearbeiten (eigene Beiträge, Zeitfenster einstellbar), Seiten, „neu“-Markierung je Mitglied, „Alles gelesen“,
  Sprung zum Beitrag (`/forum/p/{id}`), Aufrufzähler, Suche, „Neueste Beiträge“.
- **Mitglieder** sind die Konten der Website (Gastbereich, `/anmelden`). Eigene Registrierung unter `/forum/registrieren`
  (Einstellung, aus bei Lieferung), Profilseite mit Beitragszahl und Signatur, Abos mit E-Mail bei neuen Antworten.
- **Moderation**: Mitglieder mit der Rolle `forum-moderator` (einstellbar) heften an, schließen, verschieben, benennen um,
  bearbeiten und löschen direkt auf der Website. Im Admin: Übersicht, Foren, Themen, Meldungen (mit Zähler und Eintrag unter
  „Braucht Aufmerksamkeit“), Mitglieder (sperren, Signatur entfernen), Einstellungen. Mitglieder melden Beiträge.
- **UI-Durchgang** (1.4): Themenliste startet auf „Alle Foren“, Status-Badges stehen am Thementitel, Mitglieder ohne Konto (z. B. nach einem Restore) erscheinen in der Mitgliederliste wie in der Übersicht, Erklärtexte und das Feld „Reihenfolge“ sind weg, Zeitzone ist eine Auswahl; öffentlich: geschlossenes Thema lädt nicht mehr zum Anmelden ein, Anmelde-Hinweis mit Knopf in einer Zeile, Kopfleiste auf dem Handy aufgeräumt, ein `[code]` ohne `[/code]` bleibt Text.
- **Schutz**: Jeder Text wird kodiert, Markup entsteht nur aus dem erlaubten BBCode (Links nur http/https/mailto/lokal, Bilder
  nur https oder `/uploads/`), Antiforgery auf allen Formularen, Mindestabstand zwischen Beiträgen, Honeypot gegen Bots.
- **Artikel-Diskussionen** (wie bei Golem.de): In den Einstellungen (Gruppe „Artikel-Diskussionen“) ein Forum nennen, z. B. `news`.
  Dann hat jeder Blog-Beitrag dort ein eigenes Thema mit dem Titel des Beitrags; es entsteht beim ersten Kommentar
  (`/forum/artikel/blog-{slug}`, nur angemeldet, nur für veröffentlichte Beiträge). Unter dem Beitrag stehen alle Beiträge der
  Diskussion (höchstens 50, dann ein Link ins Forum), „Mitdiskutieren“ und „Im Forum öffnen“; das Thema verlinkt zurück auf den
  Beitrag. Blog und Forum reden über die Ereignisse `content.discussion` und `content.item`.
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
