# Vorschlag: Template-Dateien ohne feste Namen

Entwurf, nichts gebaut. Alle Zeilenangaben beziehen sich auf den Stand von `main` am 18.08.2026 —
nicht auf noch nicht eingecheckte Arbeit im Arbeitsverzeichnis.

## Was heute weh tut

Ein Template hat genau fünf Dateien, und die Liste steht als Literal im Code — `body.html`,
`article.html`, `styles.css`, `script.js`, `maintenance.html`
(`src/MatCMS/Pages/Admin/Templates/Edit.cshtml:61-75`, dieselbe Liste noch einmal in
`src/MatCMS.Cloud/Pages/Admin/Profiles/Template.cshtml:29-53` und in
`src/MatCMS.Cloud/Pages/Admin/Store/Template.cshtml:29-49`); es sind keine Dateien, sondern
Spalten des Datensatzes, wie der Kommentar selbst sagt
(`src/MatCMS.Shared.Web/Pages/Shared/TemplateFilesModel.cs:9-12`). Im laufenden Bestand auf 9101
liegen fünf Templates, und bei „Villa Nika“ stehen 5.431 Zeichen CSS in *einer* Datei `styles.css`
neben 2.245 Zeichen `body.html` — es gibt keinen Weg, das in Kopf/Blöcke/Fuß zu zerlegen, und
`script.js` ist in allen fünf Templates leer, weil eine sechste, siebte Datei schlicht nicht
vorgesehen ist. Dazu kommt: eine Template-Datei kann keine andere einbinden — keine der drei
Ersetzungsstellen kennt so etwas (`src/MatCMS/Content/LayoutRenderer.cs:28-80`,
`src/MatCMS/Content/TemplateSchema.cs:182-197`).

**Wer die fünf Dateien liest.** `body.html` → `Template.LayoutHtml`, gerendert in
`src/MatCMS/Pages/Shared/_Layout.cshtml:66` und `:91`, aber nur wenn `{{content}}` darin vorkommt.
`styles.css` → `CustomCss`, `_Layout.cshtml:151-155`. `script.js` → `CustomJs`,
`_Layout.cshtml:248-251`. `article.html` und `maintenance.html` sind zwei Einträge in
`Template.PartsJson` und werden über `TemplateSchema.EffectivePart` geholt
(`src/MatCMS/Pages/Post.cshtml:59-60`, `src/MatCMS/Services/MaintenancePage.cs:69-70`); welche
Schlüssel es dort geben *darf*, entscheidet die Weißliste `TemplateSchema.KnownParts`
(`TemplateSchema.cs:36`), und `Serialize` wirft beim Speichern alles weg, was nicht darin steht
(`TemplateSchema.cs:161-168`).

**Die Platzhalter.** Es gibt keine zentrale Liste der erlaubten. Der Renderer ersetzt, was er
kennt: Menüs und Sprachen in `LayoutRenderer.cs:39-68`, danach die globalen Werte, die
`_Layout.cshtml:73-87` zusammenstellt (`content`, `logo`, `site_name`, `footer_text`, `year`,
`nav`, `footer_nav`, `toolbar` plus `param:*`). `{{param:id}}` läuft über
`src/MatCMS/Content/TemplateParams.cs:30` und `:67-75`. Die beiden Parts haben ihre eigenen
Tokensätze (`TemplateSchema.cs:45-51`, dokumentiert in `:56-58` und `:78-81`). Was der Editor als
einfügbare Chips anbietet, ist eine *dritte*, handgepflegte Aufzählung je Datei
(`Templates/Edit.cshtml:37-59`) — ein Angebot, kein Vertrag: ein Tippfehler dort kostet einen
falschen Vorschlag, mehr nicht.

## Drei Wege

### A — alles beim Alten, nur der Anzeigename wird frei

Ein neues Feld am Template (Rolle → Anzeigename, als String mit JSON). Die fünf Spalten und der
Renderer bleiben unangetastet; der Editor liest den Namen aus dem Feld statt aus dem Literal.

- **Danach kann der Benutzer:** seine Dateien nennen, wie er will (`theme.css` statt `styles.css`),
  pro Template verschieden. Sonst nichts.
