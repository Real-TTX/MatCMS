using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The sidebar registry: the CMS's own entries and a plugin's stand in one ordered list per group, and a plugin's
/// counter can neither slow the back office down nor take it down.</summary>
public class AdminNavTests
{
    private static string Script(string group, int order, string badge) => $$"""
        Plugin.Admin.Section("Referenz", "ti-flask", AdminGroup.{{group}}, {{order}})
            .Page("start", "Start", ctx => PluginResult.Page("x"))
            {{badge}};
        """;

    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    [Fact]
    public async Task A_plugin_takes_its_place_between_the_cores_entries_by_group_and_order()
    {
        using var h = new Harness(builtIns: true);
        h.AddScript("shop-like", Script("Content", 25, ""));
        await h.Runner.RunAllAsync();
        var nav = new AdminNav(h.Registry);

        var content = await nav.GroupAsync(AdminGroup.Content, Services());
        var labels = content.Select(e => e.Label).ToList();
        // The blog is a plugin like the one under test: it sits at order 20 between "Seiten" and the plugin at 25.
        Assert.Equal(new[] { "nav.pages", "Beiträge", "Referenz", "nav.menus", "nav.forms", "nav.media" }, labels);
        Assert.False(content.Single(e => e.Label == "Referenz").IsCore);
        Assert.StartsWith("/admin/p/shop-like", content.Single(e => e.Label == "Referenz").Url);

        // The other groups are untouched, and a plugin that asks for no group gets the block of its own.
        Assert.DoesNotContain(await nav.GroupAsync(AdminGroup.Design, Services()), e => e.Label == "Referenz");
        Assert.False(nav.HasPluginEntries(AdminGroup.Plugins));
    }

    [Fact]
    public async Task A_sections_counter_shows_next_to_its_entry_and_is_reused_for_a_moment()
    {
        using var h = new Harness();
        h.AddScript("counter", Script("Plugins", 100, ".Badge(ctx => \"2\")"));
        await h.Runner.RunAllAsync();
        var nav = new AdminNav(h.Registry);
        Assert.True(nav.HasPluginEntries(AdminGroup.Plugins));
        var entry = (await nav.GroupAsync(AdminGroup.Plugins, Services())).Single();
        Assert.Equal("2", entry.Badge);
    }

    [Fact]
    public async Task A_counter_that_throws_or_takes_too_long_is_left_out_and_the_rest_of_the_menu_still_draws()
    {
        using var h = new Harness();
        h.AddScript("broken", Script("Plugins", 100, ".Badge(ctx => { throw new System.InvalidOperationException(\"kaputt\"); })"));
        h.AddScript("slow", Script("Plugins", 101, ".Badge(async (ctx, ct) => { await System.Threading.Tasks.Task.Delay(5000); return \"9\"; })").Replace("Referenz", "Langsam"));
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        var nav = new AdminNav(h.Registry);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = await nav.GroupAsync(AdminGroup.Plugins, Services());
        sw.Stop();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Null(e.Badge));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "the sidebar waited for a slow counter: " + sw.Elapsed);
        Assert.Contains(h.Registry.LogOf("broken"), l => l.Contains("kaputt"));
    }

    [Fact]
    public async Task A_section_has_one_badge_and_one_section_per_plugin()
    {
        using var h = new Harness();
        h.AddScript("twice", Script("Plugins", 100, ".Badge(ctx => \"1\").Badge(ctx => \"2\")"));
        await h.Runner.RunAllAsync();
        Assert.Contains(h.Registry.Errors.Values, e => e.Contains("one badge"));
    }
}
