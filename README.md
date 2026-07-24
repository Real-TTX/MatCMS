# FEUSYS Web – Block-basiertes CMS

Nachbau der Website **feusys.de** als eigenständige, editierbare Anwendung.
Das Layout ist optisch übernommen, die Inhalte sind über ein kleines CMS mit
**block-basiertem Editor** (im Stil von Shopify-Sections) bearbeitbar.

- **Framework:** ASP.NET Core 10 (Razor Pages), C#
- **Datenbank:** SQLite (via EF Core) – eine Datei, kein extra DB-Container
- **Auth:** Cookie-basiert, Login **nur** über `/login` (Standard: `admin` / `admin`)
- **Port:** `9101`
- **Docker:** ASP.NET-Core-Basis-Image, einfaches `docker-compose`, Basis-Images werden immer neu gezogen

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
docker compose logs -f      # Logs ansehen
docker compose down         # stoppen (Daten bleiben im Volume erhalten)
docker compose up -d --build # neu bauen & starten
```

### „Always Repull“ / immer aktuell

- In `docker-compose.yml` ist `build.pull: true` gesetzt – bei jedem `--build`
  werden die Basis-Images (`sdk:10.0`, `aspnet:10.0`) **neu gezogen**.
- Die NuGet-Pakete sind als `10.0.*` referenziert; ein frischer Build zieht damit
  automatisch die neuesten Patch-Versionen.

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

Läuft ebenfalls auf **http://localhost:9101** und aktualisiert sich bei
Code- und Ansichts-Änderungen automatisch.

---

## Das CMS bedienen

Nach dem Login unter `/login` stehen im Admin-Menü zur Verfügung:

| Bereich          | Zweck                                                                 |
|------------------|-----------------------------------------------------------------------|
| **Seiten**       | Seiten anlegen/löschen, **Blöcke** hinzufügen, bearbeiten, verschieben |
| **Anfragen**     | Einsendungen aus dem Kontaktformular                                   |
| **Benutzer**     | Benutzer anlegen, Passwörter zurücksetzen, löschen                    |
| **Einstellungen**| Logo, Seitenname, Header-Links, Footer-Text                           |

### Seiten & Blöcke

Jede Seite ist eine **geordnete Liste von Blöcken**. Über *Seiten → Bearbeiten*
lassen sich Blöcke hinzufügen (Auswahl aus der Block-Palette), per ▲/▼ verschieben,
bearbeiten oder löschen. Das Layout/CSS bleibt dabei fix – nur die Inhalte ändern sich.

Verfügbare Block-Typen:

- **Hero / Kopfbereich** – große Überschrift, optionales Hintergrundbild, Button
- **Textabschnitt** – Überschrift + formatierter Text (Rich-Text-Editor)
- **Spalten** – 2–3 Spalten mit Titel + Text
- **Leistungs-Raster** – Kachel-Raster (z. B. die 8 Leistungen der Startseite)
- **Call-to-Action** – hervorgehobene Aussage mit Button
- **Kontaktformular** – rendert das Formular; Einsendungen erscheinen unter *Anfragen*
- **Bild** – einzelnes Bild mit Upload-Möglichkeit

Ein neuer Block-Typ lässt sich hinzufügen, indem man ihn in
`Content/BlockRegistry.cs` definiert und ein passendes Partial unter
`Pages/Shared/Blocks/_<Name>.cshtml` anlegt – der Editor baut das Eingabeformular
automatisch aus dem Feld-Schema.

---

## Wichtige Sicherheitshinweise

- **Standard-Zugang `admin` / `admin` nach dem ersten Start ändern** – unter
  *Benutzer → Bearbeiten* ein neues Passwort setzen (und/oder einen neuen
  Admin-Benutzer anlegen und `admin` löschen).
- Für den öffentlichen Betrieb sollte die App **hinter einem Reverse-Proxy mit
  HTTPS** (z. B. nginx/Traefik/Caddy) laufen. Der Container selbst spricht HTTP auf 9101.
- **Login-Schutz:** `/login` ist pro Client-IP ratenbegrenzt (10 Versuche/Minute).
  Hinter einem Reverse-Proxy sollte `ForwardedHeaders` aktiviert werden, damit die
  echte Client-IP (statt der Proxy-IP) zählt.
- Rich-Text-/HTML-Inhalte werden nur von angemeldeten Admins erstellt und im
  Frontend bewusst unverändert (als HTML) ausgegeben – jeder Account hat aktuell
  die Rolle *Admin* und gilt damit als vertrauenswürdig. Wird später eine
  eingeschränkte Redakteurs-Rolle eingeführt, sollte dieser HTML-Inhalt vor der
  Ausgabe bereinigt werden (z. B. mit einem HTML-Sanitizer).

---

## Projektstruktur

```
MatCMS/
├─ Program.cs                  # Startup, DI, Auth, Routing, Upload-Endpoint
├─ Content/                    # Block-System (Definitionen, Felder, JSON-Reader)
├─ Data/                       # EF Core DbContext + Seed-Daten (feusys-Inhalte)
├─ Models/                     # Entities (Page, ContentBlock, User, …)
├─ Services/                   # AuthService, SiteContext, SettingKeys
├─ Pages/
│  ├─ View.cshtml              # Öffentlicher Seiten-Renderer  (Route "/{slug?}")
│  ├─ Login / Logout / Error
│  ├─ Shared/                  # Layouts + Block-Render-Partials
│  └─ Admin/                   # Admin-Bereich (Dashboard, Seiten, Blöcke, …)
├─ wwwroot/                    # CSS, JS (Block-Editor), Logo
├─ Dockerfile / docker-compose.yml
└─ appdata/                    # Laufzeitdaten: SQLite-DB + Keys (Docker-Volume, gitignored)
```

### Daten & Persistenz

- SQLite-DB und Data-Protection-Keys liegen unter `appdata/` (im Docker-Volume
  `feusys-data`). Uploads liegen im Volume `feusys-uploads`
  (`wwwroot/uploads`). Beide bleiben über Neustarts hinweg erhalten.
- Das Schema wird beim Start automatisch angelegt (`EnsureCreated`) und mit den
  feusys-Inhalten (Start, Über uns, Kontakt, Partner, Produkte, Impressum,
  Datenschutz, AGB) befüllt – nur, wenn die DB noch leer ist.
- **Zurücksetzen:** Volume löschen mit `docker compose down -v` (setzt Inhalte,
  Benutzer und Anfragen auf den Auslieferungszustand zurück).
