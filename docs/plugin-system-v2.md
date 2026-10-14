# Entwurf: Plugin-System v2 — Script und Native, eine API

Entwurf, nichts gebaut. Stand von `main` am 14.10.2026 (ohne die noch nicht eingecheckte Arbeit im
Arbeitsverzeichnis). Anlass: ein Shop soll ins CMS, und der Blog ist im Grunde auch ein Plugin.

**Vorgabe (Matthias, 14.10.2026), ersetzt den ersten Entwurf vom selben Tag:**

1. Es gibt **zwei Bauarten** von Plugin: **Script** (C#-Skript, `.csx`, wie bisher) und **Native**
   (kompiliert, DLL). Weitere Script-Plugins bleiben jederzeit möglich.
2. Im **UI verhalten sich beide gleich**. Einziger Unterschied: ein Native-Plugin ist nicht editierbar,
   weil kompiliert.
3. Beide lassen sich **laden und entladen** und über **Export/Import** hochladen, herunterladen und
   updaten — auch über die Cloud.
4. Der **Inhalt ist gleich aufgebaut**: ein Ordner `src/`, in dem Quellcode (Script) oder DLL (Native)
   liegt.
5. Die **Plugin-API ist für beide dieselbe**, und sie muss **perfekt** sein.

Der Shop wird ein Native-Plugin, der Blog das erste, das auf die neue API umzieht.

## Stand der Umsetzung (14.10.2026)

Gebaut und **gegen die echte App und das echte Linux-Image geprüft**:

- **Plugin-API v2** (`MatCMS.Plugin.Abstractions`, ohne ASP.NET/EF): `IPlugin`, `IPluginBuilder`
  (`Route`, `Admin.Section(...).Page(...)`, `Block`, `Head`/`Body`/`Script`/`Style`), `IPluginContext`,
  `PluginResult`. Script (`Plugin.…` als Global) und Native (`IPlugin.Configure`) sehen dieselben Typen.
- **Registry in Scheiben:** jedes Plugin ein eigener, atomar ersetzbarer Abschnitt. **Ein** Plugin laden,
  neu laden, entladen — alle anderen bleiben unberührt. Ein Skript, das beim Laden wirft, registriert
  **nichts** (früher blieb der Menüpunkt stehen, den es vor dem Fehler erreicht hatte); ein fehlgeschlagenes
  Neuladen lässt die laufende Version weiterlaufen.
- **Routenweiche:** ein einziger dynamischer Endpunkt vor dem CMS-`/{slug?}`, die Routentabelle ist die
  Registry — deshalb ist Entladen sofort wirksam. Öffentlich im Seitenlayout (Titel, Meta, Sprachpräfix),
  Admin unter `/admin/p/{key}/…` mit Tab-Leiste; Menüpunkt in der gewählten Gruppe.
- **Native-Lader:** entladbarer `AssemblyLoadContext` je Plugin, Prüfsumme bei jedem Laden, Update
  nebeneinander mit atomarem Umschalten, Entladen mit Prüfung per `WeakReference`.
- **Bundle:** Export/Import/Rundlauf für Native (`src/`), Zip-Schutz, Pfad-/Typprüfung, Aufräumen alter
  Versionen. **Script-Bundles bleiben unverändert** (siehe 2.1). **Backup/Restore** trägt Native-Plugins samt Dateien;
  die Erlaubnis `plugins.allowNative` kommt nie aus einem Backup (siehe 9).
- **Sicherheitsriegel `plugins.allowNative`** (Standard aus, live umschaltbar), UI für beide Bauarten
  (Liste, Art-Badge, eigene Native-Seite, Export, Löschen räumt die Dateien auf).
- **Alt-Plugins laufen unverändert** (`leserstimmen`, `cookie-consent`, `google-reviews`, `bewertungen`,
  `todo-verwaltung-beispiel` — alle fünf geladen). `leserstimmen` greift in die Registry und ersetzt
  Menüeinträge nach Index; dafür bleibt `AdminMenu` schreibbar (`AdminMenuView`).
- **Tests** (`tests/MatCMS.Plugins.Tests`, 65): Konformität Script ↔ Native (dasselbe Referenz-Plugin, zwei
  Bauarten, gleiche Registrierungen und gleiche Antworten), Entladen mit echter DLL samt EF-Kontext und JSON,
  Bundle-Rundlauf, Ablehnungsfälle, Routen-Vorrang. Der Entlade-Test wurde per Mutation geprüft: schaltet man
  den EF-Griff ab, schlägt er fehl.
- **CI/Docker:** `MatCMS.Plugin.Abstractions` in beiden COPY-Zeilen des CMS-Dockerfiles und in den
  `paths:`-Filtern von `dev.yml`/`release.yml`; das Image baut.

**Seit dem ersten Stand dazugekommen (gleicher Tag, auch gegen die laufende App geprüft):**

- **UI-Bibliothek (1b):** ~40 Komponenten als Records in `MatCMS.Plugin` (`Ui.*`), gezeichnet von `UiRenderer` in `MatCMS.Shared.Web`
  (admin und öffentlich), mit Rich-Text, Bild, Bild-/Dateiliste, Tags, Tabelle mit Suche, Dialog, Formular samt Antiforgery;
  Seitenleiste als Registry (`AdminNav`, Kern- und Plugin-Einträge gemeinsam geordnet, Zähler); Verhalten in `ui-fields.js`.
- **Daten, Einstellungen, Ereignisse, Jobs, Lifecycle, Texte (2):** `Data.Table` (Schicht 1, Abgleich ohne Löschen, Backup/Restore/Aufräumen
  eingebunden), `Settings` (Formular, Prüfung, verschlüsselte Geheimnisse), `On`/`ctx.Events`, `Job`, `Lifecycle`, `Strings`/`ctx.T` samt
  `strings/{culture}.json`, `Sitemap`, `MediaReferences`, `Operation`, Dashboard-Kachel/Zuletzt/Aufmerksamkeit.
- **Der Blog ist ein eingebautes Plugin (3):** `Kind = builtin`, standardmäßig an, adoptiert die Tabelle `Posts` (kein Datenumzug,
  No-op-Migration `BlogLeavesTheModel`), ersetzt Post-Seite, Admin-Seiten, Block `_Posts`, Dashboard-Anteile, Cloud-Aktion `post.create`.
  Der Vergleich gegen den alten Stand (gleiche Datenbank, 20 öffentliche Seiten inkl. Blöcke, Seitenumbruch, Tag-Filter, Entwurf/geplant,
  404) ist **Byte-für-Byte gleich** bis auf Port und die gewollten Änderungen (siehe §9).
- **Script-Editor:** neue Blaupausen für die Plugin-API (Route, Admin-Seiten, Einstellungen, Tabelle, Aufgabe, Ereignis, Texte), Starter-Code
  der neuen Plugins in der neuen API, Überblick-Karte „Was dieses Plugin beiträgt“ auf allen drei Plugin-Seiten, Hinweis auf das
  Einstellungsformular, Sprachdateien `strings/*.json`. (Dateibaum, Unterdateien, Assets und Ordnerrollen gab es schon.)
- **Script-Editor, im Browser geprüft (Chrome per DevTools-Protokoll):** CodeMirror mit C#-Hervorhebung vor der Textarea (deren `.value` es übernimmt, die Seite
  bleibt unverändert), **Autovervollständigung der Plugin-API** aus dem Katalog (`PluginApiCatalog`: per Reflexion und der XML-Dokumentation der
  Abstractions, folgt Ketten wie `Plugin.Admin.Section(…).`), **„Prüfen“** (`PluginRunner.Check`: übersetzt wie der Lader, führt nie aus, Zeilen wie im Editor,
  Klick springt in die Datei) mit Markierung im Text; auf dem Handy bleibt die Textarea.
