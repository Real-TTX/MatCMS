# MatCMS Cloud – API-Handbuch (für Automatisierung / KI)

Dieses Dokument beschreibt, wie ein externes Programm **oder eine KI** verbundene MatCMS-Websites
über die **MatCMS-Cloud** verwaltet: Instanzen, Backups (Inhalte ziehen/zurückspielen), **Profile**
(ausgerollte Konfiguration) und **Inhalte** (Seiten/Beiträge/Blöcke) über den MCP-Server.

Alles läuft über **eine** Cloud (`https://<deine-cloud>`, z. B. `https://matcmscloud.matthix.de`).
Die Cloud greift nie in eine Site hinein – eine Site meldet sich per Heartbeat und **holt** sich
Aufträge ab. Schreibaktionen (Restore, Content-Ops) werden daher **vorgemerkt** und greifen beim
nächsten Heartbeat der Instanz (~1 Minute).

---

## 1. Authentifizierung

Alle Aufrufe nutzen einen **Operator-API-Schlüssel** (`mck_…`) im Header:

```
Authorization: Bearer mck_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
```

Schlüssel werden im Cloud-Admin unter **API-Schlüssel** angelegt (SHA-256 gespeichert, einmalig im
Klartext sichtbar). Rechte pro Schlüssel:

| Recht | Bedeutung |
|---|---|
| *(Basis)* | Instanzen listen, Backups ziehen/hochladen/listen, Profile **lesen**, Content-Ops lesen. |
| **CanRestore** | Darf ein Backup **live** zurückspielen (`…/restore`) – überschreibt die Produktivseite. |
| **CanManageProfiles** | Darf Profile und ihre Inhalte **schreiben** und Instanzen einem Profil zuordnen. |
| **Instanz-Umfang** | „Alle Instanzen" oder auf ausgewählte begrenzt. Eine unbekannte/außerhalb liegende Instanz gibt immer **404** (nicht 403), damit ein begrenzter Schlüssel keine Instanzen aufzählen kann. |

Fehlerformat immer JSON: `{ "error": "…" }` mit passendem HTTP-Status (401/403/404/400/409).
Rate-Limit-Policy `operatorApi` gilt für alle Endpunkte.

---

## 2. Instanzen

- `GET /api/v1/instances` → `{ canRestore, instances:[{ id, name, status, hosting, version, url, lastHeartbeatUtc }] }`
  `id` ist die **publicId** (im Rest der API als `{publicId}` verwendet).
- `GET /api/v1/instances/{publicId}/profile` → aktuelle Profil-Zuordnung + Sync-Stand
  `{ profileId, profileName, appliedRevision, profileRevision, inSync, lastSyncError }`

---

## 3. Backups – Inhalte ziehen & live zurückspielen

Ein Backup ist eine **ZIP** mit `content.json` (Seiten, Templates, Menüs, Einstellungen, Medien,
Formulare, Beiträge, Komponenten, Benutzer, Plugins) + Ordner `assets/` (die Mediendateien).
`content.json` ist UTF-8 **mit BOM**, hübsch eingerückt. Für zielgenaue Änderungen den exakten String
erst im `content.json` verifizieren; alles außerhalb der Änderung byte-für-byte lassen.

- `POST /api/v1/instances/{publicId}/backups/request` → `{ requestId }` – fordert ein **frisches**
  Backup an; die Instanz lädt es beim nächsten Heartbeat hoch.
- `GET /api/v1/instances/{publicId}/backups` → Liste (neueste zuerst) mit
  `{ id, fileName, sizeBytes, createdAt, uploadedAt, origin, sha256, requestId, restorePending, restoreDoneAt, restoreError }`.
  Auf `uploadedAt` + passende `requestId` pollen.
- `GET /api/v1/instances/{publicId}/backups/{id}/download` → ZIP-Bytes.
- `POST /api/v1/instances/{publicId}/backups` → lädt eine (bearbeitete) ZIP als **rohen Body** hoch.
  Dateiname im Header `X-MatCMS-Backup-Name` (muss auf `.zip` enden). → `{ ok, id, fileName, sha256 }`.
- `POST /api/v1/instances/{publicId}/backups/{id}/restore` → spielt das Backup **live** zurück
  (Recht **CanRestore**). Nur **vorgemerkt**; die Instanz zieht & wendet es beim nächsten Heartbeat an.

