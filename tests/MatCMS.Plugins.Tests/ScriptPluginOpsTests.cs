using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The operations the store's script plugins describe (rule: plugins/OPERATIONS.md), run for real inside the whole application: every
/// declared one exists with the right flags, create → read → update → undo comes back to where it started, and bad input is refused.
/// One application per plugin (one test class each): the review plugins share short names (review.list), and a host that does not yet
/// qualify names with the plugin key would run whichever was loaded first.
/// </summary>
public static class ScriptPluginOps
{
    /// <summary>A plugin's source straight from plugins/ — the files the store bundle is packed from.</summary>
    public static string Source(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "plugins", path));
    }

    /// <summary>The name the host reaches an operation by: <c>{key}.{name}</c>, or the declared name on a host that keeps names as declared.</summary>
    public static string Name(AppFactory app, string key, string name)
    {
        var plugins = app.Services.GetRequiredService<PluginContributions>();
        return name.StartsWith(key + ".", StringComparison.Ordinal) || !plugins.HasOperation(key + "." + name) ? name : key + "." + name;
    }

    /// <summary>Every declared operation is there, with exactly these flags (W = writes, P = personal data, I = internal).</summary>
    public static void AssertDeclared(AppFactory app, string key, params (string Name, string Flags)[] expected)
    {
        var registry = app.Services.GetRequiredService<PluginRegistry>();
        var plugins = app.Services.GetRequiredService<PluginContributions>();
        var mine = registry.Operations.Where(o => o.Owner == key).ToList();
        Assert.Equal(expected.Select(e => e.Name).OrderBy(n => n), mine.Select(o => o.Name.StartsWith(key + ".") ? o.Name[(key.Length + 1)..] : o.Name).OrderBy(n => n));
        foreach (var (name, flags) in expected)
        {
            var full = Name(app, key, name);
            Assert.True(plugins.HasOperation(full), full + " missing");
            var spec = mine.Single(o => o.QualifiedName == full || o.Name == full).Spec;
            Assert.NotNull(spec);
            Assert.False(string.IsNullOrWhiteSpace(spec!.Description), name + " has no description");
            Assert.True(spec.Writes == flags.Contains('W'), name + ": Writes");
            Assert.True(spec.PersonalData == flags.Contains('P'), name + ": PersonalData");
            Assert.True(spec.Internal == flags.Contains('I'), name + ": Internal");
            if (!spec.Internal && spec.Arguments != "{}") Assert.Contains("\"", spec.Arguments);   // an example, not an empty promise
        }
    }

    public static async Task<JsonObject> RunAsync(AppFactory app, string key, string name, JsonObject? args = null, bool @internal = false)
    {
        using var scope = app.Services.CreateScope();
        var plugins = scope.ServiceProvider.GetRequiredService<PluginContributions>();
        var answer = await plugins.RunOperationAsync(scope.ServiceProvider, Name(app, key, name), args ?? new JsonObject(), default, allowInternal: @internal);
        return Assert.IsType<JsonObject>(answer);
    }

    /// <summary>Runs the undo an answer carries, as the assistant's "Rückgängig" does (the name is short: the plugin's own operation).</summary>
    public static async Task<JsonObject> UndoAsync(AppFactory app, string key, JsonObject answer)
    {
        var undo = Assert.IsType<JsonObject>(answer["undo"]);
        var name = undo["name"]!.GetValue<string>();
        using (var scope = app.Services.CreateScope())
        {
            // The host qualifies an undo name with its plugin ("todo-verwaltung-beispiel.todo.restore"); an older host leaves it short.
            var spec = scope.ServiceProvider.GetRequiredService<PluginRegistry>().Operations.Single(o => o.Owner == key && (o.QualifiedName == name || o.Name == name)).Spec;
            Assert.True(spec!.Internal, name + " (an undo) must be internal");
        }
        // A public caller may not run it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(app, key, name, undo["args"]!.DeepClone().AsObject()));
        return await RunAsync(app, key, name, undo["args"]!.DeepClone().AsObject(), @internal: true);
    }

    public static string Status(JsonObject o) => o["status"]!.GetValue<string>();

    public static async Task SettingAsync(AppFactory app, string key, string? value)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (value is null) { if (row is not null) db.SiteSettings.Remove(row); }
        else if (row is null) db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }

    public static async Task<string> SettingValueAsync(AppFactory app, string key)
    {
        using var scope = app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value ?? "";
    }
}