- **Kostet:** eine Spalte, eine Migration, drei Stellen mit der Dateiliste. Ein Tag.
- **Kann kaputtgehen:** praktisch nichts. Der Name ist eine Beschriftung, kein Pfad — dieselbe
  Unterscheidung, die der Assets-Ordner der Plugins schon macht
  (`src/MatCMS/Services/PluginMapping.cs:16-19`).
- **Bestand / Rückweg:** keine Wanderung, leeres Feld ergibt die heutigen Namen. Rückweg = Spalte
  fallenlassen, die Namen sind wieder die alten.
- **Was es nicht löst:** die Anzahl bleibt fünf, Include bleibt unmöglich. Es beantwortet die
  Beschwerde über die *Namen*, nicht die über das *Format*.

### B — freie Dateien mit Rollen, die Spalten bleiben die Auslieferung

Zwei neue Felder am Template, gebaut wie beim Plugin: eine flache Dateikarte Pfad → Inhalt
(Vorbild `Plugin.FilesJson`, `src/MatCMS/Models/Plugin.cs:40`) und eine Rollenkarte Pfad → Rolle
(Vorbild `Plugin.MappingJson`, `Plugin.cs:53`). Rollen: *Layout*, *Stylesheet*, *Skript*,
*Artikel*, *Wartung* — und Dateien ganz ohne Rolle, die nur zum Einbinden da sind.
**Entscheidend: die Spalten bleiben die Wahrheit für die Auslieferung.** Beim Speichern schreibt
der Editor die Datei mit Rolle *Layout* nach `LayoutHtml`, die Dateien mit Rolle *Stylesheet*
aneinandergehängt nach `CustomCss` und so fort. Renderer, Backup, Wire-Vertrag und Cloud sehen
weiterhin genau das, was sie heute sehen.

- **Danach kann der Benutzer:** beliebig viele Dateien anlegen, frei benennen, in Ordner legen und
  einbinden. „Villa Nika“ wird aus einer 5-KB-Datei zu `kopf.css`, `bloecke.css`, `fuss.css`.
- **Kostet:** zwei Spalten + Migration; der geteilte Baum
  (`src/MatCMS.Shared.Web/Pages/Shared/_TemplateFiles.cshtml`, 489 Zeilen) muss von einer festen
  auf eine dynamische Liste umgebaut werden und Anlegen/Umbenennen/Löschen/Rolle-setzen lernen.
  Zum Vergleich: der Plugin-Editor, der genau das kann, ist 1.652 Zeilen View + 344 Zeilen
  Seitenmodell (`src/MatCMS/Pages/Admin/Plugins/Edit.cshtml`, `.cshtml.cs`) — hier weniger, weil
  kein Datei-Upload und kein Drag&Drop nötig ist, aber es ist die Größenordnung.
- **Kann kaputtgehen:** die Spalten sind ab dann *abgeleitet*. Schreibt jemand von außen direkt auf
  `CustomCss` — die Cloud beim Ausrollen, ein Backup-Restore, die Rohform — und passt das nicht
  mehr zur Dateikarte, dann überschreibt der nächste Speichervorgang im Editor diese Änderung. Das
  braucht eine ausdrückliche Regel: **die Spalte gewinnt, und die Karte wird aus ihr neu erzeugt**
  (eine Datei, wie heute). Zweiter Stolperstein: `TemplateFonts.Code` kappt still bei 20.000
  Zeichen (`src/MatCMS/Content/TemplateFonts.cs:55-59`) — der Deckel trifft dann die
  *zusammengefügte* Fassung und muss hoch, sonst schneidet Speichern ohne Meldung ab.
- **Bestand / Rückweg:** keine Wanderung. Leere Dateikarte heißt „Bestand“ und ergibt genau die
  fünf heutigen Pseudo-Dateien — dieselbe Semantik, die `PluginMapping.Parse` schon hat
  (`PluginMapping.cs:55`, `:73-74`), inklusive der Unterscheidung leer ≠ `{}`. Der Rückweg ist der
  billigste der drei: Spalten fallenlassen (wie `20260817164938_PluginFolderRoles.cs:24-27`), und
  weil in `LayoutHtml`/`CustomCss` weiterhin die vollständige, zusammengefügte Fassung steht, läuft
  **jede Website unverändert weiter** — verloren ist nur die Aufteilung.

