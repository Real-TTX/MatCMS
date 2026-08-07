# MatCMS

Monorepo mit zwei Anwendungen, die denselben Stack teilen und im Gleichschritt entwickelt werden:

| Projekt | Was es ist | Port | Image |
| --- | --- | --- | --- |
| [`src/MatCMS`](src/MatCMS) | Das CMS: block-basierter Seiten-Editor, Templates, Plugins, Formulare, Mehrsprachigkeit | 9101 | `ghcr.io/real-ttx/matcms` |
| [`src/MatCMS.Cloud`](src/MatCMS.Cloud) | Die Control Plane: Update-Überwachung, Benachrichtigungen, Profil-Konfiguration für verbundene Instanzen | 9100 | `ghcr.io/real-ttx/matcms-cloud` |

Beide sind ASP.NET Core 10 mit Razor Pages, SQLite via EF Core und Docker-first. Die ausführliche
Dokumentation steht jeweils im README des Projekts.

## Warum ein Repo

Die beiden Anwendungen teilen sich einen **Vertrag** (`CloudProtocol` ↔ `InstanceProtocol` — dieselben
DTOs auf beiden Seiten), das **Plugin-Paketformat** und die komplette **Admin-Oberfläche**
(`site.css`, `admin.css`, `admin-list.js`, CodeMirror, geteilte Partials). Solange das zwei Repos
waren, ließ sich das nur durch Disziplin und `diff` zusammenhalten; jede Vertragsänderung musste an
zwei Stellen von Hand nachgezogen werden. In einem Repo ändert ein Commit beide Seiten.

> **Nächster Schritt:** Diese geteilten Teile in ein Projekt `src/MatCMS.Shared` (Razor Class
> Library) ziehen, statt sie zu kopieren. Bis dahin gilt: die kopierten Dateien sind byte-identisch
> zu halten — siehe `src/MatCMS.Cloud/CLAUDE.md`.

## Bauen & starten

Jedes Projekt bringt sein eigenes Compose mit und startet unabhängig:

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

## CI/CD

Vier Workflows in `.github/workflows/`, je zwei pro Anwendung:

| Workflow | Baut | Auslöser |
| --- | --- | --- |
| `release.yml` / `dev.yml` | `matcms` | Änderungen unter `src/MatCMS/**` |
| `cloud-release.yml` / `cloud-dev.yml` | `matcms-cloud` | Änderungen unter `src/MatCMS.Cloud/**` |

Die **`paths:`-Filter** sind der Grund, warum das Zusammenlegen nichts kostet: Eine Änderung am CMS
baut kein Cloud-Image und umgekehrt. Das Versionsschema bleibt je Anwendung unverändert
(`MAJOR.MINOR` aus der jeweiligen `VERSION`-Datei, `<build>` aus der Lauf-Nummer).