public class ScriptPluginOpsLeserstimmenTests : IClassFixture<AppFactory>
{
    private const string Key = "leserstimmen";
    private readonly AppFactory _app;
    public ScriptPluginOpsLeserstimmenTests(AppFactory app) => _app = app;

    private async Task InstallAsync()
    {
        await ScriptPluginOps.SettingAsync(_app, "plugin.leserstimmen.buch", new JsonArray(
            new JsonObject { ["id"] = 1, ["name"] = "Anna", ["text"] = "Sehr schön.", ["rating"] = 4, ["email"] = "anna@example.org", ["link"] = "", ["date"] = "2026-10-05T10:00:00Z", ["approved"] = true },
            new JsonObject { ["id"] = 2, ["name"] = "Ben", ["text"] = "Wartet.", ["rating"] = 5, ["email"] = "ben@example.org", ["link"] = "", ["date"] = "2026-10-06T10:00:00Z", ["approved"] = false }
        ).ToJsonString());
        await ScriptPluginSite.InstallAsync(_app, Key, "Leserstimmen", ScriptPluginOps.Source("leserstimmen/plugin.csx"));
    }

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags()
    {
        await InstallAsync();
        ScriptPluginOps.AssertDeclared(_app, Key, ("review.list", ""), ("review.read", ""), ("review.pending", "P"), ("review.create", "W"),
            ("review.update", "W"), ("review.delete", "W"), ("review.remove", "WI"), ("review.restore", "WI"));
    }

    [Fact]
    public async Task List_and_read_show_what_the_site_shows_and_never_an_address()
    {
        await InstallAsync();
        var list = await ScriptPluginOps.RunAsync(_app, Key, "review.list", new JsonObject { ["store"] = "buch" });
        var rows = Assert.IsType<JsonArray>(list["data"]);
        Assert.Single(rows);
        Assert.Equal("Anna", rows[0]!["name"]!.GetValue<string>());
        Assert.DoesNotContain("@", list.ToJsonString());
        Assert.Contains("1 warten", list["message"]!.GetValue<string>());

        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 2 })));   // pending: only review.pending
        var pending = await ScriptPluginOps.RunAsync(_app, Key, "review.pending");
        Assert.Equal("ben@example.org", pending["data"]![0]!["email"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_read_update_and_undo_come_back_to_the_start()
    {
        await InstallAsync();
        var created = await ScriptPluginOps.RunAsync(_app, Key, "review.create", new JsonObject
        {
            ["store"] = "Buch", ["name"] = "Clara", ["text"] = "Von Instagram.", ["rating"] = 5, ["link"] = "instagram.com/clara", ["date"] = "2026-11-01"
        });
        Assert.Equal("applied", ScriptPluginOps.Status(created));
        var id = created["data"]!["id"]!.GetValue<long>();
        Assert.Equal("buch", created["data"]!["store"]!.GetValue<string>());

        var read = await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id });
        Assert.Equal("Clara", read["data"]!["name"]!.GetValue<string>());
        Assert.Equal("https://instagram.com/clara", read["data"]!["link"]!.GetValue<string>());
        Assert.True(read["data"]!["published"]!.GetValue<bool>());

        // the same once more is not a second review
        Assert.Equal("skipped-exists", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.create",
            new JsonObject { ["store"] = "buch", ["name"] = "Clara", ["text"] = "Von Instagram." })));

        var updated = await ScriptPluginOps.RunAsync(_app, Key, "review.update", new JsonObject { ["id"] = id, ["text"] = "Geändert.", ["rating"] = 3, ["published"] = false });
        Assert.Equal("applied", ScriptPluginOps.Status(updated));
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id })));   // hidden now
        Assert.Equal("applied", ScriptPluginOps.Status(await ScriptPluginOps.UndoAsync(_app, Key, updated)));
        read = await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id });
        Assert.Equal("Von Instagram.", read["data"]!["text"]!.GetValue<string>());
        Assert.Equal(5, read["data"]!["rating"]!.GetValue<int>());

        // moderation: approve Ben, undo puts him back to waiting
        var approved = await ScriptPluginOps.RunAsync(_app, Key, "review.update", new JsonObject { ["id"] = 2, ["published"] = true });
        Assert.Equal("Ben", (await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 2 }))["data"]!["name"]!.GetValue<string>());
        await ScriptPluginOps.UndoAsync(_app, Key, approved);
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 2 })));

        // delete and undo: back with its e-mail address
        var deleted = await ScriptPluginOps.RunAsync(_app, Key, "review.delete", new JsonObject { ["id"] = 2 });
        Assert.DoesNotContain("ben@", await ScriptPluginOps.SettingValueAsync(_app, "plugin.leserstimmen.buch"));
        await ScriptPluginOps.UndoAsync(_app, Key, deleted);
        Assert.Contains("ben@example.org", await ScriptPluginOps.SettingValueAsync(_app, "plugin.leserstimmen.buch"));

        // undo of the create takes Clara out again
        await ScriptPluginOps.UndoAsync(_app, Key, created);
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id })));
        Assert.Equal(2, JsonNode.Parse(await ScriptPluginOps.SettingValueAsync(_app, "plugin.leserstimmen.buch"))!.AsArray().Count);
    }

    [Theory]
    [InlineData("""{"name": "", "text": "x"}""")]
    [InlineData("""{"name": "A", "text": ""}""")]
    [InlineData("""{"name": "A", "text": "x", "rating": 7}""")]
    [InlineData("""{"name": "A", "text": "x", "link": "javascript:alert(1)"}""")]
    [InlineData("""{"name": "A", "text": "x", "date": "gestern"}""")]
    public async Task Bad_input_is_refused_and_nothing_is_written(string args)
    {
        await InstallAsync();
        var before = await ScriptPluginOps.SettingValueAsync(_app, "plugin.leserstimmen.default");
        var answer = await ScriptPluginOps.RunAsync(_app, Key, "review.create", JsonNode.Parse(args)!.AsObject());
        Assert.Equal("failed", ScriptPluginOps.Status(answer));
        Assert.Null(answer["undo"]);
        Assert.Equal(before, await ScriptPluginOps.SettingValueAsync(_app, "plugin.leserstimmen.default"));
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.update",
            new JsonObject { ["id"] = 1, ["name"] = new string('x', 81) })));
    }
}

