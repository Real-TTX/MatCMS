using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>
/// The cloud takes plugins over from the MatCMS Store (<see cref="MatcmsStoreService"/>): only with a valid signature of exactly the downloaded
/// bytes, never over an own entry with the same key, with publisher and pictures, and the list says where this cloud stands with each entry.
/// The service's seams are static, so this class is its own collection (never in parallel with itself).
/// </summary>
[Collection(nameof(MatcmsStoreTakeOverTests))]
public sealed class MatcmsStoreTakeOverTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly StoreService _store;
    private readonly MatcmsStoreService _matcms;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;
    private readonly FakeStore _server = new();

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public MatcmsStoreTakeOverTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _store = new StoreService(_db, new ProfileService(_db, new SecretProtector(new EphemeralDataProtectionProvider())));
        _matcms = new MatcmsStoreService(_db, _store, NullLogger<MatcmsStoreService>.Instance);
        var spki = _key.ExportSubjectPublicKeyInfo();
        _keyId = StoreSignature.KeyId(spki);
        MatcmsStoreService.KeysOverride = new Dictionary<string, string> { [_keyId] = Convert.ToBase64String(spki) };
        MatcmsStoreService.HandlerFactory = () => _server;
    }

    public void Dispose()
    {
        MatcmsStoreService.KeysOverride = null;
        MatcmsStoreService.HandlerFactory = null;
        _db.Dispose(); _conn.Dispose(); _key.Dispose();
    }

    private static byte[] Bundle(string key, string version)
    {
        var manifest = new JsonObject { ["Format"] = 1, ["Key"] = key, ["Name"] = "Forum " + key, ["Version"] = version, ["Description"] = "d", ["Code"] = "// x" };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("plugin.json").Open(), Encoding.UTF8);
            w.Write(manifest.ToJsonString());
        }
        return ms.ToArray();
    }

    /// <summary>Publishes one version in the fake store; <paramref name="tamper"/> serves other bytes than the signed ones.</summary>
    private void Publish(string key, string version, bool tamper = false, string? keyId = null)
    {
        var bytes = Bundle(key, version);
        var sha = StoreSignature.Sha256Hex(bytes);
        var sig = StoreSignature.Sign(StoreSignature.Text("script", key, version, sha), _key);
        _server.Files[$"/store/api/plugins/{key}/{version}/download"] = tamper ? Bundle(key, version + ".1") : bytes;
        _server.Files[$"/store/api/plugins/{key}/image/1"] = Png;
        _server.Plugins[key] = new JsonObject
        {
            ["key"] = key, ["name"] = "Forum " + key, ["version"] = version, ["kind"] = "script", ["summary"] = "s",
            ["publisher"] = "MATTHIX", ["publisherUrl"] = "https://matthix.de", ["publisherVerified"] = true,
            ["sha256"] = sha, ["signature"] = sig, ["keyId"] = keyId ?? _keyId,
            ["download"] = $"/store/api/plugins/{key}/{version}/download",
            ["images"] = new JsonArray(new JsonObject { ["url"] = $"/store/api/plugins/{key}/image/1", ["caption"] = "Übersicht" },
                                       new JsonObject { ["url"] = "https://evil.example/x.png", ["caption"] = "fremd" }),
        };
    }

    [Fact]
    public async Task A_signed_plugin_is_taken_over_with_publisher_and_pictures_and_an_update_replaces_it()
    {
        Publish("forum", "1.0");
        var list = (await _matcms.ListAsync(fresh: true)).Entries;
        Assert.Equal("new", Assert.Single(list).State);

        var r = await _matcms.ImportAsync("forum");
        Assert.True(r.Ok, r.Message);
        var row = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal(("1.0", MatcmsStoreService.Origin, _keyId, "MATTHIX"), (row.Version, row.Origin, row.SignatureKeyId, row.Publisher));
        Assert.NotEmpty(row.Signature);
        var images = await _store.ListPluginImagesAsync("forum");
        Assert.Equal("Übersicht", Assert.Single(images!).Caption);                       // the address on another host was not followed
        Assert.Equal("current", (await _matcms.ListAsync(fresh: true)).Entries.Single().State);

        Publish("forum", "1.1");
        Assert.Equal("update", (await _matcms.ListAsync(fresh: true)).Entries.Single().State);
        Assert.True((await _matcms.ImportAsync("forum")).Ok);
        Assert.Equal("1.1", (await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum")).Version);
        Assert.Single((await _store.ListPluginImagesAsync("forum"))!);                    // pictures replaced, not doubled
    }

    [Fact]
    public async Task Other_bytes_than_the_signed_ones_or_an_unknown_key_are_refused_and_nothing_is_stored()
    {
        Publish("forum", "1.0", tamper: true);
        var r = await _matcms.ImportAsync("forum");
        Assert.False(r.Ok);
        Assert.Contains("Signatur", r.Message);

        Publish("verein", "1.0", keyId: "0000000000000000");
        Assert.False((await _matcms.ImportAsync("verein")).Ok);
        Assert.False(await _db.StorePlugins.AnyAsync());
    }

    [Fact]
    public async Task An_own_entry_with_the_same_key_is_never_overwritten_and_an_own_upload_makes_a_taken_over_entry_own()
    {
        await _store.UpsertPluginAsync(Bundle("forum", "0.9"));
        Publish("forum", "1.0");
        Assert.Equal("own", (await _matcms.ListAsync(fresh: true)).Entries.Single().State);
        var r = await _matcms.ImportAsync("forum");
        Assert.False(r.Ok);
        var own = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal("0.9", own.Version);
        Assert.False((await _matcms.ImportAsync("gibtsnicht")).Ok);

        // Only when asked in that very request: the SAME row (profiles that selected it keep it) becomes the signed store version.
        Assert.True((await _matcms.ImportAsync("forum", replaceOwn: true)).Ok);
        var replaced = await _db.StorePlugins.AsNoTracking().SingleAsync(p => p.Key == "forum");
        Assert.Equal((own.Id, "1.0", MatcmsStoreService.Origin), (replaced.Id, replaced.Version, replaced.Origin));
        Assert.Equal("current", (await _matcms.ListAsync(fresh: true)).Entries.Single().State);
    }

    [Fact]
    public async Task An_unreachable_store_is_a_message_not_an_exception()
    {
        _server.Down = true;
        var (entries, error) = await _matcms.ListAsync(fresh: true);
        Assert.Empty(entries);
        Assert.NotNull(error);
        Assert.False((await _matcms.ImportAsync("forum")).Ok);
    }

    private sealed class FakeStore : HttpMessageHandler
    {
        public readonly Dictionary<string, JsonObject> Plugins = new();
        public readonly Dictionary<string, byte[]> Files = new();
        public bool Down;

        // The service disposes its client (and with it the handler) after every call; this one stands for the whole test.
        protected override void Dispose(bool disposing) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("down");
            var uri = request.RequestUri!;
            if (uri.Host != "matcms.de") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (uri.AbsolutePath == "/store/api/catalog")
            {
                var json = new JsonObject { ["plugins"] = new JsonArray(Plugins.Values.Select(p => (JsonNode)p.DeepClone()).ToArray()) };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(Files.TryGetValue(uri.AbsolutePath, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