- **Cloud (5, Teil):** der Store nimmt Native-Bundles an, zeigt sie **schreibgeschützt** (Hauptdatei, Api, Prüfsumme, Dateien) statt eines Code-Editors,
  das Neu-Packen lässt `Code` (den Hinweis für ältere Instanzen) und `src/` unberührt; Rundlauf Store → Download → Import auf einer Instanz geprüft.
  Importgrenze 90 MB (Native darf 60 MB Assemblies bringen). Nicht gebaut: Cloud rendert Plugin-Einstellungen (`UiRenderer` liegt in `Shared.Web`, die Cloud
  referenziert die Abstractions seit dieser Phase — nutzt sie noch nicht).
- **Native bauen:** `plugins/pack-native.ps1`, `templates/matcms-native-plugin`, `plugins/pack-abstractions.ps1` (NuGet lokal).
- **Namespaces:** `MatCMS.Plugin` = API, `MatCMS.Plugin.{Name}` = Code eines Plugins (Blog: `MatCMS.Plugin.Blog`).

**Offen:** Schicht 2 für Native (EF-Optionen vom Host) und der Shop (4), Cloud: Einstellungen/Admin-Seiten von Plugins rendern, Cloud-API/MCP für den
Plugin-Zustand (5), eine echte Linux-Probe des Blogs im Image, `ctx.Json`, Template-Teile, die ein Plugin selbst definiert (Details in 8 und 9).

## 1. Was heute nicht reicht

Am Code gelesen (`src/MatCMS/Services/PluginRuntime.cs`, `Models/Plugin.cs`, `Program.cs`):

| Lücke | Heute | Folge für einen Shop |
| --- | --- | --- |
| **Eigene Daten** | JSON-Blobs in `SiteSettings` (hart auf 1000 Einträge begrenzt) oder roher `AppDbContext`. Keine eigene Tabelle. | Produkte, Bestand, Bestellungen brauchen Tabellen, Indizes, Migrationen. Jeder Restore ersetzt `SiteSettings`. |
| **Öffentliche Routen** | Nur `/plugin/{key}`, Handler liefert einen HTML-String. | Keine URLs wie `/shop/{slug}`, `/kasse`; kein Template, kein `<title>`/OG-Meta, keine Locale. |
| **Admin-UI** | Eine Seite je Plugin aus HTML-Strings (`AdminUi`), hart deutsch. | Liste, Bearbeiten, Bestellungen, Einstellungen, Tabs; i18n. |
| **Lebenszyklus** | `RunAllAsync` leert die Registry und kompiliert **alle** Plugins neu — bei jedem Speichern. Kein Install/Update/Entfernen. | Tabellen anlegen/aktualisieren/aufräumen; ein Plugin ändern darf kein anderes anfassen. |
| **Hooks, Jobs** | Keine (`PluginBlueprints`: ausdrücklich keine geplante Aufgabe). | „Bestellung bezahlt“ → Mail/Bestand; Warenkörbe aufräumen. |
| **Einstellungen** | `Config("key")`, frei, ohne Formular, ohne Typen. | Steuersätze, Versandzonen, Zahlungsanbieter als Formular, ausrollbar. |
| **Rest des Systems** | Backup, Cleanup, Sitemap, Cloud-Inhaltskanal kennen Plugin-Daten nicht. | Ein Shop-Bild gälte im Cleanup als „unbenutzt“. |
| **Typen** | Es gibt nur Skripte. | Ein Shop mit Zahlung und Steuer will IDE, Debugger, Tests. |

**Warum der Blog kein Plugin ist:** Er hängt an Punkten, die ein Plugin nicht hat — `Models/Post.cs`,
`AppDbContext.Posts`, `Pages/Admin/Posts/*`, `Pages/Post.cshtml` (`/blog/{slug}`, liest das
Template-Teil `article.html`), Block `_Posts`, `PublicContentService`, `PostImportService`, und
`_db.Posts` in `BlockRegistry`, `DbSeeder`, `Admin/Index`, `CleanupService`, `ContentOpApplier`,
`ContentTransferService`.

## 2. Das Modell: ein Plugin, zwei Bauarten

```
plugin.json     Kind: "script" | "native", Api, Key, Name, Version, Description, Requires, Mapping
src/            Script: plugin.csx + weitere .csx   |   Native: <Entry>.dll + Abhängigkeits-DLLs
assets/         unverändert (JS/CSS/Bilder, ausgeliefert unter /plugin-assets/{Key}/…)
```

| | Script | Native |
| --- | --- | --- |
| `src/` enthält | Quellcode | kompilierte DLLs |
| Im Editor | bearbeitbar | **schreibgeschützt** (Dateibaum, Hash, Version, Konfiguration, Abhängigkeiten) |
| Speicherort auf der Instanz | Datenbank (wie heute: `Code` + `FilesJson`) | Platte: `appdata/plugins/{key}/{version}/src/` |
| Geladen von | Roslyn, ein Plugin allein | eigener, entladbarer `AssemblyLoadContext` je Plugin |
| Export/Import/Cloud/Store | dasselbe ZIP | dasselbe ZIP |
| Laden/Entladen, Aktivieren | gleiche Knöpfe, gleiche Zustände | gleiche Knöpfe, gleiche Zustände |
| **API** | **dieselbe** | **dieselbe** |

Ein vierter Lader, `BuiltIn`, lädt ein im Image mitkompiliertes Plugin (der Blog). Es nutzt dieselbe API
und denselben Zustand, lässt sich deaktivieren, aber weder entfernen noch per Bundle austauschen — ein
Update kommt mit dem Image. So bleibt MatCMS ohne Cloud, Store und Import voll funktionsfähig
(`CLAUDE.md`: „standalone = WordPress-Ersatz“). Die Regel „MatCMS ships no plugins“ richtete sich gegen
den Seeder, der Skript-Plugins bei jedem Start überschrieb; ein BuiltIn-Plugin hat einen Zustand und
wird nie überschrieben.

### 2.1 Bundle-Format

