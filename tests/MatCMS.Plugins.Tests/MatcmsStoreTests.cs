using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using MatCMS.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The whole application with the MatCMS Store server (plugins/matcms-store) loaded as a native plugin, and a signing key for the tests.</summary>
public sealed class StoreServerApp : IDisposable
{
    public AppFactory App { get; } = new();
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string KeyId { get; }
    public string PublicKey { get; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _installed;

    public StoreServerApp()
    {
        var spki = Key.ExportSubjectPublicKeyInfo();
        KeyId = StoreSignature.KeyId(spki);
        PublicKey = Convert.ToBase64String(spki);
    }

    public IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string> { [KeyId] = PublicKey };

    public async Task InstallAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_installed) return;
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!db.SiteSettings.Any(s => s.Key == SettingKeys.AllowNativePlugins)) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.AllowNativePlugins, Value = "1" });
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var src = StoragePaths.PluginSrc(env, "matcms-store", "0.1");
            Directory.CreateDirectory(src);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "matcms-store", "MatcmsStore.dll"), Path.Combine(src, "MatcmsStore.dll"), overwrite: true);
            var row = new Models.Plugin
            {
                Key = "matcms-store", Name = "MatCMS Store", Version = "0.1", Kind = PluginKind.Native, Entry = "MatcmsStore.dll", Enabled = true,
                Sha256 = NativePluginHost.HashOf(Path.Combine(src, "MatcmsStore.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major,
                // the test key instead of the real store key; no 2FA for the test publishers; no mail address
                ConfigJson = new JsonObject { ["requireTwoFactor"] = "false", ["signingKeys"] = KeyId + "=" + PublicKey, ["notifyEmail"] = "" }.ToJsonString()
            };
            db.Plugins.Add(row);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("matcms-store"), "store did not load: " + string.Join(" | ", registry.Errors.Values));
            _installed = true;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() { Key.Dispose(); App.Dispose(); }
}

