using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>
/// The store's publisher ("Herausgeber") and the public catalogue's pictures: the publisher belongs to the store ENTRY (a new bundle keeps
/// it, a bundle only suggests one while the entry has none), only http(s) addresses survive, and the public catalogue names each picture
/// by an address that is only answered for the plugin it belongs to.
/// </summary>
public sealed class StorePublisherTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly StoreService _store;

    public StorePublisherTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _store = new StoreService(_db, new ProfileService(_db, new SecretProtector(new EphemeralDataProtectionProvider())));
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static byte[] Bundle(string key, string version, string? publisher = null, string? url = null)
    {
        var manifest = new JsonObject { ["Format"] = 1, ["Key"] = key, ["Name"] = key, ["Version"] = version, ["Description"] = "d", ["Code"] = "// x" };
        if (publisher is not null) manifest["Publisher"] = publisher;
        if (url is not null) manifest["PublisherUrl"] = url;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("plugin.json").Open(), Encoding.UTF8);
            w.Write(manifest.ToJsonString());
        }
        return ms.ToArray();
    }

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task The_publisher_belongs_to_the_store_entry_and_a_bundle_only_suggests_one()
    {
        await _store.UpsertPluginAsync(Bundle("forum", "1.0", "Someone Else", "https://example.org"));
        var row = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal(("Someone Else", "https://example.org/"), (row.Publisher, row.PublisherUrl));      // empty entry: the bundle's suggestion

        Assert.True(await _store.SetPluginPublisherAsync("forum", "  MATTHIX\n ", "https://matthix.de"));
        await _store.UpsertPluginAsync(Bundle("forum", "1.1", "Someone Else"));                         // a new bundle keeps the store's word
        row = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal(("MATTHIX", "https://matthix.de/", "1.1"), (row.Publisher, row.PublisherUrl, row.Version));

        Assert.True(await _store.SetPluginPublisherAsync("forum", "MATTHIX", "javascript:alert(1)"));    // only http(s) is linked
        row = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal("", row.PublisherUrl);
        Assert.True(await _store.SetPluginPublisherAsync("forum", "", "https://matthix.de"));            // empty name = none
        row = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal(("", ""), (row.Publisher, row.PublisherUrl));
        Assert.False(await _store.SetPluginPublisherAsync("nope", "x", null));
    }

    [Fact]
    public async Task The_public_catalogue_names_publisher_and_pictures_and_a_picture_answers_only_for_its_plugin()
    {
        await _store.UpsertPluginAsync(Bundle("forum", "1.3"));
        await _store.UpsertPluginAsync(Bundle("verein", "1.13"));
        await _store.SetPluginPublisherAsync("forum", "MATTHIX", "https://matthix.de");
        var (id, error) = await _store.AddPluginImageAsync("forum", Png, "Übersicht");
        Assert.Null(error);

        var catalog = await _store.PublicCatalogAsync();
        var forum = catalog["plugins"]!.AsArray().OfType<JsonObject>().Single(p => (string?)p["key"] == "forum");
        Assert.Equal("MATTHIX", (string?)forum["publisher"]);
        Assert.Equal("https://matthix.de/", (string?)forum["publisherUrl"]);
        var pic = forum["images"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal(id, (int?)pic["id"]);
        Assert.Equal("Übersicht", (string?)pic["caption"]);
        Assert.Equal($"/api/public/store/plugins/forum/image/{id}", (string?)pic["url"]);
        var verein = catalog["plugins"]!.AsArray().OfType<JsonObject>().Single(p => (string?)p["key"] == "verein");
        Assert.Equal("", (string?)verein["publisher"]);
        Assert.Empty(verein["images"]!.AsArray());

        Assert.NotNull(await _store.GetPublicPluginImageAsync("forum", id!.Value));
        Assert.Null(await _store.GetPublicPluginImageAsync("verein", id.Value));      // an id alone does not walk through the table

        var instanceCatalog = await _store.CatalogAsync();                          // the sites' store dialog gets the publisher too
        Assert.Equal("MATTHIX", instanceCatalog.Plugins.Single(p => p.Key == "forum").Publisher);
    }
}
