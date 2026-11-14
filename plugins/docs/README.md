# Docs / Wiki (natives Plugin, Key `docs`)

Eine Dokumentation bzw. ein Wiki für eine MatCMS-Website. Referenziert nur `MatCMS.Plugin.Abstractions`.

## Was es kann
- **Baum**: Bereiche (oberste Ebene) mit Seiten und Unterseiten (bis 6 Ebenen), sortierbar, Entwürfe nur für Admins (auch alles darunter).
- **Texte** in Markdown (eigener, sicherer Renderer `DocsMarkdown`: Überschriften mit Ankern, Codeblöcke mit Sprache + Kopierknopf, Tabellen,
  Aufgabenlisten, Hinweiskästen `> [!NOTE|TIP|IMPORTANT|WARNING|CAUTION]`, Links zwischen Seiten `[Text](doc:bereich/seite)`; rohes HTML wird als Text
  gezeigt, nur sichere Adressen) oder als formatierter Text (vom Host bereinigt).
- **Seiten**: `/docs` (Start mit Bereichskarten), `/docs/{pfad}`, Suche `/docs/suche` bzw. `/docs/search`; Seitenleiste, Brotkrumen,
  Inhaltsverzeichnis (h2/h3), Zurück/Weiter, Unterseiten-Liste, „Zuletzt geändert“.
- **Sprachen**: je Sprache ein eigener Baum (`/en/docs/…`), Übersetzungen verknüpft (`group_key`), der Sprachumschalter führt zur Übersetzung.
- **Versionen** (optional): Standardversion unter `/docs`, andere unter `/docs/v/{name}/…` (Hinweis + noindex); die erste Version übernimmt alle
  Seiten, weitere beginnen leer oder als Kopie.
- **Aussehen**: Template-Teile `docs-page.html`, `docs-home.html`, `docs-search.html`, `docs-searchbox.html`; CSS über `--docs-*` → Variablen der Website.
- **Weiteres**: Blöcke „Docs: Bereiche“ und „Docs: Suche“, Menüelemente `plugin:docs.docs` (mit Bereichen) und `plugin:docs.search`, Sitemap,
  Link-Ziele, `site.search`, Medienreferenzen, Dashboard-Kachel, „Zuletzt bearbeitet“.
- **API / Operationen** (zum Befüllen): `page.list/read/upsert/delete`, `version.list`; HTTP `GET|PUT /api/v1/docs/pages`, `GET|DELETE /api/v1/docs/pages/{id}`,
  `GET /api/v1/docs/versions` (Site-API-Schlüssel `docs:read`/`docs:write`). Eine Seite heißt nach ihrem Pfad (`erste-schritte/docker`) je Sprache/Version;
  `translationOf` = Pfad derselben Seite in der Standardsprache. Elternseiten zuerst schreiben.

## Editor im Backoffice (1.3)
- **Visuell | Markdown**: eine Markdown-Seite wird wahlweise visuell (WYSIWYG) oder als reines Markdown bearbeitet; der Schalter wandelt in beide
  Richtungen. **Gespeichert wird immer Markdown** – der visuelle Modus ist nur eine Ansicht davon. Werkzeugleiste: Absatz/H2–H4, fett, kursiv,
  durchgestrichen, Code, Link, **Link zu einer Doku-Seite** (Dialog mit Seitenauswahl und Anker → `[Text](doc:pfad#anker)`, fehlende Ziele gestrichelt
  markiert), Bild aus der Mediathek, Listen, Aufgaben, Zitat, **Hinweiskasten** (Hinweis/Tipp/Wichtig/Achtung/Vorsicht – Klick auf den Titel ändert die
  Art oder entfernt den Kasten), Codeblock (Klick auf die Sprache ändert sie), Tabelle (Zeilen/Spalten einfügen und löschen), Trennlinie.
