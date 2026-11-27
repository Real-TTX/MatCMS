using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The forum's operations (plugins/forum/ForumOps.cs) for the AI assistant, the cloud and MCP — run against the forum's built DLL inside the whole
/// application: every declared operation is there, boards and threads go create → read → change → undo, bad input is refused, internal undo operations
/// run only for the host, and reports and members never reach the assistant.
/// </summary>
public class ForumOpsTests : IClassFixture<ForumApp>
{
    private readonly ForumApp _f;
    public ForumOpsTests(ForumApp f) { _f = f; PluginPostLimiter.Reset(); }

    private static readonly string[] Declared =
    [
        "board.list", "board.read", "board.create", "board.update", "board.delete", "board.remove", "board.restore",
        "thread.list", "thread.read", "thread.update", "thread.restore",
        "report.list", "report.update", "report.restore", "member.list", "member.update", "member.restore",
    ];
    private static readonly string[] InternalOps = ["board.remove", "board.restore", "thread.restore", "report.restore", "member.restore"];
    private static readonly string[] Personal = ["report.list", "report.update", "report.restore", "member.list", "member.update", "member.restore"];

    /// <summary>The name a caller uses: the QUALIFIED one (<c>forum.board.list</c>) where the host qualifies operation names, else the short one.</summary>
    private string Name(string op)
    {
        if (op.StartsWith("forum.", StringComparison.Ordinal)) return op;
        var ops = _f.App.Services.GetRequiredService<PluginContributions>();
        return ops.HasOperation("forum." + op) ? "forum." + op : op;
    }