### C — vollständiger Umstieg auf eine Dateikarte

Die Karte wird die Wahrheit, `LayoutHtml`/`CustomCss`/`CustomJs`/`PartsJson` entfallen.

- **Danach kann der Benutzer:** dasselbe wie in B, plus was erst ein Renderer kann, der jede Datei
  einzeln kennt: eigene `<style>`-Blöcke, echte Reihenfolge, und beliebig viele Seitentyp-Layouts
  statt der Weißliste `KnownParts` (`TemplateSchema.cs:36`).
- **Kostet:** die feste Feldliste steht rund ein Dutzend Mal — vier Modelle (`Template.cs`,
  `MatCMS.Cloud/Models/Profile.cs:231-268`, `Models/Store.cs:37-65`,
  `ContentTransferService.cs:1177-1201`), das Wire-DTO
  (`src/MatCMS.Shared/CloudProtocol.cs:287-312`) und sechs Kopierstellen
  (`Cloud/Services/ProfileService.cs:310-348`, `Cloud/Program.cs:476-499`,
  `CloudSyncService.cs:541-563`, `CloudCatalogService.cs:102-142`,
  `ContentTransferService.cs:199-211` und `:940-963`), dazu vier Importer und vier Exporter. Plus
  ein Sprung von `CloudProtocol.Version` (`CloudProtocol.cs:18`), der jede verbundene
  Instanz als *veraltet* markiert, bis beide Seiten ausgerollt sind. Plus die Umstellung der
  Cloud-Vorschau von Element-ids auf Rollen (siehe unten).
- **Kann kaputtgehen — und das ist der Grund, warum ich C nicht empfehle:** der Template-Import
  setzt ein *fehlendes* Feld auf seinen Vorgabewert
  (`src/MatCMS/Pages/Admin/Templates/Index.cshtml.cs:144-146` mit
  `src/MatCMS.Shared/JsonImport.cs:27-33`, Vorgabe `""`). Ein Template im neuen Format, das in eine
  ältere MatCMS importiert wird, bekommt `LayoutHtml = ""` und `CustomCss = ""` — die Website
  verliert Layout und CSS, ohne eine einzige Fehlermeldung. Dasselbe beim Backup: `TemplateDto` hat
  kein `JsonExtensionData`, unbekannte Felder fallen beim Einlesen still weg
  (`ContentTransferService.cs:1177-1201`).
- **Bestand / Rückweg:** eine echte Datenwanderung über alle Template-Zeilen. Der Mechanismus dafür
  existiert und läuft beim Start (`TemplateSchema.Upgrade`, `TemplateSchema.cs:204-230`, aufgerufen
  aus `src/MatCMS/Data/DbSeeder.cs:231-250`) — es wäre `SchemaVersion` 3. Der Rückweg ist aber
  *nicht* „Spalte weg“, sondern eine Rückwanderung, die die Dateien wieder zusammenfügt. Die müsste
  geschrieben und geprüft sein, **bevor** V3 auf einen Kundenrechner kommt.

## Die Include-Frage, ausdrücklich

Heute geht es gar nicht, und zwar nicht aus Versehen: `RenderPost`/`RenderTokens` machen bewusst
genau einen Durchgang und durchsuchen Eingefügtes nicht erneut (`TemplateSchema.cs:180-197`), und
`LayoutRenderer` ersetzt `{{content}}` absichtlich **zuletzt**, damit Blockinhalt nicht selbst
gescannt wird (`LayoutRenderer.cs:70-79`). Ein `{{include:…}}` wäre also der erste Platzhalter, der
einen zweiten Durchgang braucht — mit Tiefenbegrenzung und Zyklusschutz.

