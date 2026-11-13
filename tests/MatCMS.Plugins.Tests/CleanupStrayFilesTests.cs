using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>"Aufräumen" also finds files in the upload folder that no media row owns and no text names — another site's
/// photos that rode along in a backup from a shared workbench — and deletes only those, only after a fresh check.</summary>
public class CleanupStrayFilesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public CleanupStrayFilesTests(AppFactory app) => _app = app;

    [Fact]
    public async Task Foreign_files_in_the_upload_folder_are_offered_and_deleted_named_ones_stay()
    {
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var uploads = StoragePaths.Uploads(sp.GetRequiredService<IWebHostEnvironment>());
        Directory.CreateDirectory(uploads);
        File.WriteAllText(Path.Combine(uploads, "fremd-logo.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(uploads, "im-text.png"), "x");
        File.WriteAllText(Path.Combine(uploads, "eigenes.jpg"), "x");
        var db = sp.GetRequiredService<AppDbContext>();
        db.SiteSettings.Add(new SiteSetting { Key = "test.strayRef", Value = "/uploads/im-text.png" });
        db.Media.Add(new Media { Url = "/uploads/eigenes.jpg", FileName = "eigenes.jpg", ContentType = "image/jpeg" });
        await db.SaveChangesAsync();

        var cleanup = sp.GetRequiredService<CleanupService>();
        var names = (await cleanup.AnalyzeAsync()).StrayFiles.Select(f => f.Name).ToList();
        Assert.Contains("fremd-logo.svg", names);
        Assert.DoesNotContain("im-text.png", names);                      // a setting names it
        Assert.DoesNotContain("eigenes.jpg", names);                      // a media row owns it (unused media is the other list)

        Assert.Equal(1, await cleanup.DeleteStrayFilesAsync(["fremd-logo.svg", "im-text.png", "../appdata.db"]));
        Assert.False(File.Exists(Path.Combine(uploads, "fremd-logo.svg")));
        Assert.True(File.Exists(Path.Combine(uploads, "im-text.png")));
    }
}