**Zwei Formen, bewusst nicht dieselbe.** Das war im ersten Entwurf anders und ist am Code korrigiert worden:
ältere Leser (`PluginPackager.ImportAsync`, der Store-Editor der Cloud) **ignorieren `Format` völlig**. Ein
neues Format für Skripte hätte ein älteres CMS also als **leeres** Plugin importiert, ohne ein Wort. Deshalb:

- **Script-Bundle: unverändert.** Format 1, Einstiegsdatei im Manifestfeld `Code`, weitere Dateien unter
  `files/`. Es wird byte-genau so geschrieben wie vorher (ein Test sichert das), und Instanzen und Cloud aus der
  Zeit davor lesen es weiter. `src/` ist für Skripte der Name der Wurzel im Editor, nicht ihr Ablageort im ZIP.
- **Native-Bundle: neu, nur additiv.** `Format: 2`, `Kind: "native"`, `Api`, `Entry`, `Sha256`; die
  Assemblies liegen als **je ein Eintrag unter `src/`**. Das Manifestfeld `Code` trägt eine Zeile, die wirft:
  `dies ist ein natives Plugin, bitte MatCMS aktualisieren` — das Einzige, was ein **älteres** CMS damit tun kann,
  und es sagt wenigstens, warum. Die Cloud kopiert alle Einträge außer dem Manifest byteweise; ein Native-Bundle
  übersteht eine Cloud-Runde unverändert.

```
plugin.json   Native: Format 2, Kind "native", Api "2", Entry "Shop.dll", Sha256, Code = Hinweiszeile
src/          Native: Shop.dll + Abhängigkeiten (.dll, .json, .txt, .so), auch in Unterordnern
assets/       unverändert (JS/CSS/Bilder, /plugin-assets/{Key}/…)
```

- Neue Manifestfelder sind **Zeichenketten** (wie `Mapping`); gelesen werden sie nachsichtig, auch als Zahl —
  die Cloud schreibt `plugin.json` Feld für Feld um und macht aus `"2"` leicht `2`.
- Zip-Schutz in `MatCMS.Shared/PluginBundle.cs`: je Datei 25 MB, insgesamt 60 MB, 300 Dateien, erlaubte Endungen
  `.dll .json .txt .so`, Pfade ohne `..`/versteckte Namen. **Gezählt wird beim Lesen**, nicht nach dem Längenfeld
  im ZIP — das ist nur die Behauptung des Autors.
- Eine eigene Kopie von `MatCMS.Plugin.Abstractions.dll` in `src/` wird abgelehnt: der Host lädt sie selbst, eine
  zweite ließe das Plugin ein `IPlugin` implementieren, das der Host nicht wiedererkennt.
- Die Prüfsumme der Hauptdatei wird beim Import verglichen **und bei jedem Laden** (eine geänderte Datei auf dem
  Volume ist ein geändertes Programm).
- Ein Native-Update legt `appdata/plugins/{key}/{version}/` **neben** die alte und wird dann umgeschaltet; nie
  in-place. Behalten werden die aktuelle und die vorherige Version.
- Ein Skript-Bundle über ein Native-Plugin gleichen Keys (und umgekehrt) ist erlaubt; die kompilierten Dateien
  werden dann entfernt.

### 2.2 Lebenszyklus und Zustand

Zustände je Plugin, in beiden Bauarten gleich: **Installiert (aus)** → **Aktiv** ⇄ **Inaktiv**,
**Fehler** (mit Meldung), **Neustart nötig**. Die bestehende Regel bleibt: per Store oder Import
angekommen = **deaktiviert**, bis jemand es freigibt.

- **Laden/Entladen = Aktivieren/Deaktivieren, sofort wirksam.** Das Plugin wird einzeln geladen; kein
  anderes wird angefasst (heute: alle neu kompiliert).
- **Deinstallieren** ist eine eigene Aktion mit Vorschau („N Produkte, M Bestellungen werden gelöscht“)
  und doppelter Bestätigung — nie ein Nebeneffekt von „aus“. Plugin-Daten bleiben beim Deaktivieren.
- Native-Plugins sind zusätzlich durch einen **Instanz-Schalter** `plugins.allowNative` (Standard: aus)
  gesperrt. Der Schalter ist **nicht** von außen setzbar — weder per Cloud-Rollout noch per MCP noch aus einem
  Backup (`SettingKeys.Protected`); siehe 6.

## 3. Die Plugin-API

Die API wird **einmal** definiert, in `src/MatCMS.Plugin.Abstractions` (eigenes Projekt). Es ist
bewusst **frei von ASP.NET und EF** — reine Typen: Builder-Schnittstellen, Anfrage/Ergebnis, das
UI-Komponentenmodell (3.5). Dadurch leckt kein ASP.NET-Typ in die API (Stabilität), und die Cloud kann
es referenzieren, um Plugin-Einstellungen mit denselben Komponenten zu rendern. `MatCMS.Shared` bleibt
der Wire-Vertrag. Beide Bauarten sehen **dieselben Typen mit denselben Namen**:

```csharp
// Native
public sealed class ShopPlugin : IPlugin
{
    public void Configure(IPluginBuilder b)
    {
        b.Route("/shop/{slug}", async (ctx, ct) => PluginResult.Page(ctx, "Produkt", await RenderProduct(ctx)));
        b.Admin.Section("Shop", "ti-shopping-cart").Page("Produkte", "/products", ListProducts);
        b.Settings.Text("currency", "Währung", def: "EUR");
        b.On<FormSubmitted>(OnForm);
    }
}
```

```csharp
// Script — globale Variable `Plugin` ist derselbe IPluginBuilder
Plugin.Route("/shop/{slug}", async (ctx, ct) => PluginResult.Page(ctx, "Produkt", await RenderProduct(ctx)));
Plugin.Admin.Section("Shop", "ti-shopping-cart").Page("Produkte", "/products", ListProducts);
Plugin.Settings.Text("currency", "Währung", def: "EUR");
Plugin.On<FormSubmitted>(OnForm);
```

Das Skript ist **kein** zweites API, sondern Roslyn vor demselben Builder. Das alte
`PluginContext` (`AddBlock`, `AddAdminPage`, `Config`, …) bleibt als **Kompatibilitätsschicht** auf dem
neuen Builder erhalten, damit `leserstimmen`, `cookie-consent` & Co. unverändert laufen.

### 3.1 Grundsätze — woran „perfekt“ gemessen wird

1. **Eine Definition, zwei Türen.** Kein Feature, das nur eine Bauart hat (Ausnahmen: siehe 3.4, und sie
   sind benannt). **Prüfstein:** ein Referenz-Plugin wird einmal als Script und einmal als Native
   geschrieben; dieselben Tests laufen gegen beide und müssen dasselbe Verhalten zeigen. Ohne grünen
   Konformitätstest gilt eine API-Änderung als nicht fertig.
2. **Deklarativ zuerst.** `Configure` registriert nur — keine Nebenwirkung, kein Datenbankzugriff. Der
   Host kann dadurch auflisten, was ein Plugin beiträgt, es sauber entladen und vor dem Aktivieren
   anzeigen, was es tun will.