public class MatcmsStoreTests : IClassFixture<StoreServerApp>
{
    private readonly StoreServerApp _s;
    public MatcmsStoreTests(StoreServerApp s) => _s = s;

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>A script package as MatCMS exports one: a zip with plugin.json.</summary>
    private static byte[] Package(string key, string version, string name = "Hallo")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("plugin.json").Open(), Encoding.UTF8);
            w.Write(new JsonObject { ["Format"] = 1, ["Key"] = key, ["Name"] = name, ["Version"] = version, ["Description"] = "Sagt Hallo.", ["Code"] = "// hallo" }.ToJsonString());
        }
        return ms.ToArray();
    }

    private static async Task<string> TokenOf(HttpClient c, string url) => AppFactory.Token(await c.GetStringAsync(url)) ?? throw new InvalidOperationException("no token on " + url);

    /// <summary>A visitor registers as a publisher through the store's own page and is signed in afterwards.</summary>
    private static async Task<HttpClient> PublisherAsync(StoreServerApp s, string name, string email)
    {
        var c = s.App.Browser();
        var res = await c.PostAsync("/store/registrieren", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = name, ["email"] = email, ["password"] = "correct horse battery", ["password2"] = "correct horse battery", ["terms"] = "1",
            ["__RequestVerificationToken"] = await TokenOf(c, "/store/registrieren")
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/store/konto?ok=welcome", res.Headers.Location?.OriginalString);
        return c;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient c, byte[] package, string notes = "Erste Fassung.", bool picture = false)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await TokenOf(c, "/store/konto/hochladen")), "__RequestVerificationToken" },
            { new ByteArrayContent(package), "package", "plugin.zip" },
            { new StringContent(notes), "notes" },
            { new StringContent("Ein Gruß auf jeder Seite."), "summary" },
            { new StringContent("Sagt Hallo.\n\nUnd sonst nichts."), "description" },
            { new StringContent("Demo, Gruß"), "tags" }
        };
        if (picture) form.Add(new ByteArrayContent(Png), "pictures", "shot.png");
        return await c.PostAsync("/store/konto/hochladen", form);
    }

    private static async Task<string> ErrorOfAsync(HttpResponseMessage r)
    {
        var html = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
        var m = Regex.Match(html, "mst-notice mst-err\">([^<]*)");
        return m.Success ? m.Groups[1].Value : ((int)r.StatusCode) + " " + html[..Math.Min(html.Length, 300)];
    }

    private async Task<HttpResponseMessage> DecideAsync(HttpClient admin, long versionId, string action, string signature = "", string reason = "")
    {
        var url = "/admin/p/matcms-store/review/" + versionId;
        return await admin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = action, ["signature"] = signature, ["reason"] = reason, ["__RequestVerificationToken"] = await TokenOf(admin, url)
        }));
    }

    private static long VersionIdIn(string reviewPage, string name) =>
        long.Parse(Regex.Match(reviewPage, "href=\"/admin/p/matcms-store/review/(\\d+)\"[^>]*>" + Regex.Escape(name)).Groups[1].Value);

    [Fact]
    public async Task A_publisher_uploads_and_the_version_is_public_only_after_the_operator_approved_it_with_a_valid_signature()
    {
        await _s.InstallAsync();
        var guest = _s.App.Browser();
        Assert.DoesNotContain("acme-hello", await guest.GetStringAsync("/store"));                 // (other tests of this class may have published a template)

        var pub = await PublisherAsync(_s, "Acme Plugins", "acme@store.example");
        Assert.Contains("Acme Plugins", await pub.GetStringAsync("/store/konto"));

        var package = Package("acme-hello", "1.0");
        var sha = StoreSignature.Sha256Hex(package);
        var up = await UploadAsync(pub, package, picture: true);
        Assert.True(up.StatusCode == HttpStatusCode.Redirect, await ErrorOfAsync(up));
        Assert.Equal("/store/konto/eintrag/acme-hello?ok=submitted", up.Headers.Location?.OriginalString);
        var own = await pub.GetStringAsync("/store/konto/eintrag/acme-hello");
        Assert.Contains("in Prüfung", WebUtility.HtmlDecode(own));

        // nothing is public before the review
        Assert.DoesNotContain("acme-hello", await guest.GetStringAsync("/store"));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/store/acme-hello")).StatusCode);
        Assert.DoesNotContain(JsonNode.Parse(await guest.GetStringAsync("/store/api/catalog"))!["plugins"]!.AsArray(), p => (string?)p!["key"] == "acme-hello");

        var admin = _s.App.Browser();
        await AppFactory.LoginAsync(admin);
        var queue = await admin.GetStringAsync("/admin/p/matcms-store/review");
        var vid = VersionIdIn(queue, "Hallo 1.0");
        var review = await admin.GetStringAsync("/admin/p/matcms-store/review/" + vid);
        Assert.Contains("sign script acme-hello 1.0 " + sha, review);                       // the command for the operator's machine

        // a signature that is not for exactly this package is refused
        var wrong = StoreSignature.Sign(StoreSignature.Text("script", "acme-hello", "1.1", sha), _s.Key);
        var refused = await DecideAsync(admin, vid, "approve", _s.KeyId + ":" + wrong);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Matches("passt nicht|does not match", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));

        // signed with the SHARED code (what sites and clouds check), accepted by the plugin's own copy
        var sig = StoreSignature.Sign(StoreSignature.Text("script", "acme-hello", "1.0", sha), _s.Key);
        var ok = await DecideAsync(admin, vid, "approve", _s.KeyId + ":" + sig);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        // public now: the catalogue, the page, the package, the picture
        var store = await guest.GetStringAsync("/store");
        Assert.Contains("Hallo", store);
        Assert.Contains("Acme Plugins", store);
        Assert.DoesNotMatch(new Regex("store\\.[a-z]+\\.[a-z]"), store);                     // no raw text keys
        var page = await guest.GetStringAsync("/store/acme-hello");
        Assert.Contains("Und sonst nichts.", WebUtility.HtmlDecode(page));
        Assert.Contains("data-lightbox", page);

        var catalog = JsonNode.Parse(await guest.GetStringAsync("/store/api/catalog"))!;
        var entry = catalog["plugins"]!.AsArray().OfType<JsonObject>().Single(p => (string?)p["key"] == "acme-hello");
        Assert.Equal(("acme-hello", "1.0", "script", sha, _s.KeyId, sig), ((string?)entry["key"], (string?)entry["version"], (string?)entry["kind"],
            (string?)entry["sha256"], (string?)entry["keyId"], (string?)entry["signature"]));
        Assert.Equal("Acme Plugins", (string?)entry["publisher"]);
        var bundle = await guest.GetByteArrayAsync((string)entry["download"]!);
        Assert.Equal(package, bundle);
        Assert.Equal(package, await guest.GetByteArrayAsync((string)entry["download"]!));          // the same address again the same day: counted once
        Assert.True(StoreSignature.VerifyPackage(bundle, "script", "acme-hello", "1.0", sha, sig, _s.KeyId, _s.Keys));   // what a site will check
        var pic = await guest.GetAsync((string)entry["images"]![0]!["url"]!);
        Assert.Equal("image/png", pic.Content.Headers.ContentType?.MediaType);

        var item = JsonNode.Parse(await guest.GetStringAsync("/store/api/plugins/acme-hello"))!;
        Assert.Equal(1, (int)item["downloads"]!);
        Assert.Single(item["versions"]!.AsArray());
    }

    private static async Task<HttpResponseMessage> UploadJsonAsync(HttpClient c, byte[] json, string version)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await TokenOf(c, "/store/konto/hochladen")), "__RequestVerificationToken" },
            { new ByteArrayContent(json), "package", "template.json" },
            { new StringContent(version), "version" },
            { new StringContent("Ein frisches Design."), "summary" }
        };
        return await c.PostAsync("/store/konto/hochladen", form);
    }

    [Fact]
    public async Task Templates_and_components_are_json_files_signed_like_plugins_and_listed_in_their_own_arrays()
    {
        await _s.InstallAsync();
        var pub = await PublisherAsync(_s, "Design Studio", "design@store.example");
        var template = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["Name"] = "Grüne Wiese 2", ["AccentColor"] = "#22aa77", ["HeadingFont"] = "Inter", ["LayoutHtml"] = "<main>{{content}}</main>", ["CustomJs"] = ""
        }.ToJsonString());
        var key = StoreItemPackage.Key("Grüne Wiese 2");                                   // the SHARED rule, what sites and clouds check
        Assert.Equal("gruene-wiese-2", key);

        Assert.Contains("Version", await ErrorOfAsync(await UploadJsonAsync(pub, template, "")));   // a JSON file names no version itself
        var up = await UploadJsonAsync(pub, template, "1.0");
        Assert.True(up.StatusCode == HttpStatusCode.Redirect, await ErrorOfAsync(up));
        Assert.Equal("/store/konto/eintrag/" + key + "?ok=submitted", up.Headers.Location?.OriginalString);
        // the key is a template's now: a plugin under it is refused
        Assert.Matches("andere|another", await ErrorOfAsync(await UploadAsync(pub, Package(key, "2.0"))));

        var admin = _s.App.Browser();
        await AppFactory.LoginAsync(admin);
        var vid = VersionIdIn(await admin.GetStringAsync("/admin/p/matcms-store/review"), "Grüne Wiese 2 1.0");
        var sha = StoreSignature.Sha256Hex(template);
        Assert.Contains("sign template " + key + " 1.0 " + sha, await admin.GetStringAsync("/admin/p/matcms-store/review/" + vid));
        var sig = StoreSignature.Sign(StoreSignature.Text("template", key, "1.0", sha), _s.Key);
        Assert.Equal(HttpStatusCode.Redirect, (await DecideAsync(admin, vid, "approve", _s.KeyId + ":" + sig)).StatusCode);

        var guest = _s.App.Browser();
        var catalog = JsonNode.Parse(await guest.GetStringAsync("/store/api/catalog"))!;
        Assert.DoesNotContain(catalog["plugins"]!.AsArray(), p => (string?)p!["key"] == key);
        var entry = catalog["templates"]!.AsArray().OfType<JsonObject>().Single(t => (string?)t["key"] == key);
        Assert.Equal(("template", "#22aa77", "/store/api/items/" + key + "/1.0/package"), ((string?)entry["kind"], (string?)entry["accentColor"], (string?)entry["download"]));
        var file = await guest.GetByteArrayAsync((string)entry["download"]!);
        Assert.Equal(template, file);
        Assert.True(StoreSignature.VerifyPackage(file, "template", key, "1.0", sha, (string?)entry["signature"], _s.KeyId, _s.Keys));
        Assert.Contains("Grüne Wiese 2", WebUtility.HtmlDecode(await guest.GetStringAsync("/store?art=template")));
        Assert.DoesNotContain("Grüne Wiese 2", WebUtility.HtmlDecode(await guest.GetStringAsync("/store?art=component")));
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string page, string url, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await TokenOf(c, page);
        return await c.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Accounts_review_published_entries_the_publisher_answers_anybody_reports_and_the_operator_hides()
    {
        await _s.InstallAsync();
        var pub = await PublisherAsync(_s, "Review Labs", "labs@store.example");
        var package = Package("labs-rated", "1.0", "Bewertbar");
        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(pub, package)).StatusCode);
        var admin = _s.App.Browser();
        await AppFactory.LoginAsync(admin);
        var vid = VersionIdIn(await admin.GetStringAsync("/admin/p/matcms-store/review"), "Bewertbar 1.0");
        var sig = StoreSignature.Sign(StoreSignature.Text("script", "labs-rated", "1.0", StoreSignature.Sha256Hex(package)), _s.Key);
        Assert.Equal(HttpStatusCode.Redirect, (await DecideAsync(admin, vid, "approve", _s.KeyId + ":" + sig)).StatusCode);

        // a guest is asked to sign in; the publisher cannot rate the own entry
        var guest = _s.App.Browser();
        Assert.Contains("/anmelden?returnUrl=", await guest.GetStringAsync("/store/labs-rated"));
        // (the publisher sees no form on the own entry; the token comes from another page of the same session)
        var own = await PostFormAsync(pub, "/store/konto/hochladen", "/store/labs-rated/bewerten", new() { ["stars"] = "5", ["name"] = "Ich", ["action"] = "save" });
        Assert.Contains("rev=own", own.Headers.Location?.OriginalString);

        // another account rates (and changes its mind — one review per account)
        var fan = await PublisherAsync(_s, "Fan Account", "fan@store.example");
        Assert.Contains("rev=invalid", (await PostFormAsync(fan, "/store/labs-rated", "/store/labs-rated/bewerten", new() { ["stars"] = "9", ["name"] = "Fan", ["action"] = "save" })).Headers.Location?.OriginalString);
        await PostFormAsync(fan, "/store/labs-rated", "/store/labs-rated/bewerten", new() { ["stars"] = "2", ["name"] = "Fan", ["text"] = "<b>geht so</b>", ["action"] = "save" });
        await PostFormAsync(fan, "/store/labs-rated", "/store/labs-rated/bewerten", new() { ["stars"] = "4", ["name"] = "Fan", ["text"] = "<b>doch gut</b>", ["action"] = "save" });
        var page = await guest.GetStringAsync("/store/labs-rated");
        Assert.Contains("&lt;b&gt;doch gut&lt;/b&gt;", page);                                                   // text, never markup
        Assert.DoesNotContain("geht so", page);
        var item = JsonNode.Parse(await guest.GetStringAsync("/store/api/plugins/labs-rated"))!;
        Assert.Equal((4.0, 1), ((double)item["rating"]!["average"]!, (int)item["rating"]!["count"]!));

        // the publisher answers from the account page
        var account = await pub.GetStringAsync("/store/konto/eintrag/labs-rated");
        var reviewId = Regex.Match(account, "name=\"id\" value=\"(\\d+)\"><label for=\"st-ans-").Groups[1].Value;
        Assert.NotEmpty(reviewId);
        await PostFormAsync(pub, "/store/konto/eintrag/labs-rated", "/store/konto/eintrag/labs-rated", new() { ["action"] = "answer", ["id"] = reviewId, ["answer"] = "Danke!" });
        Assert.Contains("Danke!", await guest.GetStringAsync("/store/labs-rated"));

        // a visitor reports it, the operator sees it in the badge and hides it
        Assert.Contains("rev=reported", (await PostFormAsync(guest, "/store/labs-rated", "/store/labs-rated/melden", new() { ["id"] = reviewId })).Headers.Location?.OriginalString);
        var list = await admin.GetStringAsync("/admin/p/matcms-store/reviews");
        Assert.Contains("doch gut", WebUtility.HtmlDecode(list));
        await admin.PostAsync("/admin/p/matcms-store/reviews", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["action"] = "hide", ["id"] = reviewId, ["__RequestVerificationToken"] = AppFactory.Token(list)! }));
        Assert.DoesNotContain("doch gut", await guest.GetStringAsync("/store/labs-rated"));
        Assert.Equal(0, (int)JsonNode.Parse(await guest.GetStringAsync("/store/api/plugins/labs-rated"))!["rating"]!["count"]!);
    }

    [Fact]
    public async Task A_key_belongs_to_its_publisher_versions_only_go_up_and_reserved_keys_need_a_verified_publisher()
    {
        await _s.InstallAsync();
        var first = await PublisherAsync(_s, "Erster", "first@store.example");
        var firstUp = await UploadAsync(first, Package("shared-key", "2.0"));
        Assert.True(firstUp.StatusCode == HttpStatusCode.Redirect, await ErrorOfAsync(firstUp));

        var second = await PublisherAsync(_s, "Zweiter", "second@store.example");
        var taken = await UploadAsync(second, Package("shared-key", "3.0"));
        Assert.Equal(422, (int)taken.StatusCode);
        Assert.Contains("gehört schon einem anderen Herausgeber", WebUtility.HtmlDecode(await taken.Content.ReadAsStringAsync()));

        var lower = await UploadAsync(first, Package("shared-key", "1.9"));
        Assert.Contains("muss höher sein", WebUtility.HtmlDecode(await lower.Content.ReadAsStringAsync()));
        var higher = await UploadAsync(first, Package("shared-key", "2.10"));                                       // 2.10 is higher than 2.0
        Assert.True(higher.StatusCode == HttpStatusCode.Redirect, Regex.Match(WebUtility.HtmlDecode(await higher.Content.ReadAsStringAsync()), "mst-notice mst-err\">([^<]*)").Groups[1].Value);

        var reserved = await UploadAsync(second, Package("forum", "9.0"));
        Assert.Contains("reserviert", WebUtility.HtmlDecode(await reserved.Content.ReadAsStringAsync()));
        var notZip = await UploadAsync(second, "not a zip"u8.ToArray());
        Assert.Contains("kein lesbares ZIP", WebUtility.HtmlDecode(await notZip.Content.ReadAsStringAsync()));

        // a visitor without an account is sent to sign in before anything is read
        var post = await _s.App.Browser().PostAsync("/store/konto/hochladen", new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/anmelden", post.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_catalogue_keeps_answering_in_maintenance_mode_while_the_pages_do_not()
    {
        await _s.InstallAsync();
        using (var scope = _s.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.MaintenanceEnabled, Value = "1" });
            await db.SaveChangesAsync();
        }
        try
        {
            var guest = _s.App.Browser();
            Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/store/api/catalog")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await guest.GetAsync("/store")).StatusCode);
        }
        finally
        {
            using var scope = _s.App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key == SettingKeys.MaintenanceEnabled));
            await db.SaveChangesAsync();
        }
    }
}
