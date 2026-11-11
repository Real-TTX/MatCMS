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
| `store-einblick/` | Store-Einblick — zeigt den Store einer MatCMS-Cloud als Block: Plugins mit Titelbild, weiteren Bildern, Art, Herausgeber und einer Detailansicht (Bilderleiste, Text, Fakten), Templates als kleine Seite in ihren eigenen Farben, Komponenten mit Symbol, dazu Reiter und Suche; liest `GET {cloud}/api/public/store`, ohne Schlüssel; die Bilder reicht die Website selbst durch (`/store-einblick/bild/{key}/{id}`) | 1.2, Skript-Variante (`plugin.csx` lädt `showcase.csx`) |
| `matcms-store/` | **MatCMS Store** — der Store-Server, nur für matcms.de: Katalog `/store`, Herausgeber registrieren sich und laden hoch, Prüfen und Freigeben mit Signatur im Admin (Schlüssel nur beim Betreiber, `tools/StoreSign`), API `/store/api` für Clouds und Websites (`docs/official-store.md`) | 0.1, nativ (`./pack-native.ps1 -Path ./matcms-store`) |
| `store-einblick-native/` | Store-Einblick (Native) — **dieselbe** Code-Datei `../store-einblick/showcase.csx`, als DLL kompiliert; `StoreEinblickTests` prüft, dass beide Varianten dasselbe HTML zeichnen | aktuell, Native-Variante (`./pack-native.ps1 -Path ./store-einblick-native`) |
| `coverage-map/` | Abdeckungskarte — Block mit anklickbarer Europakarte (Länder + Region Norditalien `IT-N`, Status verfügbar/teilweise/demnächst/geplant, Liste mit Hinweisen); Umrisse in `map.csx`, erzeugt von `tools/build-map.mjs` aus Natural Earth 1:50m (`tools/ne50.geojson`) | aktuell, im Global Store; benutzt auf corner-society.com |
| `forum/` | Forum (Native) — Kategorien und Foren mit Rechten, Themen/Beiträge mit BBCode, Zitate, Abos, Suche, Profile, Meldungen, Moderation; Mitglieder = Konten der Website (siehe `forum/README.md`) | neu, Paket mit `./pack-native.ps1 -Path ./forum` |
| `verein/` | Verein (Native) — Mitglieder mit Rollen, Mitgliederbereich unter `/mitglied`, Dokumente zum Bestätigen/Unterschreiben mit Nachweis, Sitzungen mit Einladung, Zusagen und Protokoll, Termine (Block „Vereinstermine“), Vorlagen, Bilder & Material (siehe `verein/README.md`) | neu, Paket mit `./pack-native.ps1 -Path ./verein`; erster Einsatz Schalksteinnarren |
| `booking/` | Buchungen (Native) — Ferienhäuser/Villen/Apartments: Objekte, Einheiten (auch „ganzes Haus“), Saisons mit Rang, Preise je Nacht mit Mindestnächten, Anreisetag, Wochenpreis und Personenaufschlag, Gebühren und Kurtaxe, Sperren ohne Doppelbuchung, Preis-Vorschau `/buchung/preis`, Blöcke Anfrage/Belegungskalender/Unterkünfte, Anfrage → Angebot → Bestätigung mit Gastseite und Mails, Belegungsplan, Zahlungen, Bestätigungs-PDF, Gästekartei, iCal-Abgleich mit Airbnb/Booking.com/Rentlio, Online-Check-in, Gästemails, Reinigung, Kundenverbund mehrerer Seiten über die Plugin-API (siehe `booking/README.md`, Entwurf `docs/booking-plugin.md`) | Phase B6 (0.6), Paket mit `./pack-native.ps1 -Path ./booking`; erste Seiten Villa Nika/Sogi |
| `moments/` | Moments (Native) — Gästebereich privater Veranstaltungen: Zugang je Haushalt per Code/QR (ohne E-Mail), Galerie, Hochladen vom Handy mit Freigabe, Personen/Stichworte, Herzen, Kommentare, Moderation, Zugangskarten (siehe `moments/README.md`) | neu, Paket mit `./pack-native.ps1 -Path ./moments`; erster Einsatz Grit & Steffen |
| `matmon-live/` | Matmon Live — für matmon.eu: Blöcke **Partner** (`/api/partners`, Logos über die eigene Seite durchgereicht: `/matmon-live/partner-logo/{id}`), **Dienststatus** (`/api/status` der Matmon Cloud, alle 5 min per Job) und **Sensorkatalog** (`/api/sensor-types`, alle 6 h; mitgelieferter Stand in `snapshot.csx` aus `tools/snapshot.mjs`, ein älterer Katalog verdrängt nie einen neueren) und **Templates** (`templates.csx`: die veröffentlichten Sets aus `/api/store/templates` der Cloud, alle 10 min, Details aufklappbar, Download über die eigene Seite `/matmon-live/template/{slug}`; Einreichen und Prüfen nur in der Cloud — Konto → Templates, Admin → Template store) | aktuell, auf matmon.eu |
| `mx-easter-egg/` | MATTHIX Arcade von matthix.de: nur Dateien unter `assets/` (egg.js = Hülle, spiel-leben.js, spiel-kaltgang.js = Raycaster mit drei Leveln), ausgeliefert unter `/plugin-assets/mx-easter-egg/`; das Template „MATTHIX Klar“ lädt sie beim Klick auf „Spielen“ | aktuell, auf matthix.de |
| `shop/` | **Shop** (Native, Key `shop`) — bis CMS 1.14 eingebautes Plugin, seit Release B ein Paket des MatCMS Store: Produkte, Varianten, Eigenschaften, Marken, Warenkorb, Kasse mit Stripe/PayPal/Vorkasse, Bestellungen, Rechnungen, Downloads, Gutscheine, Kundenkonto, ERP-API (Entwurf und Regeln: `docs/shop-plugin.md`). Tabellen `p_shop_*`, Einstellungen, Seiten und Menüelemente sind dieselben wie eingebaut — das signierte Paket übernimmt die Zeile einer Website mit Ein/Aus und Einstellungen | 2.0, Paket mit `./pack-native.ps1 -Path ./shop`, danach `tools/StoreSign sign-code` (Matthias) |
| `bot/` | **KI-Bot** (Native, Key `bot`) — bis CMS 1.14 eingebaut, jetzt ein Paket des MatCMS Store: Chat-Assistent für Besucher; das Widget (`assets/ai-bot.js`, `assets/ai-bot.css`) liegt unter `/plugin-assets/bot/`. Tabellen `p_bot_*` und Einstellungen wie eingebaut | 2.0, Paket mit `./pack-native.ps1 -Path ./bot`, danach `tools/StoreSign sign-code` (Matthias) |
| `blog/` | **Blog (Native, signiert)** — bis 2026-11 eingebaut, jetzt Paket des MatCMS Store: Beiträge mit Titelbild, Galerie, Tags, Terminierung, Block „Beiträge“, Übersicht `/blog`, **mehrsprachig** (`/en/blog/…`, Übersetzungen verknüpft über `TranslationGroup`, Sprachumschalter führt zur Übersetzung, Admin: „Übersetzung anlegen“, Liste nach Sprache filtern). Übernimmt die **Kerntabelle `Posts`** (adoptiert — darf nur ein vom Store code-signiertes Paket; die Sicherung trägt `Posts` auch ohne das Plugin). Übernimmt beim Import die alte `builtin`-Zeile mit Ein/Aus und Einstellungen. | 2.0, Paket mit `./pack-native.ps1 -Path ./blog`, danach **signieren** (`tools/StoreSign sign-code`); unsigniert wird der Key `blog` abgelehnt |
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
  Cloud oder Backup. Es gibt keinen Sandkasten. **Ausnahme:** vom MatCMS Store code-signierte Pakete
  (`tools/StoreSign sign-code`, geprüft bei jedem Laden) laufen ohne den Schalter — so kommen Shop und KI-Bot auf die Websites
  (`docs/builtin-extraction.md`). Ihre Keys `shop`/`bot` nimmt nur ein signiertes Paket an.

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

## Lizenz

Die Plugins in diesem Ordner stehen unter der [MatCMS License](../LICENSE.md) wie der Rest des Repos.
Eigene Plugins, die nur gegen `MatCMS.Plugin.Abstractions` geschrieben sind, gehören ihren Autoren und dürfen beliebig lizenziert werden.
