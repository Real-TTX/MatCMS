# Eingebaute Plugins herausziehen (Entwurf, 2026-11-11)

Auftrag (Matthias, 09.11. und 11.11.): „Die Builtin Plugins rausziehen … als Native machen und auch im Store anbieten" — nach der
Übersetzung von Buchungen, Verein und Moments.

## Bestand

| Plugin | Umfang | Was es vom Kern braucht, das nicht in der Plugin-API steht | Aufwand |
|---|---|---|---|
| KI-Bot (`bot`) | 6 Dateien, ~800 Zeilen | nichts (nur `BuiltInPlugin`-Definition) | klein |
| Shop (`shop`) | 62 Dateien, ~13 300 Zeilen, 33 Testdateien | `SafeHtml.Sanitize` (Produkt-/Kategorietexte), `Localizer.DefaultCulture` | mittel (Tests umhängen) |
| Blog (`blog`) | 6 Dateien, ~1 200 Zeilen | **adoptiert die Kerntabelle `Posts`** (nur eingebaute Plugins dürfen `Adopt`), die Sicherung liest sie über `BlogStore.Table`, und über `ctx.Unsafe` `AiService`, `RazorPartialRenderer`, `PostImportService` | groß |

## Vorschlag

1. **Bot und Shop werden native Plugins** in `plugins/bot`, `plugins/shop` (Namensraum bleibt `MatCMS.Plugin.Shop`/`.Bot`), signiert im
   MatCMS Store angeboten. Die zwei Kern-Abhängigkeiten des Shops kommen in die Plugin-API (`IPluginSite.SanitizeHtml`,
   `IPluginSite.DefaultCulture`, als Default-Interface-Member wie `LogoUrl`, damit ein älterer Host den Shop noch lädt).
2. **Der Blog bleibt eingebaut.** Er gehört zum Kern eines CMS (MatCMS muss ohne Cloud und Store ein vollständiges CMS sein) und hängt an
   der Kerntabelle `Posts` und an drei Kerndiensten — ein Herausziehen hieße Datenumzug auf jeder Website für wenig Gewinn.
3. **Übergang ohne Ausfall** (dieselbe Key-Bezeichnung, also bleiben Einstellungen, Tabellen `p_shop_*`, Seiten, Menüelemente):
   - Release A: Der Host lässt ein **signiertes** natives Paket aus dem MatCMS Store mit dem Key eines eingebauten Plugins an dessen Stelle
     treten (heute abgelehnt). Die Zeile wird `Kind = native`, Ein/Aus bleibt wie es war.
   - Shop und Bot als native Pakete in den Store; die Cloud rollt sie auf die Websites aus, die sie eingeschaltet haben (topas-shop,
     Röstkunst-Demo, Sites mit Bot).
   - Release B: Der eingebaute Code fliegt aus dem Image. Eine Website, die noch eine `builtin`-Zeile ohne Code hat, zeigt unter Plugins
     „nicht mehr im Image – aus dem Store installieren" statt still zu fehlen.
4. **Updates** kommen danach über den Store (Version je Paket), nicht mehr mit dem Image.

## Zu entscheiden

1. **Freischaltung:** Native Plugins laufen nur mit „Native Plugins erlauben" (Standard aus). Bleibt das so — jede Shop-Website braucht den
   Schalter (Cloud-Profil kann ihn setzen) — oder dürfen **vom MatCMS Store signierte** Plugins ohne ihn laufen (neue Stufe
   „nur signierte"; Standard?)? Empfehlung: eigene Stufe „Signierte Plugins aus dem MatCMS Store", Standard **an** — der Schlüssel steckt im
   Image, also ist es Code von MATTHIX wie der eingebaute heute.
2. **Blog** eingebaut lassen (Empfehlung) oder doch herausziehen?
3. **Neue Websites ohne Store/Cloud:** Wer offline installiert, hätte keinen Shop mehr im Image — reicht der Import eines Pakets von Hand?
