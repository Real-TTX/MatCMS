using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The bot's operations: usage and limits (no personal data — offered to the assistant), and the stored conversations (personal data — never offered to
/// the assistant, whose every answer goes to an outside model).
/// </summary>
public class BotOpsTests : IClassFixture<BotFixture>
{
    private readonly BotFixture _f;
    public BotOpsTests(BotFixture f) { _f = f; PluginPostLimiter.Reset(); }

    private string Op(string name)
    {
        var c = _f.App.Services.GetRequiredService<PluginContributions>();
        return c.HasOperation("bot." + name) ? "bot." + name : name;
    }

    private async Task<JsonObject> RunAsync(string op, JsonObject args)
    {
        using var scope = _f.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, Op(op), args))!;
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();

    private async Task AskAsync(string question, string session)
    {
        _f.Ai.Say("Wir haben Montag bis Freitag geöffnet.");
        var c = _f.App.Browser();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BotOpsTest/" + Guid.NewGuid().ToString("N")[..8]);
        var res = await c.PostAsync("/ai-bot/ask", new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = question, ["history"] = "[]", ["session"] = session, ["lang"] = "de" }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public void The_operations_are_reads_and_the_conversations_are_personal_data()
    {
        var c = _f.App.Services.GetRequiredService<PluginContributions>();
        var registry = _f.App.Services.GetRequiredService<PluginRegistry>();
        PluginOperationSpec Spec(string name) => registry.Operations.Single(o => o.Owner == "bot" && o.Name == name).Spec!;
        foreach (var name in new[] { "usage.read", "chat.list", "chat.read" }) { Assert.True(c.HasOperation(Op(name)), name); Assert.False(Spec(name).Writes, name); Assert.False(Spec(name).Internal, name); }
        Assert.False(Spec("usage.read").PersonalData);
        Assert.True(Spec("chat.list").PersonalData);
        Assert.True(Spec("chat.read").PersonalData);
        var assistant = c.AssistantOperations.Where(o => o.Owner == "bot").Select(o => o.Name).ToList();
        Assert.Contains("usage.read", assistant);
        Assert.DoesNotContain("chat.list", assistant);
        Assert.DoesNotContain("chat.read", assistant);
    }

    [Fact]
    public async Task Usage_counts_what_was_asked_and_names_the_limits()
    {
        var before = await RunAsync("usage.read", new JsonObject());
        var asked = before["data"]!["today"]!["questions"]!.GetValue<long>();
        await AskAsync("Wann habt ihr Öffnungszeiten?", "opsusage" + Guid.NewGuid().ToString("N")[..8]);
        var after = await RunAsync("usage.read", new JsonObject { ["days"] = 3 });
        Assert.Equal("applied", Status(after));
        var data = (JsonObject)after["data"]!;
        Assert.Equal(asked + 1, data["today"]!["questions"]!.GetValue<long>());
        Assert.True(data["month"]!["tokens"]!.GetValue<long>() > 0);
        Assert.Equal(30, data["limits"]!["questionsPerVisitorDay"]!.GetValue<int>());
        Assert.Equal(7, data["status"]!["keepDays"]!.GetValue<int>());
        Assert.True(((JsonArray)data["days"]!).Count is >= 1 and <= 3);
        // the visitors' own counters (a hash of the day) never come out
        Assert.DoesNotContain("v:", after.ToJsonString());
    }

    [Fact]
    public async Task Stored_conversations_are_listed_and_read_and_a_missing_one_is_failed()
    {
        var session = "opschat" + Guid.NewGuid().ToString("N")[..8];
        await AskAsync("Habt ihr samstags Öffnungszeiten?", session);
        var list = await RunAsync("chat.list", new JsonObject { ["search"] = "samstags" });
        Assert.Equal("applied", Status(list));
        var row = Assert.Single(((JsonArray)list["data"]!["chats"]!).OfType<JsonObject>(), x => x["session"]!.GetValue<string>() == session);
        Assert.Equal(1, row["turns"]!.GetValue<int>());

        var read = await RunAsync("chat.read", new JsonObject { ["session"] = session });
        Assert.Equal("applied", Status(read));
        var turn = (JsonObject)((JsonArray)read["data"]!["turns"]!)[0]!;
        Assert.Equal("Habt ihr samstags Öffnungszeiten?", turn["question"]!.GetValue<string>());

        Assert.Equal("failed", Status(await RunAsync("chat.read", new JsonObject { ["session"] = "gibt-es-nicht" })));
        Assert.Equal("failed", Status(await RunAsync("chat.read", new JsonObject())));
    }
}