- **Weg A:** nicht möglich. Es gibt weiter genau fünf Dateien.
- **Weg B:** zweigeteilt, und das ist der eigentliche Gewinn. Für **CSS und JS** braucht es gar
  keinen Platzhalter, sondern nur die *Rolle*: mehrere Dateien mit Rolle *Stylesheet* werden in
  Baumreihenfolge aneinandergehängt — genau das Muster, mit dem Plugins ihre Include-Ordner vor die
  Einstiegsdatei laden (`PluginMapping.cs:98-146`). Für **HTML** reicht das nicht, dort will man an
  einer bestimmten Stelle einbinden; dafür ein sehr kleiner Platzhalter `{{file:kopf.html}}`,
  aufgelöst *gegen die Dateikarte des Templates*, vor allen anderen Ersetzungen, mit begrenzter
  Tiefe. Das ist das Gegenstück zu `PluginFileResolver` (`src/MatCMS/Services/PluginFileResolver.cs:20-24`,
  `:72-90`), der `#load` gegen die Karte statt gegen die Platte auflöst. **Achtung:** bei Plugins
  trägt Roslyn diese Umlenkung; für Templates gibt es nichts Vergleichbares, die Auflösungsschicht
  ist neu zu schreiben. Ohne sie tragen „freie Dateien“ im HTML nichts.
- **Weg C:** dasselbe wie B, nur dass die Karte zusätzlich die Auslieferung trägt.

## Was das für die Cloud bedeutet

Templates reisen als `ConfigTemplate` mit fest aufgezählten Feldern
(`src/MatCMS.Shared/CloudProtocol.cs:287-312`); Store und Profil halten dieselben Spalten
(`Models/Store.cs:37-65`, `Models/Profile.cs:231-268`).

Die Eigenschaft, die `src/MatCMS.Cloud/CLAUDE.md` schützt — feldweise bearbeiten, damit unbekannte
Eigenschaften überleben — gilt für **Plugin-Manifeste**, nicht für Templates. Der Repack kopiert
zwar jede unbekannte Property mit, drückt aber Verschachteltes über `ToString()` zu einem String
platt (`src/MatCMS.Cloud/Pages/Admin/Profiles/Plugin.cshtml.cs:246-261`, besonders `:259`). Daraus
folgt für jedes neue Template-Feld genau eine Regel, und die gilt in allen drei Wegen:

> **Ein neues Feld ist ein String, der JSON enthält — kein verschachteltes Objekt.** Genauso reist
> heute schon `Mapping` beim Plugin (`src/MatCMS/Services/PluginPackager.cs:41`). Und es muss
> **additiv** sein: Weg B ist das, Weg C nicht.

Zwei praktische Folgen:

- **Weg B lässt die Cloud unverändert funktionieren.** Eine ältere Instanz ignoriert die zwei neuen
  Strings und bekommt trotzdem ein vollständiges Template, weil die Spalten weiterhin gefüllt sind.
  Auch die Live-Vorschau bleibt heil: sie liest das Formular über Element-**ids**
  (`src/MatCMS.Cloud/wwwroot/js/template-preview.js:97-112` und `:168`, Miniatur
  `Pages/Admin/TemplatePreview.cshtml:22-39`), und diese ids sind heute identisch mit den
  Feldnamen (`_TemplateFiles.cshtml:98-101`). In Weg C fällt `id="layoutHtml"` weg und die Vorschau
  müsste über Rollen auflösen — Aufwand, den B sich spart.
- **Die CMS-Miniatur ist in allen Wegen unbetroffen.** Sie rendert die echte Startseite in einem
  iframe (`src/MatCMS/Pages/Admin/Templates/Index.cshtml:15`, `Services/SiteContext.cs:180-187`),
  hat also gar keine Feldkopplung.

## Empfehlung

**Weg B**, in dieser Reihenfolge gebaut: erst die zwei Spalten und die Ableitung, dann der Editor,
dann `{{file:…}}` für HTML.