3. **Der Host besitzt Routen, Registry und Layout.** Ein Plugin berührt nie `IEndpointRouteBuilder`
   oder `HttpContext`. Es meldet Routenmuster an; der Host hat **eine** Weiche vor dem Razor-Routing und
   eine Routentabelle je Plugin. Nur so ist Entladen sofort wirksam — ASP.NET kann registrierte
   Endpunkte nicht entfernen. Das löst nebenbei den Vorrang gegenüber dem Fang-alles `/{slug?}`.
4. **Schmal und stabil statt roher `IServiceProvider`.** Der Kontext bietet Fassaden: `Settings`, `Data`,
   `Media`, `Mail`, `Members`, `Pages`, `Site` (kanonische URL, Template, Locale), `Events`, `Log`, `T()`.
   Heute reicht `Service<AppDbContext>()` jedem Plugin alles — jede interne Umstellung bricht dann
   Plugins. Ein roher Zugriff bleibt als **benannte Notluke** (`ctx.Unsafe.Services`), nur für Native,
   ausdrücklich ohne Stabilitätszusage.
5. **Async und abbrechbar überall** (`CancellationToken` in jedem Handler). Heute ist alles synchron.
6. **Ein Ergebnistyp.** `PluginResult`: `Page` (im Seiten- bzw. Admin-Layout, mit Titel/Meta/Canonical),
   `Fragment`, `Redirect`, `Json`, `File`, `Status`. Öffentlich und Admin gleich.
7. **Oberfläche nur aus der UI-Bibliothek des Hosts.** Ein Plugin beschreibt Oberfläche als **Daten**
   (Komponentenbaum, `ctx.Ui`), der Host rendert sie mit denselben Bausteinen wie das CMS selbst —
   Menüeinträge, Tabs, Listen, Formulare, Dialoge. Kein eigenes CSS, kein Weiß im Dunkelmodus, keine
   Emojis, keine Browser-Dialoge; das gilt dadurch **per Konstruktion**, nicht per Disziplin. Ausführlich
   in 3.5.
8. **i18n von Anfang an.** Texte über `ctx.T("key")` aus `src/…/strings/{culture}.json` des Plugins,
   Fallback auf `de`, dann den Schlüssel. Kein hart deutscher Text im Plugin-Kern.
9. **Fehler bleiben im Plugin.** Jeder Handler läuft in einer Hülle: eine Ausnahme landet im
   Plugin-Log (je Plugin, nicht ein gemeinsamer Ring), der Besucher bekommt eine neutrale Fehlerseite,
   wiederholte Fehler schalten das Plugin auf **Fehler**, ohne dass die Seite fällt.
10. **Versioniert.** `Api` = Hauptversion im Manifest. Der Host unterstützt die aktuelle und die
    vorige; innerhalb einer Hauptversion wird die API **nur ergänzt** (`[Since("2.3")]`). Ein Plugin mit
    unbekannter Hauptversion bleibt aus und sagt warum. Abstractions wird als NuGet-Paket gebaut,
    damit Native-Plugins auch außerhalb des Monorepos kompilieren.
11. **Plugins sprechen über Namen und JSON, nie über gemeinsame Typen.** Jedes Native-Plugin hat einen
    eigenen `AssemblyLoadContext` — ein Typ aus Plugin A ist für Plugin B ein anderer Typ. Ereignisse
    zwischen Plugins (`OrderPaid`) und Erweiterungspunkte (`shop.payment`) tragen deshalb einen Namen und
    eine JSON-Nutzlast. So reden Script und Native, und zwei Native-Plugins, ohne gemeinsamen Typ.
12. **Transparenz statt Scheinsicherheit.** Das Manifest nennt `Capabilities` (Tabellen, öffentliche
    Routen, Jobs, Mail, ausgehende Hosts); die UI zeigt sie vor dem Aktivieren. Das ist keine Sandbox
    (siehe 6), aber der Betreiber sieht, was er freigibt.

### 3.2 Die Beiträge (`IPluginBuilder`)

| Beitrag | Inhalt | Ersetzt heute |
| --- | --- | --- |
| `Route(pattern, handler)` | Öffentliche Seite/Endpunkt, GET/POST, Locale-Präfix, Rate-Limit-Klasse. | `AddPublicPage` (`/plugin/{key}`) |
| `Admin.Nav(...)` + `Admin.Page(...)` | **Ein** Menüpunkt in einer Gruppe (`content`/`design`/`system` oder eigene), Seiten dahinter als **Tabs**, Badge, Recht, unter `/admin/p/{key}/…`. Siehe 3.5. | `AddAdminMenu` + `AddAdminPage` |
| `Admin.DashboardCard`, `Admin.Attention`, `Admin.AddMenu`, `Admin.TopbarTool` | Karte auf der Startseite, Zeilen in „Braucht Aufmerksamkeit“ (`AttentionService`), Einträge im „+ Neu“-Menü, Werkzeug in der Kopfzeile. | gibt es für Plugins nicht |
| `Block(type, …, fields, render)` | Block im Seiteneditor, Feldschema, Render-Handler. | `AddBlock` |
| `TemplatePart("product.html")` | Teil, den ein Template für dieses Plugin mitbringen darf. | Weißliste `TemplateSchema.KnownParts` |
| `Settings.*` | Typisiertes Schema (Text, Zahl, Bool, Auswahl, **Geheimnis**, Liste); Formular automatisch; Ablage `plugin.{key}.*`; über Cloud-Profile ausrollbar; Geheimnisse mit DataProtection verschlüsselt. | `Config("key")` |
| `Data.*` | Siehe 3.3. | `SiteSettings`-Blobs |
| `On<TEvent>(handler)` | Host-Ereignisse: Head/Body-Einschub, Formular gesendet, Mitglied registriert, Seite gespeichert, Backup, Sitemap, Mail. Plugin-Ereignisse: `On("shop.order.paid", …)` / `Emit(...)`. | `AddHeadHtml`/`AddBodyHtml` |
| `Job(name, schedule, handler)` | Geplante Aufgabe; Sperre gegen Doppellauf; letzter Lauf/Fehler im Admin. | gibt es nicht |
| `Lifecycle` | `OnInstall`, `OnEnable`, `OnDisable`, `OnUpdate(fromVersion)`, `OnUninstall`. | `Migrate(version, Action)` |
| `Backup` | Standard: Plugin-Daten werden automatisch gesichert; überschreibbar. | — |
| `MediaReferences` | Welche Mediendateien das Plugin nutzt — der Cleanup bietet sie nicht an. | Scan in `CleanupService` |
| `Sitemap`, `Search` | Einträge für Sitemap/Suche. | — |
| `Extension(name)` | Erweiterungspunkt, den andere Plugins bedienen (`shop.payment`), per Name + JSON. | — |

### 3.3 Daten — die schwierigste Entscheidung

