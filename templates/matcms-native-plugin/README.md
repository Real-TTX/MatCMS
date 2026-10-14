# MatCmsPlugin

Ein natives MatCMS-Plugin (kompiliert, DLL). Es spricht dieselbe API wie ein Skript-Plugin
(`MatCMS.Plugin.Abstractions`); der Einstieg ist `PluginEntry.Configure`.

## Bauen und paketieren

```
dotnet build
pwsh <MatCMS>/plugins/pack-native.ps1 -Path .       # oder: powershell -File ...
```

`pack-native.ps1` baut das Projekt (`dotnet publish`), liest `meta.json` (Key, Name, Version, Beschreibung),
bestimmt die Plugin-API aus der DLL und schreibt `plugins/dist/<key>-<version>.zip`. Vor jedem neuen Upload die
**Version in `meta.json` erhöhen**.

Im Admin unter **Plugins** importieren. Das Plugin landet deaktiviert und läuft erst, wenn der Betreiber
**„Native Plugins erlauben“** eingeschaltet hat. Es gibt keinen Sandkasten.

## Regeln

- **Keine eigene Kopie** von `MatCMS.Plugin.Abstractions.dll` ins Bundle (die Referenz hat `ExcludeAssets=runtime`).
- **JSON nur quellgeneriert** (`Json.cs`) oder über `JsonNode` — Reflexions-JSON über eigene Typen hält das Plugin im
  Speicher fest.
- `Configure` registriert nur: kein Datenbank-, kein Netzzugriff.
- Statische Dateien (JS/CSS/Bilder) in einen Ordner `assets/` neben dem Projekt legen; sie kommen unter
  `/plugin-assets/<key>/…` und werden mit `plugin.Script(...)` / `plugin.Style(...)` eingebunden.
