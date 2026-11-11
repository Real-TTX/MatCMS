# Eingebaute Plugins herausziehen (Entwurf, 2026-11-11)

Auftrag (Matthias, 09.11. und 11.11.): „Die Builtin Plugins rausziehen … als Native machen und auch im Store anbieten" — nach der
Übersetzung von Buchungen, Verein und Moments (erledigt, 8724292).

## Entscheidungen (Matthias, 2026-11-11)

„Blog auch raus! Ja, dürfen laufen. und ja reicht." — im Interview dazu: Blog beim Umbau gleich mehrsprachig.

1. **Alle drei kommen raus:** KI-Bot, Shop **und Blog** werden native Plugins, im MatCMS Store angeboten.
2. **Vom MatCMS Store signierte native Plugins laufen ohne „Native Plugins erlauben".** Der Schalter bleibt für alles andere (eigene
   DLLs, Pakete Dritter ohne Signatur).
3. **Ohne Cloud und Store reicht der Import von Hand** — ein signiertes Paket bleibt signiert, auch wenn es von Hand importiert wird.
4. **Der Blog wird dabei mehrsprachig** (Beiträge je Sprache, `/en/blog/…`).

## Bestand (Stand 11.11., aus dem Code gezählt)

| Plugin | Umfang | Was es vom Kern braucht, das nicht in der Plugin-API steht |
|---|---|---|
| KI-Bot (`bot`) | 6 Dateien, ~820 Zeilen | `BuiltInPlugin`; Kern-Dateien `/js/ai-bot.js`, `/css/ai-bot.css` |
| Shop (`shop`) | 66 Dateien, ~14 100 Zeilen, 37 Testdateien | `IndexModel.Slugify` (20×), `SafeHtml.Sanitize` (8×), `SiteSearch`-Helfer (4×), `Localizer.DefaultCulture` (1×) |
| Blog (`blog`) | 6 Dateien, ~1 200 Zeilen, 6 Testdateien | **adoptiert die Kerntabelle `Posts`** (nur eingebaut darf `Adopt`); Sicherung liest sie über `BlogStore`; `ctx.Unsafe` → `AiService`, `RazorPartialRenderer` (`_AiSeoDialog`), `PostImportService`; `Slugify`, `SafeHtml`, `DefaultCulture`, `TagUtil`, `GermanDate`, `SiteSearch`; Kern-Teil `post` (article.html) des Templates; `/js/blog-editor.js` |

## Die Signatur, die ein natives Plugin freischaltet

Heute prüft eine Website die Store-Signatur nur beim Holen (Hash der ganzen ZIP-Datei) und merkt sich nichts — danach ist ein Paket aus dem
Store nicht von einer fremden DLL zu unterscheiden. Für „signiert läuft ohne Schalter" muss die Signatur **an den Dateien hängen, die
geladen werden**, und **bei jedem Laden** geprüft werden (wie heute die Prüfsumme der DLL):

- **Code-Signatur** über `matcms-store-code-v1\n{key}\n{version}\n{tree}`; `tree` = SHA-256 über die sortierten Zeilen
  `{Pfad unter src/}\n{SHA-256 der Datei}\n` ALLER Dateien in `src/` (nicht nur der Hauptdatei: der Lader löst Abhängigkeiten aus demselben
  Ordner auf, eine ausgetauschte Neben-DLL liefe sonst mit). ECDSA P-256 wie die Paket-Signatur, Schlüssel aus `StoreKeys.Known`.
- Sie reist **im Paket** als Feld `CodeSignature` (`keyid:signatur`) in `plugin.json` — ein String, die Cloud schreibt das Manifest
  feldweise und lässt unbekannte Felder stehen. Damit ist ein von Hand importiertes Paket genauso signiert wie eins aus dem Store (Entscheidung 3).
- `tools/StoreSign sign-code <bundle.zip>` schreibt das Feld in das Paket; danach wird wie bisher das Paket selbst signiert (`sign-package`).
  Der private Schlüssel bleibt auf Matthias' Rechner.
- Die Website speichert das Feld in der Plugin-Zeile (`Plugin.CodeSignature`, Migration) und in Sicherungen; **geprüft wird beim Laden**:
  Baum-Hash neu rechnen → Signatur gegen die bekannten Schlüssel. Gültig = läuft auch ohne `plugins.allowNative` und darf, was ein
  eingebautes durfte (Kerntabellen adoptieren, `Unsafe`). Ungültig oder fehlend = wie jedes andere native Plugin (Schalter nötig).
