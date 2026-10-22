using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The assistant beyond pages: plugin actions (blog, shop), components, templates and plugins — each written through the admin's
/// own code, refused where it must be, and taken back by "Rückgängig".</summary>
public class AssistantMoreTests : IClassFixture<AssistantApp>
{
    private readonly AssistantApp _app;
    public AssistantMoreTests(AssistantApp app) => _app = app;

    private async Task<HttpClient> AdminAsync() { var c = _app.Browser(); await AppFactory.LoginAsync(c); return c; }

    private async Task<(int id, JsonArray steps)> SendAsync(HttpClient c, string message)
    {
        var html = await c.GetStringAsync("/admin/ai");
        var form = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(html)!), "__RequestVerificationToken" },
            { new StringContent(message), "message" },
            { new StringContent("false"), "locked" },
        };
        var res = await c.PostAsync("/admin/ai?handler=Send", form);
        var answer = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(answer["ok"]!.GetValue<bool>(), answer["error"]?.ToString());
        var conv = answer["id"]!.GetValue<int>();
        for (var n = 0; n < 400; n++)
        {
            var state = JsonNode.Parse(await c.GetStringAsync($"/admin/ai?handler=Steps&id={conv}"))!.AsObject();
            if (state["status"]!.GetValue<string>() == "idle") return (conv, state["steps"]!.AsArray());
            await Task.Delay(50);
        }
        throw new TimeoutException("the run did not finish");
    }

    private static List<JsonObject> Actions(JsonArray steps) =>
        steps.Select(s => s!.AsObject()).Where(s => s["kind"]!.GetValue<string>() == "action").Select(s => s["data"]!.AsObject()).ToList();

    private static async Task UndoAsync(HttpClient c, int conv)
    {
        var token = AppFactory.Token(await c.GetStringAsync("/admin/ai"))!;
        var r = await c.PostAsync("/admin/ai?handler=Undo", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = conv.ToString(), ["__RequestVerificationToken"] = token }));
        var o = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.True(o["ok"]!.GetValue<bool>(), o["message"]?.ToString());
    }

    private static string Act(string tool, string args) => $$"""{"message":"","actions":[{"tool":"{{tool}}","args":{{args}}}],"done":false}""";

    [Fact]
    public async Task Blog_posts_are_written_and_changed_through_the_blogs_actions_customers_data_is_never_offered_and_undo_takes_it_back()
    {
        var admin = await AdminAsync();
        _app.Ai.Say(
            Act("plugin_action", """{"name":"post.create","args":{"title":"Herbst am Berg","contentHtml":"<p>Neu!</p><script>x()</script>","publish":true}}"""),
            Act("plugin_action", """{"name":"post.update","args":{"slug":"herbst-am-berg","title":"Herbst in den Bergen"}}"""));
        var (conv, steps) = await SendAsync(admin, "Schreib einen Blogbeitrag.");

        var prompt = _app.Ai.Requests.Last().Messages[0].Content;
        Assert.Contains("post.create", prompt);
        Assert.DoesNotContain("order.list", prompt);           // orders carry customers' data
        Assert.DoesNotContain("post.restore", prompt);         // internal: only the undo runs it

        var acts = Actions(steps);
        Assert.All(acts.Take(2), a => Assert.True(a["ok"]!.GetValue<bool>(), a.ToJsonString()));
        // The blog adopted the old Posts table (it is no DbSet of the host any more).
        var title = _app.Use(db => db.Database.SqlQueryRaw<string>("SELECT Title AS Value FROM Posts WHERE Slug = {0}", "herbst-am-berg").ToList().Single());
        var body = _app.Use(db => db.Database.SqlQueryRaw<string>("SELECT ContentHtml AS Value FROM Posts WHERE Slug = {0}", "herbst-am-berg").ToList().Single());
        Assert.Equal("Herbst in den Bergen", title);
        Assert.DoesNotContain("script", body);

        await UndoAsync(admin, conv);
        Assert.Empty(_app.Use(db => db.Database.SqlQueryRaw<string>("SELECT Slug AS Value FROM Posts WHERE Slug = {0}", "herbst-am-berg").ToList()));
    }

    [Fact]
    public async Task Shop_products_are_created_and_changed_in_cents_and_undo_removes_them()
    {
        using (var scope = _app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var shop = db.Plugins.Single(p => p.Key == "shop");
            shop.Enabled = true;
            db.SaveChanges();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(shop.Id);
        }
        var admin = await AdminAsync();
        _app.Ai.Say(
            Act("plugin_action", """{"name":"category.create","args":{"name":"Tee"}}"""),
            Act("plugin_action", """{"name":"product.create","args":{"title":"Grüner Tee","price":"7,90","category":"tee"}}"""),
            Act("plugin_action", """{"name":"product.update","args":{"product":"gruener-tee","price_cents":850,"tags":["Bio"]}}"""),
            Act("plugin_action", """{"name":"product.read","args":{"product":"gruener-tee"}}"""));
        var (conv, steps) = await SendAsync(admin, "Leg Tee an.");
        var acts = Actions(steps);
        Assert.All(acts, a => Assert.True(a["ok"]!.GetValue<bool>(), a.ToJsonString()));
        var read = acts.Last()["result"]!["data"]!;
        Assert.Equal(850, read["priceCents"]!.GetValue<long>());
        Assert.Equal("tee", read["category"]!.GetValue<string>());
        Assert.Equal("Bio", read["tags"]!.GetValue<string>());

        await UndoAsync(admin, conv);
        using var s2 = _app.App.Services.CreateScope();
        var data = s2.ServiceProvider.GetRequiredService<AppDbContext>();
        var products = await data.Database.SqlQueryRaw<string>("SELECT slug AS Value FROM p_shop_products").ToListAsync();
        Assert.DoesNotContain("gruener-tee", products);
        var cats = await data.Database.SqlQueryRaw<string>("SELECT slug AS Value FROM p_shop_categories").ToListAsync();
        Assert.DoesNotContain("tee", cats);
    }

    [Fact]
    public async Task A_component_with_a_script_is_refused_a_clean_one_is_usable_at_once_and_undo_removes_it()
    {
        var admin = await AdminAsync();
        _app.Ai.Say(
            Act("create_component", """{"name":"Hinweis","fields":[{"id":"text","label":"Text","type":"text"}],"html":"<div onclick=\"x()\">{{text}}</div>"}"""),
            Act("create_component", """{"name":"Hinweis","fields":[{"id":"text","label":"Text","type":"text"}],"html":"<div class=\"hint\"><a href=\"{{url}}\">{{text}}</a></div>"}"""),
            Act("create_page", """{"title":"Mit Hinweis","slug":"mit-hinweis","blocks":[{"type":"hinweis","data":{"text":"Achtung"}}]}"""));
        var (conv, steps) = await SendAsync(admin, "Bau einen Hinweis-Block.");
        var acts = Actions(steps);
        Assert.False(acts[0]["ok"]!.GetValue<bool>());
        Assert.Contains("onclick", acts[0].ToJsonString());
        Assert.True(acts[1]["ok"]!.GetValue<bool>(), acts[1].ToJsonString());
        Assert.True(acts[2]["ok"]!.GetValue<bool>(), acts[2].ToJsonString());
        Assert.True(_app.Use(db => db.ContentBlocks.Any(b => b.BlockType == "hinweis")));

        await UndoAsync(admin, conv);
        Assert.False(_app.Use(db => db.Components.Any(c => c.Type == "hinweis")));
        Assert.False(_app.Use(db => db.Pages.Any(p => p.Slug == "mit-hinweis")));
    }

    [Fact]
    public async Task Template_files_are_changed_without_scripts_script_js_stays_the_users_and_undo_restores_them()
    {
        var before = _app.Use(db => db.Templates.AsNoTracking().Single(t => t.IsActive));
        var admin = await AdminAsync();
        _app.Ai.Say(
            Act("update_template", """{"files":{"body.html":"<header><script>alert(1)</script></header>{{content}}"}}"""),
            Act("update_template", """{"files":{"body.html":"<header class=\"top\">{{logo}}</header><main>{{content}}</main>","styles.css":".top{padding:2rem}","script.js":"alert(1)"}}"""),
            Act("read_template", "{}"));
        var (conv, steps) = await SendAsync(admin, "Mach den Kopf luftiger.");
        var acts = Actions(steps);
        Assert.False(acts[0]["ok"]!.GetValue<bool>());
        Assert.True(acts[1]["ok"]!.GetValue<bool>(), acts[1].ToJsonString());
        Assert.Contains("script.js", acts[1].ToJsonString());      // the warning says it was left alone
        var t = _app.Use(db => db.Templates.AsNoTracking().Single(x => x.Id == before.Id));
        Assert.Contains("<main>{{content}}</main>", t.LayoutHtml);
        Assert.Equal(".top{padding:2rem}", t.CustomCss);
        Assert.Equal(before.CustomJs, t.CustomJs);

        await UndoAsync(admin, conv);
        t = _app.Use(db => db.Templates.AsNoTracking().Single(x => x.Id == before.Id));
        Assert.Equal((before.LayoutHtml, before.CustomCss), (t.LayoutHtml, t.CustomCss));
    }

    [Fact]
    public async Task Plugin_code_from_the_ai_is_compiled_first_and_never_runs_without_the_admin()
    {
        int running;
        using (var scope = _app.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = new Models.Plugin { Name = "Läuft", Key = "laeuft", Kind = PluginKind.Script, Code = "// v1", Enabled = true };
            db.Plugins.Add(p);
            db.SaveChanges();
            running = p.Id;
        }
        var admin = await AdminAsync();
        _app.Ai.Say(
            Act("create_plugin", """{"name":"Kaputt","code":"das ist kein C#"}"""),
            Act("create_plugin", """{"name":"Neu von KI","code":"// nichts"}"""),
            Act("update_plugin", """{"plugin":"laeuft","code":"// v2"}"""));
        var (conv, steps) = await SendAsync(admin, "Schreib Plugins.");
        var acts = Actions(steps);
        Assert.False(acts[0]["ok"]!.GetValue<bool>());
        Assert.False(_app.Use(db => db.Plugins.Any(p => p.Name == "Kaputt")));
        Assert.True(acts[1]["ok"]!.GetValue<bool>(), acts[1].ToJsonString());
        Assert.False(_app.Use(db => db.Plugins.Single(p => p.Name == "Neu von KI").Enabled));
        Assert.True(acts[2]["ok"]!.GetValue<bool>(), acts[2].ToJsonString());
        var changed = _app.Use(db => db.Plugins.AsNoTracking().Single(p => p.Id == running));
        Assert.Equal(("// v2", false), (changed.Code, changed.Enabled));

        await UndoAsync(admin, conv);
        changed = _app.Use(db => db.Plugins.AsNoTracking().Single(p => p.Id == running));
        Assert.Equal(("// v1", true), (changed.Code, changed.Enabled));
        Assert.False(_app.Use(db => db.Plugins.Any(p => p.Name == "Neu von KI")));
    }

    [Theory]
    [InlineData("<a href=\"{{url}}\">x</a>", true)]
    [InlineData("<a href=\"/kontakt\">x</a><img src=\"https://x.de/a.png\">", true)]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", true)]
    [InlineData("<style>.a{color:red}</style><svg viewBox=\"0 0 1 1\"><path d=\"M0 0\"/></svg>", true)]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>", false)]
    [InlineData("<a href=\"JAVASCRIPT:alert(1)\">x</a>", false)]
    [InlineData("<img src=\"data:image/svg+xml,<svg onload=x>\">", false)]
    [InlineData("<svg><animate onbegin=\"x()\"/></svg>", false)]
    [InlineData("<iframe srcdoc=\"x\"></iframe>", false)]
    [InlineData("<div style=\"background:url(javascript:x)\"></div>", false)]
    [InlineData("<base href=\"https://evil.example/\">", false)]
    public void The_markup_check_lets_layouts_through_and_stops_what_runs(string html, bool ok) =>
        Assert.Equal(ok, AssistantMarkup.Problems(html).Count == 0);
}
