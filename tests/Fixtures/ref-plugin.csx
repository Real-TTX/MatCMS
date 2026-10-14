// The reference plugin, written as a SCRIPT. tests/Fixtures/RefPlugin/RefPlugin.cs is the same plugin as a
// native assembly; the conformance test runs the same assertions against both, line for line.
// (No /db route here: a script has no EF context of its own — that difference is named in the design.)
var key = Plugin.Info.Key;

Plugin.Route("/" + key, ctx => PluginResult.Page("<h1>Start</h1>", "Start", "Die Startseite"));

Plugin.Route("/" + key + "/{slug}", ctx =>
    PluginResult.Page("<h1>Produkt " + System.Net.WebUtility.HtmlEncode(ctx.Request.R("slug")) + "</h1><p>" + ctx.Locale + "</p>",
                      "Produkt " + ctx.Request.R("slug")));

Plugin.Route("/" + key + "/api/{id}", ctx =>
    PluginResult.Json(new System.Text.Json.Nodes.JsonObject { ["id"] = ctx.Request.R("id"), ["method"] = ctx.Request.Method }));

Plugin.Route("/" + key + "/danke", ctx => PluginResult.Text("danke " + ctx.Request.F("name")),
    new PublicRouteOptions { Methods = PluginMethod.Post });


Plugin.Data.Table("items", t => t.Text("name", required: true).Int("stock", def: "0").UniqueIndex("name"));
Plugin.Settings.Text("greeting", "Gruß", def: "Hallo").Number("tax", "Steuer", def: 19, min: 0, max: 100).Secret("token", "Token");
Plugin.Strings("de", new Dictionary<string, string> { ["hello"] = "Hallo {0}!", ["ref.title"] = "Referenz" });
Plugin.Strings("en", new Dictionary<string, string> { ["hello"] = "Hello {0}!" });
Plugin.On("ref.echo", async (ctx, payload, ct) => new System.Text.Json.Nodes.JsonObject { ["echo"] = payload!["v"]!.GetValue<string>() + "!" });
Plugin.Lifecycle.OnInstall(async ctx => await ctx.Data.Table("items").InsertAsync(new Dictionary<string, object?> { ["name"] = "seed", ["stock"] = 1 }));
Plugin.Job("tidy", JobSchedule.Daily(new TimeOnly(3, 30)), async ctx => await ctx.Data.Table("items").DeleteWhereAsync(DataQuery.Everything.Eq("stock", -1)));

Plugin.Route("/" + key + "/items", async (ctx, ct) =>
{
    var items = ctx.Data.Table("items");
    if (ctx.Request.IsPost) await items.InsertAsync(new Dictionary<string, object?> { ["name"] = ctx.Request.F("name"), ["stock"] = 5 }, ct);
    var rows = await items.QueryAsync(DataQuery.Everything.OrderBy("name"), ct);
    var list = new System.Text.Json.Nodes.JsonArray();
    foreach (var r in rows) list.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = r.Text("name"), ["stock"] = r.Int("stock") });
    return PluginResult.Json(list);
});

Plugin.Route("/" + key + "/greet", ctx =>
    PluginResult.Text(ctx.Settings.Get("greeting") + "|" + ctx.Settings.GetNumber("tax") + "|" + ctx.T("hello", "Max") + "|" + ctx.T("ref.title")));

Plugin.Route("/" + key + "/echo", async (ctx, ct) =>
{
    var answers = await ctx.Events.CollectAsync("ref.echo", new System.Text.Json.Nodes.JsonObject { ["v"] = ctx.Request.Q("v") }, ct);
    return PluginResult.Text(string.Join(",", answers.Select(a => a["echo"]!.GetValue<string>())));
});

Plugin.Admin.Section("Referenz", "ti-flask", AdminGroup.Content, 10)
    .Page("products", "Produkte", ctx => PluginResult.Page("<div class=\"card\">Produkte</div>"))
    .Page("orders", "Bestellungen", ctx => PluginResult.Page("<div class=\"card\">Bestellungen</div>"))
    .Page("ui", "Oberfläche", ctx => PluginResult.Page(
                Ui.Stack(
                    Ui.PageHead("Alle Produkte", null, Ui.Button("Neu", "/admin/p/ref/products/0", small: true)),
                    Ui.Card("Liste",
                        Ui.Table(new[] { Ui.Column("Name"), Ui.Column("Preis", right: true), Ui.Column("", actions: true) },
                            new[] { Ui.RowFor("tasse", "Tasse", "9,90", Ui.Action("Löschen", "delete", "1", UiButtonKind.Danger, confirm: "Wirklich?")) },
                            searchPlaceholder: "Suchen", emptyText: "Keine Produkte")),
                    Ui.Form(Ui.Input("title", "Titel", "x") with { Required = true }, Ui.Checkbox("pub", "Veröffentlicht", true),
                        Ui.FormActions(Ui.Submit("Speichern", name: "action", value: "save")))),
                "Referenz-Seite"))
    .Badge(ctx => "3")
    .Page("products/{id}", null, ctx => PluginResult.Page("<div class=\"card\">Produkt " + ctx.Request.R("id") + "</div>", "Produkt bearbeiten"));

Plugin.Block(
    new BlockSpec("ref-hello", "Referenz-Hallo", "Testblock")
    {
        Fields = new[]
        {
            new FieldSpec("who", "Wer", FieldKind.Text) { Placeholder = "Name" },
            new FieldSpec("tone", "Ton", FieldKind.Select)
            {
                Options = new[] { new FieldOption("a", "Freundlich"), new FieldOption("b", "Knapp") }
            }
        }
    },
    ctx => "<p>Hallo " + System.Net.WebUtility.HtmlEncode(ctx.Block!.Get("who")) + "</p>");

Plugin.Head("<meta name=\"ref\" content=\"" + key + "\">");
