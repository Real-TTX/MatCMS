using MatCMS.Services;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// An automated update (cloud rollout, cloud store, MatCMS Store) keeps a plugin's on/off; a hand upload and a NEW plugin still arrive off.
/// Found on 2026-11-11: a profile rollout of Buchungen 0.14 switched the plugin off on two live sites.
/// </summary>
public class PluginUpdateStateTests
{
    private static async Task<Models.Plugin> ImportAgain(Harness h, Models.Plugin p, bool keep)
    {
        var bundle = PluginPackager.Export(p, h.Env);
        var (plugin, updated, error) = await PluginPackager.ImportAsync(new MemoryStream(bundle), h.Env, h.Db, keepStateOnUpdate: keep);
        Assert.Null(error);
        Assert.True(updated);
        return plugin!;
    }

    [Fact]
    public async Task An_automated_update_keeps_a_running_plugin_on()
    {
        using var h = new Harness();
        var p = h.AddScript("upd-on", Harness.FixtureScript());
        Assert.True((await ImportAgain(h, p, keep: true)).Enabled);
    }

    [Fact]
    public async Task An_automated_update_does_not_switch_on_what_was_off()
    {
        using var h = new Harness();
        var p = h.AddScript("upd-off", Harness.FixtureScript());
        p.Enabled = false;
        h.Db.SaveChanges();
        Assert.False((await ImportAgain(h, p, keep: true)).Enabled);
    }

    [Fact]
    public async Task A_hand_upload_still_switches_an_updated_plugin_off()
    {
        using var h = new Harness();
        var p = h.AddScript("upd-hand", Harness.FixtureScript());
        Assert.False((await ImportAgain(h, p, keep: false)).Enabled);
    }

    [Fact]
    public async Task A_new_plugin_arrives_off_even_from_an_automated_way()
    {
        using var h = new Harness();
        var p = h.AddScript("upd-new", Harness.FixtureScript());
        var bundle = PluginPackager.Export(p, h.Env);
        h.Db.Plugins.Remove(p);
        h.Db.SaveChanges();
        var (plugin, updated, error) = await PluginPackager.ImportAsync(new MemoryStream(bundle), h.Env, h.Db, keepStateOnUpdate: true);
        Assert.Null(error);
        Assert.False(updated);
        Assert.False(plugin!.Enabled);
    }
}
