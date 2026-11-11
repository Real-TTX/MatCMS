using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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
/// A site installs from the MatCMS Store itself (<see cref="MatcmsStoreClient"/>, docs/official-store.md S3): only signed packages, through
/// the store dialog's own tab, switched off on arrival; a site without a cloud decides by its own switch; a package that came through the
/// cloud's store under the MatCMS Store's name must carry a signature that fits it. The client's seams are static: own collection.
/// </summary>
[Collection(nameof(MatcmsStoreClientTests))]
public sealed class MatcmsStoreClientTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;
    private readonly Dictionary<string, string> _keys;
    private readonly FakeStore _server = new();

    public MatcmsStoreClientTests()
    {
        var spki = _key.ExportSubjectPublicKeyInfo();
        _keyId = StoreSignature.KeyId(spki);
        _keys = new Dictionary<string, string> { [_keyId] = Convert.ToBase64String(spki) };
        MatcmsStoreClient.KeysOverride = _keys;
        MatcmsStoreClient.HandlerFactory = () => _server;
        MatcmsStoreClient.BaseUrl = "https://store.test";
    }

    public void Dispose()
    {
        MatcmsStoreClient.KeysOverride = null;
        MatcmsStoreClient.HandlerFactory = null;
        MatcmsStoreClient.BaseUrl = MatcmsStoreClient.DefaultBaseUrl;
        _key.Dispose();
    }

    private static byte[] Bundle(string key, string version)
    {
        var manifest = new JsonObject { ["Format"] = 1, ["Key"] = key, ["Name"] = "Gästebuch " + key, ["Version"] = version, ["Description"] = "d",
            ["Code"] = "// nothing to run" };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry(PluginBundle.ManifestEntry).Open(), Encoding.UTF8);
            w.Write(manifest.ToJsonString());
        }
        return ms.ToArray();
    }

    private string Sign(byte[] bytes, string key, string version) =>
        StoreSignature.Sign(StoreSignature.Text("script", key, version, StoreSignature.Sha256Hex(bytes)), _key);

    private void Publish(string key, string version, bool tamper = false)
    {
        var bytes = Bundle(key, version);
        _server.Files[$"/store/api/plugins/{key}/{version}/bundle"] = tamper ? Bundle(key, version + "9") : bytes;
        _server.Files[$"/store/api/plugins/{key}/images/7"] = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        _server.Plugins[key] = new JsonObject
        {
            ["key"] = key, ["name"] = "Gästebuch " + key, ["version"] = version, ["kind"] = "script", ["summary"] = "Ein Gästebuch",
            ["publisher"] = "MATTHIX", ["publisherVerified"] = true, ["sha256"] = StoreSignature.Sha256Hex(bytes),
            ["signature"] = Sign(bytes, key, version), ["keyId"] = _keyId, ["download"] = $"/store/api/plugins/{key}/{version}/bundle",
            ["images"] = new JsonArray(new JsonObject { ["id"] = 7, ["caption"] = "Übersicht", ["url"] = $"/store/api/plugins/{key}/images/7" })
        };
    }

    [Fact]
    public async Task A_site_without_a_cloud_browses_and_installs_signed_plugins_switched_off_and_its_own_switch_closes_the_store()
    {
        using var app = new AppFactory();
        var c = app.Browser();
        await AppFactory.LoginAsync(c);
        Publish("gaestebuch", "1.0");
        Publish("boese", "1.0", tamper: true);

        var page = await c.GetStringAsync("/admin/plugins?browse=matcms");
        Assert.Contains("Gästebuch gaestebuch", WebUtility.HtmlDecode(page));
        Assert.Contains("handler=MatcmsImage", page);                                             // pictures through the site, not from the store
        var token = AppFactory.Token(page)!;

        var res = await AppFactory.PostAsync(c, "/admin/plugins?handler=InstallFromMatcms&key=gaestebuch", new() { ["__RequestVerificationToken"] = token });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        res = await AppFactory.PostAsync(c, "/admin/plugins?handler=InstallFromMatcms&key=boese", new() { ["__RequestVerificationToken"] = token });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var installed = await db.Plugins.AsNoTracking().SingleAsync(p => p.Key == "gaestebuch");
            Assert.False(installed.Enabled);
            Assert.Equal("1.0", installed.Version);
            Assert.False(await db.Plugins.AnyAsync(p => p.Key == "boese"));                        // other bytes than the signed ones
        }

        // A newer version: the background check notices it, the list and the dashboard say so (from memory), "Aktualisieren" updates in place.
        Publish("gaestebuch", "1.1");
        using (var scope = app.Services.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<MatcmsStoreClient>();
            await client.RefreshUpdatesAsync();
            Assert.Equal("1.1", (await client.UpdatesAsync())["gaestebuch"]);
        }
        var list = await c.GetStringAsync("/admin/plugins");
        Assert.Contains("Update 1.1 i", list);
        Assert.Contains("pluginUpdates", await c.GetStringAsync("/admin/attention"));
        res = await AppFactory.PostAsync(c, "/admin/plugins?handler=InstallFromMatcms&key=gaestebuch&back=list", new() { ["__RequestVerificationToken"] = token });
        Assert.Equal("/admin/plugins", res.Headers.Location?.OriginalString.ToLowerInvariant());
        using (var scope = app.Services.CreateScope())
        {
            Assert.Equal("1.1", (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.AsNoTracking().SingleAsync(p => p.Key == "gaestebuch")).Version);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MatcmsStoreClient>().UpdatesAsync());
        }
        Assert.DoesNotContain("Update 1.1 i", await c.GetStringAsync("/admin/plugins"));

        // The site's own switch: off, the dialog does not open and nothing installs.
        res = await AppFactory.PostAsync(c, "/admin/plugins?handler=MatcmsStore", new() { ["__RequestVerificationToken"] = token });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.DoesNotContain("store-dialog", await c.GetStringAsync("/admin/plugins?browse=matcms"));
        using (var scope = app.Services.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<MatcmsStoreClient>();
            Assert.False((await client.AccessAsync()).Allowed);
            Assert.False((await client.InstallAsync("gaestebuch")).Ok);
        }
    }

    [Fact]
    public void A_cloud_package_under_the_stores_name_must_carry_a_fitting_signature()
    {
        var bytes = Bundle("forum", "1.3");
        var sig = Sign(bytes, "forum", "1.3");
        Assert.Null(MatcmsStoreClient.CheckSignedPackage(bytes, "forum", "matcms", sig, _keyId, _keys));
        Assert.NotNull(MatcmsStoreClient.CheckSignedPackage(Bundle("forum", "1.4"), "forum", "matcms", sig, _keyId, _keys));         // other bytes
        Assert.NotNull(MatcmsStoreClient.CheckSignedPackage(bytes, "forum", "matcms", sig, _keyId, new Dictionary<string, string>())); // not a key of this image
        Assert.NotNull(MatcmsStoreClient.CheckSignedPackage(bytes, "forum", "matcms", "", "", _keys));                               // store's name, no signature
        Assert.NotNull(MatcmsStoreClient.CheckSignedPackage(bytes, "shop", "matcms", sig, _keyId, _keys));                           // signed, but another plugin
        Assert.Null(MatcmsStoreClient.CheckSignedPackage(bytes, "forum", "", "", "", _keys));                                        // the cloud's own entry: nothing to check
    }

    [Fact]
    public async Task The_profile_rollout_checks_a_store_plugins_signature_too()
    {
        using var app = new AppFactory();
        app.CreateClient();
        var good = Bundle("gaestebuch", "1.0");
        var sig = Sign(good, "gaestebuch", "1.0");
        InstanceConfig Config(int rev) => new()
        {
            Revision = rev, PluginsMode = "keep",
            Plugins = [new ConfigPlugin { Key = "gaestebuch", Name = "G", Version = "1.0", Origin = "matcms", Signature = sig, SignatureKeyId = _keyId }]
        };

        // Other bytes under the store's signature: refused, reported, nothing installed.
        using (var scope = app.Services.CreateScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<CloudSyncService>()
                .ApplyAsync(Config(1), (_, _) => Task.FromResult<byte[]?>(Bundle("gaestebuch", "1.0-evil")));
            Assert.False(r.Ok);
            Assert.Contains(r.Report, x => x.Id == "gaestebuch" && x.Outcome == "failed");
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.AnyAsync(p => p.Key == "gaestebuch"));
        }
        // The signed bytes go through.
        using (var scope = app.Services.CreateScope())
        {
            var r = await scope.ServiceProvider.GetRequiredService<CloudSyncService>()
                .ApplyAsync(Config(2), (_, _) => Task.FromResult<byte[]?>(good));
            Assert.True(r.Ok, r.Error);
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Plugins.AnyAsync(p => p.Key == "gaestebuch"));
        }
    }

    [Fact]
    public void Both_switches_are_protected_against_every_generic_writer()
    {
        Assert.Contains(SettingKeys.MatcmsStore, SettingKeys.Protected);
        Assert.Contains(SettingKeys.MatcmsStoreCloud, SettingKeys.Protected);
        Assert.True(SettingKeys.IsProtected(SettingKeys.MatcmsStoreCloud));
    }

    private sealed class FakeStore : HttpMessageHandler
    {
        public readonly Dictionary<string, JsonObject> Plugins = new();
        public readonly Dictionary<string, byte[]> Files = new();

        protected override void Dispose(bool disposing) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host != "store.test") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (uri.AbsolutePath == "/store/api/catalog")
            {
                var json = new JsonObject { ["format"] = 1, ["plugins"] = new JsonArray(Plugins.Values.Select(p => (JsonNode)p.DeepClone()).ToArray()) };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") });
            }
            if (!Files.TryGetValue(uri.AbsolutePath, out var b)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var content = new ByteArrayContent(b);
            if (uri.AbsolutePath.Contains("/images/")) content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