public class ScriptPluginOpsBewertungenTests : IClassFixture<AppFactory>
{
    private const string Key = "bewertungen";
    private readonly AppFactory _app;
    public ScriptPluginOpsBewertungenTests(AppFactory app) => _app = app;

    private async Task InstallAsync()
    {
        await ScriptPluginOps.SettingAsync(_app, "plugin.reviews.default", new JsonArray(
            new JsonObject { ["id"] = 11, ["name"] = "Dora", ["text"] = "Toll.", ["rating"] = 5, ["date"] = "2026-10-05T10:00:00Z", ["approved"] = true },
            new JsonObject { ["id"] = 12, ["name"] = "Emil", ["text"] = "Wartet.", ["rating"] = 4, ["date"] = "2026-10-06T10:00:00Z", ["approved"] = false }
        ).ToJsonString());
        await ScriptPluginSite.InstallAsync(_app, Key, "Bewertungen", ScriptPluginOps.Source("bewertungen/plugin.csx"));
    }

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags()
    {
        await InstallAsync();
        ScriptPluginOps.AssertDeclared(_app, Key, ("review.list", ""), ("review.read", ""), ("review.pending", "P"), ("review.update", "W"),
            ("review.delete", "W"), ("review.restore", "WI"));
    }

    [Fact]
    public async Task Moderate_delete_and_undo_come_back_to_the_start()
    {
        await InstallAsync();
        var start = await ScriptPluginOps.SettingValueAsync(_app, "plugin.reviews.default");
        var list = await ScriptPluginOps.RunAsync(_app, Key, "review.list");
        Assert.Equal("Dora", Assert.Single(list["data"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 12 })));
        Assert.Equal("Emil", (await ScriptPluginOps.RunAsync(_app, Key, "review.pending"))["data"]![0]!["name"]!.GetValue<string>());