Gleiche API verlangt, dass auch ein Script-Plugin **echte Tabellen** bekommt, ohne EF-Migrationen zu
kompilieren. Zwei Schichten:

- **Schicht 1, beide Bauarten:** `Data.Table("products", schema)` mit Spalten, Indizes und
  **Versionsschritten** (`Migrate(2, t => t.AddColumn(...))`). Der Host erzeugt das SQL, legt Tabellen
  mit dem Präfix `p_{key}_` an und liest sie typisiert (`Query`, `Insert`, `Update`, `Transaction`).
  Backup, Restore, Cleanup und Deinstallieren kennen sie. Löst das 1000er-Limit und das „Restore
  überschreibt `SiteSettings`“.
- **Schicht 2, nur Native (benannte Ausnahme):** ein eigener EF-`DbContext` (vom Host erzeugt, siehe 7) mit mitgelieferten
  Migrationen für alles, was Schicht 1 nicht ausdrückt (Joins, Berichte). Er teilt die SQLite-Datei,
  hat eine eigene Migrationshistorie `__ef_{key}` und wird beim Aktivieren migriert.

Der Shop läuft auf Schicht 2, wenn Schicht 1 nicht reicht; der Blog auf Schicht 1, damit er beweist,
dass die gemeinsame Schicht trägt. **Das ist der Teil der API, den ich vor dem Bauen mit dir
durchgehen will.**

### 3.4 Bewusste Unterschiede (und nur diese)

- Script: keine Abhängigkeits-DLLs, kein EF-Kontext (Schicht 2).
- Native: darf `ctx.Unsafe` nutzen, eigene Abhängigkeiten mitbringen (z. B. ein Zahlungs-SDK), einen
  EF-Kontext mitbringen.
- **Razor-Views in Plugins gibt es für keine Bauart.** Die Oberfläche kommt aus dem Komponentenmodell
  (3.5); das hält Plugins entladbar und die Bauarten gleich.
- Alles andere ist identisch — und durch den Konformitätstest gedeckt.

### 3.5 UI-Bibliothek — Plugins bauen Oberfläche aus Bausteinen des Hosts

**Prinzip:** Ein Plugin liefert keinen HTML-String und hofft auf die richtigen Klassen (so ist es heute:
`AdminUi` baut Strings, der Menüpunkt hängt als flache Zeile unten in `admin-nav-plugins`). Es
beschreibt Oberfläche als **Komponentenbaum**; der Host rendert ihn.

Warum das die richtige Antwort auf „Zugriff auf die UI-Bibliothek“ ist:

1. **Konformität per Konstruktion.** Dunkelmodus (Farbvariablen), Mobilansicht (Zwei-Schritt-Dateibaum),
   Tabler-Icons, `MatDialog` statt Browser-Dialogen, Antiforgery, Leerzustände, Barrierefreiheit — der
   Renderer hat das einmal, jedes Plugin bekommt es.
2. **Die Bibliothek darf sich ändern, Plugins ziehen mit.** Ein geändertes Tabellen-Layout erreicht
   jedes Plugin, ohne dass eine DLL neu gebaut oder ein Skript angefasst wird.
3. **Kein Razor im Plugin** — entladbar, und ein Script-Plugin kann alles, was ein Native kann.
4. **Der Baum ist testbar und lesbar.** Der Konformitätstest vergleicht Komponentenbäume statt HTML;
   Vorschau, MCP und die Cloud können ihn lesen.
5. **Die Cloud rendert dasselbe.** `MatCMS.Shared.Web` ist schon heute beiden Apps gemeinsam; mit dem
   Einstellungs-Schema (3.2) zeigt die Cloud-Profilseite ein Plugin-Formular mit **denselben**
   Komponenten wie die Instanz.

**Schichten:**

| Schicht | Wo | Inhalt |
| --- | --- | --- |
| Modell + Builder | `MatCMS.Plugin.Abstractions` | `UiNode`-Records, `ctx.Ui` (fließende Builder). Keine ASP.NET-Abhängigkeit. |
| Renderer | `MatCMS.Shared.Web` | je Komponente ein Partial (Tag Helper sind dort aus: `Html.PartialAsync`); von CMS und Cloud benutzt. |
| Verhalten | `MatCMS.Shared.Web/wwwroot/js` | `admin-list.js`, `mat-dialogs.js`, `icon-picker.js`, `code-editor.js`, … werden von den Komponenten ausgelöst; ein Plugin schreibt für Standardfälle **kein** JS. |
| Eigenes JS/CSS | `assets/` des Plugins | nur für Sonderfälle, über die dokumentierten `data-`-Verträge (`data-confirm`, …) und `window.Mat*`. |

**Chrome — Menü und Einstiegspunkte.** Heute ist die Seitenleiste in `_AdminLayout.cshtml` fest
geschrieben (drei Gruppen, durch `admin-nav-sep` getrennt). Künftig ist sie eine **Registry**, aus der
das Layout rendert — **auch die Kernpunkte** (Seiten, Beiträge, Menüs …) und der Blog tragen sich dort
ein. Ein Plugin wählt eine Gruppe und eine Reihenfolge und steht damit **zwischen** den Kernpunkten, nicht
mehr als lose Zeile darunter. Weitere Einstiegspunkte derselben Art: Karte auf der Startseite
(`dash-card`), Zeilen in „Braucht Aufmerksamkeit“, Eintrag im „+ Neu“-Menü, Werkzeug in der Kopfzeile.
Ein Menüpunkt je Plugin; Unterseiten sind **Tabs** auf der Seite (Hausregel „ein Menüpunkt mit Tabs“).

**Katalog.** „Heute“ = liegt als gemeinsamer Baustein in `Shared.Web` (oder als CSS-Klasse + JS dort);
„Extrahieren“ = existiert, aber **nur im CMS** — in `src/MatCMS/wwwroot/js` oder inline in einer
einzelnen Seite — und muss erst in die Bibliothek gezogen werden:

| Gruppe | Bausteine | Stand |
| --- | --- | --- |
| Gerüst | PageHead, Card, Tabs, ActionBar, KeyValue, Alert, Badge, Kennzahlen/Diagramm, Leerzustand | Heute (CSS; `_SiteStats`, `_EmptyRow`) |
| Liste | Tabelle mit Suche/Sortierung/Seiten, Zeilenaktionen, Liste/Raster-Umschalter, Löschen-Icon | Heute (`admin-list.js`, `_ViewSwitch`, `_IconTrash`) |
| Einfache Felder | Text, Textarea, Zahl, Auswahl, Checkbox/Schalter, Farbe | Heute (CSS) |
| Besondere Felder | Icon-Wahl, Code-Editor (CodeMirror) | Heute (`_IconField`, `code-editor.js`) |
| Besondere Felder | **Rich-Text**, **Bild-/Medienwahl**, **Bildliste (Galerie)**, **Link-/Seitenwahl**, **Tag-Eingabe**, **Datum/Zeit**, **Listeneditor**, **Datei-Upload** | **Extrahieren.** Rich-Text ist heute `contenteditable` + `execCommand` **inline in `Posts/Edit.cshtml`**; Medienwahl `media-picker.js`, Datepicker `mat-datepicker.js`, Rich-Select `mat-richselect.js`, Sortierung `admin-sortable.js` liegen in der CMS-`wwwroot`, nicht in `Shared.Web` |
| Dialoge | confirm/alert/prompt, Modal | Heute (`mat-dialogs.js`, `.modal`); Medienwahl-Modal: **Extrahieren** |
| Verwaltung | Datei-Baum-Editor (`_TemplateFiles`) | Heute |
| Nicht als Baustein | `_ComponentEditor` (Feld-Designer für Komponenten) | CMS-spezifisch, bleibt draußen |

