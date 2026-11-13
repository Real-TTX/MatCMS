using MatCMS.Data;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The starter templates and the example component are offered ONCE: a site that deletes them keeps them deleted
/// through every later start, and a site from before the marker gets its marks without anything being added twice.</summary>
public class StarterContentTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public StarterContentTests(AppFactory app) => _app = app;

    private async Task<(string[] templates, bool component, string offered)> StateAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Templates.Select(t => t.Name).ToArrayAsync(),
                await db.Components.AnyAsync(c => c.Type == "cta-box"),
                (await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == "seed.offered"))?.Value ?? "");
    }

    [Fact]
    public async Task Deleted_starter_templates_and_the_example_component_stay_deleted()
    {
        _ = _app.Services;                                                   // start the app: the seeder runs
        var (templates, component, offered) = await StateAsync();
        Assert.Contains("MatBusiness", templates);
        Assert.True(component);
        Assert.Contains("template:MatBusiness", offered);
        Assert.Contains("component:cta-box", offered);

        // A site from before the marker: the items are there, the marker is not — the next start writes it and adds nothing.
        byte[] oldBackup;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key == "seed.offered"));
            await db.SaveChangesAsync();
            oldBackup = await scope.ServiceProvider.GetRequiredService<ContentTransferService>().ExportAsync(new ContentTransferService.BackupOptions());
        }
        await DbSeeder.SeedAsync(_app.Services);
        (templates, component, offered) = await StateAsync();
        Assert.Single(templates, t => t == "MatBusiness");
        Assert.Contains("template:MatArt", offered);

        // A backup from before the marker is restored (it brings its templates, as a restore should) and the operator
        // clears them out — they do not come back: the restore kept this site's marks.
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ContentTransferService>().ImportAsync(oldBackup);
        Assert.Contains("template:MatBusiness", (await StateAsync()).offered);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Templates.RemoveRange(db.Templates.Where(t => !t.IsActive));
            db.Components.RemoveRange(db.Components.Where(c => c.Type == "cta-box"));
            await db.SaveChangesAsync();
        }
        await DbSeeder.SeedAsync(_app.Services);
        await DbSeeder.SeedAsync(_app.Services);
        (templates, component, _) = await StateAsync();
        Assert.DoesNotContain("MatBusiness", templates);
        Assert.DoesNotContain("MatCMS Modern", templates);
        Assert.False(component);
    }
}
