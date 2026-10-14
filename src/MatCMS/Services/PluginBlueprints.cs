namespace MatCMS.Services;

/// <summary>
/// Gerüste für die Aufgaben, die ein Plugin hier wirklich erledigt.
///
/// <para>Die Auswahl ist am BESTAND abgelesen, nicht ausgedacht: die vorhandenen Plugins registrieren
/// einen Menüpunkt samt Adminseite, melden Blöcke für den Seiteneditor an, lesen ihre Einstellungen
/// über <c>Config(...)</c>, nehmen Eingaben von Besuchern über einen öffentlichen Endpunkt entgegen
/// und legen ihre Daten als JSON in einer <c>SiteSetting</c> ab. Genau diese fünf gibt es hier.</para>
///
/// <para><b>Was hier bewusst FEHLT: eine „geplante Aufgabe“.</b> Der Plugin-Kontext bietet keinen Weg,
/// etwas wiederkehrend auszuführen — Plugin-Code läuft beim Start und bei jedem Speichern, sonst nie.
/// Eine Blaupause dafür könnte nur einen Zeitvergleich in den Code schreiben, der immer dann greift,
/// wenn zufällig jemand speichert. Das sähe nach einer Aufgabenplanung aus und wäre keine.</para>
///
/// <para><b>Die Kennung ist der ganze Trick gegen doppeltes Einfügen.</b> Jede Blaupause beginnt mit
/// einer Kommentarzeile, die ihre <see cref="Marker"/>-Zeichenfolge trägt. Vor dem Einfügen sucht der
/// Editor sie in ALLEN Dateien des Plugins samt Einstiegsdatei; wird sie gefunden, wird nichts
/// eingefügt. Ein Vergleich des Codes selbst wäre nach der ersten Zeile, die jemand anpasst, wertlos —
/// und genau das passiert mit einem Gerüst.</para>
///
/// <para>Der Code der Blaupausen steht hier hart auf Deutsch, wie der Starter-Code eines neuen
/// Plugins: er ist Inhalt, den der Autor danach umschreibt, keine Oberfläche. Übersetzt sind Name und
/// Beschreibung, die im Menü stehen.</para>
/// </summary>
public static class PluginBlueprints
{
    /// <param name="Id">Kennung für Menütext und Kennzeichnung, z. B. "adminpage".</param>
    /// <param name="Code">Das Gerüst — beginnt mit der Kennzeichnungszeile.</param>
    public sealed record Blueprint(string Id, string Code)
    {
        /// <summary>Die Zeichenfolge, an der die Blaupause im Code wiedererkannt wird.</summary>
        public string Marker => "bp:" + Id;
    }

    private const string MarkerHint =
        " — an dieser Kennung erkennt der Editor die Blaupause wieder; ohne sie ließe sie sich ein zweites Mal einfügen.";