**Zyklus:** request → warten (~60 s) → download → `content.json`/`assets/` bearbeiten → upload → restore.
Immer eine datierte lokale Kopie des Vor- und Nach-Zustands behalten (Rollback).

---

## 4. Profile – ausgerollte Konfiguration

Ein **Profil** bündelt Konfiguration (Einstellungen, SMTP, Übersetzung, Backup-Plan, Benutzer,
Plugins, Komponenten, Templates, Mail-Templates) und wird an alle zugeordneten Instanzen ausgerollt.
Jede Schreibänderung erhöht die **Revision** des Profils; die Instanz zieht die neue Konfiguration
beim nächsten Heartbeat. **Nichts wird gelöscht**, weil ein Profil es nicht mehr listet (add-only für
Benutzer; Entfernen stoppt nur künftige Rollouts). Sync-Modi je Payload: `keep` (synchron halten,
überschreibt), `add` (nur ergänzen), `once` (einmalig übernehmen). Benutzer sind immer add-only.

Schreiben braucht **CanManageProfiles**. Lesen braucht nur einen gültigen Schlüssel.

### Profile allgemein
- `GET /api/v1/profiles` → `{ canManageProfiles, profiles:[{ id, name, description, isDefault, revision, joinCode, instanceCount }] }`
- `GET /api/v1/profiles/{id}` → vollständiges Profil inкл. aller Payload-Listen (Secrets maskiert als `***`) und zugeordneter Instanzen.
- `POST /api/v1/profiles` `{ name, description? }` → `{ id, name, joinCode }`
- `PATCH /api/v1/profiles/{id}` – Felder optional, nur gesetzte werden geändert:
  `name, description, autoApprove, autoUpdateLocal, notifyOffline, notifyUpdate, notifyRecipients,
  activateTemplateName, syncSettings, syncUsers, syncPlugins, syncComponents, syncTemplates,
  syncMailTemplates, removeDefaultAdmin, settingsMode, usersMode, pluginsMode, componentsMode,
  templatesMode, mailTemplatesMode` (Modi: `keep|add|once`; Users nie `keep`).
- `PUT /api/v1/profiles/{id}/ai` `{ syncAi?, aiMonthlyTokenBudget?, aiInstruction?, backupBeforeAiChange? }`
- `DELETE /api/v1/profiles/{id}` (zugeordnete Instanzen fallen auf globale Policy zurück)
- `POST /api/v1/profiles/{id}/duplicate` → `{ id, name, joinCode }`
- `POST /api/v1/profiles/{id}/make-default`
- `POST /api/v1/profiles/{id}/rotate-join-code` → `{ joinCode }`
- `POST /api/v1/profiles/{id}/touch` – Revision erhöhen (erzwingt Neu-Sync aller Instanzen)

### Freie Einstellungen (Key/Value)
- `POST /api/v1/profiles/{id}/settings` `{ key, value }` – **Gruppen-Keys** (smtp.*, translate.*, backup.*, ai.*, mail.transport) werden abgelehnt; dafür die Gruppen-Endpunkte nutzen.
- `DELETE /api/v1/profiles/{id}/settings/{key}`

### Gruppen SMTP / Übersetzung
- `PUT /api/v1/profiles/{id}/smtp` `{ mailSource: "global"|"own"|"cloud", host?, port?, user?, password?, fromEmail?, fromName?, ssl? }` – Passwort wird verschlüsselt gespeichert; leer lassen behält das gespeicherte.
- `DELETE /api/v1/profiles/{id}/smtp` – Rollout aus (gespeicherte Werte bleiben).
- `PUT /api/v1/profiles/{id}/translation` `{ provider?, apiKey?, url? }` – `apiKey` verschlüsselt.
- `DELETE /api/v1/profiles/{id}/translation`

### Benutzer (add-only, Passwort wird serverseitig gehasht)
- `POST /api/v1/profiles/{id}/users` `{ username, email?, displayName?, password? | passwordHash?, role? }`
  Neuer Benutzer braucht `password` **oder** `passwordHash`. Klartext wird nie gespeichert.
- `DELETE /api/v1/profiles/{id}/users/{username}`

### Komponenten
- `POST /api/v1/profiles/{id}/components` `{ type, name, description?, icon?, fieldsJson?, templateHtml? }` (`fieldsJson` muss gültiges JSON sein; `type` = Identität)
- `DELETE /api/v1/profiles/{id}/components/{type}`

