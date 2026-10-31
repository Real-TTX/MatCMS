using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The cloud's ONE way to decide <c>plugins.allowNative</c> on a site: the dedicated <see cref="InstanceConfig.NativePlugins"/> field.
/// The generic ways — a free settings row of the profile — must still not reach it, or a package could bring compiled code and the
/// permission to run it at once.
/// </summary>
public class NativePluginsPolicyTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public NativePluginsPolicyTests(AppFactory app) => _app = app;

    private static readonly Func<string, CancellationToken, Task<byte[]?>> NoBundles = (_, _) => Task.FromResult<byte[]?>(null);

    private async Task<string?> AllowedAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKeys.AllowNativePlugins))?.Value;
    }

    private async Task<CloudSyncService.SyncResult> ApplyAsync(InstanceConfig config, bool preview = false)
    {
        using var scope = _app.Services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<CloudSyncService>();
        return preview ? await sync.PreviewAsync(config) : await sync.ApplyAsync(config, NoBundles);
    }

    [Fact]
    public async Task The_dedicated_field_allows_and_forbids_but_a_free_settings_row_never_does()
    {
        _app.CreateClient();   // starts the application (database, migrations)
        var revision = 100;

        // A free settings row carrying the key is refused, as before.
        await ApplyAsync(new InstanceConfig { Revision = ++revision, Settings = new() { [SettingKeys.AllowNativePlugins] = "1" } });
        Assert.NotEqual("1", await AllowedAsync());

        // A preview reports the change and writes nothing.
        var preview = await ApplyAsync(new InstanceConfig { Revision = ++revision, NativePlugins = "allow" }, preview: true);
        Assert.Contains(preview.Report, r => r.Id == SettingKeys.AllowNativePlugins && r.Outcome is "installed" or "updated");
        Assert.NotEqual("1", await AllowedAsync());

        // The field does it.
        var on = await ApplyAsync(new InstanceConfig { Revision = ++revision, NativePlugins = "allow" });
        Assert.True(on.Ok, on.Error);
        Assert.Equal("1", await AllowedAsync());

        // Empty / unknown = leave it alone.
        await ApplyAsync(new InstanceConfig { Revision = ++revision, NativePlugins = null });
        await ApplyAsync(new InstanceConfig { Revision = ++revision, NativePlugins = "maybe" });
        Assert.Equal("1", await AllowedAsync());

        // And off means off.
        await ApplyAsync(new InstanceConfig { Revision = ++revision, NativePlugins = "deny" });
        Assert.Equal("0", await AllowedAsync());
    }

    [Theory]
    [InlineData("allow", "allow"), InlineData("ALLOW", "allow"), InlineData("true", "allow"), InlineData("deny", "deny"), InlineData("off", "deny"),
     InlineData("", ""), InlineData(null, ""), InlineData("maybe", "")]
    public void Wire_values_are_normalised_and_never_guessed(string? given, string expected) =>
        Assert.Equal(expected, NativePluginsPolicy.Normalize(given));
}