        var approved = await ScriptPluginOps.RunAsync(_app, Key, "review.update", new JsonObject { ["id"] = 12, ["published"] = true });
        Assert.Equal("applied", ScriptPluginOps.Status(approved));
        Assert.Equal("Emil", (await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 12 }))["data"]!["name"]!.GetValue<string>());
        await ScriptPluginOps.UndoAsync(_app, Key, approved);
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 12 })));

        var deleted = await ScriptPluginOps.RunAsync(_app, Key, "review.delete", new JsonObject { ["id"] = 11 });
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 11 })));
        await ScriptPluginOps.UndoAsync(_app, Key, deleted);
        Assert.Equal("Dora", (await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = 11 }))["data"]!["name"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(await ScriptPluginOps.SettingValueAsync(_app, "plugin.reviews.default"))!.AsArray().Count);
        Assert.Equal(JsonNode.Parse(start)!.AsArray().Select(n => n!["id"]!.GetValue<long>()).OrderBy(x => x),
            JsonNode.Parse(await ScriptPluginOps.SettingValueAsync(_app, "plugin.reviews.default"))!.AsArray().Select(n => n!["id"]!.GetValue<long>()).OrderBy(x => x));
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        await InstallAsync();
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.update", new JsonObject { ["id"] = 11 })));                       // published missing
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.update", new JsonObject { ["id"] = 999, ["published"] = true })));
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.delete", new JsonObject { ["id"] = "x" })));
    }
}

public class ScriptPluginOpsGoogleReviewsTests : IClassFixture<AppFactory>
{
    private const string Key = "google-reviews";
    private const string Manual = "plugin.googlereviews.manual";
    private readonly AppFactory _app;
    public ScriptPluginOpsGoogleReviewsTests(AppFactory app) => _app = app;

    private async Task InstallAsync()
    {
        await ScriptPluginOps.SettingAsync(_app, Manual, new JsonArray(
            new JsonObject { ["id"] = 21, ["name"] = "Franz", ["rating"] = 5, ["text"] = "Super.", ["date"] = "Juli 2026" }).ToJsonString());
        await ScriptPluginOps.SettingAsync(_app, "plugin.googlereviews.cfg", new JsonObject { ["mode"] = "manual", ["apiKey"] = "AIza-secret" }.ToJsonString());
        await ScriptPluginSite.InstallAsync(_app, Key, "Google Bewertungen", ScriptPluginOps.Source("google-reviews/plugin.csx"));
    }

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags()
    {
        await InstallAsync();
        ScriptPluginOps.AssertDeclared(_app, Key, ("review.list", ""), ("review.read", ""), ("review.create", "W"), ("review.update", "W"),
            ("review.delete", "W"), ("review.remove", "WI"), ("review.restore", "WI"));
    }