Begründung: B ist der einzige Weg, der freie Namen, beliebig viele Dateien und Include liefert,
**ohne dass irgendein bestehender Leser etwas Neues verstehen muss** — Renderer, Backup, Wire, alte
Instanzen sehen weiterhin die fünf Spalten. Der Plugin-Teil hat exakt so gebaut: `Code` blieb die
Einstiegsdatei und `FilesJson` kam daneben, ausdrücklich damit ein alter Leser etwas Gültiges
bekommt statt einer leeren Hülle (`Plugin.cs:29-40`). Und der Rückweg kostet nichts: die Spalten
enthalten die vollständige Fassung, also überlebt jede Kundenwebsite ein Zurücknehmen unbeschadet.
Weg A ist zu wenig — er beantwortet die Namensfrage und lässt die Formatfrage stehen. Weg C ist
richtig gedacht, aber der stille Datenverlust beim Import in eine ältere Version
(`Index.cshtml.cs:144-146`) macht ihn erst dann verantwortbar, wenn B gebaut ist und der Bestand
sich als tragfähig erwiesen hat. C bleibt danach offen — B verbaut ihn nicht.

**Was ich nicht weiß:**

1. **Reihenfolge bei mehreren CSS-Dateien.** Bei Plugins ist alphabetisch die Regel
   (`PluginMapping.cs:94-96`). Bei CSS entscheidet die Reihenfolge über das Ergebnis; ob
   alphabetisch reicht oder eine gesetzte Ordnung nötig ist, zeigt der erste echte Fall.
2. **Zwei Schreiber gleichzeitig.** Die Regel „die Spalte gewinnt, die Karte wird neu erzeugt“
   klingt sauber, ist aber erst am laufenden System zu beurteilen — Cloud-Rollout während der Kunde
   bearbeitet.
3. **Das Abzeichen „angepasst / leer“** hat bei freien Dateien keine Bedeutung mehr; es vergleicht
   heute gegen eine Baseline pro Pseudo-Datei (`_TemplateFiles.cshtml:150-153`, gespeist aus
   `Templates/Edit.cshtml:68` und `:74`). Was an seine Stelle tritt, weiß ich noch nicht — und
   `src/MatCMS.Cloud/CLAUDE.md` nennt es ausdrücklich als das, was der Baum nicht verlieren darf.
4. **Der Deckel.** Wie hoch 20.000 Zeichen werden dürfen, hängt an SQLite und am Formularlimit,
   nicht an einer Meinung.
5. **Ob der geteilte Baum den Umbau übersteht** oder ob am Ende doch zwei Editoren dastehen.

## Nebenbefunde (unabhängig von diesem Vorschlag)

- **Ein voller Backup-Restore verliert die Template-Parameter.** `TemplateDto` führt
  `ParametersJson`/`ParamValuesJson` gar nicht (`ContentTransferService.cs:1177-1201`), der
  Voll-Restore löscht alle Templates und baut sie ohne diese Felder neu auf (`:502-542`). Der
  JSON-Export der Editor-Seite nimmt beide dagegen mit (`Templates/Edit.cshtml.cs:104-106`).
- **`src/MatCMS.Cloud/CLAUDE.md` verortet den Plugin-Repack in
  `Pages/Admin/Profiles/Edit.cshtml.cs`; er steht in `Pages/Admin/Profiles/Plugin.cshtml.cs:237-292`**
  (Zweitfassung für den Store in `Pages/Admin/Store/Plugin.cshtml.cs:242`).

## Fragen, die nur du beantworten kannst

1. Reicht dir „freie Namen, beliebig viele Dateien“, oder sollen Templates auch **Ordner** haben wie
   die Plugins?
2. Mehrere CSS-Dateien: **alphabetisch** aneinanderhängen, oder willst du die Reihenfolge selbst
   festlegen?
3. Include: nur für **HTML** (ein `{{file:…}}`), oder auch für CSS/JS ein ausdrückliches Einbinden
   statt automatischem Anhängen?
4. Darf ein Template Dateien mitbringen, die **nicht ausgeliefert** werden (reine Bausteine)? Wenn
   ja: eigene Rolle, oder ist „keine Rolle“ genau das?
5. Sollen **Bilder und Schriften** zum Template gehören (wie `plugin-assets/`), oder bleiben die bei
   den Medien?
6. Darf ich für Weg B `CloudProtocol.Version` hochzählen und damit jede verbundene
   Instanz kurz als *veraltet* markieren — oder soll das neue Feld ohne Versionssprung mitreisen?
7. Wie viele **echte Kundentemplates** gibt es außerhalb dieses Rechners? Der Rückweg ist billig,
   aber die Zahl entscheidet, wie vorsichtig der Hinweg sein muss.
