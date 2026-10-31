using System.Net;
using System.Runtime.CompilerServices;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// "Off means gone" through the WHOLE application: a native plugin that answered visitors over HTTP — its page drawn inside the site layout, its
/// admin page inside the back office — must still be freed when it is unloaded. PluginSystemTests unload without a web request; this is the path
/// a real site takes.
/// </summary>
public class PluginHttpUnloadTests
{
    private static async Task InstallRefAsync(AppFactory app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = "1" });
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var src = StoragePaths.PluginSrc(env, "ref-native", "1.0");
        Directory.CreateDirectory(src);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "refplugin")))
            File.Copy(file, Path.Combine(src, Path.GetFileName(file)), overwrite: true);
        var row = new Models.Plugin
        {
            Key = "ref-native", Name = "Ref", Version = "1.0", Kind = PluginKind.Native, Entry = "RefPlugin.dll", Enabled = true,
            Sha256 = NativePluginHost.HashOf(Path.Combine(src, "RefPlugin.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
        };
        db.Plugins.Add(row);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
        Assert.True(scope.ServiceProvider.GetRequiredService<PluginRegistry>().IsLoaded("ref-native"));
    }

    /// <summary>
    /// The path a site takes: visitors open the plugin's pages and pages of the site (whose layout asks every plugin), then the administrator switches
    /// the plugin off in Admin → Plugins — a request of its own. The load context must be freed by that, with no "Neustart nötig" in the plugin's log.
    /// <para>Why not unload right after the visitor's request in the test's own code: the test server finishes a request and then runs the CALLER's code
    /// on the same thread, with the request's frames still below it on the stack — they hold the plugin until they unwind, which looked like a leak
    /// (a heap dump showed no reference left once they had). On a real server the unload is another request on another stack.</para>
    /// </summary>
    [Fact]
    public async Task A_native_plugin_that_served_pages_is_freed_when_the_admin_switches_it_off()
    {
        using var app = new AppFactory();
        await InstallRefAsync(app);
        var visitor = app.Browser();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/ref-native")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/ref-native/api/5")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/")).StatusCode);

        var admin = app.Browser();
        await AppFactory.LoginAsync(admin);
        int id;
        using (var scope = app.Services.CreateScope()) id = scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.Single(p => p.Key == "ref-native").Id;
        var page = await admin.GetStringAsync("/admin/plugins");
        var off = await admin.PostAsync("/admin/plugins?handler=Toggle&id=" + id,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)! }));
        Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            var host = scope.ServiceProvider.GetRequiredService<NativePluginHost>();
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.False(host.IsLoaded("ref-native"));
            Assert.DoesNotContain(registry.LogOf("ref-native"), l => l.Contains("Neustart") || l.Contains("Speicher"));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/ref-native")).StatusCode);
    }

}