    private async Task<JsonObject> RunAsync(string op, JsonObject args, bool allowInternal = false)
    {
        await _f.InstallAsync();
        using var scope = _f.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>()
            .RunOperationAsync(scope.ServiceProvider, Name(op), args, default, allowInternal))!;
    }

    /// <summary>Runs the undo an answer carries, as the assistant's "Rückgängig" does.</summary>
    private async Task<JsonObject> UndoAsync(JsonObject answer)
    {
        var undo = (JsonObject)answer["undo"]!;
        return await RunAsync(undo["name"]!.GetValue<string>(), (JsonObject)undo["args"]!.DeepClone(), allowInternal: true);
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient c, string url, Dictionary<string, string> fields)
    {
        var page = await c.GetStringAsync(url);
        fields["__RequestVerificationToken"] = AppFactory.Token(page)!;
        return await c.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    /// <summary>A member starts a thread on the public site (the way threads come about) and the thread's id comes back.</summary>
    private async Task<long> StartThreadAsync(string who, string title, string board = "allgemeines")
    {
        await _f.SetAsync(("floodSeconds", "0"));
        var c = await _f.AsAsync(who);
        var res = await SubmitAsync(c, "/forum/" + board + "/neu", new() { ["title"] = title, ["body"] = "Erster Beitrag zu " + title, ["action"] = "save" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!.ToString();
        Assert.StartsWith("/forum/t/", loc);
        return long.Parse(loc["/forum/t/".Length..].Split('-', '?')[0]);
    }

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags_and_the_assistant_never_sees_personal_or_internal_ones()
    {
        await _f.InstallAsync();
        var contributions = _f.App.Services.GetRequiredService<PluginContributions>();
        var registry = _f.App.Services.GetRequiredService<PluginRegistry>();
        foreach (var op in Declared)
        {
            Assert.True(contributions.HasOperation(Name(op)), op);
            var spec = registry.Operations.Single(o => o.Owner == "forum" && o.Name == op).Spec!;
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), op);
            Assert.Equal(InternalOps.Contains(op), spec.Internal);
            Assert.Equal(Personal.Contains(op), spec.PersonalData);
            Assert.Equal(!op.EndsWith(".list") && !op.EndsWith(".read"), spec.Writes);
        }
        var offered = contributions.AssistantOperations.Where(o => o.Owner == "forum").Select(o => o.Name).ToList();
        Assert.Contains("board.create", offered);
        Assert.Contains("thread.update", offered);
        Assert.DoesNotContain(offered, n => n.StartsWith("member.") || n.StartsWith("report.") || n.EndsWith(".remove") || n.EndsWith(".restore"));
    }

    [Fact]
    public async Task A_board_goes_create_read_update_undo_and_its_creation_is_undone_too()
    {
        var made = await RunAsync("board.create", new JsonObject
        {
            ["title"] = "KI-Ecke", ["description"] = "Fragen an die KI", ["category"] = "Technik", ["read"] = "members", ["write"] = "members"
        });
        Assert.Equal("applied", Status(made));
        var id = made["data"]!["id"]!.GetValue<long>();
        Assert.Equal("ki-ecke", made["data"]!["slug"]!.GetValue<string>());
        Assert.EndsWith("board.remove", made["undo"]!["name"]!.GetValue<string>());

        // the same title again is that board, never a second one
        Assert.Equal("skipped-exists", Status(await RunAsync("board.create", new JsonObject { ["title"] = "ki-ecke" })));

        var read = await RunAsync("board.read", new JsonObject { ["slug"] = "ki-ecke" });
        Assert.Equal("applied", Status(read));
        Assert.Equal(id, read["data"]!["id"]!.GetValue<long>());
        Assert.Equal("members", read["data"]!["read"]!.GetValue<string>());
        Assert.Contains((await RunAsync("board.list", new JsonObject()))["data"]!["boards"]!.AsArray().OfType<JsonObject>(), b => b["id"]!.GetValue<long>() == id);

        var changed = await RunAsync("board.update", new JsonObject { ["id"] = id, ["title"] = "KI-Werkstatt", ["locked"] = true, ["read"] = "all" });
        Assert.Equal("applied", Status(changed));
        var after = (await RunAsync("board.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("KI-Werkstatt", after["title"]!.GetValue<string>());
        Assert.True(after["locked"]!.GetValue<bool>());
        Assert.Equal("all", after["read"]!.GetValue<string>());
        Assert.Equal("Fragen an die KI", after["description"]!.GetValue<string>());     // what was not named stays

        Assert.Equal("applied", Status(await UndoAsync(changed)));
        var back = (await RunAsync("board.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("KI-Ecke", back["title"]!.GetValue<string>());
        Assert.False(back["locked"]!.GetValue<bool>());
        Assert.Equal("members", back["read"]!.GetValue<string>());
        Assert.Equal("ki-ecke", back["slug"]!.GetValue<string>());

        Assert.Equal("applied", Status(await UndoAsync(made)));
        Assert.Equal("failed", Status(await RunAsync("board.read", new JsonObject { ["id"] = id })));
    }

    [Fact]
    public async Task Bad_board_input_is_refused_and_nothing_is_written()
    {
        Assert.Equal("failed", Status(await RunAsync("board.create", new JsonObject { ["title"] = "X" })));                                   // too short
        Assert.Equal("failed", Status(await RunAsync("board.create", new JsonObject { ["title"] = "Geheim", ["read"] = "everyone" })));       // unknown access
        Assert.Equal("failed", Status(await RunAsync("board.create", new JsonObject { ["title"] = "Geheim", ["write"] = "all" })));           // nobody writes unsigned
        Assert.Equal("failed", Status(await RunAsync("board.create", new JsonObject { ["title"] = "Geheim", ["read"] = "role" })));          // a role without its name
        Assert.Equal("failed", Status(await RunAsync("board.update", new JsonObject { ["id"] = 999999, ["title"] = "Gibt es nicht" })));
        Assert.Equal("failed", Status(await RunAsync("thread.update", new JsonObject { ["id"] = 999999, ["closed"] = true })));
        var boards = (await RunAsync("board.list", new JsonObject()))["data"]!["boards"]!.AsArray();
        Assert.DoesNotContain(boards.OfType<JsonObject>(), b => b["title"]!.GetValue<string>() == "Geheim");

        // a reserved address is never a board's
        var made = await RunAsync("board.create", new JsonObject { ["title"] = "Suchen", ["slug"] = "suche" });
        Assert.Equal("applied", Status(made));
        Assert.NotEqual("suche", made["data"]!["slug"]!.GetValue<string>());
        await UndoAsync(made);
    }

    [Fact]
    public async Task Internal_operations_run_only_when_the_host_asks()
    {
        await _f.InstallAsync();
        foreach (var op in InternalOps)
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(op, new JsonObject { ["id"] = 1 }));
    }

    [Fact]
    public async Task A_thread_is_listed_read_moderated_moved_and_the_moderation_is_undone()
    {
        var threadId = await StartThreadAsync("olga", "Wie moderiert die KI?");
        var target = await RunAsync("board.create", new JsonObject { ["title"] = "Archiv der KI" });
        var targetId = target["data"]!["id"]!.GetValue<long>();

        var list = await RunAsync("thread.list", new JsonObject { ["board"] = "allgemeines", ["limit"] = 100 });
        Assert.Equal("applied", Status(list));
        var row = list["data"]!["threads"]!.AsArray().OfType<JsonObject>().Single(t => t["id"]!.GetValue<long>() == threadId);
        Assert.Equal("Olga", row["author"]!.GetValue<string>());
        Assert.Contains(((await RunAsync("thread.list", new JsonObject { ["search"] = "moderiert" }))["data"]!["threads"]!).AsArray().OfType<JsonObject>(),
            t => t["id"]!.GetValue<long>() == threadId);

        var read = await RunAsync("thread.read", new JsonObject { ["id"] = threadId });
        Assert.Equal("applied", Status(read));
        var post = read["data"]!["posts"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal("Erster Beitrag zu Wie moderiert die KI?", post["body"]!.GetValue<string>());
        Assert.True(post["first"]!.GetValue<bool>());

        Assert.Equal("failed", Status(await RunAsync("thread.update", new JsonObject { ["id"] = threadId, ["title"] = "x" })));
        Assert.Equal("failed", Status(await RunAsync("thread.update", new JsonObject { ["id"] = threadId, ["board"] = "gibt-es-nicht" })));
        Assert.Equal("skipped", Status(await RunAsync("thread.update", new JsonObject { ["id"] = threadId })));

        var moderated = await RunAsync("thread.update", new JsonObject
        {
            ["id"] = threadId, ["title"] = "Wie moderiert die KI? (erledigt)", ["sticky"] = true, ["closed"] = true, ["board"] = targetId
        });
        Assert.Equal("applied", Status(moderated));
        Assert.EndsWith("thread.restore", moderated["undo"]!["name"]!.GetValue<string>());
        var now = (await RunAsync("thread.read", new JsonObject { ["id"] = threadId }))["data"]!;
        Assert.Equal("Wie moderiert die KI? (erledigt)", now["title"]!.GetValue<string>());
        Assert.True(now["sticky"]!.GetValue<bool>());
        Assert.True(now["closed"]!.GetValue<bool>());
        Assert.Equal(targetId, now["boardId"]!.GetValue<long>());
        Assert.Equal(1, (await RunAsync("board.read", new JsonObject { ["id"] = targetId }))["data"]!["threads"]!.GetValue<long>());   // counters follow the move

        // a board with threads is not deleted (and its creation's undo leaves it standing)
        Assert.Equal("failed", Status(await RunAsync("board.delete", new JsonObject { ["id"] = targetId })));
        Assert.Equal("skipped", Status(await UndoAsync(target)));

        Assert.Equal("applied", Status(await UndoAsync(moderated)));
        var back = (await RunAsync("thread.read", new JsonObject { ["id"] = threadId }))["data"]!;
        Assert.Equal("Wie moderiert die KI?", back["title"]!.GetValue<string>());
        Assert.False(back["sticky"]!.GetValue<bool>());
        Assert.False(back["closed"]!.GetValue<bool>());
        Assert.NotEqual(targetId, back["boardId"]!.GetValue<long>());
        Assert.Equal(0, (await RunAsync("board.read", new JsonObject { ["id"] = targetId }))["data"]!["threads"]!.GetValue<long>());

        // empty again: now it may go, and its undo brings it back
        var deleted = await RunAsync("board.delete", new JsonObject { ["id"] = targetId });
        Assert.Equal("applied", Status(deleted));
        Assert.Equal("failed", Status(await RunAsync("board.read", new JsonObject { ["id"] = targetId })));
        Assert.Equal("applied", Status(await UndoAsync(deleted)));
        var again = (await RunAsync("board.read", new JsonObject { ["slug"] = "archiv-der-ki" }))["data"]!;
        Assert.Equal("Archiv der KI", again["title"]!.GetValue<string>());
        await RunAsync("board.remove", new JsonObject { ["id"] = again["id"]!.GetValue<long>() }, allowInternal: true);
    }

    [Fact]
    public async Task Reports_are_closed_and_reopened_and_members_banned_and_unbanned_with_undo()
    {
        var threadId = await StartThreadAsync("paul", "Ein gemeldetes Thema");
        var postId = (await RunAsync("thread.read", new JsonObject { ["id"] = threadId }))["data"]!["posts"]![0]!["id"]!.GetValue<long>();
        var reporter = await _f.AsAsync("rita");
        var res = await SubmitAsync(reporter, "/forum/p/" + postId + "/melden", new() { ["reason"] = "Werbung im Beitrag" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var open = await RunAsync("report.list", new JsonObject());
        var report = open["data"]!["reports"]!.AsArray().OfType<JsonObject>().Single(r => r["post"]?["id"]?.GetValue<long>() == postId);
        Assert.Equal("Werbung im Beitrag", report["reason"]!.GetValue<string>());
        Assert.Equal("Rita", report["reporter"]!.GetValue<string>());
        var reportId = report["id"]!.GetValue<long>();

        Assert.Equal("failed", Status(await RunAsync("report.update", new JsonObject { ["id"] = reportId })));     // done must be said
        var done = await RunAsync("report.update", new JsonObject { ["id"] = reportId, ["done"] = true });
        Assert.Equal("applied", Status(done));
        Assert.DoesNotContain((await RunAsync("report.list", new JsonObject()))["data"]!["reports"]!.AsArray().OfType<JsonObject>(), r => r["id"]!.GetValue<long>() == reportId);
        Assert.Contains((await RunAsync("report.list", new JsonObject { ["open"] = false }))["data"]!["reports"]!.AsArray().OfType<JsonObject>(), r => r["id"]!.GetValue<long>() == reportId);
        Assert.Equal("applied", Status(await UndoAsync(done)));
        Assert.Contains((await RunAsync("report.list", new JsonObject()))["data"]!["reports"]!.AsArray().OfType<JsonObject>(), r => r["id"]!.GetValue<long>() == reportId);

        var paul = await _f.MemberIdAsync("paul");
        var member = (await RunAsync("member.list", new JsonObject { ["search"] = "paul@" }))["data"]!["members"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal(paul, member["id"]!.GetValue<long>());
        Assert.Equal("paul@forum.example", member["email"]!.GetValue<string>());
        Assert.False(member["banned"]!.GetValue<bool>());

        Assert.Equal("failed", Status(await RunAsync("member.update", new JsonObject { ["id"] = 999999, ["banned"] = true })));
        var ban = await RunAsync("member.update", new JsonObject { ["id"] = paul, ["banned"] = true, ["banReason"] = "Werbung" });
        Assert.Equal("applied", Status(ban));
        var banned = (await RunAsync("member.list", new JsonObject { ["search"] = "paul@" }))["data"]!["members"]![0]!;
        Assert.True(banned["banned"]!.GetValue<bool>());
        Assert.Equal("Werbung", banned["banReason"]!.GetValue<string>());
        // a banned member cannot write any more
        var page = await (await _f.AsAsync("paul")).GetAsync("/forum/allgemeines/neu");
        Assert.DoesNotContain("name=\"title\"", await page.Content.ReadAsStringAsync());

        Assert.Equal("applied", Status(await UndoAsync(ban)));
        Assert.False((await RunAsync("member.list", new JsonObject { ["search"] = "paul@" }))["data"]!["members"]![0]!["banned"]!.GetValue<bool>());
    }
}