**Das ist der eigentliche Aufwand der UI-Seite**, und er gehört in die Planung: der Blog-Editor
(Titel, Rich-Text, Titelbild, Galerie, Tags, Terminierung, Anhänge) und der Shop-Produkteditor
brauchen genau die Felder, die heute als Seitencode vorliegen. Jedes davon wird beim Umzug **zu einem
Baustein der Bibliothek**, nicht zu einer Plugin-Kopie — das CMS gewinnt dabei nebenbei einen Baustein,
den es bisher mehrfach hatte.

**Öffentliche Seite.** Dieselbe Idee in klein: `ctx.Ui.Public` kennt Kartenraster, Seitenumbruch, Tag-Chips,
Formular, Knopf — gerendert mit `site.css`, damit das Template des Betreibers sie gestaltet. Plugin-Seiten
liegen im Seitenlayout; Template-Teile (`TemplatePart`) bleiben das Mittel für Layouts wie `article.html`.

**Ausweg, benannt:** `Ui.Html(raw)` setzt rohes HTML ein, damit nichts an einem fehlenden Baustein
scheitert. Der Konformitätstest und `pack.ps1` melden darin feste Farben (`#fff`, `rgb(`) und
eigene `<style>`-Blöcke als **Warnung** — kein Verbot.

## 4. Laden und Entladen — was technisch stimmt

**Entladen** heißt: alle Registrierungen des Plugins verschwinden sofort (Routen, Blöcke, Menü, Hooks,
Jobs), der `AssemblyLoadContext` wird freigegeben. Ob der **Speicher** zurückkommt, hängt davon ab, dass
nichts im Host auf den Kontext zeigt; bekannte Halter sind Razor-Teile, EF-Modell-Caches und
`System.Text.Json`-Caches. Der Host hält deshalb nur schwache Verweise, prüft nach dem Entladen per
`WeakReference` und schreibt ins Log, wenn der Kontext noch lebt. Zusage: **deaktivieren ist immer
sofort und vollständig; Speicher geben wir zurück, wo es technisch geht; ein Plugin, das das nicht
erlaubt, meldet „Neustart nötig“** für ein Update seiner DLL. Das ist kein Mangel der Planung, sondern
das Verhalten von .NET.

**Update eines Native-Plugins:** neue Version neben die alte laden, Registrierungen in einem Schritt
umschalten, alte Version fallen lassen. Schlägt das Laden fehl, bleibt die alte aktiv.

## 5. Blog, Shop

**Blog** (BuiltIn, Standard **an**, Schicht 1): Ziel ist **kein sichtbarer Unterschied** auf live
laufenden Seiten. Fallen:

1. Tabelle bleibt `Posts`; die Schicht-1-Tabelle wird als **bestehend übernommen** (Baseline), nicht
   neu angelegt — wie `EnsureSchemaCurrentAsync` es für Altdatenbanken tut.
2. `AppDbContext` gibt `Post` ab, ohne die Tabelle zu löschen. `dotnet ef migrations add` erzeugt
   `DropTable("Posts")`; die Migration wird von Hand zum **No-op**. Ein vergessener Handgriff löscht jeden
   Beitrag, daher vorab ein Test mit echter Datenbank.
3. Alle `_db.Posts`-Zugriffe im Kern laufen danach über Hooks/`Pages`-Fassade, nicht über `AppDbContext`.
4. Der Cloud-Inhaltskanal (`ContentOpApplier`) schreibt Beiträge im Auftrag von Cloud/MCP; der
   Wire-Vertrag (`CloudProtocol`) bleibt, das Plugin meldet seine Operationen an.
5. `/blog/{slug}` bleibt. Die Locale ist dort heute fest `"de"` (`Post.cshtml.cs`) — ein bestehender
   Mangel, den der Umzug nicht still mitnimmt oder „repariert“; er kommt als eigener Punkt ins Backlog.
6. Der Blog hat heute einen Editor von 395 Zeilen Razor (`Posts/Edit.cshtml`) mit inline-JS und den
   Block `_Posts`. **Das ist der Prüfstein für die UI-Bibliothek (3.5):** der Editor muss sich aus
   Bausteinen zusammensetzen lassen. Fehlt einer (Rich-Text, Bildliste, Terminierung), wird er **in die
   Bibliothek gezogen**, nicht im Plugin nachgebaut. Der Blog darf nichts können, was der Shop nicht auch
   kann — sonst taugt er nicht als Beweis der API.

**Shop** (Native, Standard **aus**, Schicht 1+2): eigenes Dokument nach dem Blog-Umzug. Er verlangt von
der API: Tabellen, Routen mit Template (Liste, Produkt, Warenkorb, Kasse, Danke, Bestellstatus),
Zahlungsanbieter als Erweiterungspunkt `shop.payment`, Ereignisse (`order.placed/paid/shipped`) → Mail
über `MailTemplates`, Jobs (Warenkörbe, Zahlungsabgleich), Cleanup-Medien, Geheimnisse für API-Schlüssel,
und rechtliche Datenmodellfragen (Steuersatz je Position, brutto/netto, AGB-/Widerrufs-Beleg je
Bestellung, lückenloser Rechnungsnummernkreis, DSGVO-Export/-Löschung per Hook).

## 6. Vertrauen und Sicherheit

- Plugin-Code läuft im Prozess mit allen Rechten — Script heute, Native genauso. **Es gibt keinen
  Sandkasten**, und das UI behauptet keinen.
- Native bringt kompilierten Code von außen ins System. Deshalb: Instanz-Schalter
  `plugins.allowNative` (Standard **aus**, nur vor Ort vom Admin zu setzen — **nicht** per Cloud-Rollout, MCP oder Backup), SHA-256 je DLL im
  Manifest und in der UI sichtbar, Import landet **deaktiviert**, `Capabilities` vor dem Aktivieren
  sichtbar. Signatur durch den Store-Betreiber ist ein späterer Schritt und hier nicht versprochen.
- Zip-Schutz für DLLs wie für Assets (Größe, Anzahl, Pfade, nur erlaubte Endungen unter `src/`).
- Eine Native-DLL darf **nicht** eine eigene Kopie von `MatCMS.Plugin.Abstractions` mitbringen (der Host
  lädt sie aus dem Standardkontext, sonst bricht die Typidentität); der Import prüft das.