### Templates (Designer-Felder; Identität = `name`)
- `POST /api/v1/profiles/{id}/templates` `{ name, accentColor?, secondaryColor?, headingFont?, bodyFont?, buttonStyle?, headingColor?, textColor?, backgroundColor?, customCss?, customJs?, layoutHtml?, menuMapJson?, parametersJson?, paramValuesJson? }`
- `DELETE /api/v1/profiles/{id}/templates/{name}`

### Mail-Templates
- `POST /api/v1/profiles/{id}/mail-templates` `{ key, name?, description?, subject, body?, enabled?, isHtml? }`
- `DELETE /api/v1/profiles/{id}/mail-templates/{key}`

### Plugins (rohes ZIP-Bundle)
- `GET /api/v1/profiles/{id}/plugins/{pluginKey}/download`
- `POST /api/v1/profiles/{id}/plugins` – Body = das **rohe Plugin-ZIP** (wie `PluginPackager.Export`); Key/Name/Version werden aus `plugin.json` gelesen, Upsert nach Key.
- `DELETE /api/v1/profiles/{id}/plugins/{pluginKey}`

### Instanz ↔ Profil + Sync
- `PUT /api/v1/instances/{publicId}/profile` `{ profileId }` – zuordnen/verschieben (setzt `appliedRevision=0`, erzwingt Neu-Pull). Braucht **CanManageProfiles** **und** Instanz-Umfang.
- `DELETE /api/v1/instances/{publicId}/profile` – Zuordnung entfernen.
- `POST /api/v1/instances/{publicId}/sync` – Neu-Sync vormerken (Instanz zieht `/config` beim nächsten Heartbeat). Es gibt **keinen** cloud-seitigen Sofort-Apply; das Ergebnis erscheint im Sync-Report der Instanz.

---

## 5. MCP-Server (`/mcp`) – Inhalte per KI verwalten

Für KI-Clients (ChatGPT, Claude, Cursor) bietet die Cloud einen **Model-Context-Protocol**-Server
unter `/mcp` (streamable HTTP). Auth = **derselbe** `mck_…`-Schlüssel als `Authorization: Bearer …`.
Die Tools laufen über dieselben Dienste wie die REST-API und sind auf den Schlüssel-Umfang begrenzt.

Inhalts-Tools schreiben **nicht** direkt in die Site, sondern reihen eine **Content-Op** ein, die die
Instanz beim nächsten Heartbeat über ihre **eigenen** Validierer anwendet (add-only). Sie sind daher
**asynchron**: das Tool liefert eine `opId`, dann `get_content_op(opId)` pollen.

| Tool | Zweck |
|---|---|
| `list_instances` | verbundene Sites auflisten |
| `get_instance` | Detail einer Site |
| `list_backups` / `request_backup` / `restore_backup` | Backup-Zyklus (restore braucht CanRestore) |
| `list_pages` | Seiten einer Site (async → `get_content_op`) |
| `get_page` | eine Seite mit Blöcken lesen (async) |
| `create_page` | neue Seite anlegen (add-only) |
| `update_page_blocks` | die Blöcke einer Seite ersetzen (CanRestore) |
| `create_post` | neuen Beitrag anlegen (add-only) |
| `get_content_op` | Status/Ergebnis einer Op abfragen: `pending` / `applied` / `skipped-exists` / `failed` |

**`create_page`** (Beispiel-Eingabe):
```json
{
  "publicId": "oC4LaoOTRafCBoT8",
  "title": "Über uns",
  "slug": "ueber-uns",
  "blocksJson": "[{\"type\":\"hero\",\"data\":{\"heading\":\"Über uns\",\"subheading\":\"…\"}},{\"type\":\"richtext\",\"data\":{\"heading\":\"Unsere Geschichte\",\"body\":\"<p>…</p>\"}}]",
  "showInNav": true
}
```
Blöcke sind ein JSON-Array `[{ "type": "<blocktyp>", "data": { "<feld-id>": "<wert>" } }]`. Die Instanz
**re-validiert**: nur bekannte Blocktypen und **Textfelder** überleben (Bilder/Auswahlfelder werden
verworfen und über den Editor bzw. Backup/Restore gesetzt). RichText wird server-seitig **bereinigt**
(kein `<script>`/`on*`/`javascript:`). Slug existiert schon → `skipped-exists` (nie überschreiben).

