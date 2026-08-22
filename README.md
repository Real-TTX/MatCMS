<div align="center">

<img src="src/MatCMS/wwwroot/img/logo.svg" width="240" alt="MatCMS" />

# MatCMS

**Ein leichtgewichtiges, block-basiertes CMS – selbst gehostet, in einem Container.**

Seiten aus Blöcken bauen, Templates und Menüs pflegen, Formulare klicken, mehrsprachig
übersetzen – und alles per Backup sichern. Kein Cloud-Zwang, keine Fremd-Dienste, eine SQLite-Datei.

</div>

![Der Live-Editor: Blockliste links, echte Seitenvorschau rechts](docs/images/page-editor.png)

---

## Worum es geht

Die meisten CMS sind entweder ein Datenbank-Monster mit Plugin-Zoo oder ein Baukasten, der dir
nach dem ersten Kunden über den Kopf wächst. MatCMS liegt dazwischen: eine **ASP.NET-Core-Anwendung
in genau einem Docker-Container**, die eine Website aus **Blöcken** zusammensetzt – mit Live-Vorschau,
Templates, Plugins, Formularen und voller Mehrsprachigkeit. Eine Instanz = eine Website; mehrere
Websites laufen als mehrere Container nebeneinander und lassen sich zentral über die
[Cloud-Control-Plane](#cloud) überwachen und aktualisieren.

## Auf einen Blick

**Seiten & Inhalte**
- **Block-basierter Editor** mit Blockliste, **Drag & Drop** und **echter Live-Vorschau** der Seite
- **Kategorisierte Blockauswahl** mit Suche, Favoriten und „zuletzt verwendet" – Layout, Text,
  Medien, Design, Formular, Embed, Plugin- und Custom-Blöcke
- **Verschachtelte Blöcke** (Spalten, Sektionen, Karten-Grids mit Kind-Elementen)
- **Posts/Blog**, Menüs, Medien-Bibliothek und wiederverwendbare **Komponenten**

**Templates & Design**
- **Templates** je Seitentyp (Header/Footer/Layout-Teile), zentral gepflegt
- **Plugins** als in C# geschriebene Blöcke (Roslyn-Scripting, zur Laufzeit ausgeführt) –
  z. B. Google-Bewertungen mit im Block einstellbarer Überschrift
- Heller/dunkler Auftritt, sauberes Off-Canvas-Layout auf schmalen Screens

**Formulare**
- **Visueller Formular-Builder** mit Live-Vorschau: Text, E-Mail, Auswahl, Gruppen, Bedingungen
- **Eigene Controls**: Bild-Auswahl mit Titel/Tags/Beschreibung, **Datums- und Zeitraum-Picker**
  (zwei Monate, flexible „± Tage") – auf Mobil als Vollbild-Dialog
- Alle Button-/Feld-Texte pro Feld anpassbar und **je Sprache übersetzbar**
- Bestätigungstext, E-Mail-Benachrichtigung, Einsendungen im Admin

**Mehrsprachigkeit**
- Eigene Seiten-/Formular-Version **pro Sprache** (de/en/hr/sk … über `<html lang>` gesteuert)
- **Übersetzungs-Vergleich** (Diff) feldweise: was ist übersetzt, was fehlt, Klick-zum-Bearbeiten
- Datepicker-Monatsnamen, Wochentage und Standard-Texte automatisch je Sprache

**Betrieb**
- **Backup/Restore**: selektiver Export/Import (Seiten, Formulare, Medien, Einstellungen …)
- Benutzer & Rollen, Mail-Templates, SMTP
- **Ein Container, ein Volume** – DB, Data-Protection-Keys, Uploads und geplante Backups darin
- Zentrale **Update-Überwachung & Fern-Updates** über die [Cloud](#cloud)

## Screenshots

### Block-basiertes Bearbeiten

![Live-Editor mit Blockliste und Vorschau](docs/images/page-editor.png)

Jede Seite besteht aus Blöcken. Links die sortierbare Liste (per Drag & Drop), rechts die echte
Vorschau – Änderungen erscheinen sofort.

### Blockauswahl – kategorisiert, mit Suche

![Der Add-Block-Dialog mit Kategorien und Kacheln](docs/images/block-picker.png)

Suche, Kategorien (Layout, Text, Medien, Design, Formular, Embed, Plugins, Custom) sowie
**Favoriten** und **zuletzt verwendet**. Gleich große, klare Kacheln.

### Formular-Builder

![Formular-Editor mit Elementliste und Live-Vorschau](docs/images/form-builder.png)

Elemente zusammenklicken, rechts sofort sehen. Neben Standardfeldern gibt es eigene Controls wie die
Bild-Auswahl und den Zeitraum-Picker – und pro Feld übersetzbare Texte.

### Frontend

| Startseite | Kontaktformular |
|---|---|
| ![Öffentliche Startseite](docs/images/home.png) | ![Gerendertes Formular](docs/images/form.png) |

Aus denselben Blöcken entsteht die öffentliche Seite – schnell, ohne Aufbau-Ruckeln, in Hell/Dunkel.

### Verwaltung

| Templates | Plugins | Medien |
|---|---|---|
| ![Templates](docs/images/templates.png) | ![Plugins](docs/images/plugins.png) | ![Medien-Bibliothek](docs/images/media.png) |

Templates, Plugins, Medien, Komponenten, Menüs, Benutzer, Mail-Templates und Backup – alles in einer
Oberfläche, in Deutsch **und** Englisch.

### Auf dem Handy

| Startseite | Formular |
|---|---|
| ![Startseite auf dem Handy](docs/images/mobile-home.png) | ![Formular auf dem Handy](docs/images/mobile-form.png) |

## Schnellstart

Fertige Images liegen in der GitHub Container Registry:

| Tag | Gebaut aus | Wofür |
|---|---|---|
| `ghcr.io/real-ttx/matcms:latest` | `main` | Releases |
| `ghcr.io/real-ttx/matcms:nightly` | `dev` | die neuesten Features |

`docker-compose.yml`:

```yaml
services:
  matcms:
    image: ghcr.io/real-ttx/matcms:latest
    container_name: matcms
    restart: unless-stopped
    ports:
      - "9101:8080"
    volumes:
      - matcms-data:/app/appdata   # DB, Keys, Uploads, Backups – alles hier drin

volumes:
  matcms-data:
```

```bash
docker compose up -d
```

**http://localhost:9101** öffnen und mit **`admin` / `admin`** anmelden. Ein Update ist danach nur
`docker compose pull && docker compose up -d` – das Volume behält Inhalte, Einstellungen und Keys.

## Cloud

Mehrere MatCMS-Instanzen zentral im Blick: **MatCMS.Cloud** ist die Control Plane für selbst
gehostete Instanzen. Eine Instanz verbindet sich mit der Cloud, danach übernimmt diese:

- **Update-Überwachung** – **einmal zentral** die GHCR-Registry abfragen statt in jeder Instanz,
  „Update verfügbar" pro Instanz
- **Benachrichtigungen** – E-Mail bei *Instanz offline*, *neue Version* und *fehlgeschlagenem Update*
- **Updates ausführen** – für **lokale** Instanzen per Klick (Image ziehen, Container identisch neu
  erstellen, **Rollback** bei Fehler); für **remote** nur Hinweis + Befehl
- **Profile & Sync** – Einstellungen, Benutzer, Plugins und Komponenten an Profilen pflegen und auf
  zugeordnete Instanzen ausrollen

→ **Volle Doku & Screenshots: [src/MatCMS.Cloud/README.md](src/MatCMS.Cloud/README.md)**
(Image `ghcr.io/real-ttx/matcms-cloud`, Port `9100`).

## Monorepo

Zwei Anwendungen, ein Repo, gemeinsamer Stack – im Gleichschritt entwickelt:

| Projekt | Was es ist | Port | Image |
|---|---|---|---|
| [`src/MatCMS`](src/MatCMS) | Das CMS (dieses README) | 9101 | `ghcr.io/real-ttx/matcms` |
| [`src/MatCMS.Cloud`](src/MatCMS.Cloud) | Die Control Plane | 9100 | `ghcr.io/real-ttx/matcms-cloud` |

Beide teilen einen **Vertrag** (`CloudProtocol` ↔ `InstanceProtocol`), das **Plugin-Paketformat** und
die komplette **Admin-Oberfläche** (`site.css`, `admin.css`, geteilte Partials, CodeMirror). In einem
Repo ändert **ein Commit beide Seiten** – das war der Grund für die Zusammenlegung. Geteilte Teile
wandern nach `src/MatCMS.Shared` / `src/MatCMS.Shared.Web`.

### Bauen & starten

```bash
cd src/MatCMS       && docker compose up -d --build   # → http://localhost:9101
cd src/MatCMS.Cloud && docker compose up -d --build   # → http://localhost:9100
```

Lokal mit Hot Reload (.NET SDK 10):

```bash
cd src/MatCMS       && ./run-dev.ps1
cd src/MatCMS.Cloud && ./run-dev.ps1
```

Alle Projekte auf einmal: `dotnet build MatCMS.slnx`.

### Stack

ASP.NET Core 10 · Razor Pages · C# (`net10.0`) · SQLite via EF Core (mit Migrationen) ·
Docker-first · `InvariantGlobalization`. Kein separater DB-Container, keine Node-Build-Kette im
Betrieb.

### CI/CD

Vier Workflows in `.github/workflows/`, je zwei pro Anwendung; die **`paths:`-Filter** sorgen dafür,
dass eine CMS-Änderung kein Cloud-Image baut und umgekehrt:

| Workflow | Baut | Auslöser |
|---|---|---|
| `release.yml` / `dev.yml` | `matcms` | Änderungen unter `src/MatCMS/**` |
| `cloud-release.yml` / `cloud-dev.yml` | `matcms-cloud` | Änderungen unter `src/MatCMS.Cloud/**` |

Versionsschema je Anwendung: `MAJOR.MINOR` aus der jeweiligen `VERSION`-Datei, `<build>` aus der
Lauf-Nummer.
