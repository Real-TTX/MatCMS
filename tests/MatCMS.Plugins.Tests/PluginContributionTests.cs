using System.Text.Json.Nodes;
using MatCMS.Services.Plugins;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>What plugins add to the host's own screens and channels, and the guarantee that one bad plugin cannot break them.</summary>
public class PluginContributionTests
{
    private const string Good = """
        Plugin.Data.Table("items", t => t.Text("image"));
        Plugin.Admin.Tile(async (ctx, ct) => Ui.Stat("Dinge", (await ctx.Data.Table("items").CountAsync(null, ct)).ToString(), "ok", href: "/admin/p/good"));
        Plugin.Admin.RecentlyEdited(async (ctx, ct) => new[] { new RecentItem("ti-star", "Ding", true, new System.DateTime(2026, 1, 2, 0, 0, 0, System.DateTimeKind.Utc), "/admin/p/good/x") });
        Plugin.Admin.Attention(async (ctx, ct) => new[] { new AttentionNote("Bestand", "3 Dinge fast leer", "/admin/p/good") { Tone = UiTone.Warning } });
        Plugin.Sitemap(async (ctx, ct) => new[] { new SitemapEntry("/dinge/eins"), new SitemapEntry("/dinge/zwei", new System.DateTime(2026, 1, 1)) });
        Plugin.MediaReferences(async (ctx, ct) => new[] { "/uploads/bild-aus-provider.jpg" });
        Plugin.Operation("thing.create", async (ctx, args, ct) => new System.Text.Json.Nodes.JsonObject { ["status"] = "applied", ["message"] = "angelegt: " + args["name"] });
        """;

    private const string Broken = """
        Plugin.Admin.Tile((ctx, ct) => throw new System.InvalidOperationException("tile kaputt"));
        Plugin.Admin.RecentlyEdited((ctx, ct) => throw new System.InvalidOperationException("recent kaputt"));
        Plugin.Admin.Attention((ctx, ct) => throw new System.InvalidOperationException("attention kaputt"));
        Plugin.Sitemap((ctx, ct) => throw new System.InvalidOperationException("sitemap kaputt"));
        """;

    private static async Task<Harness> Start(params (string Key, string Code)[] plugins)
    {
        var h = new Harness();
        foreach (var (k, c) in plugins) h.AddScript(k, c);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        return h;
    }

    [Fact]
    public async Task Tiles_recent_rows_attention_and_sitemap_come_from_every_loaded_plugin_and_a_broken_one_is_skipped()
    {
        using var h = await Start(("good", Good), ("broken", Broken));
        var c = new PluginContributions(h.Registry);

        var tiles = await c.TilesAsync(h.Services);
        Assert.Equal("Dinge", Assert.Single(tiles).Label);
        var recent = Assert.Single(await c.RecentAsync(h.Services));
        Assert.Equal(("ti-star", "Ding"), (recent.Kind, recent.Title));
        var note = Assert.Single(await c.AttentionAsync(h.Services));
        Assert.Equal(UiTone.Warning, note.Tone);
        Assert.Equal(new[] { "/dinge/eins", "/dinge/zwei" }, (await c.SitemapAsync(h.Services)).Select(e => e.Url));

        // The broken plugin logged why, on its own log, and nothing else happened.
        var log = string.Join("\n", h.Registry.LogOf("broken"));
        Assert.Contains("tile kaputt", log);
        Assert.Contains("sitemap kaputt", log);
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_is_skipped_instead_of_holding_the_page()
    {
        using var h = await Start(("slow", "Plugin.Admin.Tile(async (ctx, ct) => { await System.Threading.Tasks.Task.Delay(30000); return Ui.Stat(\"x\", \"1\"); });"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await new PluginContributions(h.Registry).TilesAsync(h.Services));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), sw.Elapsed.ToString());
        Assert.Contains(h.Registry.LogOf("slow"), l => l.Contains("antwortete nicht"));
    }

    [Fact]
    public async Task Media_a_plugin_names_is_in_use_and_if_a_plugin_cannot_say_the_cleanup_stops()
    {
        using var h = await Start(("good", Good));
        var c = new PluginContributions(h.Registry);
        Assert.Contains("/uploads/bild-aus-provider.jpg", await c.MediaInUseAsync(h.Services));

        h.AddScript("lost", "Plugin.MediaReferences((ctx, ct) => throw new System.InvalidOperationException(\"keine Ahnung\"));");
        await h.Runner.RunAllAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => c.MediaInUseAsync(h.Services));
        Assert.Contains("lost", ex.Message);
        Assert.Contains("keine Ahnung", ex.Message);
    }

    [Fact]
    public async Task The_cleanup_corpus_reads_the_tables_of_every_plugin_even_one_that_is_switched_off()
    {
        using var h = await Start(("good", Good));
        await h.Context("good", new RequestData()).Data.Table("items").InsertAsync(new Dictionary<string, object?> { ["image"] = "/uploads/produkt-tasse.jpg" });
        Assert.Contains("produkt-tasse.jpg", await h.Data.CorpusAsync(h.Db));

        var p = h.Db.Plugins.Single(x => x.Key == "good");
        p.Enabled = false;
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.False(h.Registry.IsLoaded("good"));
        Assert.Contains("produkt-tasse.jpg", await h.Data.CorpusAsync(h.Db));      // off is not gone
    }

    [Fact]
    public async Task A_named_operation_runs_in_the_plugin_that_offers_it_and_an_unknown_one_is_refused()
    {
        using var h = await Start(("good", Good));
        var c = new PluginContributions(h.Registry);
        Assert.True(c.HasOperation("thing.create"));
        var answer = (JsonObject)(await c.RunOperationAsync(h.Services, "thing.create", new JsonObject { ["name"] = "Tasse" }))!;
        Assert.Equal("applied", answer["status"]!.GetValue<string>());
        Assert.Equal("angelegt: Tasse", answer["message"]!.GetValue<string>());

        Assert.False(c.HasOperation("nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.RunOperationAsync(h.Services, "nope", null));
        await h.Runner.UnloadAsync("good");
        Assert.False(c.HasOperation("thing.create"));
    }
}