- **Verlustfrei**: der Server schneidet das Markdown in die Blöcke, die `DocsMarkdown` selbst findet (`DocsMarkdown.ToEditor`), und schickt je Block das
  Original **und** sein HTML. Ein Block, den niemand angefasst hat, geht als sein Original zurück (Byte für Byte, Zeilenenden und Leerzeilen inklusive),
  nur ein geänderter Block wird aus seinem HTML neu als Markdown geschrieben (`DocsVisual`, in C#). Umschalten ohne Änderung ändert also nichts; geprüft an
  den 108 Seiten von docs-matmon (unberührt: 108/108 identisch; jeden der 3390 Blöcke neu geschrieben: dieselbe Seite).
- **Live-Vorschau** (Knopf „Vorschau“): gezeichnet vom Server mit `DocsMarkdown` und dem öffentlichen Stylesheet (`DocsHtml.Css` + eigenes CSS) – kein
  zweiter Renderer im Browser; `doc:`-Links werden wie auf der Seite aufgelöst.
- Der Endpunkt dafür ist `/admin/p/docs/convert` (POST, Admin + Antiforgery): `visual`, `markdown`, `preview`. Das Formular speichert im visuellen Modus
  den Editor-Zustand (`md_mode=visual`, `md_visual`), der Server schreibt ihn beim Speichern zu Markdown.
- Das Rich-Text-Format („Formatierter Text“) bleibt unverändert der Editor des Hosts.

**Warum kein Toast UI Editor / Milkdown?** (1) Beide haben ihren eigenen Markdown-Parser und -Serialisierer (markdown-it/remark/ProseMirror) – der versteht
die Doku-Syntax nicht genau wie `DocsMarkdown` (`doc:`-Links, `> [!NOTE]`-Kästen, `\|` in Tabellen, die Lockerheit von Listen) und schreibt beim
Zurückwandeln normalisiertes Markdown: andere Listenzeichen, Leerzeilen, Escapes – „ohne Verlust hin und zurück“ wäre nicht zu halten, und die Vorschau
wäre ein zweiter Renderer. (2) Größe: Toast UI ~500 kB, Milkdown samt ProseMirror ähnlich, gegen ~25 kB eigenes Skript. (3) Sicherheit und Pflege: alles,
was zurückkommt, läuft wieder durch den eigenen, sicheren Renderer; keine fremde Bibliothek im Plugin, die mitgepflegt werden muss. Preis: der visuelle
Modus ist schlichter (contenteditable + `execCommand`), und ein Block, den man ändert, wird in einer einheitlichen Schreibweise neu geschrieben.

## Bilder (1.4)
- **Einfügen oder Hineinziehen**: ein Bild aus der Zwischenablage (Screenshot) oder aus dem Explorer landet über den Upload des Admins
  (`/admin/api/upload`, dieselben Regeln: Typen, 8 MB, SVG bereinigt) in der Mediathek und steht an der Stelle – im visuellen Modus als Bild,
  im Markdown-Modus als `![…](/uploads/…)` am Cursor. Der Knopf „Bild aus der Mediathek“ (Auswahl + Hochladen) bleibt.
- **Im ZIP**: der Export legt jedes Bild, das eine Seite zeigt (`/uploads/…`), als `media/{name}` bei. Der Import nimmt Bilder aus einem Ordner
  `media` (in beliebiger Tiefe, z. B. `content/media/`) und biegt die Verweise der Seiten (`/uploads/{name}` oder relativ `media/{name}`) auf ihre
  Adresse in der Mediathek um. Ein identisches Bild, das schon da ist, wird wiederverwendet (Vergleich der Bytes) – ein zweiter Import kopiert nichts
  und ändert nichts. Der Probelauf zeigt „n Bilder neu“ je Seite und „Bild fehlt: x“ für Verweise, die weder im ZIP noch in der Mediathek sind.
- Dafür braucht es **MatCMS mit Plugin-API 2.3** (`IPluginSite.ReadMediaAsync/FindMediaAsync/AddMediaAsync`). Auf einem älteren Host läuft das
  Plugin weiter: Export ohne Bilder, der Import sagt, dass er die Bilder nicht übernehmen kann (`DocsMedia`, Aufrufe in `NoInlining`-Helfern).

## Import / Export (1.3)
- **Export** (Docs → Import / Export): ein ZIP mit einer `.md` je Seite, `<sprache>/<pfad>.md`, andere als die Standardversion unter
  `<sprache>/v/<version>/<pfad>.md`. Jede Datei beginnt mit YAML-Front-Matter: `title`, `summary`, `icon`, `sort`, `translationOf` (Pfad in der
  Standardsprache), `draft`; `format: html` nur bei formatiertem Text.
- **Import**: dasselbe ZIP. Erst ein **Probelauf** (neu / geändert / unverändert / Fehler, mit Grund), geschrieben wird erst nach Bestätigung – über
  dieselbe Prüfung und Bereinigung wie die API (`DocsOps.UpsertAsync`), Eltern vor Kindern, die Standardsprache vor ihren Übersetzungen. Unveränderte Seiten
  werden nicht angefasst, ein Export wieder importiert ändert also nichts.
- **Front Matter ist freiwillig**: ein Ordner wie `docs-matmon/content/<sprache>/<pfad>.md` (Texte für die API, Titel in `structure.mjs`) lässt sich
  gezippt importieren – Ordner vor dem Sprachordner (`content/`) werden übersprungen; ohne `title` bleibt der Titel der vorhandenen Seite, eine neue Seite
  bekommt ihn aus dem Dateinamen. Leerzeilen am Textende zählen nicht als Änderung.
- Grenzen: höchstens 5000 Dateien und 50 MB entpackt (beim Lesen gezählt). Das hochgeladene ZIP liegt bis zur Bestätigung als `import.zip` in den
  Plugin-Dateien; bestätigt wird mit seinem SHA-256 (ein zweiter Upload dazwischen wird nicht unbemerkt geschrieben).

## Bauen
`./pack-native.ps1 -Path ./docs` → `dist/docs-<version>.zip`, dann `tools/StoreSign sign-code` (Matthias). Tests: `DocsTests.cs`.