Deep-Edits an Bildern, Layout, Farben etc. laufen über **Backup/Restore** (Abschnitt 3), nicht über
Content-Ops.

---

## 6. Block-Katalog

Blöcke, die eine MatCMS-Site kennt, mit ihren Feld-IDs. Spalte **KI-setzbar** = das Feld überlebt eine
`create_page`/`update_page_blocks`-Content-Op (nur Text-/RichText-Felder). Bild-/Auswahl-/Link-Felder
setzt man über den Editor oder Backup/Restore. Container-Blöcke (`section`, `columns`, `cards`,
`accordion`, `leistungen`, `servicegrid`, `timeline`, `references`, `logostrip`, `gallery`) enthalten
Kind-Blöcke (`card`, `column`, `faq`, `step`, `service`, `leistung`, `reference` …).

> ¹ „KI-setzbar" = über den Content-Op-Kanal setzbar (Textfelder). Der Katalog wird aus
> `src/MatCMS/Content/BlockRegistry.cs` erzeugt (`tools/gen-api-blocks.js`) und kann bei einer
> Instanz um Komponenten-/Plugin-Blöcke erweitert sein.

<!-- BLOCKS:BEGIN (generiert – nicht von Hand pflegen) -->
### `hero` — Hero (Kopfbereich)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `subheading` | Textarea | ✅ | Untertext |
| `image` | Image | — | Bild (Band darunter) |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |
| `align` | Select | — | Ausrichtung |
| `imageHeight` | Select | — | Bildhöhe |
| `imagePosition` | Select | — | Bildausschnitt (Fokus) |
| `heroStyle` | Select | — | Hero-Stil |
| `heroBg` | Select | — | Hero-Hintergrund |
| `images` | List | — | Weitere Bilder (Slideshow) |
| `image` | Image | — | Bild (Band darunter) |
| `alt` | Text | ✅ | Alternativtext |
| `heroMedia` | Select | — | Slideshow-Effekt |
| `heroInterval` | Select | — | Wechsel alle |
| `heroFit` | Select | — | Bildanzeige |
| `heroWidth` | Select | — | Bildbreite |

### `richtext` — Text

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `body` | RichText | ✅ | Inhalt |
| `align` | Select | — | Ausrichtung |
| `width` | Select | — | Breite |

### `columns` — Spalten

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spaltenanzahl |

### `column` — Spalte

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `title` | Text | ✅ | Titel |
| `body` | RichText | ✅ | Text |
| `bg` | Select | — | Hintergrundfarbe |
| `fg` | Select | — | Textfarbe |

### `servicegrid` — Leistungen (Raster)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spalten (Desktop) |

### `service` — Service

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Tabler-Name) |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Beschreibung |

### `cta` — Call-to-Action

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `text` | Textarea | ✅ | Text |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |

### `countup` — Zähler

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `date` | Text | ✅ | Datum/Uhrzeit |
| `mode` | Select | — | Richtung |

### `form` — Formular

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `form` | Select | — | Formular |
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |

### `memberlogin` — Mitglieder-Login

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |

### `image` — Bild

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |
| `width` | Select | — | Breite |

### `accordion` — Akkordeon (FAQ)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |

### `faq` — Frage

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `question` | Text | ✅ | Frage |
| `answer` | RichText | ✅ | Antwort |

### `quote` — Zitat

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `quote` | Textarea | ✅ | Zitat |
| `author` | Text | ✅ | Autor / Quelle |

### `imagetext` — Bild & Text

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `image` | Image | — | Bild |
| `heading` | Text | ✅ | Überschrift |
| `body` | RichText | ✅ | Inhalt |
| `imageSide` | Select | — | Bildseite |

### `spacer` — Abstand

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `size` | Select | — | Größe |

### `logostrip` — Logo-Leiste

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `items` | List | — | Logos |
| `image` | Image | — | Logo |
| `alt` | Text | ✅ | Alternativtext |
| `url` | Url | — | Link (optional) |

### `leistungen` — Leistungen (Container)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spaltenanzahl |

### `leistung` — Leistung

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |
| `image` | Image | — | Bild (optional) |

