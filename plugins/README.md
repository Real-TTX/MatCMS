# MatCMS-Plugins

Die Quellen aller Plugins, die wir für MatCMS-Seiten pflegen. **MatCMS selbst bringt keine Plugins
mehr mit** — bis 2026-10 hat der Seeder drei angelegt und die beiden Bewertungs-Plugins sogar bei jedem
Start neu geschrieben, so dass eine Seite nie eigene Korrekturen behalten konnte. Auf bestehenden
Instanzen bleiben diese Zeilen unverändert stehen; neue Instanzen bekommen ein Plugin nur noch über den
Store der Cloud oder einen Bundle-Import.

| Ordner | Plugin | Stand |
| --- | --- | --- |
| `leserstimmen/` | Leserstimmen — Rezensionen mit Sternen/Flammen/Herzen | aktuell, im Global Store |
| `cookie-consent/` | Cookie-Banner — Einwilligung für Statistik/Marketing/externe Medien, blockiert bis zur Zustimmung | aktuell, im Global Store |
| `bewertungen/` | Bewertungen (früher eingebaut) | Altbestand, ersetzt durch Leserstimmen (dort „Aus Bewertungen übernehmen“) |
| `google-reviews/` | Google Bewertungen (früher eingebaut) | Altbestand; Vorgabe-Konfiguration steht in `meta.json` unter `DefaultConfig` (reist nicht im Bundle mit) |
| `todo-verwaltung-beispiel/` | Todo-Verwaltung (Beispiel, früher eingebaut) | Anschauungsbeispiel für die Plugin-API |

## Aufbau eines Plugins
- `plugin.csx` — der Code (C#-Skript, Globals = `PluginContext` in `src/MatCMS/Services/PluginRuntime.cs`).
- `meta.json` — `Key`, `Name`, `Version`, `Description`. Der Key ist die Identität: ein Import mit
  gleichem Key aktualisiert das Plugin an Ort und Stelle.
- `README.md` (wo nötig) — was es kann, Konfiguration, Fallen.

## Bauen und verteilen
- `./pack.ps1 -Key <key>` → `dist/<key>-<version>.zip` (`dist/` ist nicht im Git).
- Store: `POST /api/v1/store/plugins` mit dem ZIP als Body (API-Schlüssel mit CanManageStore).
- **Vor jedem Upload die Version in `meta.json` erhöhen**, sonst holen Instanzen das Update nicht.

## Regeln, die man sonst teuer lernt
- **Nie den Key eines Plugins wiederverwenden, das auf Instanzen schon anders existiert.**
- Plugin-Daten in `SiteSettings` werden von **jedem Restore ersetzt** — vor einem Live-Restore die
  Live-Daten in den Build übernehmen.
- Per **Store oder Bundle-Import** angekommen = **deaktiviert** (Code von außen, erst prüfen). Ein
  **Restore** stellt den Zustand aus dem Backup wieder her (an/aus) und startet die Plugins neu — ab
  MatCMS mit Commit „Restore stellt Plugins vollständig wieder her“; ältere Instanzen: neu = aus.
- MatCMS läuft mit `InvariantGlobalization`: deutsche Zahlen/Monate von Hand formatieren.
- Block-CSS über `AddHeadHtml` kommt im `<head>` nach dem Template-CSS — Templates überschreiben mit
  höherer Spezifität (`body .x{…}`).