    [Fact]
    public async Task Create_read_update_delete_and_undo_come_back_to_the_start()
    {
        await InstallAsync();
        var start = await ScriptPluginOps.SettingValueAsync(_app, Manual);
        var list = await ScriptPluginOps.RunAsync(_app, Key, "review.list");
        Assert.Single(list["data"]!.AsArray());
        Assert.Contains("manual", list["message"]!.GetValue<string>());
        Assert.DoesNotContain("AIza", list.ToJsonString());

        var created = await ScriptPluginOps.RunAsync(_app, Key, "review.create", new JsonObject { ["name"] = "Greta", ["rating"] = 4, ["text"] = "Gut.", ["date"] = "August 2026" });
        Assert.Equal("applied", ScriptPluginOps.Status(created));
        var id = created["data"]!["id"]!.GetValue<long>();
        Assert.Equal("Greta", (await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id }))["data"]!["name"]!.GetValue<string>());

        var updated = await ScriptPluginOps.RunAsync(_app, Key, "review.update", new JsonObject { ["id"] = id, ["rating"] = 2 });
        Assert.Equal(2, (await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id }))["data"]!["rating"]!.GetValue<int>());
        await ScriptPluginOps.UndoAsync(_app, Key, updated);
        Assert.Equal(4, (await ScriptPluginOps.RunAsync(_app, Key, "review.read", new JsonObject { ["id"] = id }))["data"]!["rating"]!.GetValue<int>());

        // deleting the FIRST card and undoing it puts it back in its place
        var deleted = await ScriptPluginOps.RunAsync(_app, Key, "review.delete", new JsonObject { ["id"] = 21 });
        await ScriptPluginOps.UndoAsync(_app, Key, deleted);
        var rows = (await ScriptPluginOps.RunAsync(_app, Key, "review.list"))["data"]!.AsArray();
        Assert.Equal(21, rows[0]!["id"]!.GetValue<long>());

        await ScriptPluginOps.UndoAsync(_app, Key, created);
        Assert.Equal(JsonNode.Parse(start)!.ToJsonString(), JsonNode.Parse(await ScriptPluginOps.SettingValueAsync(_app, Manual))!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"name": "", "text": "x"}""")]
    [InlineData("""{"name": "A", "rating": 0}""")]
    [InlineData("""{"name": "A", "rating": "viele"}""")]
    public async Task Bad_input_is_refused(string args)
    {
        await InstallAsync();
        var before = await ScriptPluginOps.SettingValueAsync(_app, Manual);
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "review.create", JsonNode.Parse(args)!.AsObject())));
        Assert.Equal(before, await ScriptPluginOps.SettingValueAsync(_app, Manual));
    }
}

public class ScriptPluginOpsTodoTests : IClassFixture<AppFactory>
{
    private const string Key = "todo-verwaltung-beispiel";
    private readonly AppFactory _app;
    public ScriptPluginOpsTodoTests(AppFactory app) => _app = app;

    private async Task InstallAsync()
    {
        await ScriptPluginOps.SettingAsync(_app, "plugin.todos", new JsonArray(
            new JsonObject { ["id"] = 1, ["text"] = "Fenster putzen", ["done"] = false },
            new JsonObject { ["id"] = 2, ["text"] = "Einkaufen", ["done"] = true }).ToJsonString());
        await ScriptPluginSite.InstallAsync(_app, Key, "Todo-Verwaltung (Beispiel)", ScriptPluginOps.Source("todo-verwaltung-beispiel/plugin.csx"));
    }

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags()
    {
        await InstallAsync();
        ScriptPluginOps.AssertDeclared(_app, Key, ("todo.list", ""), ("todo.read", ""), ("todo.create", "W"), ("todo.update", "W"),
            ("todo.delete", "W"), ("todo.remove", "WI"), ("todo.restore", "WI"));
    }

    [Fact]
    public async Task Create_read_update_delete_and_undo_come_back_to_the_start()
    {
        await InstallAsync();
        var start = await ScriptPluginOps.SettingValueAsync(_app, "plugin.todos");
        Assert.Single((await ScriptPluginOps.RunAsync(_app, Key, "todo.list", new JsonObject { ["open"] = true }))["data"]!.AsArray());

        var created = await ScriptPluginOps.RunAsync(_app, Key, "todo.create", new JsonObject { ["text"] = "Rasen mähen" });
        var id = created["data"]!["id"]!.GetValue<int>();
        Assert.Equal(3, id);
        Assert.Equal("Rasen mähen", (await ScriptPluginOps.RunAsync(_app, Key, "todo.read", new JsonObject { ["id"] = id }))["data"]!["text"]!.GetValue<string>());

        var updated = await ScriptPluginOps.RunAsync(_app, Key, "todo.update", new JsonObject { ["id"] = id, ["done"] = true, ["text"] = "Rasen gemäht" });
        Assert.True((await ScriptPluginOps.RunAsync(_app, Key, "todo.read", new JsonObject { ["id"] = id }))["data"]!["done"]!.GetValue<bool>());
        await ScriptPluginOps.UndoAsync(_app, Key, updated);
        var read = (await ScriptPluginOps.RunAsync(_app, Key, "todo.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.False(read["done"]!.GetValue<bool>());
        Assert.Equal("Rasen mähen", read["text"]!.GetValue<string>());

        var deleted = await ScriptPluginOps.RunAsync(_app, Key, "todo.delete", new JsonObject { ["id"] = 1 });
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "todo.read", new JsonObject { ["id"] = 1 })));
        await ScriptPluginOps.UndoAsync(_app, Key, deleted);
        await ScriptPluginOps.UndoAsync(_app, Key, created);
        Assert.Equal(JsonNode.Parse(start)!.ToJsonString(), JsonNode.Parse(await ScriptPluginOps.SettingValueAsync(_app, "plugin.todos"))!.ToJsonString());
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        await InstallAsync();
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "todo.create", new JsonObject { ["text"] = "  " })));
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "todo.create", new JsonObject { ["text"] = new string('x', 501) })));
        Assert.Equal("failed", ScriptPluginOps.Status(await ScriptPluginOps.RunAsync(_app, Key, "todo.update", new JsonObject { ["id"] = 99, ["done"] = true })));
    }
}
