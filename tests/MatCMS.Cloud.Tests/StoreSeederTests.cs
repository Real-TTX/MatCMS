using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>
/// The store's starter set is offered ONCE per entry. It used to be "add when missing", which put every deleted starter entry back on
/// the next start — the test plugin "Wartungsfenster" returned with each cloud update after the operator had removed it.
/// </summary>
public sealed class StoreSeederTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;

    public StoreSeederTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private async Task RestartAsync()
    {
        _db.ChangeTracker.Clear();
        await StoreSeeder.SeedAsync(_db);
    }

    [Fact]
    public async Task A_deleted_or_changed_starter_entry_stays_as_the_operator_left_it_after_the_next_start()
    {
        await StoreSeeder.SeedAsync(_db);
        Assert.True(await _db.StorePlugins.AnyAsync(p => p.Key == "wartungsfenster"));
        Assert.True(await _db.StoreComponents.AnyAsync(c => c.Type == "image-text"));
        Assert.True(await _db.StoreTemplates.AnyAsync(t => t.Name == "Küste"));

        _db.StorePlugins.Remove(await _db.StorePlugins.SingleAsync(p => p.Key == "wartungsfenster"));
        _db.StoreComponents.Remove(await _db.StoreComponents.SingleAsync(c => c.Type == "quote-card"));
        _db.StoreTemplates.Remove(await _db.StoreTemplates.SingleAsync(t => t.Name == "Vanta"));
        (await _db.StoreComponents.SingleAsync(c => c.Type == "image-text")).Description = "Eigener Text des Betreibers.";
        await _db.SaveChangesAsync();

        await RestartAsync();
        await RestartAsync();

        Assert.False(await _db.StorePlugins.AnyAsync(p => p.Key == "wartungsfenster"));
        Assert.False(await _db.StoreComponents.AnyAsync(c => c.Type == "quote-card"));
        Assert.False(await _db.StoreTemplates.AnyAsync(t => t.Name == "Vanta"));
        Assert.Equal("Eigener Text des Betreibers.", (await _db.StoreComponents.SingleAsync(c => c.Type == "image-text")).Description);
        Assert.Equal(1, await _db.StoreTemplates.CountAsync(t => t.Name == "Küste"));
    }

    [Fact]
    public async Task A_cloud_seeded_before_the_marker_existed_gets_its_entries_marked_without_duplicates_and_deletions_stick_from_then_on()
    {
        await StoreSeeder.SeedAsync(_db);
        var counts = (await _db.StorePlugins.CountAsync(), await _db.StoreComponents.CountAsync(), await _db.StoreTemplates.CountAsync());
        // A cloud from before: the entries are there, the bookkeeping is not.
        _db.CloudSettings.Remove(await _db.CloudSettings.SingleAsync(s => s.Key == SettingKeys.StoreSeeded));
        await _db.SaveChangesAsync();

        await RestartAsync();
        Assert.Equal(counts, (await _db.StorePlugins.CountAsync(), await _db.StoreComponents.CountAsync(), await _db.StoreTemplates.CountAsync()));
        var marked = (await _db.CloudSettings.SingleAsync(s => s.Key == SettingKeys.StoreSeeded)).Value!.Split('\n');
        Assert.Contains("plugin:wartungsfenster", marked);
        Assert.Contains("component:image-text", marked);
        Assert.Contains("template:Küste", marked);

        _db.StorePlugins.Remove(await _db.StorePlugins.SingleAsync(p => p.Key == "wartungsfenster"));
        await _db.SaveChangesAsync();
        await RestartAsync();
        Assert.False(await _db.StorePlugins.AnyAsync(p => p.Key == "wartungsfenster"));
    }
}