### `herocta` — Hero mit Aktion

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Textarea | ✅ | Überschrift |
| `priceOld` | Text | ✅ | Alter Preis (durchgestrichen) |
| `priceNew` | Text | ✅ | Neuer Preis (hervorgehoben) |
| `sub` | Textarea | ✅ | Untertext |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |
| `button2Text` | Text | ✅ | Zweiter Button – Text |
| `button2Url` | Url | — | Zweiter Button – Link |
| `note` | Text | ✅ | Hinweiszeile (z. B. „Vertrauen von über 30 Unternehmen“) |
| `arrow` | Select | — | Pfeil auf den Button zeigen |
| `arrowText` | Text | ✅ | Pfeil-Notiz (handschriftlich) |
| `colors` | Select | — | Farben |

### `timeline` — Ablauf (Timeline)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `colors` | Select | — | Farben |

### `step` — Schritt

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Emoji) |
| `title` | Text | ✅ | Titel |
| `text` | Textarea | ✅ | Text |

### `gallery` — Galerie

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `source` | Select | — | Quelle |
| `tags` | MultiSelect | — | Tag |
| `showFilter` | Select | — | Tag-Filter anzeigen |
| `layout` | Select | — | Layout |
| `columns` | Select | — | Spalten |
| `strip` | Select | — | Bildstreifen in der Großansicht |
| `images` | List | — | Bilder |
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |

### `cards` — Karten / Vergleich

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `columns` | Select | — | Spalten |
| `layout` | Select | — | Anordnung |
| `autoplay` | Select | — | Automatisch bewegen |

### `card` — Karte

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `icon` | Text | ✅ | Icon (Emoji) |
| `image` | Image | — | Bild |
| `title` | Text | ✅ | Titel |
| `tags` | Text | ✅ | Merkmale (Chips) |
| `text` | Textarea | ✅ | Text |
| `features` | Textarea | ✅ | Detail-Kacheln |
| `buttonText` | Text | ✅ | Button-Text |
| `buttonUrl` | Url | — | Button-Link |

### `section` — Bereich (Container)

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `layout` | Select | — | Anordnung |
| `bg` | Select | — | Hintergrund |

### `posts` — Beiträge

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `display` | Select | — | Anzeige |
| `tag` | Text | ✅ | Tag-Filter |
| `columns` | Select | — | Spalten |
| `perPage` | Text | ✅ | Pro Seite |
| `limit` | Text | ✅ | Anzahl |
| `showFilter` | Select | — | Tag-Filter anzeigen |

### `references` — Referenzen / Projekte

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `heading` | Text | ✅ | Überschrift |
| `intro` | Textarea | ✅ | Einleitung |
| `display` | Select | — | Darstellung |
| `columns` | Select | — | Spalten |

### `reference` — Referenz

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `image` | Image | — | Hauptbild (optional) |
| `title` | Text | ✅ | Titel |
| `subtitle` | Textarea | ✅ | Subtext |
| `url` | Url | — | URL |
| `tags` | Text | ✅ | Tags (Stack) |
| `screenshots` | List | — | Screenshots |
| `image` | Image | — | Bild |
| `alt` | Text | ✅ | Alternativtext |
| `caption` | Text | ✅ | Bildunterschrift |

### `html` — HTML

| Feld | Typ | KI-setzbar¹ | Bezeichnung |
|---|---|:--:|---|
| `html` | Textarea | ✅ | HTML |
<!-- BLOCKS:END -->

---

## 7. Rezepte für eine KI

- **Neue Unterseite mit Text anlegen:** `create_page` mit `hero` + `richtext`/`cards` → `opId` → `get_content_op` bis `applied`.
- **Bestehende Seite umtexten:** `get_page` (async) → Blöcke anpassen → `update_page_blocks` (CanRestore).
- **Bild/Design ändern:** Backup ziehen (Abschnitt 3) → `content.json`/`assets/` bearbeiten → hochladen → restore.
- **Konfiguration auf viele Sites ausrollen:** Profil per Abschnitt 4 bearbeiten (`PATCH`/Payload-Endpunkte); die Instanzen ziehen die neue Revision automatisch. Mit `POST …/instances/{publicId}/sync` einen sofortigen Neu-Pull vormerken.
- **Neue Site an ein Profil hängen:** `PUT /api/v1/instances/{publicId}/profile { profileId }`.
