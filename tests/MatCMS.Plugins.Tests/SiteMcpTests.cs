using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The site's own MCP server (<c>/mcp</c>, <see cref="SiteMcpServer"/>) through the real SDK client against the whole application: closed until
/// switched on, a key with the right rights, the assistant's write path (nested blocks survive), plugin operations by plugin right, and undo.
/// </summary>
public class SiteMcpTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public SiteMcpTests(AppFactory app) => _app = app;

    private T Use<T>(Func<AppDbContext, T> f)
    {
        using var scope = _app.Services.CreateScope();
        return f(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task SetMcpAsync(bool on)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == SettingKeys.McpEnabled);
        if (row is null) db.SiteSettings.Add(new SiteSetting { Key = SettingKeys.McpEnabled, Value = on ? "1" : "0" });
        else row.Value = on ? "1" : "0";
        await db.SaveChangesAsync();
    }

    private async Task<(int Id, string Key)> KeyAsync(string name, params string[] rights)
    {
        using var scope = _app.Services.CreateScope();
        var (row, key) = await scope.ServiceProvider.GetRequiredService<SiteApiKeyService>().CreateAsync(name, rights, null);
        return (row.Id, key);
    }

    private async Task<McpClient> ClientAsync(string key)
    {
        var http = _app.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonObject Body(CallToolResult r) =>
        JsonNode.Parse(r.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();

    private static async Task<JsonObject> CallAsync(McpClient c, string tool, object args)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        return Body(await c.CallToolAsync(tool, dict));
    }

    [Fact]
    public async Task The_door_is_closed_until_switched_on_and_then_needs_a_usable_key()
    {
        var http = _app.CreateClient();
        HttpRequestMessage Post(string? key)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            m.Headers.Accept.ParseAdd("application/json");
            m.Headers.Accept.ParseAdd("text/event-stream");
            if (key is not null) m.Headers.Authorization = new("Bearer", key);
            return m;
        }
        var (_, key) = await KeyAsync("door", "core.pages:read");

        await SetMcpAsync(false);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Post(key))).StatusCode);

        await SetMcpAsync(true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post("msk_doesnotexist0000000000000000000000000000"))).StatusCode);
        // The switch is a protected setting: no rollout, content op or restore may open the door, and no backup carries it.
        Assert.True(SettingKeys.IsProtected(SettingKeys.McpEnabled));
        Assert.True(SettingKeys.NotInBackup(SettingKeys.McpEnabled));
    }

    [Fact]
    public async Task Tools_list_shows_only_what_the_key_may_call_with_hints_and_the_site_in_the_instructions()
    {
        await SetMcpAsync(true);
        var (_, reader) = await KeyAsync("reader", "core.pages:read");
        await using var c = await ClientAsync(reader);
        Assert.Contains("MatCMS website", c.ServerInstructions);

        var tools = (await c.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.Contains("site_overview", tools.Keys);
        Assert.Contains("block_catalog", tools.Keys);
        Assert.Contains("read_page", tools.Keys);
        Assert.DoesNotContain("create_page", tools.Keys);
        Assert.DoesNotContain("upload_media", tools.Keys);
        Assert.DoesNotContain("undo_last_change", tools.Keys);   // nothing to undo for a key that cannot write
        Assert.DoesNotContain(SiteMcpServer.RunOperation, tools.Keys);
        Assert.True(tools["read_page"].ProtocolTool.Annotations!.ReadOnlyHint);

        // Writing refused even if a client calls a tool it was not shown.
        var refused = await c.CallToolAsync("create_page", new Dictionary<string, object?> { ["title"] = "Nope" });
        Assert.True(refused.IsError);
        Assert.Empty(Use(db => db.Pages.Where(p => p.Title == "Nope").ToList()));

        var (_, writer) = await KeyAsync("writer", "core.pages:write");
        await using var w = await ClientAsync(writer);
        var wt = (await w.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.Contains("site_overview", wt.Keys);            // write includes read
        Assert.True(wt["set_page_blocks"].ProtocolTool.Annotations!.DestructiveHint);
        Assert.False(wt["create_page"].ProtocolTool.Annotations!.DestructiveHint);
        Assert.Contains("undo_last_change", wt.Keys);
        Assert.DoesNotContain("update_theme", wt.Keys);       // design is its own right
    }

    [Fact]
    public async Task Pages_are_written_through_the_assistants_path_with_nested_blocks_logged_and_undone()
    {
        await SetMcpAsync(true);
        var (keyId, key) = await KeyAsync("Claude Code", "core.pages:write");
        await using var c = await ClientAsync(key);

        var catalog = await CallAsync(c, "block_catalog", new { });
        Assert.Contains("columns", catalog["catalog"]!.GetValue<string>());

        var created = await CallAsync(c, "create_page", new
        {
            title = "MCP Spalten", slug = "mcp-spalten", published = true,
            blocks = new object[]
            {
                new { type = "columns", data = new { heading = "Drei Gründe", columns = "3" }, children = new object[]
                {
                    new { type = "column", data = new { title = "Schnell", body = "<p>Ohne <strong>Cloud</strong></p><script>x()</script>" } },
                    new { type = "column", data = new { title = "Sicher", body = "<p>Mit Schlüssel</p>" } },
                } },
            },
        });
        Assert.True(created["ok"]!.GetValue<bool>(), created.ToJsonString());

        var page = Use(db => db.Pages.Single(p => p.Slug == "mcp-spalten"));
        var blocks = Use(db => db.ContentBlocks.Where(b => b.PageId == page.Id).ToList());
        var parent = Assert.Single(blocks, b => b.BlockType == "columns");
        var kids = blocks.Where(b => b.ParentId == parent.Id).ToList();
        Assert.Equal(2, kids.Count);
        Assert.All(kids, k => Assert.Equal("column", k.BlockType));
        Assert.DoesNotContain("<script", string.Concat(kids.Select(k => k.DataJson)));   // SafeHtml, as in the assistant

        var read = await CallAsync(c, "read_page", new { page = "mcp-spalten" });
        Assert.Contains("Schnell", read.ToJsonString());

        // One run per write call, logged as the key's MCP conversation — what the admin reads in KI-Assistent.
        var conv = Use(db => db.AiConversations.Single(x => x.Source == AiConversation.SourceMcp && x.ApiKeyId == keyId));
        Assert.Equal("api:Claude Code", conv.CreatedBy);
        Assert.Contains(Use(db => db.AiSteps.Where(s => s.ConversationId == conv.Id).ToList()), s => s.Kind == "action" && s.DataJson!.Contains("create_page"));

        var undone = await CallAsync(c, "undo_last_change", new { });
        Assert.True(undone["ok"]!.GetValue<bool>(), undone.ToJsonString());
        Assert.Empty(Use(db => db.Pages.Where(p => p.Slug == "mcp-spalten").ToList()));
        Assert.True((await c.CallToolAsync("undo_last_change", new Dictionary<string, object?>())).IsError);   // nothing left

        // The admin's page shows the log, but nobody types into it.
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var html = await admin.GetStringAsync($"/admin/ai?id={conv.Id}");
        Assert.Contains("Claude Code", html);
        Assert.Contains("id=\"ai-input\" rows=\"3\"", html);
        Assert.Matches("<textarea id=\"ai-input\"[^>]*disabled", html);
    }

    [Fact]
    public async Task Media_upload_goes_through_the_upload_rules()
    {
        await SetMcpAsync(true);
        var (_, key) = await KeyAsync("media", "core.media:write");
        await using var c = await ClientAsync(key);
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/><script>alert(1)</script></svg>";
        var up = await CallAsync(c, "upload_media", new { fileName = "logo.svg", dataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)), alt = "Logo" });
        Assert.True(up["ok"]!.GetValue<bool>(), up.ToJsonString());
        var url = up["result"]!["url"]!.GetValue<string>();
        var media = Use(db => db.Media.Single(m => m.Url == url));
        Assert.Equal("Logo", media.Alt);
        var stored = File.ReadAllText(Path.Combine(_app.Root, "appdata", "uploads", url["/uploads/".Length..]));
        Assert.DoesNotContain("script", stored);

        var bad = await c.CallToolAsync("upload_media", new Dictionary<string, object?> { ["fileName"] = "x.exe", ["dataBase64"] = Convert.ToBase64String([1, 2, 3]) });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Plugin_operations_follow_the_plugin_rights_internal_ones_never()
    {
        await BlogFixture.InstallAsync(_app.Services);
        await SetMcpAsync(true);

        var (_, none) = await KeyAsync("pages only", "core.pages:read");
        await using (var c = await ClientAsync(none))
            Assert.DoesNotContain(SiteMcpServer.ListOperations, (await c.ListToolsAsync()).Select(t => t.Name));

        var (_, blog) = await KeyAsync("blog", "blog:write");
        await using var b = await ClientAsync(blog);
        var ops = (await CallAsync(b, SiteMcpServer.ListOperations, new { }))["operations"]!.AsArray().Select(o => o!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("blog.post.create", ops);      // qualified names, as the cloud lists them
        Assert.DoesNotContain("blog.post.restore", ops);         // internal: only the host's undo runs it

        var made = await CallAsync(b, SiteMcpServer.RunOperation, new { name = "blog.post.create", args = new { title = "Aus Claude", contentHtml = "<p>Hallo</p>", publish = true } });
        Assert.True(made["ok"]!.GetValue<bool>(), made.ToJsonString());
        Assert.Single(Use(db => db.Database.SqlQueryRaw<string>("SELECT Slug AS Value FROM Posts WHERE Title = {0}", "Aus Claude").ToList()));

        var internalOp = await b.CallToolAsync(SiteMcpServer.RunOperation, new Dictionary<string, object?> { ["name"] = "post.restore", ["args"] = new { } });
        Assert.True(internalOp.IsError);

        Assert.True((await CallAsync(b, "undo_last_change", new { }))["ok"]!.GetValue<bool>());
        Assert.Empty(Use(db => db.Database.SqlQueryRaw<string>("SELECT Slug AS Value FROM Posts WHERE Title = {0}", "Aus Claude").ToList()));
    }

    [Fact]
    public async Task The_admin_switches_it_on_and_makes_a_key_with_core_rights()
    {
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var index = await admin.GetStringAsync("/admin/apikeys");
        Assert.Contains("id=\"mcp\"", index);
        var on = await admin.PostAsync("/admin/apikeys?handler=Mcp", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["on"] = "true", ["__RequestVerificationToken"] = AppFactory.Token(index)!,
        }));
        Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);
        Assert.Contains("/mcp", await admin.GetStringAsync("/admin/apikeys"));
        Assert.Equal("1", Use(db => db.SiteSettings.Single(s => s.Key == SettingKeys.McpEnabled).Value));

        var create = await admin.GetStringAsync("/admin/apikeys/create");
        Assert.Contains("core.pages:write", create);
        var made = await admin.PostAsync("/admin/apikeys/create", new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("name", "Claude Desktop"), new("rights", "core.pages:write"), new("rights", "core.media:read"),
            new("__RequestVerificationToken", AppFactory.Token(create)!),
        }));
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        Assert.Equal("core.media:read,core.pages:write", Use(db => db.SiteApiKeys.Single(k => k.Name == "Claude Desktop").Rights));
    }

    [Fact]
    public void Customers_data_needs_the_personal_right_and_writing_it_write_as_well()
    {
        var info = new PluginInfo("shop", "Shop", "1", "", 2);
        Services.PluginRegistry.OperationEntry Op(bool writes, bool personal, bool isInternal = false) =>
            new("shop", info, "x", (_, _, _) => Task.FromResult<JsonNode?>(null), new PluginOperationSpec("x", "x", "{}", writes, personal, isInternal));

        Assert.True(SiteMcpServer.MayRun("shop:read", Op(writes: false, personal: false)));
        Assert.False(SiteMcpServer.MayRun("shop:read", Op(writes: true, personal: false)));
        Assert.True(SiteMcpServer.MayRun("shop:write", Op(writes: true, personal: false)));
        Assert.False(SiteMcpServer.MayRun("shop:write", Op(writes: false, personal: true)));       // write does not imply personal
        Assert.True(SiteMcpServer.MayRun("shop:personal", Op(writes: false, personal: true)));
        Assert.False(SiteMcpServer.MayRun("shop:personal", Op(writes: true, personal: true)));
        Assert.True(SiteMcpServer.MayRun("shop:personal,shop:write", Op(writes: true, personal: true)));
        Assert.False(SiteMcpServer.MayRun("shop:personal,shop:write", Op(writes: false, personal: false, isInternal: true)));
        Assert.False(SiteMcpServer.MayRun("blog:write", Op(writes: false, personal: false)));       // another plugin's right

        // Core rights are cleaned like plugin rights; an invented area or "personal" on one is dropped.
        Assert.Equal("core.pages:write,shop:read", SiteApiKeyService.CleanRights(["core.pages:write", "core.secrets:write", "core.pages:personal", "shop:read"]));
    }
}