    public static IReadOnlyList<Blueprint> All { get; } = new List<Blueprint>
    {
        // Gegen die Plugin-API geschrieben (Plugin.Route, Plugin.Admin, Plugin.Settings, Plugin.Data …) — dieselbe wie bei nativen Plugins.
        new("route",
            "// Blaupause bp:route" + MarkerHint + "\n" +
            "// Eine öffentliche Seite unter einer eigenen Adresse, im Layout der Website (Titel, Meta, Sprachpräfix).\n" +
            "// {slug} fängt ein Adressstück; die Adresse muss mit einem festen Stück beginnen (/mein-plugin/…).\n" +
            "Plugin.Route(\"/mein-plugin/{slug}\", ctx =>\n" +
            "    PluginResult.Page(\n" +
            "        Ui.Stack(\n" +
            "            Ui.Heading(\"Seite \" + ctx.Request.R(\"slug\")),\n" +
            "            Ui.Text(\"Das ist eine Seite des Plugins.\"),\n" +
            "            Ui.Button(\"Zur Startseite\", \"/\", UiButtonKind.Ghost)),\n" +
            "        \"Meine Seite\"));\n"),
        new("page",
            "// Blaupause bp:page" + MarkerHint + "\n" +
            "// Ein Menüpunkt im Admin mit zwei Reitern. Die Seiten werden aus Bausteinen der Bibliothek beschrieben —\n" +
            "// Dunkelmodus, Handy-Ansicht und Antiforgery-Schutz kommen dadurch von selbst.\n" +
            "Plugin.Admin.Section(\"Mein Plugin\", \"ti-plug\")\n" +
            "    .Page(\"start\", \"Start\", ctx => PluginResult.Page(Ui.Stack(\n" +
            "        Ui.PageHead(\"Was dieses Plugin hier tut.\"),\n" +
            "        Ui.Card(\"Eingabe\", Ui.Form(\n" +
            "            Ui.Input(\"wert\", \"Wert\", ctx.Request.F(\"wert\")),\n" +
            "            Ui.FormActions(Ui.Submit(\"Speichern\", name: \"action\", value: \"save\"))))), \"Mein Plugin\"))\n" +
            "    .Page(\"liste\", \"Liste\", ctx => PluginResult.Page(Ui.Table(\n" +
            "        new[] { Ui.Column(\"Name\"), Ui.Column(\"Zustand\") },\n" +
            "        new[] { Ui.Row(\"Beispiel\", Ui.Badge(\"aktiv\", UiTone.Success)) },\n" +
            "        searchPlaceholder: \"Suchen\", emptyText: \"Noch nichts da.\")));\n"),
        new("settings",
            "// Blaupause bp:settings" + MarkerHint + "\n" +
            "// Einstellungen als Formular: Das Plugin DEKLARIERT sie, der Admin zeichnet das Formular (Reiter „Einstellungen“),\n" +
            "// prüft die Eingabe und speichert — ein Geheimnis verschlüsselt und nie wieder im Browser sichtbar.\n" +
            "Plugin.Settings\n" +
            "    .Text(\"titel\", \"Titel\", def: \"Hallo\", help: \"Erscheint über der Liste.\", required: true)\n" +
            "    .Number(\"anzahl\", \"Anzahl\", def: 6, min: 1, max: 50)\n" +
            "    .Bool(\"aktiv\", \"Aktiv\", def: true)\n" +
            "    .Secret(\"apikey\", \"API-Schlüssel\");\n" +
            "\n" +
            "// Lesen: ctx.Settings.Get(\"titel\"), ctx.Settings.GetInt(\"anzahl\"), ctx.Settings.GetBool(\"aktiv\") …\n" +
            "Plugin.Route(\"/mein-plugin\", ctx => PluginResult.Text(ctx.Settings.Get(\"titel\")));\n"),
        new("table",
            "// Blaupause bp:table" + MarkerHint + "\n" +
            "// Eine eigene Tabelle. Das Plugin deklariert sie; beim Laden wird sie angelegt oder um neue Spalten ergänzt\n" +
            "// (gelöscht oder umgebaut wird nie etwas von selbst). Backup, Wiederherstellung und Aufräumen kennen sie.\n" +
            "Plugin.Data.Table(\"dinge\", t => t\n" +
            "    .Text(\"name\", required: true)\n" +
            "    .Int(\"bestand\", def: \"0\")\n" +
            "    .Bool(\"aktiv\", def: \"1\")\n" +
            "    .Timestamp(\"angelegt\")\n" +
            "    .UniqueIndex(\"name\"));\n" +
            "\n" +
            "Plugin.Route(\"/mein-plugin/dinge\", async (ctx, ct) =>\n" +
            "{\n" +
            "    var dinge = ctx.Data.Table(\"dinge\");\n" +
            "    if (ctx.Request.IsPost)\n" +
            "        await dinge.InsertAsync(new Dictionary<string, object?> { [\"name\"] = ctx.Request.F(\"name\"), [\"angelegt\"] = DateTime.UtcNow }, ct);\n" +
            "    var zeilen = await dinge.QueryAsync(DataQuery.Everything.Eq(\"aktiv\", true).OrderBy(\"name\"), ct);\n" +
            "    return PluginResult.Page(Ui.Table(new[] { Ui.Column(\"Name\"), Ui.Column(\"Bestand\", right: true) },\n" +
            "        zeilen.Select(z => Ui.Row(z.Text(\"name\"), z.Int(\"bestand\")))), \"Dinge\");\n" +
            "}, new PublicRouteOptions { PostRedirectGet = true });\n"),
        new("job",
            "// Blaupause bp:job" + MarkerHint + "\n" +
            "// Eine geplante Aufgabe: läuft einzeln (nie doppelt), Fehler landen im Log dieses Plugins, der letzte Lauf\n" +
            "// steht unter Plugins. Mindestabstand eine Minute.\n" +
            "Plugin.Job(\"aufraeumen\", JobSchedule.EveryInterval(TimeSpan.FromHours(6)), async (ctx, ct) =>\n" +
            "{\n" +
            "    ctx.Log(\"Aufräumen läuft\");\n" +
            "    await Task.CompletedTask;\n" +
            "});\n" +
            "// Oder einmal täglich zu einer Uhrzeit (UTC): JobSchedule.Daily(new TimeOnly(3, 30))\n"),
        new("event",
            "// Blaupause bp:event" + MarkerHint + "\n" +
            "// Auf ein Ereignis hören. Plugins sprechen über NAMEN und JSON, nie über gemeinsame Typen — so reden Skript-\n" +
            "// und native Plugins miteinander. Vom Host kommen u. a. HostEvents.FormSubmitted und HostEvents.PageSaved.\n" +
            "Plugin.On(HostEvents.FormSubmitted, async (ctx, payload) =>\n" +
            "{\n" +
            "    ctx.Log(\"Formular \" + payload?[\"form\"] + \" eingegangen\");\n" +
            "    await Task.CompletedTask;\n" +
            "});\n" +
            "\n" +
            "// Selbst ein Ereignis auslösen und Antworten sammeln (ein Erweiterungspunkt):\n" +
            "// var antworten = await ctx.Events.CollectAsync(\"mein-plugin.zahlarten\", new JsonObject());\n"),
        new("strings",
            "// Blaupause bp:strings" + MarkerHint + "\n" +
            "// Texte in mehreren Sprachen. ctx.T(\"schluessel\") liefert den Text der Seitensprache, sonst der Standardsprache,\n" +
            "// sonst Englisch, sonst den Schlüssel. Beschriftungen, die selbst ein Schlüssel sind (Menüpunkt, Reiter,\n" +
            "// Einstellungen), übersetzt der Admin für dich. Alternativ: Dateien strings/de.json, strings/en.json im Plugin.\n" +
            "Plugin.Strings(\"de\", new Dictionary<string, string> { [\"mein.titel\"] = \"Mein Plugin\", [\"mein.gruss\"] = \"Hallo {0}!\" });\n" +
            "Plugin.Strings(\"en\", new Dictionary<string, string> { [\"mein.titel\"] = \"My plugin\", [\"mein.gruss\"] = \"Hello {0}!\" });\n" +
            "\n" +
            "Plugin.Admin.Section(\"mein.titel\", \"ti-plug\")\n" +
            "    .Page(\"start\", \"mein.titel\", ctx => PluginResult.Page(Ui.Text(ctx.T(\"mein.gruss\", \"Welt\"))));\n"),

        // Die älteren Mitglieder des Plugin-Kontexts (AddAdminMenu, AddBlock, Config …): laufen weiter, für neue Plugins gibt es oben das Bessere.
        new("adminpage",
            "// Blaupause bp:adminpage" + MarkerHint + "\n" +
            "// Ein Menüpunkt im Admin und die Seite dahinter. Die Seite liegt unter\n" +
            "// /admin/plugin/<schlüssel>; Key ist der Schlüssel DIESES Plugins.\n" +
            "AddAdminMenu(\"Mein Plugin\", \"/admin/plugin/\" + Key, \"ti-plug\");\n" +
            "AddAdminPage(Key, req =>\n" +
            "{\n" +
            "    // Der Knopf unten schickt action=hallo an dieselbe Seite zurück.\n" +
            "    if (req.IsPost && req.Action == \"hallo\")\n" +
            "        req.Log(\"Eingegeben: \" + req.F(\"wert\"));\n" +
            "\n" +
            "    var ui = req.Ui;   // baut Karten und Formulare samt Antiforgery-Feld\n" +
            "    return ui.PageHead(\"Was dieses Plugin hier tut.\")\n" +
            "         + ui.Card(\n" +
            "               ui.Form(\"<label>Wert</label><input name=\\\"wert\\\" />\"\n" +
            "                       + \"<div style=\\\"margin-top:10px;\\\"><button type=\\\"submit\\\" class=\\\"btn\\\">Speichern</button></div>\",\n" +
            "                       new Dictionary<string, string> { [\"action\"] = \"hallo\" }),\n" +
            "               \"Eingabe\");\n" +
            "});\n"),

        new("block",
            "// Blaupause bp:block" + MarkerHint + "\n" +
            "// Ein Block für den Seiteneditor. Die Felder aus fieldsJson stehen dem Redakteur zur\n" +
            "// Verfügung und kommen zur Laufzeit als JSON in req.Data an.\n" +
            "AddBlock(\"mein-block\", \"Mein Block\", \"Kurze Beschreibung im Blockmenü.\", req =>\n" +
            "{\n" +
            "    var titel = \"\";\n" +
            "    try\n" +
            "    {\n" +
            "        var d = System.Text.Json.Nodes.JsonNode.Parse(req.Data) as System.Text.Json.Nodes.JsonObject;\n" +
            "        if (d != null) titel = d[\"titel\"]?.GetValue<string>() ?? \"\";\n" +
            "    }\n" +
            "    catch { }\n" +
            "\n" +
            "    // Alles, was aus den Feldern kommt, wird maskiert — sonst schreibt der Redakteur\n" +
            "    // versehentlich Markup in die Seite.\n" +
            "    var text = System.Net.WebUtility.HtmlEncode(titel);\n" +
            "    return \"<section class='section'><div class='container'><h2>\" + text + \"</h2></div></section>\";\n" +
            "},\n" +
            "\"[{\\\"id\\\":\\\"titel\\\",\\\"label\\\":\\\"Überschrift\\\",\\\"type\\\":\\\"text\\\",\\\"default\\\":\\\"Hallo\\\"}]\");\n"),

        new("config",
            "// Blaupause bp:config" + MarkerHint + "\n" +
            "// Ein Einstellungsfeld: gepflegt wird es unter „Allgemein → Konfiguration“, ohne den\n" +
            "// Code anzufassen. Der Helfer liefert die Vorgabe, solange das Feld leer ist — sonst\n" +
            "// verhält sich ein frisch installiertes Plugin anders als ein eingestelltes.\n" +
            "string Setting(string name, string fallback)\n" +
            "{\n" +
            "    var value = Config(name);\n" +
            "    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();\n" +
            "}\n" +
            "\n" +
            "var maxItems = Setting(\"maxItems\", \"6\");\n" +
            "Log(\"Einstellung maxItems = \" + maxItems);\n"),

        new("publicform",
            "// Blaupause bp:publicform" + MarkerHint + "\n" +
            "// Ein Formular für BESUCHER: der Block zeigt es, der öffentliche Endpunkt nimmt es an.\n" +
            "// POST an /plugin/<schlüssel> leitet danach auf __return zurück — das Feld muss dabei\n" +
            "// sein, sonst landet der Besucher auf der Startseite.\n" +
            "AddPublicPage(Key, req =>\n" +
            "{\n" +
            "    if (!req.IsPost) return \"\";\n" +
            "    var name = (req.F(\"name\") ?? \"\").Trim();\n" +
            "    // Honigtopf: das Feld ist unsichtbar, nur ein Roboter füllt es aus.\n" +
            "    if (name.Length == 0 || (req.F(\"website\") ?? \"\").Length > 0) return \"\";\n" +
            "    req.Log(\"Eingegangen von: \" + name);\n" +
            "    return \"\";\n" +
            "});\n" +
            "\n" +
            "AddBlock(\"mein-formular\", \"Mein Formular\", \"Formular für Besucher.\", req =>\n" +
            "{\n" +
            "    var back = System.Net.WebUtility.HtmlEncode((string.IsNullOrEmpty(req.Path) ? \"/\" : req.Path) + \"?danke=1\");\n" +
            "    return \"<section class='section'><div class='container'>\"\n" +
            "         + (req.Q(\"danke\") == \"1\" ? \"<p>Vielen Dank!</p>\" : \"\")\n" +
            "         + \"<form method='post' action='/plugin/\" + Key + \"'>\"\n" +
            "         + \"<input type='hidden' name='__return' value='\" + back + \"' />\"\n" +
            "         + \"<label>Name</label><input type='text' name='name' required />\"\n" +
            "         + \"<div style='position:absolute;left:-9999px;'><input type='text' name='website' tabindex='-1' autocomplete='off' /></div>\"\n" +
            "         + \"<button class='btn' type='submit'>Absenden</button>\"\n" +
            "         + \"</form></div></section>\";\n" +
            "});\n"),

        new("store",
            "// Blaupause bp:store" + MarkerHint + "\n" +
            "// Daten speichern und lesen. Sie liegen als JSON in EINER SiteSetting, benannt nach dem\n" +
            "// Schlüssel dieses Plugins — so bleibt alles zusammen und ein Backup nimmt es mit.\n" +
            "string StoreKey() => \"plugin.\" + Key;\n" +
            "\n" +
            "string LoadJson(PluginRequest req)\n" +
            "{\n" +
            "    var db = req.Service<AppDbContext>();\n" +
            "    var row = db?.SiteSettings.FirstOrDefault(s => s.Key == StoreKey());\n" +
            "    return string.IsNullOrWhiteSpace(row?.Value) ? \"[]\" : row.Value;\n" +
            "}\n" +
            "\n" +
            "void SaveJson(PluginRequest req, string json)\n" +
            "{\n" +
            "    var db = req.Service<AppDbContext>();\n" +
            "    if (db == null) return;\n" +
            "    var key = StoreKey();\n" +
            "    var row = db.SiteSettings.FirstOrDefault(s => s.Key == key);\n" +
            "    if (row == null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = json });\n" +
            "    else row.Value = json;\n" +
            "    db.SaveChanges();\n" +
            "}\n")
    };
}
