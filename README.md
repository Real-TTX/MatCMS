# MatCMS – leichtgewichtiges, block-basiertes CMS

**MatCMS** ist ein schlankes, selbst-gehostetes CMS als WordPress-Alternative: ein
**block-basierter Seiten-Editor** (im Stil von Shopify-Sections) mit Live-Vorschau,
Templates, Menüs, Mediathek, Formularen, Blog/Beiträgen, Mehrsprachigkeit, einem
**Plugin-System** sowie vollständigem **Backup & Restore**.

Der Standard-Seed ist generisch (leeres MatCMS mit Einrichtungs-Assistent). Eine
konkrete Website wird als **Backup-ZIP** unter *Admin → Backup → Wiederherstellen*
eingespielt – so laufen beliebig viele Instanzen auf demselben Image.

- **Framework:** ASP.NET Core 10 (Razor Pages), C#
- **Datenbank:** SQLite (via EF Core) – eine Datei, kein extra DB-Container
- **Auth:** Cookie-basiert, Login **nur** über `/login` (Standard: `admin` / `admin`)
- **Ports:** intern `8080` (Basis-Image-Default), gemappt auf Host `9101`
- **Persistenz:** ein Docker-Volume `matcms-data` → `/app/appdata` (DB, Keys, Backups, Uploads)

---

## Funktionsumfang

- **Block-Editor** – Seiten als geordnete Block-Liste; Auswahl/Bearbeiten in der
  Seitenleiste, Einfügen zwischen Blöcken per Hover, **Live-Vorschau rechts**
  (persistiert erst beim Speichern). Blöcke können **hierarchisch/verschachtelt**
  sein (z. B. Spalten, Karten, Galerie).
- **Block-Typen** – u. a. Hero, Rich-Text, Spalten, Leistungs-/Service-Raster,
  Karten (mit Icon), Vergleichskarten, Galerie (Mediathek- oder Tag-Quelle),
  Carousel/Slider, Akkordeon/FAQ, Zitat, Bild-Text, Logostrip, CTA, Formular,
  Beiträge (Blog), Bild, Abstand, roher HTML-Block.
- **Komponenten** – eigene, wiederverwendbare Blöcke aus Feldern zusammenklicken
  (getrennt von den System-Blöcken).
