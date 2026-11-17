using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// A plugin UPDATE through the cloud's profile rollout runs the new version at once. The import alone wrote the new code and left the old one
/// running until the next restart — Shop 2.2 reported "applied" on two live sites and kept serving 2.1.
/// </summary>
public class PluginRolloutReloadTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public PluginRolloutReloadTests(AppFactory app) { _app = app; _app.CreateClient(); }

    private static byte[] Bundle(string version) => BundleOf("rollreload", version,
        $"Plugin.Route(\"/rollreload\", ctx => PluginResult.Text(\"version {version}\"));");

    private static byte[] BundleOf(string key, string version, string code)
    {
        var manifest = new JsonObject { ["Format"] = 1, ["Key"] = key, ["Name"] = "Roll " + key, ["Version"] = version, ["Description"] = "d", ["Code"] = code };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry(PluginBundle.ManifestEntry).Open(), Encoding.UTF8);
            w.Write(manifest.ToJsonString());
        }
        return ms.ToArray();
    }

    private async Task RollOutAsync(int revision, string version)
    {
        using var scope = _app.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<CloudSyncService>().ApplyAsync(
            new InstanceConfig { Revision = revision, PluginsMode = "keep", Plugins = [new ConfigPlugin { Key = "rollreload", Name = "R", Version = version }] },
            (_, _) => Task.FromResult<byte[]?>(Bundle(version)));
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public async Task An_update_through_the_rollout_runs_at_once_and_keeps_the_plugin_on()
    {
        await RollOutAsync(1, "1.0");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Plugins.SingleAsync(p => p.Key == "rollreload");
            Assert.False(row.Enabled);                                                  // new = off, somebody on the site switches it on
            row.Enabled = true;
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        }
        var c = _app.Browser();
        Assert.Equal("version 1.0", await c.GetStringAsync("/rollreload"));

        await RollOutAsync(2, "1.1");
        Assert.Equal("version 1.1", await c.GetStringAsync("/rollreload"));              // no restart needed
        using (var scope = _app.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.AsNoTracking().SingleAsync(p => p.Key == "rollreload")).Enabled);
    }
}