## 7. Spike-Ergebnis (14.10.2026) — Entladen funktioniert, mit drei Regeln

Wegwerf-Projekt: ein Host lädt eine Plugin-DLL in einen entladbaren `AssemblyLoadContext`, ruft einen
Handler auf, entlädt, erzwingt GC und prüft per `WeakReference`, ob der Kontext stirbt (drei Runden
je Fall). `MatCMS.Plugin.Abstractions`, EF und das Framework kommen vom Host (Typidentität), nur die
Plugin-Assembly lädt im eigenen Kontext. Ursachen wurden mit `dotnet-dump` (`gcroot`) gesucht, nicht
geraten.

| Was das Plugin tut | Entladen |
| --- | --- |
| nichts Besonderes: Handler, Komponentenbaum, Reflexion, `Activator`, generische Listen/Wörterbücher über Plugin-Typen, `JsonSerializer` über `Dictionary<string,string>`, `JsonNode` | **ja** |
| `JsonSerializer.Serialize` über einen **Plugin-eigenen Typ** (Reflexion) — auch mit eigenen `JsonSerializerOptions` | **nein** |
| dasselbe mit **quellgeneriertem** `JsonSerializerContext` | **ja** |
| EF-`DbContext` (Modell bauen, synchron, asynchron), auch mit eigenem Service-Provider oder `EnableServiceProviderCaching(false)` | **nein** |
| derselbe EF-Kontext, **wenn der Host nach dem Entladen den EF-Provider-Cache leert** | **ja** (alle Fälle) |

**Die drei Regeln, die daraus folgen:**

1. **JSON über Plugin-Typen nur quellgeneriert oder über `JsonNode`.** Reflexions-`System.Text.Json`
   hält den Ladekontext fest. Die Abstractions bieten dafür `ctx.Json` (arbeitet auf `JsonNode`); die
   Projektvorlage legt einen `JsonSerializerContext` an.
2. **EF-Kontexte baut der Host.** Die Wurzel ist EFs statischer `ServiceProviderCache`: er hält einen
   Provider, dessen `DbSetFinder` nach dem Kontexttyp des Plugins schlüsselt, und der Typ hält den
   Ladekontext. Der Host leert diesen Cache beim Entladen (ein Reflexionszugriff auf EF-Interna,
   isoliert in **einer** Methode). Das ist fragil gegenüber EF-Updates; deshalb prüft ein Test den
   Entladevorgang mit einer echten Plugin-DLL, und fehlt das Feld, fällt das Plugin auf „Neustart
   nötig“ zurück statt zu brechen. Konsequenz für die API: ein Plugin **deklariert** `Data.Context<T>()`,
   der Host erzeugt die Instanz und die Optionen.
   **Das Leeren des Caches allein reicht nicht** — beim Bau fand sich eine zweite Wurzel: der Plugin-Kontext teilte
   EFs internen Provider mit dem `AppDbContext` des Hosts. Er braucht einen **eigenen** Provider (siehe 9).
3. **Abhängigkeiten, die selbst reflexionsbasiert serialisieren oder statisch cachen** (ein
   Zahlungs-SDK!) können das Entladen verhindern. Das ist nicht prüfbar, bevor man sie lädt. Der Host
   prüft nach jedem Entladen per `WeakReference` und setzt den Zustand **„Neustart nötig“**, wenn der
   Kontext lebt — sichtbar, nicht still.

**Noch nicht gezeigt** (im Spike nicht gebaut, in Phase 0 an der echten App zu prüfen):
Routenweiche mit Vorrang vor `/{slug?}` und `/{culture}/…`; Seitenleiste als Registry; dasselbe
Referenz-Plugin als `.csx` über denselben Builder; Laden aus einem Byte-Strom statt aus einer Datei
(unter Windows sperrt `LoadFromAssemblyPath` die DLL bis zum Entladen; im Linux-Container ist das kein
Thema, die Regel „neue Version neben die alte“ (2.1) umgeht es ohnehin).

## 8. Phasen

| Phase | Inhalt | Stand |
| --- | --- | --- |
| **S** | Spike. | **erledigt** |
| **0** | API-Projekt, Konformitätstests, Zustand in der DB (`Kind`, `Entry`, `Sha256`, `Api`), Routenweiche, Kompatibilitätsschicht, ein Plugin laden statt alle. | **erledigt**; Bundle-Format abweichend vom Entwurf (2.1) |
| **1** | Native-Lader, `plugins.allowNative`, UI gleich für beide, Export/Import für `src/`. | **erledigt**, bis auf: `pack.ps1`/Projektvorlage für Native, Cloud-Store/Profile (→ 5) |
| **1b** | **UI-Bibliothek** (3.5): Komponentenmodell + Renderer in `Shared.Web`, Seitenleiste als Registry, `Badge` an `IAdminSection`. Die Bausteine „Extrahieren“ sind **nicht** nach `Shared.Web` verschoben, sondern als Komponenten gebaut (`ui-fields.js`); Medienwahl/Datepicker bleiben im CMS, weil sie nur dort einen Medienspeicher haben. | **erledigt** |
| **2** | `Data`-Schicht 1, Settings-Schema, Jobs, Hooks, `MediaReferences`, Backup/Cleanup-Anbindung. Ohne `ctx.Json` (JsonNode reicht). | **erledigt** |
| **3** | Blog als BuiltIn-Plugin (Abschnitt 5). | **erledigt** |
| **4** | Schicht 2 (EF, vom Host erzeugt) für Native. Shop-Entwurf, dann Shop. | offen |
| **5** | Cloud: Store/Profil zeigen Native-Bundles schreibgeschützt und packen sie unberührt neu (**erledigt**); Zustand von Plugins in Profilen, Cloud-API/MCP (offen). `plugins.allowNative` bleibt bewusst **nicht** ausrollbar. | teilweise |

## 9. Offene Punkte und Erkenntnisse

**Erkenntnisse aus dem Bauen, die den Entwurf geändert haben:**

- **Namen:** `PluginRequest` heißt in der neuen API `RequestData`, weil bestehende Skripte `PluginRequest` (den
  alten Typ) ausdrücklich benennen — dieselbe Bezeichnung hätte sie mehrdeutig gemacht. `RouteOptions` heißt
  `PublicRouteOptions` (Kollision mit ASP.NET). Die Skript-Global `Plugin` hat Vorrang vor dem Entity
  `MatCMS.Models.Plugin`, weil Member eines Skripts vor importierten Typen aufgelöst werden.
