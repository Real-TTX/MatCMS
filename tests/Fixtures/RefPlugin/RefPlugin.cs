using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MatCMS.Plugin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace RefPlugin;

/// <summary>A row of the plugin's own table (used by the unload test).</summary>
public class Item { public int Id { get; set; } public string Name { get; set; } = ""; }

/// <summary>The plugin's own EF context. The unload test proves the host can still release the plugin.
/// <para>It brings its OWN internal service provider. EF shares one provider between all contexts with equal
/// options, and that provider's model and query caches would hold this plugin's types for as long as any
/// context of the host lives — so a plugin context that shares the host's provider can never be unloaded.
/// (The host will build these options itself once the data API exists; until then this is the rule.)</para></summary>
public class RefDb(string path) : DbContext
{
    private static readonly IServiceProvider Provider = new ServiceCollection().AddEntityFrameworkSqlite().BuildServiceProvider();

    public DbSet<Item> Items => Set<Item>();
    protected override void OnConfiguring(DbContextOptionsBuilder o) =>
        o.UseSqlite("Data Source=" + path).UseInternalServiceProvider(Provider);
}

/// <summary>Source-generated JSON for the plugin's own types — reflection-based System.Text.Json over a
/// plugin type pins the load context (see docs/plugin-system-v2.md, "Spike-Ergebnis").</summary>
[JsonSerializable(typeof(Item))]
public partial class RefJson : JsonSerializerContext { }

/// <summary>The reference plugin. Mirrors tests/Fixtures/ref-plugin.csx line for line.</summary>
public sealed class Plugin : IPlugin
{
    public void Configure(IPluginBuilder plugin)
    {
        var key = plugin.Info.Key;

        plugin.Route("/" + key, ctx => PluginResult.Page("<h1>Start</h1>", "Start", "Die Startseite"));

        plugin.Route("/" + key + "/{slug}", ctx =>
            PluginResult.Page("<h1>Produkt " + System.Net.WebUtility.HtmlEncode(ctx.Request.R("slug")) + "</h1><p>" + ctx.Locale + "</p>",
                              "Produkt " + ctx.Request.R("slug")));

        plugin.Route("/" + key + "/api/{id}", ctx =>
            PluginResult.Json(new JsonObject { ["id"] = ctx.Request.R("id"), ["method"] = ctx.Request.Method }));

        plugin.Route("/" + key + "/danke", ctx => PluginResult.Text("danke " + ctx.Request.F("name")),
            new PublicRouteOptions { Methods = PluginMethod.Post });


        plugin.Data.Table("items", t => t.Text("name", required: true).Int("stock", def: "0").UniqueIndex("name"));
        plugin.Settings.Text("greeting", "Gruß", def: "Hallo").Number("tax", "Steuer", def: 19, min: 0, max: 100).Secret("token", "Token");
        plugin.Strings("de", new Dictionary<string, string> { ["hello"] = "Hallo {0}!", ["ref.title"] = "Referenz" });
        plugin.Strings("en", new Dictionary<string, string> { ["hello"] = "Hello {0}!" });
        plugin.On("ref.echo", async (ctx, payload, ct) => new System.Text.Json.Nodes.JsonObject { ["echo"] = payload!["v"]!.GetValue<string>() + "!" });
        plugin.Lifecycle.OnInstall(async ctx => await ctx.Data.Table("items").InsertAsync(new Dictionary<string, object?> { ["name"] = "seed", ["stock"] = 1 }));
        plugin.Job("tidy", JobSchedule.Daily(new TimeOnly(3, 30)), async ctx => await ctx.Data.Table("items").DeleteWhereAsync(DataQuery.Everything.Eq("stock", -1)));

        plugin.Route("/" + key + "/items", async (ctx, ct) =>
        {
            var items = ctx.Data.Table("items");
            if (ctx.Request.IsPost) await items.InsertAsync(new Dictionary<string, object?> { ["name"] = ctx.Request.F("name"), ["stock"] = 5 }, ct);
            var rows = await items.QueryAsync(DataQuery.Everything.OrderBy("name"), ct);
            var list = new System.Text.Json.Nodes.JsonArray();
            foreach (var r in rows) list.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = r.Text("name"), ["stock"] = r.Int("stock") });
            return PluginResult.Json(list);
        });

        plugin.Route("/" + key + "/greet", ctx =>
            PluginResult.Text(ctx.Settings.Get("greeting") + "|" + ctx.Settings.GetNumber("tax") + "|" + ctx.T("hello", "Max") + "|" + ctx.T("ref.title")));

        plugin.Route("/" + key + "/echo", async (ctx, ct) =>
        {
            var answers = await ctx.Events.CollectAsync("ref.echo", new System.Text.Json.Nodes.JsonObject { ["v"] = ctx.Request.Q("v") }, ct);
            return PluginResult.Text(string.Join(",", answers.Select(a => a["echo"]!.GetValue<string>())));
        });

        plugin.Route("/" + key + "/db", async (ctx, ct) =>
        {
            // Own table + own JSON, then the host unloads the plugin — nothing here may hold it alive.
            await using var db = new RefDb(ctx.Request.Q("db"));
            await db.Database.EnsureCreatedAsync(ct);
            db.Items.Add(new Item { Name = "a" });
            await db.SaveChangesAsync(ct);
            var first = await db.Items.OrderBy(i => i.Id).FirstAsync(ct);
            return PluginResult.Text(JsonSerializer.Serialize(first, RefJson.Default.Item));
        });

        plugin.Admin.Section("Referenz", "ti-flask", AdminGroup.Content, 10)
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

        plugin.Block(
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

        plugin.Head("<meta name=\"ref\" content=\"" + key + "\">");
    }
}