- Der Admin zeigt es: Abzeichen „Signiert · MatCMS Store" an der Zeile; „Native Plugins erlauben" erklärt, dass signierte Plugins davon
  ausgenommen sind.

## Übernahme eines eingebauten Keys

- Ein Paket mit dem Key `shop`, `bot` oder `blog` wird heute abgelehnt. Neu: angenommen, **wenn es gültig code-signiert ist**; die
  eingebaute Zeile wird zur nativen (`Kind = native`), **Ein/Aus und Einstellungen bleiben**, Tabellen `p_shop_*`, Seiten, Menüelemente,
  Plugin-Dateien gehören weiter demselben Key — für die Website ändert sich nichts.
- Ein unsigniertes Paket mit diesen Keys bleibt abgelehnt (niemand soll sich als „shop" ausgeben).

## Der Kern ohne die drei

- **Plugin-API ergänzt** (Default-Interface-Member wie `LogoUrl`, damit Plugins auf älteren Hosts weiter laden):
  `IPluginSite.SanitizeHtml(html)`, `IPluginSite.DefaultCulture`, `IPluginSite.Slugify(text)`; die `SiteSearch`-Helfer (Terms, Plain,
  Score, ExcerptOf) und `TagUtil`/`GermanDate` sind reine Funktionen und kommen als Kopie in die Plugins (oder als statische Helfer in die
  Abstractions). Was der Blog über `Unsafe` holt, wird API: `IPluginSite.AskAiAsync` gibt es schon; dazu eine Funktion „Teaser/Meta per KI"
  für den SEO-Dialog und „Beitrag aus Adresse importieren" (`PostImportService`).
- **Kern-Dateien** (`ai-bot.js/css`, `blog-editor.js`) wandern als Plugin-Assets in die Pakete (`/plugin-assets/{key}/…`).
- **Posts:** Die Tabelle bleibt eine Kerntabelle (Seeder legt sie an, Sicherung trägt sie IMMER — eigener Code im Kern statt `BlogStore`,
  damit eine Sicherung ohne Blog-Paket keine Beiträge verliert). Der Blog adoptiert sie wie heute, jetzt als signiertes Plugin.
  Mehrsprachig: `Locale` gibt es schon; Routen `/{culture}/blog/{slug}`, Übersicht je Sprache, Übersetzungen verknüpft (`TranslationGroup`).
- **Template-Teil `post`** (article.html) bleibt im Kern — er gehört dem Template, nicht dem Plugin.
- **Neue Websites:** ohne Blog, Shop, Bot. Store (oder Cloud-Profil) installiert sie; ein Cloud-Profil kann sie für neue Instanzen
  vorgeben.

## Übergang ohne Ausfall

1. **Release A (CMS 1.14):** Code-Signatur, Übernahme eingebauter Keys, API-Ergänzungen, Posts-Sicherung im Kern. Der eingebaute Code
   bleibt noch drin.
2. **Pakete:** `plugins/shop`, `plugins/bot`, `plugins/blog` (eigene csproj, nur Abstractions), Tests umgehängt (wie Forum/Buchungen: die
   gebaute DLL wird in die App geladen). Code-signiert von Matthias, in den Store; die Cloud rollt sie auf die Websites, die sie
   eingeschaltet haben.
3. **Release B (CMS 1.15):** eingebauter Code raus. Eine Website mit einer `builtin`-Zeile ohne Code zeigt unter Plugins
   „nicht mehr im Image — aus dem Store installieren oder ein Paket importieren" statt still zu fehlen; ihre Daten bleiben unberührt.

## Stand (11.11.)

- **Erledigt:** Code-Signatur + `NativeTrust` + Übernahme eingebauter Keys + Abzeichen; API 2.1 (`DefaultCulture`, `SanitizeHtml`, `PluginText.Slugify`).
- **Erledigt — Bot und Shop als Pakete (Release-B-Teil):** `plugins/bot` (2.0, Assets `ai-bot.js`/`ai-bot.css` unter `/plugin-assets/bot/`) und
  `plugins/shop` (2.0), beide nur gegen die Abstractions; `pack-native.ps1` baut `dist/bot-2.0.zip` und `dist/shop-2.0.zip` (unsigniert — `sign-code`
  macht Matthias). Im Kern sind sie raus: `BuiltInPlugins.All` = nur der Blog, `BuiltInPlugins.FormerKeys` = `shop`, `bot` (Keys bleiben reserviert,
  `IsBuiltInKey` true; eine übrig gebliebene `builtin`-Zeile läuft nicht, zeigt „Nicht mehr im Image – aus dem Store installieren oder ein Paket
  importieren“ und behält Daten und Einstellungen; ein Restore einer älteren Sicherung legt Ein/Aus und Einstellungen auf diese Zeile). Die Tests laden
  die gebauten DLLs signiert mit einem Testschlüssel (`StorePackages`, `TestStoreKey`), Übernahme der Zeile mit Ein/Aus, Einstellungen und Daten
  durch das signierte Paket ist getestet (`ShopSwitchAndBackupTests`).
- **Offen:** Pakete signieren und in den Store (Matthias), Blog (parallel), danach Release B ausliefern — vorher müssen die Pakete auf den Websites sein,
  die Shop oder Bot eingeschaltet haben.

## Reihenfolge der Arbeit

1. Host: Code-Signatur + Laden ohne Schalter + Übernahme + Abzeichen; StoreSign `sign-code`; Tests (signiert läuft ohne Schalter,
   ausgetauschte Neben-DLL läuft nicht, unsigniert mit Key `shop` abgelehnt, Übernahme behält Ein/Aus + Einstellungen + Daten).
2. API-Ergänzungen + Posts-Sicherung im Kern.
3. Bot herausziehen (klein, erprobt den Weg), dann Shop, dann Blog (+ mehrsprachig).
4. Release A, Pakete signieren (Matthias), Store, Rollout; danach Release B.

## Stand

- **Schritt 1 erledigt:** Code-Signatur, Laden ohne Schalter, Übernahme eingebauter Keys, Abzeichen „Signiert“, `StoreSign sign-code`.
- **Schritt 2 erledigt:** Plugin-API 2.1 (`DefaultCulture`, `SanitizeHtml`, `PluginText.Slugify`) und 2.2 (`IPluginSite.LocalePrefix`,
  `IPluginSite.ImportFromLinkAsync` + `LinkImport`, `PluginResult.WithAlternates` für den Sprachumschalter); Posts-Sicherung im Kern
  (`Services/PostsTable.cs`, `ContentTransferService` liest und schreibt `Posts` selbst, `CleanupService` bucht die Tabelle vor dem Korpus).
- **Blog herausgezogen (2026-11-11):** `plugins/blog` (Version 2.0, nur Abstractions), aus `BuiltInPlugins.All` entfernt; `/js/blog-editor.js`
  und eine Kopie von `ai-seo-field.js` sind Plugin-Assets; „KI: Teaser“ über `AskAiAsync` (Anweisung wörtlich wie im Kern), „Aus Link übernehmen“
  über `ImportFromLinkAsync` (`PostImportService` bleibt im Kern, die Einstellungen → Social Media brauchen ihn auch); `TagUtil`/`GermanDate`/
  `SiteSearch`-Helfer als Kopie in `BlogText`. **Mehrsprachig:** Routen ohne `DefaultLanguageOnly` (`/en/blog`, `/en/blog/{slug}`, 404 für
  eine Sprache, in der die Website nicht veröffentlicht), Übersicht/Sitemap/Link-Ziele/`site.search` je Sprache, Spalte `TranslationGroup`
  (Plugin UND Kern deklarieren sie; Sicherung trägt sie), Sprachumschalter → Übersetzung, Admin „Übersetzung anlegen“ + Sprachfilter,
  Cloud-Operation `post.create` mit `locale`/`translationOf` (ohne bleibt alles wie bisher in der Standardsprache). `SiteContext.CurrentLocale`
  liest auf Plugin-Seiten den Sprachpräfix der Adresse (vorher immer Standardsprache).
  `BuiltInPlugins.FormerKeys` = blog, shop, bot (`IsBuiltInKey` = eingebaut ODER früher eingebaut → nur signiert); eine liegengebliebene
  `builtin`-Zeile ohne Code zeigt „Nicht mehr im Image – aus dem Store installieren oder ein Paket importieren“ (de/en), lädt nichts, Daten
  bleiben; das signierte Paket übernimmt Zeile, Ein/Aus und Einstellungen. Ein Restore aus einer Sicherung mit eingebautem Blog legt genau
  diese wartende Zeile an. Tests: `BlogPluginTests`, `BlogEndToEndTests` (+ `BlogMultilingualTests`, `BlogDefaultRouteTests`) laden das
  mit dem Testschlüssel signierte Paket (`TestStoreKey`/`BlogFixture`).
- **Offen:** Blog-Paket signieren (Matthias, `tools/StoreSign sign-code`) und in den Store; Shop und Bot (parallel); Release A/B.
