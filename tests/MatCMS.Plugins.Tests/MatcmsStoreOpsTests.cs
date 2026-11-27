using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using MatCMS.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The store server's operations for AI agents: read the entries, take one out of the store and undo that, read and moderate reviews with undo, read
/// the publishers. Reviews and publishers are personal data; nothing can publish a version (that stays the operator's signature).
/// </summary>
public class MatcmsStoreOpsTests : IClassFixture<StoreServerApp>
{
    private readonly StoreServerApp _s;
    public MatcmsStoreOpsTests(StoreServerApp s) { _s = s; PluginPostLimiter.Reset(); }

    private string Op(string name)
    {
        var c = _s.App.Services.GetRequiredService<PluginContributions>();
        return c.HasOperation("matcms-store." + name) ? "matcms-store." + name : name;
    }

    private async Task<JsonObject> RunAsync(string op, JsonObject args, bool allowInternal = false)
    {
        using var scope = _s.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, Op(op), args, default, allowInternal))!;
    }

    private async Task<JsonObject> UndoAsync(JsonObject answer)
    {
        var undo = Assert.IsType<JsonObject>(answer["undo"]);
        using var scope = _s.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>()
            .RunOperationAsync(scope.ServiceProvider, undo["name"]!.GetValue<string>(), undo["args"]?.DeepClone(), default, allowInternal: true))!;
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();

    // ---- arranging, as MatcmsStoreTests does it: a publisher registers and uploads, the operator approves with a signature, an account reviews -----

    private static byte[] Package(string key, string version, string name)
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

    private async Task<HttpClient> AccountAsync(string name, string email)
    {
        var c = _s.App.Browser();
        var res = await c.PostAsync("/store/registrieren", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = name, ["email"] = email, ["password"] = "correct horse battery", ["password2"] = "correct horse battery", ["terms"] = "1",
            ["__RequestVerificationToken"] = await TokenOf(c, "/store/registrieren")
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return c;
    }

    private async Task<string> PublishedEntryAsync(string key, string name, string publisher, string email)
    {
        await _s.InstallAsync();
        var pub = await AccountAsync(publisher, email);
        var package = Package(key, "1.0", name);
        var form = new MultipartFormDataContent
        {
            { new StringContent(await TokenOf(pub, "/store/konto/hochladen")), "__RequestVerificationToken" },
            { new ByteArrayContent(package), "package", "plugin.zip" },
            { new StringContent("Erste Fassung."), "notes" },
            { new StringContent("Ein Gruß."), "summary" },
            { new StringContent("Sagt Hallo."), "description" },
        };
        Assert.Equal(HttpStatusCode.Redirect, (await pub.PostAsync("/store/konto/hochladen", form)).StatusCode);
        var admin = _s.App.Browser();
        await AppFactory.LoginAsync(admin);
        var queue = await admin.GetStringAsync("/admin/p/matcms-store/review");
        var vid = long.Parse(Regex.Match(queue, "href=\"/admin/p/matcms-store/review/(\\d+)\"[^>]*>" + Regex.Escape(name + " 1.0")).Groups[1].Value);
        var sig = StoreSignature.Sign(StoreSignature.Text("script", key, "1.0", StoreSignature.Sha256Hex(package)), _s.Key);
        var url = "/admin/p/matcms-store/review/" + vid;
        var res = await admin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["action"] = "approve", ["signature"] = _s.KeyId + ":" + sig, ["reason"] = "", ["__RequestVerificationToken"] = await TokenOf(admin, url)
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        return key;
    }

    private async Task ReviewAsync(string key, int stars, string text, string name, string email)
    {
        var fan = await AccountAsync(name, email);
        var res = await fan.PostAsync("/store/" + key + "/bewerten", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["stars"] = stars.ToString(), ["name"] = name, ["text"] = text, ["action"] = "save", ["__RequestVerificationToken"] = await TokenOf(fan, "/store/" + key)
        }));
        Assert.Contains("rev=saved", res.Headers.Location?.OriginalString);
    }

    // ---- the tests ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_operations_are_declared_with_the_right_flags_and_none_publishes()
    {
        await _s.InstallAsync();
        var c = _s.App.Services.GetRequiredService<PluginContributions>();
        var registry = _s.App.Services.GetRequiredService<PluginRegistry>();
        var mine = registry.Operations.Where(o => o.Owner == "matcms-store").ToDictionary(o => o.Name, o => o.Spec!);
        foreach (var name in new[] { "item.list", "item.read", "item.withdraw", "review.list", "review.hide", "review.show", "review.dismiss", "publisher.list", "item.unwithdraw", "review.restore" })
            Assert.True(c.HasOperation(Op(name)), name);
        Assert.All(new[] { "item.list", "item.read", "review.list", "publisher.list" }, n => Assert.False(mine[n].Writes, n));
        Assert.All(new[] { "item.withdraw", "review.hide", "review.show", "review.dismiss" }, n => Assert.True(mine[n].Writes, n));
        Assert.All(new[] { "review.list", "publisher.list" }, n => Assert.True(mine[n].PersonalData, n));
        Assert.All(new[] { "item.list", "item.read", "item.withdraw" }, n => Assert.False(mine[n].PersonalData, n));
        Assert.All(new[] { "item.unwithdraw", "review.restore" }, n => Assert.True(mine[n].Internal, n));
        // nothing approves, publishes or puts back an entry: that stays the operator's signature in the admin — the public operations are exactly these
        Assert.Equal(new[] { "item.list", "item.read", "item.withdraw", "publisher.list", "review.dismiss", "review.hide", "review.list", "review.show" },
            mine.Where(kv => !kv.Value.Internal).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal));
        var assistant = c.AssistantOperations.Where(o => o.Owner == "matcms-store").Select(o => o.Name).ToList();
        Assert.DoesNotContain("review.list", assistant);
        Assert.DoesNotContain("publisher.list", assistant);
        Assert.Contains("item.list", assistant);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("item.unwithdraw", new JsonObject { ["key"] = "x" }));
    }

    [Fact]
    public async Task Entries_are_listed_and_read_and_withdrawing_one_is_undone_to_the_published_state()
    {
        await PublishedEntryAsync("ops-hello", "Ops Hallo", "Ops Plugins", "ops@store.example");
        var list = await RunAsync("item.list", new JsonObject { ["status"] = "published", ["search"] = "ops-hello" });
        Assert.Equal("applied", Status(list));
        var row = Assert.Single(((JsonArray)list["data"]!).OfType<JsonObject>());
        Assert.Equal(("ops-hello", "1.0", "Ops Plugins"), (row["key"]!.GetValue<string>(), row["latestVersion"]!.GetValue<string>(), row["publisher"]!.GetValue<string>()));
        Assert.Equal("failed", Status(await RunAsync("item.list", new JsonObject { ["status"] = "kaputt" })));

        var read = await RunAsync("item.read", new JsonObject { ["key"] = "ops-hello" });
        Assert.Equal("applied", Status(read));
        Assert.Equal("published", ((JsonArray)read["data"]!["versions"]!)[0]!["status"]!.GetValue<string>());
        Assert.Equal("failed", Status(await RunAsync("item.read", new JsonObject { ["key"] = "gibt-es-nicht" })));

        var guest = _s.App.Browser();
        var withdrawn = await RunAsync("item.withdraw", new JsonObject { ["key"] = "ops-hello" });
        Assert.Equal("applied", Status(withdrawn));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/store/ops-hello")).StatusCode);
        Assert.Equal("skipped-exists", Status(await RunAsync("item.withdraw", new JsonObject { ["key"] = "ops-hello" })));

        Assert.Equal("applied", Status(await UndoAsync(withdrawn)));
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/store/ops-hello")).StatusCode);
        Assert.Equal("published", (await RunAsync("item.read", new JsonObject { ["key"] = "ops-hello" }))["data"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reviews_are_listed_hidden_and_shown_with_undo_and_publishers_are_listed_with_their_address()
    {
        await PublishedEntryAsync("ops-rated", "Ops Bewertbar", "Ops Labs", "opslabs@store.example");
        await ReviewAsync("ops-rated", 2, "Geht so.", "Ops Fan", "opsfan@store.example");

        var reviews = await RunAsync("review.list", new JsonObject { ["item"] = "ops-rated" });
        Assert.Equal("applied", Status(reviews));
        var review = Assert.Single(((JsonArray)reviews["data"]!).OfType<JsonObject>());
        Assert.Equal(("Ops Fan", 2, "visible"), (review["name"]!.GetValue<string>(), review["stars"]!.GetValue<int>(), review["status"]!.GetValue<string>()));
        var id = review["id"]!.GetValue<long>();
        Assert.Equal("failed", Status(await RunAsync("review.list", new JsonObject { ["status"] = "weg" })));

        var hidden = await RunAsync("review.hide", new JsonObject { ["id"] = id });
        Assert.Equal("applied", Status(hidden));
        Assert.Equal(0, (int)JsonNode.Parse(await _s.App.Browser().GetStringAsync("/store/api/plugins/ops-rated"))!["rating"]!["count"]!);
        Assert.Equal("skipped-exists", Status(await RunAsync("review.hide", new JsonObject { ["id"] = id })));
        Assert.Equal("failed", Status(await RunAsync("review.hide", new JsonObject { ["id"] = 999999 })));

        Assert.Equal("applied", Status(await UndoAsync(hidden)));
        Assert.Equal(1, (int)JsonNode.Parse(await _s.App.Browser().GetStringAsync("/store/api/plugins/ops-rated"))!["rating"]!["count"]!);

        var publishers = await RunAsync("publisher.list", new JsonObject { ["search"] = "Ops Labs" });
        var pub = Assert.Single(((JsonArray)publishers["data"]!).OfType<JsonObject>());
        Assert.Equal(("opslabs@store.example", 1), (pub["email"]!.GetValue<string>(), pub["items"]!.GetValue<int>()));
    }
}
