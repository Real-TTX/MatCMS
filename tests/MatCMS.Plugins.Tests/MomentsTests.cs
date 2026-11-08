using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The guest area of a private event (plugins/moments) — NATIVE, loaded from its built DLL into the whole application: a household signs in with its code
/// (no mail, an account nobody can write to), an upload waits for the hosts' release and is invisible to everybody else until then, the original's location is
/// removed, and tags, hearts and comments work and are encoded.
/// </summary>
public sealed class MomentsApp : IDisposable
{
    public AppFactory App { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _installed;
    private HttpClient? _admin;

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
            var src = StoragePaths.PluginSrc(env, "moments", "1.0");
            Directory.CreateDirectory(src);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "moments", "Moments.dll"), Path.Combine(src, "Moments.dll"), overwrite: true);
            var row = new Models.Plugin
            {
                Key = "moments", Name = "Moments", Version = "1.0", Kind = PluginKind.Native, Entry = "Moments.dll", Enabled = true,
                Sha256 = NativePluginHost.HashOf(Path.Combine(src, "Moments.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
            };
            db.Plugins.Add(row);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("moments"), "moments did not load: " + string.Join(" | ", registry.Errors.Values));
            _installed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<HttpClient> AdminAsync()
    {
        await InstallAsync();
        await _gate.WaitAsync();
        try
        {
            if (_admin is not null) return _admin;
            var c = App.Browser();
            await AppFactory.LoginAsync(c);
            return _admin = c;
        }
        finally { _gate.Release(); }
    }

    public T Db<T>(Func<AppDbContext, T> f)
    {
        using var scope = App.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public void Dispose() { _admin?.Dispose(); App.Dispose(); }
}

public class MomentsTests : IClassFixture<MomentsApp>
{
    private readonly MomentsApp _app;
    public MomentsTests(MomentsApp app) => _app = app;

    private static string Text(string html) => WebUtility.HtmlDecode(html);

    /// <summary>Posts a form of a page the way a browser does (with that page's antiforgery token).</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string pageUrl, Dictionary<string, string> fields, string? postUrl = null)
    {
        var html = await c.GetStringAsync(pageUrl);
        fields["__RequestVerificationToken"] = AppFactory.Token(html) ?? throw new InvalidOperationException("no token on " + pageUrl);
        return await c.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(fields));
    }

    /// <summary>A household through Admin → Moments → Gäste → Neu; answers its id and code.</summary>
    private async Task<(long Id, string Code)> GuestAsync(string name, string people, string table = "")
    {
        var admin = await _app.AdminAsync();
        var res = await PostFormAsync(admin, "/admin/p/moments/guests/new", new Dictionary<string, string> { ["action"] = "save", ["name"] = name, ["people"] = people, ["table"] = table });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var id = long.Parse(res.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        var code = _app.Db(db => db.Database.SqlQueryRaw<string>("select code as Value from p_moments_guests where id = {0}", id).AsEnumerable().Single());
        return (id, code);
    }

    /// <summary>A browser signed in with the household's code (through the form of /moments).</summary>
    private async Task<HttpClient> SignedInAsync(string code)
    {
        var c = _app.App.Browser();
        var page = await c.GetStringAsync("/moments");
        Assert.Contains("action=\"/moments/anmelden\"", page);
        var res = await c.PostAsync("/moments/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code[..4].ToLowerInvariant() + "-" + code[4..] }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/moments?willkommen=1", res.Headers.Location!.OriginalString);
        return c;
    }

    /// <summary>A small JPEG header with an EXIF block: DateTimeOriginal 2026:10:09 16:42:05 and a GPS latitude 48°51'12.34" N — no image data (the server never decodes).</summary>
    internal static byte[] Jpeg(bool gps = true)
    {
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }
        void Entry(int tag, int type, int count, int value) { U16(tag); U16(type); U32(count); U32(value); }
        // header + IFD0 (2 entries) at 8: 8 + 2 + 2*12 + 4 = 38 → Exif IFD at 38 (1 entry): 38 + 2 + 12 + 4 = 56 → GPS IFD at 56 (2 entries): 56 + 2 + 24 + 4 = 86
        // → date string at 86 (20 bytes) → latitude rationals at 106 (24 bytes)
        tiff.AddRange("II"u8.ToArray()); U16(42); U32(8);
        U16(2); Entry(0x8769, 4, 1, 38); Entry(0x8825, 4, 1, gps ? 56 : 0); U32(0);
        U16(1); Entry(0x9003, 2, 20, 86); U32(0);
        U16(2); Entry(0x0001, 2, 2, 'N'); Entry(0x0002, 5, 3, 106); U32(0);
        tiff.AddRange(Encoding.ASCII.GetBytes("2026:10:09 16:42:05\0"));
        U32(48); U32(1); U32(51); U32(1); U32(1234); U32(100);
        var app1 = new List<byte>(Encoding.ASCII.GetBytes("Exif\0\0"));
        app1.AddRange(tiff);
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, (byte)((app1.Count + 2) >> 8), (byte)((app1.Count + 2) & 0xFF) };
        b.AddRange(app1);
        // a frame header: 8 bit, 600 × 900, 1 component — enough for the size reader
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x03, 0x84, 0x02, 0x58, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xD9 });
        return b.ToArray();
    }

    private static readonly byte[] Seconds = { 0xD2, 0x04, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00 };   // 1234/100 as stored

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    /// <summary>Uploads a photo as the page's script does without a view (ajax, one file) and answers the JSON.</summary>
    private static async Task<JsonObject> UploadAsync(HttpClient c, byte[] jpeg, string name = "IMG_0001.jpg", byte[]? view = null)
    {
        var page = await c.GetStringAsync("/moments/hochladen");
        Assert.Contains("data-mo-up", page);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(page)!), "__RequestVerificationToken" }, { new StringContent("upload"), "action" }, { new StringContent("1"), "ajax" },
            { new ByteArrayContent(jpeg), "file", name }
        };
        if (view is not null)
        {
            form.Add(new ByteArrayContent(view), "view", "view.jpg");
            form.Add(new ByteArrayContent(view), "thumb", "thumb.jpg");
        }
        var res = await c.PostAsync("/moments/hochladen", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
    }

    private long LastPhoto() => _app.Db(db => db.Database.SqlQueryRaw<long>("select id as Value from p_moments_photos order by id desc limit 1").AsEnumerable().Single());

    [Fact]
    public async Task A_household_signs_in_with_its_code_or_link_and_gets_an_account_that_receives_no_mail()
    {
        var (id, code) = await GuestAsync("Zoe & Fabian", "Zoe, Fabian", "Startlinie");
        Assert.Matches("^[2-9A-HJ-NP-Z]{8}$", code);

        // a stranger: the code form, no photos, no picture
        var stranger = _app.App.Browser();
        var login = await stranger.GetStringAsync("/moments");
        Assert.Contains("name=\"code\"", login);
        Assert.DoesNotContain("Zoe", login);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/moments/bild/1/t")).StatusCode);
        var wrong = await stranger.PostAsync("/moments/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "ZZZZ-ZZZZ" }));
        Assert.Contains("kennen wir leider nicht", Text(await wrong.Content.ReadAsStringAsync()));

        // the code (typed in lower case with a dash) signs in and greets the household by name
        var c = await SignedInAsync(code);
        var home = Text(await c.GetStringAsync("/moments?willkommen=1"));
        Assert.Contains("Willkommen, Zoe & Fabian!", home);
        // the site's menu has the gallery for this visitor
        Assert.Contains("href=\"/moments/hochladen\"", await c.GetStringAsync("/"));

        // the account: made on the first sign-in, at an address that can never receive mail, and remembered
        var account = _app.Db(db => db.Database.SqlQueryRaw<string>("select account as Value from p_moments_guests where id = {0}", id).AsEnumerable().Single());
        Assert.EndsWith("@moments.invalid", account);
        Assert.Equal("Zoe & Fabian", _app.Db(db => db.SiteMembers.Single(m => m.Email == account).DisplayName));

        // the personal link of the card signs in a second phone into the same account
        var phone = _app.App.Browser();
        var res = await phone.GetAsync("/moments/z/" + code);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("Zoe & Fabian", Text(await phone.GetStringAsync("/moments")));
        Assert.Equal(1, _app.Db(db => db.SiteMembers.Count(m => m.Email == account)));

        // a new code throws the old phones out and makes the old link useless
        var admin = await _app.AdminAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(admin, "/admin/p/moments/guests/" + id, new Dictionary<string, string> { ["action"] = "newcode" })).StatusCode);
        Assert.Contains("name=\"code\"", await phone.GetStringAsync("/moments"));
        Assert.Contains("gilt nicht", Text(await _app.App.Browser().GetStringAsync("/moments/z/" + code)));
    }

    [Fact]
    public async Task An_upload_waits_for_release_and_nobody_else_sees_it_until_then()
    {
        var (_, uploaderCode) = await GuestAsync("Sina & Stratis", "Sina, Stratis");
        var (_, otherCode) = await GuestAsync("Leon", "Leon");
        var uploader = await SignedInAsync(uploaderCode);
        var other = await SignedInAsync(otherCode);

        var answer = await UploadAsync(uploader, Jpeg());
        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        Assert.True(answer["pending"]!.GetValue<bool>());
        var id = LastPhoto();

        // the uploader sees it among the own photos; another guest gets nothing — not the page, not the picture
        Assert.Contains("wartet auf Freigabe", Text(await uploader.GetStringAsync("/moments/meine")));
        Assert.Equal(HttpStatusCode.OK, (await uploader.GetAsync("/moments/bild/" + id + "/t")).StatusCode);
        Assert.DoesNotContain("/moments/foto/" + id, await other.GetStringAsync("/moments"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/moments/foto/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/moments/bild/" + id + "/v")).StatusCode);

        // the hosts release it in Admin → Moments → Freigabe
        var admin = await _app.AdminAsync();
        var review = await admin.GetStringAsync("/admin/p/moments/review");
        Assert.Contains("/moments/bild/" + id + "/t", review);
        var res = await PostFormAsync(admin, "/admin/p/moments/review", new Dictionary<string, string> { ["action"] = "approve", ["id"] = id.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/moments/foto/" + id, await other.GetStringAsync("/moments"));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/moments/bild/" + id + "/v")).StatusCode);

        // hidden again: gone for the others
        await PostFormAsync(admin, "/admin/p/moments/photos", new Dictionary<string, string> { ["action"] = "hide", ["id"] = id.ToString() });
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/moments/foto/" + id)).StatusCode);
    }

    [Fact]
    public async Task The_original_loses_its_location_and_keeps_its_date()
    {
        var (_, code) = await GuestAsync("Ramona & Reiner", "Ramona, Reiner");
        var c = await SignedInAsync(code);
        var jpeg = Jpeg();
        Assert.True(Contains(jpeg, Seconds));
        var answer = await UploadAsync(c, jpeg, view: Jpeg(gps: false));
        Assert.True(answer["ok"]!.GetValue<bool>(), answer.ToJsonString());
        var id = LastPhoto();

        Assert.Equal("2026-10-09T16:42:05", _app.Db(db => db.Database.SqlQueryRaw<string>("select taken as Value from p_moments_photos where id = {0}", id).AsEnumerable().Single()));
        Assert.Equal(600, _app.Db(db => db.Database.SqlQueryRaw<long>("select width as Value from p_moments_photos where id = {0}", id).AsEnumerable().Single()));
        // three files: the original, the view and the tile the browser made
        var files = _app.Db(db => db.Database.SqlQueryRaw<string>("select file || '|' || view || '|' || thumb as Value from p_moments_photos where id = {0}", id).AsEnumerable().Single()).Split('|');
        Assert.Equal(3, files.Distinct().Count());

        var original = await c.GetByteArrayAsync("/moments/download/" + id);
        Assert.Equal(jpeg.Length, original.Length);
        Assert.False(Contains(original, Seconds), "the GPS position is still in the original");
        Assert.True(Contains(original, Encoding.ASCII.GetBytes("2026:10:09 16:42:05")));
        var res = await c.GetAsync("/moments/download/" + id);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition?.DispositionType);

        // something that only claims to be a photo is refused
        var fake = await UploadAsync(c, Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "boese.jpg");
        Assert.False(fake["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Guests_mark_people_heart_and_comment_and_what_they_type_stays_text()
    {
        var (_, aCode) = await GuestAsync("Alice & Jens", "Alice, Jens");
        var (_, bCode) = await GuestAsync("Bianca & Sven", "Bianca, Sven");
        var a = await SignedInAsync(aCode);
        var bb = await SignedInAsync(bCode);
        await UploadAsync(a, Jpeg());
        var id = LastPhoto();
        var admin = await _app.AdminAsync();
        await PostFormAsync(admin, "/admin/p/moments/review", new Dictionary<string, string> { ["action"] = "approve", ["id"] = id.ToString() });

        var page = "/moments/foto/" + id;
        // a name from the guest list becomes a PERSON, anything else a keyword
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(bb, page, new Dictionary<string, string> { ["action"] = "tag", ["tag"] = "alice", ["q"] = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await PostFormAsync(bb, page, new Dictionary<string, string> { ["action"] = "tag", ["tag"] = "Tanzfläche", ["q"] = "" })).StatusCode);
        Assert.Equal("person", _app.Db(db => db.Database.SqlQueryRaw<string>("select kind as Value from p_moments_tags where name = 'Alice'").AsEnumerable().Single()));
        Assert.Equal("topic", _app.Db(db => db.Database.SqlQueryRaw<string>("select kind as Value from p_moments_tags where name = 'Tanzfläche'").AsEnumerable().Single()));
        var tagId = _app.Db(db => db.Database.SqlQueryRaw<long>("select id as Value from p_moments_tags where name = 'Alice'").AsEnumerable().Single());
        Assert.Contains("/moments/foto/" + id, await a.GetStringAsync("/moments?tag=" + tagId));

        // a comment is text, never markup
        await PostFormAsync(bb, page, new Dictionary<string, string> { ["action"] = "comment", ["text"] = "Wie schön! <script>alert('x')</script>", ["q"] = "" });
        var html = await a.GetStringAsync(page);
        Assert.Contains("&lt;script&gt;alert(", html);
        Assert.Contains("Wie schön!", Text(html));
        Assert.DoesNotContain("<script>alert('x')", html);

        // the heart from the page's script answers in place
        var token = AppFactory.Token(await a.GetStringAsync(page))!;
        var heart = await a.PostAsync(page, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = "heart", ["ajax"] = "1", ["q"] = "" }));
        var json = JsonNode.Parse(await heart.Content.ReadAsStringAsync())!;
        Assert.True(json["on"]!.GetValue<bool>());
        Assert.Equal(1, json["count"]!.GetValue<int>());

        // B cannot delete A's photo, nor A's... the hosts can remove the comment in the back office
        var del = await PostFormAsync(bb, page, new Dictionary<string, string> { ["action"] = "delete", ["q"] = "" });
        var delText = Text(await del.Content.ReadAsStringAsync());
        Assert.True(delText.Contains("Nur wer das Foto hochgeladen hat"), del.StatusCode + " " + del.Headers.Location + " " + delText[..Math.Min(400, delText.Length)]);
        var commentId = _app.Db(db => db.Database.SqlQueryRaw<long>("select id as Value from p_moments_comments where photo_id = {0}", id).AsEnumerable().Single());
        await PostFormAsync(admin, "/admin/p/moments/comments", new Dictionary<string, string> { ["action"] = "hide", ["id"] = commentId.ToString() });
        Assert.DoesNotContain("Wie schön!", Text(await a.GetStringAsync(page)));
    }

    [Fact]
    public async Task Many_households_at_once_and_printable_cards_for_the_hosts_only()
    {
        var admin = await _app.AdminAsync();
        var res = await PostFormAsync(admin, "/admin/p/moments/guests", new Dictionary<string, string>
        {
            ["action"] = "bulk", ["lines"] = "Tine & Tobi | Tine, Tobi | Laufglück\nHelga\nUte | Ute | Endspurt\nHelga"
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var list = Text(await admin.GetStringAsync("/admin/p/moments/guests"));
        Assert.Contains("Tine & Tobi", list);
        Assert.Contains("Laufglück", list);
        Assert.Equal(1, _app.Db(db => db.Database.SqlQueryRaw<long>("select count(*) as Value from p_moments_guests where name = 'Helga'").AsEnumerable().Single()));

        var cards = await admin.GetStringAsync("/moments/karten");
        Assert.Contains("Tine &amp; Tobi", cards);
        Assert.Contains("/moments/z/", cards);
        Assert.Contains("qrcode.min.js", cards);
        // nobody else gets the codes
        Assert.Equal(HttpStatusCode.NotFound, (await _app.App.Browser().GetAsync("/moments/karten")).StatusCode);
        var (_, code) = await GuestAsync("Frank & Katja", "Frank, Katja");
        Assert.Equal(HttpStatusCode.NotFound, (await (await SignedInAsync(code)).GetAsync("/moments/karten")).StatusCode);
    }

    [Fact]
    public async Task The_entrance_block_and_the_admin_pages_render()
    {
        var admin = await _app.AdminAsync();
        foreach (var page in new[] { "overview", "review", "photos", "comments", "guests", "guests/new", "tags", "_settings?group=Allgemein" })
        {
            var r = await admin.GetAsync("/admin/p/moments/" + page);
            Assert.True(r.StatusCode == HttpStatusCode.OK, page + ": " + r.StatusCode + " " + r.Headers.Location);
        }

        using (var scope = _app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = new Page { Title = "Gästebereich", Slug = "gaeste-test", IsPublished = true, Locale = "de" };
            db.Pages.Add(p);
            await db.SaveChangesAsync();
            db.ContentBlocks.Add(new ContentBlock { PageId = p.Id, BlockType = "moments_access", SortOrder = 1, DataJson = "{\"heading\":\"Anmelden\"}" });
            await db.SaveChangesAsync();
        }
        var anonymous = await _app.App.Browser().GetStringAsync("/gaeste-test");
        Assert.Contains("action=\"/moments/anmelden\"", anonymous);
        var (_, code) = await GuestAsync("Cüneyt & Vicky", "Cüneyt, Vicky");
        var guest = await SignedInAsync(code);
        Assert.Contains("Hallo Cüneyt & Vicky!", Text(await guest.GetStringAsync("/gaeste-test")));
    }
}
