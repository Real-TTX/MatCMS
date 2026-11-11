# Blog (natives Plugin, Key `blog`)

Bis 2026-11 Teil des Images (eingebautes Plugin), jetzt ein **code-signiertes Paket des MatCMS Store** (`docs/builtin-extraction.md`).
Referenziert nur `MatCMS.Plugin.Abstractions`.

## Was es kann
- Beiträge mit Titelbild, Galerie (Wisch-Galerie/einzeln/Raster), Tags, Anhängen, Quelle und Terminierung; Entwürfe sieht nur der Admin.
- Seiten: Übersicht `/blog`, Beitrag `/blog/{slug}` — gezeichnet über den Template-Teil `post` des **Kerns** (`article.html`), darunter
  was andere Plugins über `content.discussion` beitragen (Forum).
- Block „Beiträge“ (Kacheln oder ganze Beiträge, Tag-Filter, Seiten), Dashboard-Kachel, „Zuletzt bearbeitet“, Sitemap, Link-Ziele,
  `site.search`, `content.item`, Operationen `post.list/read/create/update/delete` (+ intern `post.restore`) für Cloud, MCP und KI-Assistent.
- Editor: „Aus Link übernehmen“ (`IPluginSite.ImportFromLinkAsync`, API 2.2), „KI: Teaser“ (`IPluginSite.AskAiAsync`); die Skripte
  `blog-editor.js` und `ai-seo-field.js` liegen in `assets/` und werden unter `/plugin-assets/blog/` ausgeliefert.

## Mehrsprachig
- Ein Beitrag hat eine Sprache (`Locale`). Beiträge der Standardsprache behalten ihre Adressen (`/blog/{slug}`), andere Sprachen liegen unter
  dem Präfix der Website (`/en/blog/{slug}`, `IPluginSite.LocalePrefix` — folgt „Standardsprache unter /“). Eine Sprache, in der die Website
  nicht veröffentlicht, hat keinen Blog (404).
- Übersetzungen teilen `TranslationGroup` (neue Spalte, vom Plugin und vom Kern deklariert, nie gelöscht/umbenannt). Der Sprachumschalter
  führt von einem Beitrag zu seiner Übersetzung (`PluginResult.WithAlternates`, API 2.2), die Übersicht zu den Übersichten der anderen Sprachen.
- Admin: neuer Beitrag wählt seine Sprache; gespeicherter Beitrag zeigt Übersetzungen und „Übersetzung anlegen (EN)“ (füllt den Editor mit dem
  Original); je Sprache höchstens eine Übersetzung; Liste mit Sprachspalte und Filter (nur bei mehr als einer Sprache).
- Forum-Quelle: `blog:{slug}` für die Standardsprache (wie bisher), `blog:{sprache}_{slug}` für andere.

## Daten
- Tabelle ist die **Kerntabelle `Posts`** (adoptiert, `BlogStore.Columns` = `src/MatCMS/Services/PostsTable.cs` — beide Listen gleich halten).
  Die Sicherung trägt `Posts` im Kern, mit oder ohne Plugin.

## Bauen
`./pack-native.ps1 -Path ./blog` → `dist/blog-<version>.zip`, dann `tools/StoreSign sign-code` (Matthias' Schlüssel). Unsigniert wird der Key
`blog` von jeder Website abgelehnt (`BuiltInPlugins.FormerKeys`).