- **Templates** – Designer (Farben, Fonts, Header/Buttons) getrennt von
  Layout/Parameter-Mapping; Seiten-Layouts („Parts"), Schema-Versionierung mit
  Auto-Konvertierung, sowie ein **Datei-Editor (CodeMirror)** für Layout-HTML/CSS/JS.
- **Menüs** – mehrere Menüs (Header, Footer, Toolbar …) mit **Submenüs/Dropdowns**
  und Icons; Zuordnung ins Template per Mapping.
- **Mediathek** – Upload von **Bildern und Dateien**, Tags, Mehrfachauswahl; ein
  einheitlicher Bildauswahl-Dialog (Upload + Mediathek) überall.
- **Formulare** – Formular-Builder wie der Block-Editor: Felder (Text, Mehrzeilig,
  E-Mail, Telefon, Auswahl, Datum, **Zeitraum/Datepicker**), Gruppen, **Conditions**,
  Vorbefüllung per Query-Parameter, konfigurierbare Erfolgsmeldung und
  **E-Mail-Benachrichtigung**; Einsendungen unter *Anfragen*.
- **Beiträge / Blog** – Beiträge mit Tags, **Veröffentlichungs-Scheduling**
  (erst ab Datum sichtbar) und Beiträge-Block mit Blog-Modus, Tag-Filter & Paging.
- **Mehrsprachigkeit (i18n)** – mehrsprachige Inhalte mit Sprach-Umschalter,
  gruppierte Seiten-Versionen + On-Page-Umschalter, **Diff-Tool** (Original vs.
  Übersetzung, fehlende/zusätzliche Blöcke werden markiert) und **Auto-Übersetzung**
  (DeepL Free oder LibreTranslate).
- **Plugins** – Installieren, Export/Import, Update/Migrate; pro Plugin eigener
  Asset-Ordner; Plugins können Admin-Seiten, öffentliche Endpunkte **und
  Content-Blöcke mit eigenen Editor-Feldern** registrieren. Mitgeliefert:
  *Google-Bewertungen* (manuell / Embed / Places-API) und ein *Bewertungs-Plugin*.
- **Einstellungen** – Logo/Favicon, Seitenname, Footer, SMTP (mit **Test-Mail**),
  Fehler-/404-Seiten, Sitemap + robots.txt, Custom-Code / Google Analytics,
  **Wartungsmodus** (themebare „Coming soon"-Seite).
- **Backup & Restore** – granularer Export/Import (Seiten, Templates, Formulare,
  Menüs, Einstellungen, Medien, optional Benutzer) als ZIP, plus **geplante
  Backups**.
- **Einrichtungs-Assistent** – Step-Wizard beim ersten Start.

---

## Schnellstart mit Docker (empfohlen)

Voraussetzung: Docker Desktop.

```bash
docker compose up -d --build
```

Danach läuft die Seite auf **http://localhost:9101**
Admin-Login: **http://localhost:9101/login** (Benutzer `admin`, Passwort `admin`).

Alternativ das mitgelieferte Skript:

```bash
./run-docker.ps1
```

Nützliche Befehle:

```bash
docker compose logs -f       # Logs ansehen
docker compose down          # stoppen (Daten bleiben im Volume erhalten)
docker compose up -d --build # neu bauen & starten
docker compose down -v       # ZURÜCKSETZEN (löscht das Volume: DB, Uploads, Backups)
```

### „Always Repull" / immer aktuell

- In `docker-compose.yml` ist `build.pull: true` gesetzt – bei jedem `--build`
  werden die Basis-Images (`sdk:10.0`, `aspnet:10.0`) **neu gezogen**.
- Die NuGet-Pakete sind als `10.0.*` referenziert; ein frischer Build zieht damit
  automatisch die neuesten Patch-Versionen.

---

## CI/CD & Versionierung

Zwei GitHub-Actions-Workflows bauen das Docker-Image und pushen es in die
**GitHub Container Registry (GHCR)** unter `ghcr.io/<owner>/matcms`
(der `<owner>` wird kleingeschrieben).

| Branch / Kontext | Workflow                        | Version                                | `:latest`? |
|------------------|---------------------------------|----------------------------------------|:----------:|
| `main` (Release) | `.github/workflows/release.yml` | `MAJOR.MINOR.<build>-<datetime>`       | ja         |
| `dev` (Nightly)  | `.github/workflows/dev.yml`     | `nightly-<build>-<datetime>`           | nein       |
| lokal            | (Dockerfile-Default)            | `local-<datetime>` (manuell empfohlen) | –          |

- `MAJOR.MINOR` kommt aus der Datei **`VERSION`** im Repo-Wurzelverzeichnis
  (Default `1.0`).
- `<build>` = `github.run_number` (fortlaufende Lauf-Nummer der Action).
- `<datetime>` = UTC-Zeitstempel `yyyyMMddHHmmss`.
- Der Login an GHCR erfolgt mit `${{ github.actor }}` und dem automatischen
  `${{ secrets.GITHUB_TOKEN }}` (Workflows brauchen `permissions: packages: write`).
- Die berechnete Version wird als Build-Arg **`APP_VERSION`** an den Docker-Build
  übergeben und im Image als `InformationalVersion` hinterlegt. Die
  In-App-Update-Prüfung vergleicht damit gegen die neuesten GHCR-Tags.

### Lokaler Build mit Version

```bash
docker build -t matcms:local \
  --build-arg APP_VERSION="local-$(date -u +%Y%m%d%H%M%S)" .
```

Ohne `--build-arg` wird schlicht `local` verwendet. `InformationalVersion` ist ein
freier String, daher bricht der Build auch bei nicht-numerischen Versionen nicht.

---

## Lokale Entwicklung (Hot Reload)

Voraussetzung: .NET SDK 10.

```bash
./run-dev.ps1
```

bzw. manuell:

```bash
dotnet restore
dotnet watch run
```

Läuft ebenfalls auf **http://localhost:9101** und aktualisiert sich bei Code- und
Ansichts-Änderungen automatisch.

---

## Neuen Block-Typ ergänzen

Ein neuer eingebauter Block-Typ wird in `Content/BlockRegistry.cs` mit seinem
**Feld-Schema** definiert; das passende Render-Partial liegt unter
`Pages/Shared/Blocks/_<Name>.cshtml`. Der Editor baut das Eingabeformular
automatisch aus dem Schema (Textfelder, Auswahl, Bild-Picker, Listen …).

Alternativ liefern **Plugins** eigene Blöcke: `AddBlock(type, name, desc, render,
fieldsJson)` – das JSON-Feld-Schema erscheint dann genauso im Block-Editor wie bei
eingebauten Blöcken.

---

## Wichtige Sicherheitshinweise

- **Standard-Zugang `admin` / `admin` nach dem ersten Start ändern** – unter
  *Benutzer* ein neues Passwort setzen (und/oder einen neuen Admin anlegen und
  `admin` löschen).
- Für den öffentlichen Betrieb sollte die App **hinter einem Reverse-Proxy mit
  HTTPS** (nginx/Traefik/Caddy) laufen. Der Container spricht intern HTTP auf
  **8080**; der Host-Port **9101** kommt nur aus dem Compose-Mapping.
- **Login-Schutz:** `/login` ist pro Client-IP ratenbegrenzt. Hinter einem
  Reverse-Proxy `ForwardedHeaders` aktivieren, damit die echte Client-IP zählt.
- Rich-Text-/HTML-Inhalte werden nur von angemeldeten Admins erstellt und im
  Frontend bewusst als HTML ausgegeben. Wird später eine eingeschränkte
  Redakteurs-Rolle eingeführt, sollte dieser HTML-Inhalt vor der Ausgabe mit einem
  HTML-Sanitizer bereinigt werden.
- **Plugins führen serverseitig C#-Code aus** (Roslyn-Scripting) – nur vertrauens-
  würdige Plugins installieren.

---

## Projektstruktur

```
MatCMS/
├─ Program.cs                  # Startup, DI, Auth, Routing, Middleware (Wartung), Upload-Endpoint
├─ Content/                    # Block-System (Registry, Felder, Layout-Renderer, Templates)
├─ Data/                       # EF Core DbContext + DbSeeder (generischer Seed + gebündelte Plugins)
├─ Models/                     # Entities (Page, ContentBlock, Post, Form, Menu, Template, Plugin, User, …)
├─ Services/                   # Auth, SiteContext, Backup, Email, Translation, Version, PluginRuntime, …
├─ Resources/                  # i18n-Strings der Admin-UI (de.json / en.json)
├─ Pages/
│  ├─ View.cshtml              # Öffentlicher Seiten-Renderer (Route "/{slug?}")
│  ├─ Login / Logout / Error
│  ├─ Shared/                  # Layouts + Block-Render-Partials
│  └─ Admin/                   # Admin-Bereich (Seiten, Beiträge, Menüs, Medien, Formulare, Templates,
│                              #   Plugins, Backup, Einstellungen, Übersetzungs-Diff, …)
├─ wwwroot/                    # CSS, JS (Block-/Formular-Editor, Datepicker), Assets
├─ Dockerfile / docker-compose.yml
├─ VERSION                     # MAJOR.MINOR für die CI-Versionierung
└─ appdata/                    # Laufzeitdaten: SQLite-DB, Keys, Backups, Uploads (Volume, gitignored)
```

### Daten & Persistenz

- SQLite-DB (`appdata/matcms.db`), Data-Protection-Keys, geplante Backups und
  Uploads liegen alle im **einen** Docker-Volume `matcms-data` (`/app/appdata`) und
  bleiben über Neustarts erhalten.
- Das Schema wird beim Start automatisch angelegt (`EnsureCreated`). **Hinweis:**
  Bei Schema-Änderungen am Modell muss das Volume zurückgesetzt werden
  (`docker compose down -v`), da ohne EF-Migrationen gearbeitet wird.
- Der Seeder befüllt eine leere DB generisch und hält die **gebündelten Plugins**
  aktuell (Code/Version), ohne deren Admin-Konfiguration zu überschreiben.
- **Zurücksetzen:** `docker compose down -v` löscht Inhalte, Benutzer, Anfragen,
  Uploads und Backups und setzt alles auf den Auslieferungszustand zurück.
