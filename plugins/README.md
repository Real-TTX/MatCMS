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
| `store-einblick/` | Store-Einblick — zeigt den Store einer MatCMS-Cloud (Plugins mit Art, Templates, Komponenten) als Block; liest `GET {cloud}/api/public/store`, ohne Schlüssel | aktuell, Skript-Variante (`plugin.csx` lädt `showcase.csx`) |
| `store-einblick-native/` | Store-Einblick (Native) — **dieselbe** Code-Datei `../store-einblick/showcase.csx`, als DLL kompiliert; `StoreEinblickTests` prüft, dass beide Varianten dasselbe HTML zeichnen | aktuell, Native-Variante (`./pack-native.ps1 -Path ./store-einblick-native`) |
| `coverage-map/` | Abdeckungskarte — Block mit anklickbarer Europakarte (Länder + Region Norditalien `IT-N`, Status verfügbar/teilweise/demnächst/geplant, Liste mit Hinweisen); Umrisse in `map.csx`, erzeugt von `tools/build-map.mjs` aus Natural Earth 1:50m (`tools/ne50.geojson`) | aktuell, im Global Store; benutzt auf corner-society.com |
| `forum/` | Forum (Native) — Kategorien und Foren mit Rechten, Themen/Beiträge mit BBCode, Zitate, Abos, Suche, Profile, Meldungen, Moderation; Mitglieder = Konten der Website (siehe `forum/README.md`) | neu, Paket mit `./pack-native.ps1 -Path ./forum` |
| `matmon-live/` | Matmon Live — für matmon.eu: Blöcke **Partner** (`/api/partners`, Logos über die eigene Seite durchgereicht: `/matmon-live/partner-logo/{id}`), **Dienststatus** (`/api/status` der Matmon Cloud, alle 5 min per Job) und **Sensorkatalog** (`/api/sensor-types`, alle 6 h; mitgelieferter Stand in `snapshot.csx` aus `tools/snapshot.mjs`, ein älterer Katalog verdrängt nie einen neueren) | aktuell, auf matmon.eu |
| `mx-easter-egg/` | MATTHIX Arcade von matthix.de: nur Dateien unter `assets/` (egg.js = Hülle, spiel-leben.js, spiel-kaltgang.js = Raycaster mit drei Leveln), ausgeliefert unter `/plugin-assets/mx-easter-egg/`; das Template „MATTHIX Klar“ lädt sie beim Klick auf „Spielen“ | aktuell, auf matthix.de |
| `todo-verwaltung-beispiel/` | Todo-Verwaltung (Beispiel, früher eingebaut) | Anschauungsbeispiel für die Plugin-API |

## Aufbau eines Plugins
- `plugin.csx` — der Code (weitere `*.csx` im Ordner packt `pack.ps1` als `files/<name>` mit, für `#load`; ein Ordner `assets/` wird als Asset-Dateien unter `/plugin-assets/<key>/` mitgepackt) (C#-Skript, Globals = `PluginContext` in `src/MatCMS/Services/PluginRuntime.cs`).
- `meta.json` — `Key`, `Name`, `Version`, `Description`. Der Key ist die Identität: ein Import mit
  gleichem Key aktualisiert das Plugin an Ort und Stelle.
- `README.md` (wo nötig) — was es kann, Konfiguration, Fallen.

## Native Plugins

Neben den Skript-Plugins (`plugin.csx`) gibt es **native** (kompilierte DLLs). Sie sprechen dieselbe API
(`MatCMS.Plugin.Abstractions`), erscheinen im Admin wie Skript-Plugins und lassen sich laden, entladen, exportieren
und über ein neues Paket aktualisieren — nur nicht bearbeiten. Das Referenz-Plugin liegt unter
`tests/Fixtures/RefPlugin` (dasselbe als Skript: `tests/Fixtures/ref-plugin.csx`); Aufbau, Regeln und Fallen stehen in
`docs/plugin-system-v2.md`.

Was man vor dem ersten eigenen wissen muss:
- Das Bundle hat `plugin.json` (`Kind: native`, `Api`, `Entry`, `Sha256`) und `src/` mit der DLL. Eine eigene Kopie von
  `MatCMS.Plugin.Abstractions.dll` gehört **nicht** hinein (Referenz mit `Private=false`, `ExcludeAssets=runtime`).
- JSON über eigene Typen **quellgeneriert** (`JsonSerializerContext`) oder über `JsonNode` — Reflexions-JSON hält das
  Plugin im Speicher fest; ebenso ein `DbContext`, der den Provider des Hosts teilt (`UseInternalServiceProvider`).
- Es läuft nur, wenn der Betreiber unter Plugins **„Native Plugins erlauben“** eingeschaltet hat — nie durch Import,
  Cloud oder Backup. Es gibt keinen Sandkasten.

Der Weg zu einem eigenen Native-Plugin (auch außerhalb dieses Repos):
1. **Paket der API bereitstellen.** `MatCMS.Plugin.Abstractions` ist als NuGet-Paket packbar, aber **nirgends
   veröffentlicht**: `./pack-abstractions.ps1` schreibt es in einen lokalen Feed (`artifacts/nuget`, nicht im Git; `-Out`
   für einen anderen Ordner). Diesen Ordner als Quelle in die `nuget.config` des Plugin-Projekts (oder des Ordners
   darüber) eintragen. Lizenz-/Repository-Angaben fehlen im Paket bewusst, bis sie entschieden sind.
2. **Vorlage installieren und Projekt anlegen:**
   `dotnet new install templates/matcms-native-plugin`, dann
   `dotnet new matcms-native-plugin -n DemoShop [--PluginKey demo-shop]` — Namespace und Dateinamen kommen aus `-n`, der Key
   (Identität, URL-Präfix) standardmäßig aus dem Namen in Kleinbuchstaben. Enthalten: `IPlugin` mit einer öffentlichen Route,
   einer Admin-Seite und quellgeneriertem JSON, dazu `meta.json` (Key, Name, Version, Beschreibung).
3. **Bauen und paketieren:** `./pack-native.ps1 -Path <Projektordner>` (baut mit `dotnet publish`) oder
   `-Path <Publish-Ordner>` → `dist/<key>-<version>.zip`. Es erkennt die Hauptdatei, liest die Plugin-API aus der DLL
   (Hauptversion der Abstractions, gegen die sie gebaut ist), setzt `plugin.json` (Format 2, Kind `native`, Prüfsumme) und legt
   DLLs, `.json`, `.txt`, `.so` unter `src/`, einen Ordner `assets/` daneben als Assets ab. Es bricht ab bei einer eigenen Kopie
   von `MatCMS.Plugin.Abstractions.dll`, fehlender Hauptdatei, nicht bestimmbarer Api-Version und Überschreitung der
   Zip-Grenzen (25 MB je Datei, 60 MB gesamt, 300 Dateien).
4. **Importieren:** im Admin unter Plugins das ZIP hochladen (oder per Store). Es landet deaktiviert; laufen tut es erst mit
   „Native Plugins erlauben“.

Vor jedem neuen Upload die Version in `meta.json` erhöhen — dasselbe wie bei Skript-Plugins.

## Bauen und verteilen
- `./pack.ps1 -Key <key>` → `dist/<key>-<version>.zip` (`dist/` ist nicht im Git). Für Native-Plugins `pack-native.ps1`
  (siehe oben).
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
