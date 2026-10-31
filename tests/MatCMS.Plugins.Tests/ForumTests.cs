using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
/// The forum (plugins/forum) — a NATIVE plugin, loaded from its built DLL into the whole application like on a site: members of the guest area write,
/// rights per board hold, moderators moderate, what members type never becomes markup, and an erased account takes its name off its posts.
/// </summary>
public sealed class ForumApp : IDisposable
{
    public AppFactory App { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HttpClient> _clients = new();
    private bool _installed;

    private static string Fixture(string path) => Path.Combine(AppContext.BaseDirectory, "fixtures", path);

    /// <summary>Puts the forum's DLL where a site keeps a native plugin, allows native plugins, and loads it.</summary>
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
            var src = StoragePaths.PluginSrc(env, "forum", "1.0");
            Directory.CreateDirectory(src);
            File.Copy(Fixture("forum/Forum.dll"), Path.Combine(src, "Forum.dll"), overwrite: true);
            var row = new Models.Plugin
            {
                Key = "forum", Name = "Forum", Version = "1.0", Kind = PluginKind.Native, Entry = "Forum.dll", Enabled = true,
                Sha256 = NativePluginHost.HashOf(Path.Combine(src, "Forum.dll")), Api = typeof(IPlugin).Assembly.GetName().Version!.Major
            };
            db.Plugins.Add(row);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("forum"), "forum did not load: " + string.Join(" | ", registry.Errors.Values));
            _installed = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Changes settings of the forum (stored on the plugin row, as the settings page does) and reloads it.</summary>
    public async Task SetAsync(params (string Key, string Value)[] values)
    {
        await InstallAsync();
        using var scope = App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Plugins.FirstAsync(p => p.Key == "forum");
        var cfg = (string.IsNullOrWhiteSpace(row.ConfigJson) ? null : JsonNode.Parse(row.ConfigJson)) as JsonObject ?? new JsonObject();
        foreach (var (k, v) in values) cfg[k] = v;
        row.ConfigJson = cfg.ToJsonString();
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
    }

    /// <summary>A signed-in browser per person, made once (the sign-in pages allow ten a minute per address).</summary>
    public async Task<HttpClient> AsAsync(string who)
    {
        await InstallAsync();
        await _gate.WaitAsync();
        try
        {
            if (_clients.TryGetValue(who, out var known)) return known;
            var c = App.Browser();
            if (who == "admin") await AppFactory.LoginAsync(c);
            else if (who != "guest")
            {
                var email = who + "@forum.example";
                using (var scope = App.Services.CreateScope())
                {
                    var (status, _) = await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", char.ToUpperInvariant(who[0]) + who[1..]);
                    Assert.Equal(MemberService.CreateStatus.Created, status);
                    if (who == "mod")
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var m = await db.SiteMembers.FirstAsync(x => x.Email == email);
                        m.RolesCsv = "forum-moderator";
                        await db.SaveChangesAsync();
                    }
                }
                var page = await c.GetStringAsync("/anmelden");
                var res = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = email, ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(page)!
                }));
                Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            }
            return _clients[who] = c;
        }
        finally { _gate.Release(); }
    }

    public async Task<long> MemberIdAsync(string who)
    {
        using var scope = App.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteMembers.FirstAsync(m => m.Email == who + "@forum.example")).Id;
    }

    public void Dispose() => App.Dispose();
}

public class ForumTests : IClassFixture<ForumApp>
{
    private readonly ForumApp _f;
    public ForumTests(ForumApp f) => _f = f;

