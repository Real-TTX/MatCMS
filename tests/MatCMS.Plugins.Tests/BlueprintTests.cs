using MatCMS.Services;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The editor's blueprints and the starter of a new plugin are code a person pastes into a live plugin. A blueprint that does not
/// compile, or throws while loading, is a bug that only shows up when someone uses it — so every one is loaded for real.</summary>
public class BlueprintTests
{
    public static IEnumerable<object[]> Blueprints() => PluginBlueprints.All.Select(b => new object[] { b.Id });

    [Theory]
    [MemberData(nameof(Blueprints))]
    public async Task Every_blueprint_compiles_and_loads_as_a_plugin(string id)
    {
        using var h = new Harness();
        var bp = PluginBlueprints.All.Single(b => b.Id == id);
        h.AddScript("bp-" + id, bp.Code);
        await h.Runner.RunAllAsync();
        Assert.True(h.Registry.IsLoaded("bp-" + id), id + ": " + string.Join(" | ", h.Registry.Errors.Values));
        Assert.Empty(h.Registry.Errors);
    }

    [Fact]
    public async Task All_blueprints_together_load_in_one_plugin_and_the_starter_of_a_new_plugin_loads()
    {
        using var h = new Harness();
        // The ones that register an admin section can only be there once per plugin; the rest combine freely.
        var combinable = PluginBlueprints.All.Where(b => b.Id is not ("page" or "strings" or "adminpage")).Select(b => b.Code);
        h.AddScript("all", string.Join("\n", combinable));
        h.AddScript("starter", MatCMS.Pages.Admin.Plugins.CreateModel.Starter);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        Assert.True(h.Registry.IsLoaded("all"));
        Assert.True(h.Registry.IsLoaded("starter"));
        Assert.Single(h.Registry.Sections, s => s.Owner == "starter");
    }

    [Fact]
    public void Every_blueprint_has_a_marker_that_tells_the_editor_it_is_there_already()
    {
        foreach (var b in PluginBlueprints.All) Assert.Contains(b.Marker, b.Code);
        Assert.Equal(PluginBlueprints.All.Count, PluginBlueprints.All.Select(b => b.Marker).Distinct().Count());
    }
}