- **EF-Kontexte brauchen einen eigenen Provider.** EF teilt *einen* internen Service-Provider zwischen allen
  Kontexten mit gleichen Optionen. Der Plugin-Kontext teilte ihn mit dem `AppDbContext` des Hosts; dessen Modell- und
  Query-Cache hielt die Plugin-Typen fest, solange irgendein Host-Kontext lebte — der Spike fand das nicht, weil er
  nur einen Kontext hatte. Gefunden wurde es mit `gcroot` auf einem Dump. Regel (und Voraussetzung für Phase 4):
  der Host baut die Optionen eines Plugin-Kontexts selbst, mit eigenem Provider. Bis dahin tut es das Referenz-Plugin
  von Hand (`UseInternalServiceProvider`).
- **Debug-Builds verfälschen Entlade-Tests,** weil sie jede Zwischenvariable bis zum Methodenende halten. Die Tests
  (und `NativePluginHost` selbst) halten deshalb nichts vom Plugin im eigenen Frame (`NoInlining`-Hilfen).
- **Ein entzogenes `allowNative` ist kein fehlgeschlagenes Laden:** die Regel „ein Fehler lässt die laufende Version
  laufen“ hätte das Plugin trotz „aus“ weiterlaufen lassen. Es wird jetzt entladen.
- **`Leserstimmen` greift in die Registry** (`reg.AdminMenu[i] = …`). Genau solche Zugriffe sollen künftig über die
  typisierte API laufen; bis dahin bleibt die Sicht schreibbar. Andere interne Registry-Zugriffe von Plugins auf
  Live-Instanzen sind nicht bekannt, aber auch nicht auszuschließen.

**Aus dem Bau von Phase 1b–3:**

- **Warum die API `MatCMS.Plugin` heißt (nicht `MatCMS.Plugins`):** Assembly und Namespace sollen übereinstimmen, und `MatCMS.Plugin.{Name}` ist
  der Platz für den Code eines Plugins. Der Preis: die Entität heißt `MatCMS.Models.Plugin`, und ein nacktes `Plugin` im Host-Code meint
  jetzt den Namespace (CS0118 an jeder Stelle, die es vergisst).
- **Der Blog bekommt ein Zeilen-Gerüst:** ein eingebautes Plugin hat eine Zeile in `Plugins` (Zustand an/aus, Einstellungen), die beim Start
  angelegt und nie überschrieben wird. Hat ein Betreiber unter dem Schlüssel `blog` ein eigenes Plugin angelegt, bleibt das unberührt und
  das eingebaute startet nicht (Meldung im Plugin-Log) — ein stilles Ersetzen wäre schlimmer als ein fehlender Blog.
- **`dotnet ef migrations add` schreibt `DropTable("Posts")`,** sobald `Post` aus dem Modell fällt. Die Migration ist von Hand leer; ein Test
  (`A_database_from_before_the_change_keeps_its_posts…`) migriert eine echte Datei-DB mit einem Beitrag und prüft, dass er bleibt.
- **Adoptierte Tabellen reisen nicht im Plugin-Backup,** sondern im Backup-Abschnitt des Kerns (`dto.Posts`, unverändert, also auch aus alten
  Backups wiederherstellbar). Das Backup liest sie über `BlogStore.Table(db)`, ohne dass das Plugin laufen muss.
- **Gewollte Unterschiede zum alten Blog:** Beiträge stehen jetzt in der Sitemap (vorher fehlten sie — ein Mangel); die Admin-Adressen sind
  `/admin/p/blog…` (die alten `/admin/posts…` leiten weiter); die Beitragsliste im Admin ist aus Komponenten gebaut (Tags als kleine Chips,
  Titel fett — sonst gleich). Unverändert, absichtlich: `/blog/{slug}` kennt nur Deutsch (`DefaultLanguageOnly`), `/en/blog/x` bleibt 404.
- **Zwei Encoder, zwei Ausgaben:** die Beitragsseite kodierte mit `WebUtility`, der Block mit dem Encoder, den Razor benutzt (lässt „→“
  durch). Beides ist erhalten, weil Templates und Websites gegen genau diese Ausgabe geschrieben sind. Der allgemeine Renderer nimmt den
  freizügigen Encoder (`UnicodeRanges.All`).
- **`WithCulture="false"`** am eingebetteten `blog.de.json`: sonst hält MSBuild `.de.` für eine Satellitenressource.

**Offen:**

- **UI-Bibliothek (3.5):** Umfang von Phase 1b ist der größte Einzelposten der UI-Seite. Cloud-Seite: nur
  Einstellungsformulare und Manifest-Anzeige rendern Plugin-Komponenten, **keine** Plugin-Adminseiten.
- **Daten-API (3.3):** Schicht 1 in der gezeigten Form ja/nein — vor Phase 2 festlegen.
- **Backup/Restore (erledigt):** Das Backup trägt die Zeilen samt `Kind`/`Entry`/`Sha256`/`Api` und die kompilierten
  Dateien unter `plugins-native/{key}/{version}/` (mit der Option *Plugins*: es ist der Code des Plugins, ohne ihn ist die
  Zeile eine leere Hülle). Beim Restore liegen die Dateien **vor** dem Start der Plugins (sonst „Hauptdatei fehlt“), nur
  für ein (Key, Version), das die Plugin-Liste des Backups selbst als nativ nennt; ein laufendes Plugin wird davor
  entladen. **Die Erlaubnis `plugins.allowNative` kommt nie aus einem Backup** — sonst könnte ein Backup, das
  kompilierten Code mitbringt, auch das Laufen-Dürfen einschalten. Dasselbe gilt für Cloud-Rollout und MCP
  (`SettingKeys.Protected` = Cloud-Verbindung + diese Erlaubnis). Preis: DLLs liegen in jedem Nacht-Backup und zählen
  gegen die Quote; ein Shop mit großen Abhängigkeiten macht Backups spürbar größer.
- **Cloud:** `StoreBundle` kennt nur Skripte (zeigt `Code`, schreibt ihn beim Speichern). Ein Native-Bundle im Store
  braucht eine schreibgeschützte Ansicht und darf nicht über den Skript-Editor gespeichert werden.
- **Native-Plugins bauen:** `plugins/pack.ps1` und eine Projektvorlage (`dotnet new`), plus das NuGet-Paket der
  Abstractions.
- **Dockerfile/Workflows:** erledigt für `MatCMS.Plugin.Abstractions`; Native-Plugin-Projekte gehören **nicht** in
  `MatCMS.slnx` des Images. Das Test-Fixture `tests/Fixtures/RefPlugin` steht in der Solution, gehört aber nicht ins
  Image (`.dockerignore` und Dockerfile kopieren nur die `src/`-Projekte).
- **Version:** `VERSION` strikt +1 Minor, nie 2.0; einmal pro Release, nicht pro Phase.
- **Rollen:** `User.Role` kennt heute nur Admin; Plugin-Rechte (`shop.manage`) kommen erst, wenn die Rollen es hergeben.
- **Testen nie gegen die Dev-DB** (`src/MatCMS/appdata`): die Tests laufen auf In-Memory-SQLite und temporären Ordnern;
  Praxistests im Container mit eigenem Volume, danach löschen.
