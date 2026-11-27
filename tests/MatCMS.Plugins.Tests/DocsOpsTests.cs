using System.Text.Json.Nodes;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The docs' operations answer like every operation ({status, message, data}) — an error is "failed", never the API's numeric status — and every write
/// carries an undo the host runs: a new page goes again, a changed one gets its text back, a deleted one comes back with its sub-pages under it.
/// </summary>
public class DocsOpsTests : IClassFixture<DocsApp>
{
    private readonly DocsApp _f;
    public DocsOpsTests(DocsApp f) => _f = f;

    private string Op(string name)
    {
        var c = _f.App.Services.GetRequiredService<PluginContributions>();
        return c.HasOperation("docs." + name) ? "docs." + name : name;
    }

    private async Task<JsonObject> RunAsync(string op, JsonObject args, bool allowInternal = false)
    {
        await _f.InstallAsync();
        using var scope = _f.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, Op(op), args, default, allowInternal))!;
    }

    private async Task<JsonObject> UndoAsync(JsonObject answer)
    {
        var undo = Assert.IsType<JsonObject>(answer["undo"]);
        using var scope = _f.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>()
            .RunOperationAsync(scope.ServiceProvider, undo["name"]!.GetValue<string>(), undo["args"]?.DeepClone(), default, allowInternal: true))!;
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();

    private async Task<JsonObject?> PageAsync(string path)
    {
        var read = await RunAsync("page.read", new JsonObject { ["path"] = path, ["locale"] = "de" });
        return Status(read) == "applied" ? (JsonObject)read["data"]! : null;
    }

    [Fact]
    public async Task Every_operation_is_declared_and_the_undo_helpers_are_internal()
    {
        await _f.InstallAsync();
        var c = _f.App.Services.GetRequiredService<PluginContributions>();
        var registry = _f.App.Services.GetRequiredService<PluginRegistry>();
        PluginOperationSpec Spec(string name) => registry.Operations.Single(o => o.Owner == "docs" && o.Name == name).Spec!;
        foreach (var name in new[] { "page.list", "page.read", "page.upsert", "page.delete", "version.list", "page.restore", "page.remove" }) Assert.True(c.HasOperation(Op(name)), name);
        Assert.All(new[] { "page.list", "page.read", "version.list" }, n => Assert.False(Spec(n).Writes));
        Assert.All(new[] { "page.upsert", "page.delete" }, n => Assert.True(Spec(n).Writes));
        Assert.All(new[] { "page.restore", "page.remove" }, n => Assert.True(Spec(n).Internal));
        Assert.All(new[] { "page.list", "page.read", "page.upsert", "page.delete", "version.list" }, n => Assert.False(Spec(n).PersonalData || Spec(n).Internal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("page.remove", new JsonObject { ["id"] = 1 }));
    }

    [Fact]
    public async Task A_read_answers_with_data_and_an_error_is_failed()
    {
        await RunAsync("page.upsert", new JsonObject { ["path"] = "opsread", ["title"] = "Ops lesen", ["body"] = "## Hallo", ["locale"] = "de" });
        var read = await RunAsync("page.read", new JsonObject { ["path"] = "opsread", ["locale"] = "de" });
        Assert.Equal("applied", Status(read));
        Assert.Equal("## Hallo", read["data"]!["body"]!.GetValue<string>());
        var list = await RunAsync("page.list", new JsonObject { ["locale"] = "de" });
        Assert.Contains(((JsonArray)list["data"]!["pages"]!).OfType<JsonObject>(), p => p["path"]!.GetValue<string>() == "opsread");

        Assert.Equal("failed", Status(await RunAsync("page.read", new JsonObject { ["path"] = "gibt/es/nicht", ["locale"] = "de" })));
        Assert.Equal("failed", Status(await RunAsync("page.upsert", new JsonObject { ["path"] = "ohne-eltern/kind", ["title"] = "Kind", ["locale"] = "de" })));   // parent first
        Assert.Equal("failed", Status(await RunAsync("page.upsert", new JsonObject { ["path"] = "ohnetitel", ["locale"] = "de" })));                             // a new page needs a title
        Assert.Equal("failed", Status(await RunAsync("page.list", new JsonObject { ["locale"] = "xx" })));
    }

    [Fact]
    public async Task A_new_page_is_removed_and_a_changed_one_gets_its_text_back()
    {
        var made = await RunAsync("page.upsert", new JsonObject { ["path"] = "opsundo", ["title"] = "Ops Undo", ["body"] = "Erste Fassung", ["locale"] = "de" });
        Assert.Equal("applied", Status(made));
        var changed = await RunAsync("page.upsert", new JsonObject { ["path"] = "opsundo", ["title"] = "Ops Undo neu", ["body"] = "Zweite Fassung", ["published"] = false, ["locale"] = "de" });
        Assert.Equal("applied", Status(changed));
        Assert.Equal("Zweite Fassung", (await PageAsync("opsundo"))!["body"]!.GetValue<string>());

        Assert.Equal("applied", Status(await UndoAsync(changed)));
        var back = (await PageAsync("opsundo"))!;
        Assert.Equal(("Erste Fassung", "Ops Undo", true), (back["body"]!.GetValue<string>(), back["title"]!.GetValue<string>(), back["published"]!.GetValue<bool>()));

        Assert.Equal("applied", Status(await UndoAsync(made)));
        Assert.Null(await PageAsync("opsundo"));
    }

    [Fact]
    public async Task A_deleted_page_comes_back_with_its_sub_pages_under_it()
    {
        await RunAsync("page.upsert", new JsonObject { ["path"] = "opsdel", ["title"] = "Ops Bereich", ["locale"] = "de" });
        await RunAsync("page.upsert", new JsonObject { ["path"] = "opsdel/seite", ["title"] = "Ops Seite", ["body"] = "Text der Seite", ["locale"] = "de" });
        await RunAsync("page.upsert", new JsonObject { ["path"] = "opsdel/seite/kind", ["title"] = "Ops Kind", ["locale"] = "de" });

        var deleted = await RunAsync("page.delete", new JsonObject { ["path"] = "opsdel/seite", ["locale"] = "de" });
        Assert.Equal("applied", Status(deleted));
        Assert.Null(await PageAsync("opsdel/seite"));
        Assert.NotNull(await PageAsync("opsdel/kind"));                                       // the sub-page moved up

        Assert.Equal("applied", Status(await UndoAsync(deleted)));
        Assert.Equal("Text der Seite", (await PageAsync("opsdel/seite"))!["body"]!.GetValue<string>());
        Assert.NotNull(await PageAsync("opsdel/seite/kind"));                                 // and is back under it
        Assert.Null(await PageAsync("opsdel/kind"));
    }
}
