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

## Bauen
`./pack-native.ps1 -Path ./docs` → `dist/docs-<version>.zip`, dann `tools/StoreSign sign-code` (Matthias). Tests: `DocsTests.cs`.