    /// <summary>GETs a page (for its form's token) and POSTs the fields to it, as a browser would.</summary>
    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient c, string url, Dictionary<string, string> fields, string? formUrl = null)
    {
        var page = await c.GetStringAsync(formUrl ?? url);
        var token = AppFactory.Token(page);
        Assert.False(string.IsNullOrEmpty(token), "no antiforgery token on " + (formUrl ?? url));
        fields["__RequestVerificationToken"] = token!;
        return await c.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private static string Location(HttpResponseMessage r) => r.Headers.Location?.ToString() ?? "";

    /// <summary>Starts a thread in the seeded board and answers its address.</summary>
    private static async Task<string> StartThreadAsync(HttpClient c, string title, string body, string board = "allgemeines")
    {
        var res = await SubmitAsync(c, "/forum/" + board + "/neu", new() { ["title"] = title, ["body"] = body, ["action"] = "save" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/forum/t/", Location(res));
        return Location(res);
    }

    private static async Task<HttpResponseMessage> ReplyAsync(HttpClient c, string threadUrl, string body) =>
        await SubmitAsync(c, threadUrl, new() { ["body"] = body, ["action"] = "save" });

    private async Task NoFloodAsync() => await _f.SetAsync(("floodSeconds", "0"));

    [Fact]
    public async Task A_fresh_forum_has_a_board_to_start_in_and_guests_may_read_but_not_write()
    {
        await _f.SetAsync(("registration", "false"));
        var guest = await _f.AsAsync("guest");
        var index = await guest.GetStringAsync("/forum");
        Assert.Contains("Allgemeine Diskussion", index);
        Assert.Contains("href=\"/forum/allgemeines\"", index);

        var write = await guest.GetAsync("/forum/allgemeines/neu");
        Assert.Equal(HttpStatusCode.Redirect, write.StatusCode);
        Assert.StartsWith("/anmelden?returnUrl=", Location(write));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/forum/gibt-es-nicht")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/forum/registrieren")).StatusCode);   // registration is off until the operator opens it
    }

    [Fact]
    public async Task Members_start_threads_and_reply_and_every_counter_follows()
    {
        await NoFloodAsync();
        var alice = await _f.AsAsync("alice");
        var bob = await _f.AsAsync("bob");
        var thread = await StartThreadAsync(alice, "Wie pflege ich Sauerteig?", "Meiner wird immer [b]zu sauer[/b].");

        var page = await bob.GetStringAsync(thread);
        Assert.Contains("Wie pflege ich Sauerteig?", page);
        Assert.Contains("<strong>zu sauer</strong>", page);
        var reply = await ReplyAsync(bob, thread, "Öfter füttern, dann wird er milder.");
        Assert.Equal(HttpStatusCode.Redirect, reply.StatusCode);
        Assert.StartsWith("/forum/p/", Location(reply));

        // /forum/p/{id} leads to the thread page with the post as the target.
        var jump = await bob.GetAsync(Location(reply));
        Assert.Equal(HttpStatusCode.Redirect, jump.StatusCode);
        Assert.Matches(@"^/forum/t/\d+-wie-pflege-ich-sauerteig\?p=\d+$", Location(jump));
        Assert.Contains("#p", await bob.GetStringAsync(Location(jump)));                 // the page itself scrolls to the post

        var board = await alice.GetStringAsync("/forum/allgemeines");
        var row = Regex.Match(board, "<div class=\"forum-row[^\"]*\">(?:(?!<div class=\"forum-row).)*?Wie pflege ich Sauerteig\\?.*?</div></div>", RegexOptions.Singleline).Value;
        Assert.Contains("<b>1</b>Antworten", row);
        Assert.Contains("forum-tag new", row);                         // bob answered after alice last looked: new for her
        await alice.GetStringAsync(thread);
        board = await alice.GetStringAsync("/forum/allgemeines");
        row = Regex.Match(board, "<div class=\"forum-row[^\"]*\">(?:(?!<div class=\"forum-row).)*?Wie pflege ich Sauerteig\\?.*?</div></div>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("forum-tag new", row);                   // ... and read once she opened it

        var index = await alice.GetStringAsync("/forum");
        Assert.Contains("Wie pflege ich Sauerteig?", index);             // the board's last post and the latest-posts box

        // A wrong slug is the same thread under its one right address.
        var id = Regex.Match(thread, @"/forum/t/(\d+)").Groups[1].Value;
        var old = await alice.GetAsync("/forum/t/" + id + "-alter-titel");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal(thread, Location(old));
    }

    [Fact]
    public async Task What_a_member_types_never_becomes_markup()
    {
        await NoFloodAsync();
        var alice = await _f.AsAsync("alice");
        var thread = await StartThreadAsync(alice, "Sicherheit <script>", "<script>alert(1)</script> <img src=x onerror=alert(2)>\n"
            + "[url=javascript:alert(3)]klick[/url] [url=https://example.org/a?b=1&c=2]gut[/url] [img]http://tracker.example/x.png[/img] [img]https://example.org/ok.png[/img]\n"
            + "[quote=Eve|1]zitat[/quote][code][b]kein fett[/b] <b>roh</b>[/code]\n[list][*]eins[*]zwei[/list] https://example.org/bare");
        var html = await alice.GetStringAsync(thread);
        var body = html[html.IndexOf("class=\"forum-posts\"", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("id=\"antworten\"", StringComparison.Ordinal)];

        Assert.DoesNotContain("<script>alert", body);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", body);
        Assert.DoesNotContain("<img src=x", body);
        Assert.DoesNotContain("href=\"javascript:", body);
        Assert.Contains("[url=javascript:alert(3)]", WebUtility.HtmlDecode(body));       // refused, shown as the text it is
        Assert.Contains("href=\"https://example.org/a?b=1&amp;c=2\" rel=\"nofollow ugc noopener\"", body);
        Assert.DoesNotContain("src=\"http://tracker.example", body);                      // pictures only over https
        Assert.Contains("<img class=\"forum-img\" src=\"https://example.org/ok.png\"", body);
        Assert.Contains("<blockquote class=\"forum-quote\"><cite><a href=\"/forum/p/1\">Eve</a></cite>", body);
        Assert.Contains("<pre class=\"forum-code\"><code>[b]kein fett[/b] &lt;b&gt;roh&lt;/b&gt;</code></pre>", body);
        Assert.Contains("<ul><li>eins</li><li>zwei</li></ul>", body);
        Assert.Contains("<a href=\"https://example.org/bare\"", body);
        Assert.Contains("Sicherheit &lt;script&gt;", html);                                // the title too
    }

    [Fact]
    public async Task A_board_for_members_is_invisible_to_guests_and_a_moderators_board_refuses_members()
    {
        await NoFloodAsync();
        var admin = await _f.AsAsync("admin");
        var made = await SubmitAsync(admin, "/admin/p/forum/boards/new", new()
        {
            ["title"] = "Intern", ["slug"] = "intern", ["description"] = "Nur für Mitglieder", ["category"] = "Verein",
            ["read"] = "members", ["write"] = "members", ["role"] = "", ["sort"] = "20"
        });
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        await SubmitAsync(admin, "/admin/p/forum/boards/new", new()
        {
            ["title"] = "Ankündigungen", ["slug"] = "news", ["read"] = "all", ["write"] = "mods", ["role"] = "", ["sort"] = "5"
        });

        var guest = await _f.AsAsync("guest");
        Assert.DoesNotContain("href=\"/forum/intern\"", await guest.GetStringAsync("/forum"));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/forum/intern")).StatusCode);

        var alice = await _f.AsAsync("alice");
        Assert.Contains("href=\"/forum/intern\"", await alice.GetStringAsync("/forum"));
        var secret = await StartThreadAsync(alice, "Vereinsinternes", "Das Passwort für den Keller ist geheim.", "intern");
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(secret)).StatusCode);
        Assert.DoesNotContain("Vereinsinternes", await guest.GetStringAsync("/forum/suche?q=Keller"));
        Assert.Contains("Vereinsinternes", await alice.GetStringAsync("/forum/suche?q=Keller"));
        Assert.DoesNotContain("/forum/intern", await guest.GetStringAsync("/sitemap.xml"));

        // Only moderators write in "Ankündigungen".
        var refused = WebUtility.HtmlDecode(await alice.GetStringAsync("/forum/news/neu"));
        Assert.Contains("Du darfst hier nicht schreiben.", refused);
        Assert.DoesNotContain("name=\"body\"", refused);
        var mod = await _f.AsAsync("mod");
        await StartThreadAsync(mod, "Willkommen im Forum", "Bitte freundlich bleiben.", "news");
    }

    [Fact]
    public async Task Moderators_pin_close_and_delete_and_members_cannot()
    {
        await NoFloodAsync();
        var alice = await _f.AsAsync("alice");
        var bob = await _f.AsAsync("bob");
        var mod = await _f.AsAsync("mod");
        var thread = await StartThreadAsync(alice, "Zu moderierendes Thema", "Erster Beitrag.");
        var reply = Location(await ReplyAsync(bob, thread, "Eine Antwort, die gelöscht wird."));
        var replyId = Regex.Match(reply, @"/forum/p/(\d+)").Groups[1].Value;

        // A member's own page has no moderation bar, and a forged action is refused.
        Assert.DoesNotContain("class=\"forum-mod\"", await bob.GetStringAsync(thread));
        var forged = await SubmitAsync(bob, thread, new() { ["action"] = "close" });
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        Assert.Contains("class=\"forum-mod\"", await mod.GetStringAsync(thread));
        await SubmitAsync(mod, thread, new() { ["action"] = "sticky" });
        await SubmitAsync(mod, thread, new() { ["action"] = "close" });
        var page = await bob.GetStringAsync(thread);
        Assert.Contains(">Angeheftet<", page);
        Assert.Contains("Dieses Thema ist geschlossen.", page);
        var closed = await ReplyAsync(bob, thread, "Noch eine?");
        Assert.Equal((HttpStatusCode)422, closed.StatusCode);

        var del = await SubmitAsync(mod, thread, new() { ["action"] = "delete_post", ["post"] = replyId });
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        page = await alice.GetStringAsync(thread);
        Assert.DoesNotContain("gelöscht wird", page);
        Assert.Contains("· 0 Antworten ·", WebUtility.HtmlDecode(page));                   // counted again from the rows
    }

    [Fact]
    public async Task Reports_reach_the_back_office_and_a_ban_stops_posting()
    {
        await NoFloodAsync();
        var alice = await _f.AsAsync("alice");
        var bob = await _f.AsAsync("bob");
        var admin = await _f.AsAsync("admin");
        var thread = await StartThreadAsync(alice, "Ein umstrittener Beitrag", "Werbung für etwas Unpassendes.");
        var postId = Regex.Match(await bob.GetStringAsync(thread), "href=\"/forum/p/(\\d+)/melden\"").Groups[1].Value;
        Assert.NotEmpty(postId);

        var reported = await SubmitAsync(bob, "/forum/p/" + postId + "/melden", new() { ["reason"] = "Das ist Spam." });
        Assert.Equal(HttpStatusCode.Redirect, reported.StatusCode);
        var reports = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/p/forum/reports"));
        Assert.Contains("Das ist Spam.", reports);
        Assert.Contains("Ein umstrittener Beitrag", reports);
        Assert.Contains("href=\"/admin/p/forum/reports\"", await admin.GetStringAsync("/admin/attention"));   // "Braucht Aufmerksamkeit"

        var bobId = await _f.MemberIdAsync("bob");
        await SubmitAsync(admin, "/admin/p/forum/members", new() { ["action"] = "ban", ["id"] = bobId.ToString() });
        var refused = await ReplyAsync(bob, thread, "Ich bin gesperrt.");
        Assert.Contains("Dein Konto ist im Forum gesperrt.", WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync()));
        await SubmitAsync(admin, "/admin/p/forum/members", new() { ["action"] = "unban", ["id"] = bobId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, (await ReplyAsync(bob, thread, "Wieder dabei.")).StatusCode);
    }

    [Fact]
    public async Task A_member_edits_their_own_post_and_not_somebody_elses()
    {
        await NoFloodAsync();
        var alice = await _f.AsAsync("alice");
        var bob = await _f.AsAsync("bob");
        var thread = await StartThreadAsync(alice, "Tippfehler", "Ein Tippfeler.");
        var postId = Regex.Match(await alice.GetStringAsync(thread), "href=\"/forum/p/(\\d+)/bearbeiten\"").Groups[1].Value;
        Assert.NotEmpty(postId);

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/forum/p/" + postId + "/bearbeiten")).StatusCode);
        var saved = await SubmitAsync(alice, "/forum/p/" + postId + "/bearbeiten", new() { ["title"] = "Tippfehler (gelöst)", ["body"] = "Ein Tippfehler.", ["action"] = "save" });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var page = WebUtility.HtmlDecode(await alice.GetStringAsync((await alice.GetAsync("/forum/p/" + postId)).Headers.Location!.ToString()));
        Assert.Contains("Tippfehler (gelöst)", page);
        Assert.Contains("Ein Tippfehler.", page);
        Assert.Contains("Zuletzt bearbeitet", page);
    }

    [Fact]
    public async Task Posting_too_fast_is_slowed_down()
    {
        await _f.SetAsync(("floodSeconds", "60"));
        try
        {
            var carol = await _f.AsAsync("carol");
            var thread = await StartThreadAsync(carol, "Schnellschuss", "Erster.");
            var again = await ReplyAsync(carol, thread, "Zweiter, sofort hinterher.");
            Assert.Equal((HttpStatusCode)422, again.StatusCode);
            var text = WebUtility.HtmlDecode(await again.Content.ReadAsStringAsync());
            Assert.Contains("Bitte warte 60 Sekunden", text);
            Assert.Contains("Zweiter, sofort hinterher.", text);                         // the text is kept, not lost
        }
        finally { await NoFloodAsync(); }
    }

    [Fact]
    public async Task An_erased_account_leaves_its_posts_without_its_name()
    {
        await NoFloodAsync();
        var dave = await _f.AsAsync("dave");
        var thread = await StartThreadAsync(dave, "Daves Frage", "Hat jemand Erfahrung damit?");
        var daveId = await _f.MemberIdAsync("dave");
        Assert.Contains("href=\"/forum/mitglied/" + daveId + "\"", await dave.GetStringAsync(thread));

        using (var scope = _f.App.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MemberService>().DeleteAsync(daveId));

        var guest = await _f.AsAsync("guest");
        var page = WebUtility.HtmlDecode(await guest.GetStringAsync(thread));
        Assert.Contains("Hat jemand Erfahrung damit?", page);                             // the conversation keeps its sense
        Assert.Contains("Gelöschtes Mitglied", page);
        Assert.DoesNotContain("/forum/mitglied/" + daveId, page);
        Assert.DoesNotContain(">Dave<", page);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/forum/mitglied/" + daveId)).StatusCode);
    }

    [Fact]
    public async Task With_registration_open_a_visitor_joins_and_is_signed_in()
    {
        await _f.SetAsync(("registration", "true"));
        var visitor = _f.App.Browser();
        await _f.InstallAsync();
        Assert.Contains("href=\"/forum/registrieren?returnUrl=", await visitor.GetStringAsync("/forum"));
        var res = await SubmitAsync(visitor, "/forum/registrieren", new()
        {
            ["name"] = "Neu Ling", ["email"] = "neuling@forum.example", ["password"] = "langes passwort", ["password2"] = "langes passwort", ["terms"] = "1", ["website"] = ""
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var index = WebUtility.HtmlDecode(await visitor.GetStringAsync(Location(res)));
        Assert.Contains("Willkommen im Forum!", index);
        Assert.Contains("Neu Ling", index);                                               // signed in: the toolbar names them

        var bot = await SubmitAsync(_f.App.Browser(), "/forum/registrieren", new()
        {
            ["name"] = "Bot", ["email"] = "bot@forum.example", ["password"] = "langes passwort", ["password2"] = "langes passwort", ["terms"] = "1", ["website"] = "http://spam"
        });
        Assert.Equal((HttpStatusCode)422, bot.StatusCode);
        await _f.SetAsync(("registration", "false"));
    }

    [Fact]
    public async Task The_back_office_pages_render_and_the_dashboard_knows_the_forum()
    {
        var admin = await _f.AsAsync("admin");
        foreach (var page in new[] { "dashboard", "boards", "boards/new", "threads", "reports", "members", "_settings" })
        {
            var res = await admin.GetAsync("/admin/p/forum/" + page);
            Assert.True(res.StatusCode == HttpStatusCode.OK || page == "_settings" && res.StatusCode == HttpStatusCode.Redirect, page + " → " + res.StatusCode);
        }
        var dash = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin"));
        Assert.Contains("value=\"card:forum\"", dash);                                  // the forum's own card on the dashboard
        var editor = await admin.GetStringAsync("/admin/templates/edit/1");
        Assert.Contains("forum-thread.html", editor);                                    // the forum's look is a template file
    }
}

/// <summary>Off means gone: the forum's load context is released when it is unloaded, after it served pages and touched its tables.</summary>
public class ForumUnloadTests
{
    /// <summary>
    /// Loaded, installed (its tables made, its first board seeded), unloaded: nothing of the forum may keep its load context alive — no static cache of a
    /// plugin type in the host, no reflection-based JSON. It is checked BEFORE the first page is served on purpose: after a request over HTTP even the host's
    /// own reference plugin is not freed yet (something in the web path holds it — a host matter, not the forum's), so a check after pages would test the host.
    /// </summary>
    [Fact]
    public async Task The_forum_can_be_unloaded_and_freed()
    {
        using var f = new ForumApp();
        await f.InstallAsync();
        Assert.True(Unload(f), "the forum's load context is still alive after unloading — something holds it (reflection JSON over a plugin type? a static cache?)");
        Assert.Equal(HttpStatusCode.NotFound, (await f.App.Browser().GetAsync("/forum/allgemeines")).StatusCode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Unload(ForumApp f)
    {
        using var scope = f.App.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<NativePluginHost>().Unload("forum", scope.ServiceProvider.GetRequiredService<PluginRegistry>());
    }
}

/// <summary>Every text key the forum's code uses has its German and English text (ForumStrings) — a missing one would show "forum.xyz" on a live page.</summary>
public class ForumStringsTests
{
    [Fact]
    public void Every_key_the_code_uses_has_both_languages()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MatCMS.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var dir = Path.Combine(root!.FullName, "plugins", "forum");
        var table = File.ReadAllText(Path.Combine(dir, "ForumStrings.cs"));
        var rows = Regex.Matches(table, @"\(""(forum\.[a-zA-Z0-9.]+)"", ""[^""]+"", ""[^""]+""\)").Select(m => m.Groups[1].Value).ToHashSet();
        var missing = Directory.GetFiles(dir, "*.cs").Where(f => !f.EndsWith("ForumStrings.cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"""(forum\.[a-z][a-zA-Z0-9.]*[a-zA-Z0-9])""").Select(m => m.Groups[1].Value))
            .Where(k => !k.StartsWith("forum.ph.") && k is not ("forum.reply" or "forum.newthread" or "forum.report") && !rows.Contains(k))
            .Distinct().ToList();
        Assert.True(missing.Count == 0, "without text: " + string.Join(", ", missing));
    }
}
