using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The club plugin's operations (plugins/verein, VereinOps + VereinEvents) — what the site's AI assistant, the cloud's MCP and the site's MCP server
/// work with: every operation is declared with the right flags, a write answers with an undo that really undoes it, bad input is refused, and nothing
/// creates an account or hands out a secret.
/// </summary>
public class VereinOpsTests : IClassFixture<VereinApp>
{
    private readonly VereinApp _app;
    public VereinOpsTests(VereinApp app) => _app = app;

    // (short name, writes, personal data, internal)
    private static readonly (string Name, bool Writes, bool Personal, bool Internal)[] Expected =
    {
        ("event.list", false, false, false), ("event.read", false, false, false), ("event.create", true, false, false), ("event.update", true, false, false),
        ("event.remove", true, false, true), ("event.restore", true, false, true),
        ("role.list", false, false, false),
        ("member.list", false, true, false), ("member.read", false, true, false), ("member.create", true, true, false), ("member.update", true, true, false),
        ("member.remove", true, true, true), ("member.restore", true, true, true),
        ("meeting.list", false, false, false), ("meeting.read", false, false, false), ("meeting.create", true, false, false), ("meeting.update", true, false, false),
        ("meeting.remove", true, false, true), ("meeting.restore", true, false, true),
        ("doc.list", false, false, false), ("doc.read", false, false, false), ("doc.create", true, false, false), ("doc.update", true, false, false),
        ("doc.remove", true, false, true), ("doc.restore", true, false, true),
        ("template.list", false, false, false), ("template.read", false, false, false), ("template.create", true, false, false), ("template.update", true, false, false),
        ("template.delete", true, false, false), ("template.remove", true, false, true), ("template.restore", true, false, true), ("template.recreate", true, false, true),
        ("upload.list", false, true, false), ("upload.update", true, true, false), ("upload.restore", true, true, true)
    };

    private sealed class Ops : IDisposable
    {
        private readonly IServiceScope _scope;
        public PluginContributions C { get; }
        public PluginRegistry Registry { get; }
        public Ops(IServiceScope scope)
        {
            _scope = scope;
            Registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            C = new PluginContributions(Registry);
        }

        /// <summary>The name a caller uses: qualified ("verein.member.list") where the host qualifies names, else the short one.</summary>
        public string Name(string shortName) => C.HasOperation("verein." + shortName) ? "verein." + shortName : shortName;

        public async Task<JsonObject> RunAsync(string shortName, JsonObject args, bool allowInternal = false) =>
            (JsonObject)(await C.RunOperationAsync(_scope.ServiceProvider, Name(shortName), args, default, allowInternal))!;

        /// <summary>Runs the undo an answer carries, as the host does (internal allowed).</summary>
        public async Task<JsonObject> UndoAsync(JsonObject answer)
        {
            var undo = (JsonObject)answer["undo"]!;
            return (JsonObject)(await C.RunOperationAsync(_scope.ServiceProvider, undo["name"]!.GetValue<string>(), undo["args"]!.DeepClone(), default, allowInternal: true))!;
        }

        public void Dispose() => _scope.Dispose();
    }

    private async Task<Ops> OpsAsync()
    {
        await _app.InstallAsync();
        return new Ops(_app.App.Services.CreateScope());
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();
    private static long NewId(JsonObject a) => a["data"]!["id"]!.GetValue<long>();

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags_and_every_write_names_an_internal_undo()
    {
        using var ops = await OpsAsync();
        foreach (var (name, writes, personal, isInternal) in Expected)
        {
            Assert.True(ops.C.HasOperation(ops.Name(name)), name + " is missing");
            var spec = ops.Registry.Operations.Single(o => o.Owner == "verein" && o.Name == name).Spec;
            Assert.NotNull(spec);
            Assert.False(string.IsNullOrWhiteSpace(spec!.Description), name + " has no description");
            Assert.True(spec.Writes == writes, name + ": Writes");
            Assert.True(spec.PersonalData == personal, name + ": PersonalData");
            Assert.True(spec.Internal == isInternal, name + ": Internal");
        }
        // nothing declared that the list above does not know (a new operation gets its flags checked here)
        Assert.Empty(ops.Registry.Operations.Where(o => o.Owner == "verein").Select(o => o.Name).Except(Expected.Select(e => e.Name)));
        // the assistant (every answer goes to an outside model) never sees the member data
        Assert.DoesNotContain(ops.C.AssistantOperations, o => o.Owner == "verein" && (o.Name.StartsWith("member.") || o.Name.StartsWith("upload.")));
    }

    [Fact]
    public async Task Internal_operations_are_refused_without_the_hosts_permission()
    {
        using var ops = await OpsAsync();
        foreach (var name in Expected.Where(e => e.Internal).Select(e => e.Name))
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RunAsync(name, new JsonObject { ["id"] = 1 }));
    }

    [Fact]
    public async Task Roles_fields_and_audiences_are_readable_for_an_agent()
    {
        using var ops = await OpsAsync();
        var a = await ops.RunAsync("role.list", new JsonObject());
        Assert.Equal("applied", Status(a));
        Assert.Contains(a["data"]!["roles"]!.AsArray(), r => r!["name"]!.GetValue<string>() == "Kassierer/in" && r["board"]!.GetValue<bool>());
        Assert.Contains(a["data"]!["audiences"]!.AsArray(), x => x!.GetValue<string>() == "board");
        Assert.Contains(a["data"]!["meeting_kinds"]!.AsArray(), x => x!.GetValue<string>() == "Mitgliederversammlung");
    }

    [Fact]
    public async Task A_member_is_entered_without_access_read_changed_undone_and_taken_back()
    {
        using var ops = await OpsAsync();
        var accountsBefore = _app.Db(db => db.SiteMembers.Count());

        Assert.Equal("failed", Status(await ops.RunAsync("member.create", new JsonObject { ["phone"] = "0171 1" })));                  // no name
        Assert.Equal("failed", Status(await ops.RunAsync("member.create", new JsonObject { ["first"] = "Bad", ["born"] = "gestern" })));  // no date

        var made = await ops.RunAsync("member.create", new JsonObject
        {
            ["first"] = "Olga", ["last"] = "Operativ", ["email"] = "Olga@Verein.Example", ["phone"] = "0171 555", ["roles"] = new JsonArray("Kassierer/in", "Mitglied|x"),
            ["joined"] = "2019-04-01", ["born"] = "1980-02-29", ["city"] = "Besigheim"
        });
        Assert.Equal("applied", Status(made));
        var id = NewId(made);
        Assert.EndsWith("member.remove", made["undo"]!["name"]!.GetValue<string>());
        Assert.Equal(accountsBefore, _app.Db(db => db.SiteMembers.Count()));                        // no account, no invitation
        Assert.Equal("skipped-exists", Status(await ops.RunAsync("member.create", new JsonObject { ["first"] = "Olga", ["last"] = "Operativ" })));

        var read = await ops.RunAsync("member.read", new JsonObject { ["id"] = id });
        Assert.Equal("olga@verein.example", read["data"]!["email"]!.GetValue<string>());
        Assert.False(read["data"]!["has_account"]!.GetValue<bool>());
        Assert.Equal(new[] { "Kassierer/in", "Mitglied/x" }, read["data"]!["roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.DoesNotContain("cal_token", read.ToJsonString());
        Assert.Contains(((JsonArray)(await ops.RunAsync("member.list", new JsonObject { ["search"] = "operativ" }))["data"]!), m => m!["id"]!.GetValue<long>() == id);

        // a child needs a grown-up of the club as guardian
        Assert.Equal("failed", Status(await ops.RunAsync("member.create", new JsonObject { ["first"] = "Kid", ["child"] = true, ["guardians"] = new JsonArray(987654) })));
        var kid = await ops.RunAsync("member.create", new JsonObject { ["first"] = "Kalle", ["last"] = "Operativ", ["child"] = true, ["guardians"] = new JsonArray(id), ["phone"] = "nope" });
        Assert.Equal("applied", Status(kid));
        Assert.Contains(((JsonArray)(await ops.RunAsync("member.read", new JsonObject { ["id"] = id }))["data"]!["children"]!), c => c!["name"]!.GetValue<string>() == "Kalle Operativ");
        Assert.Equal("", (await ops.RunAsync("member.read", new JsonObject { ["id"] = NewId(kid) }))["data"]!["phone"]!.GetValue<string>());

        var changed = await ops.RunAsync("member.update", new JsonObject { ["id"] = id, ["phone"] = "0171 999", ["active"] = false, ["note"] = "Ausgetreten", ["passive"] = true });
        Assert.Equal("applied", Status(changed));
        var after = (JsonObject)(await ops.RunAsync("member.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("0171 999", after["phone"]!.GetValue<string>());
        Assert.False(after["active"]!.GetValue<bool>());
        Assert.Equal("1980-02-29", after["born"]!.GetValue<string>());                                // not named = kept
        Assert.Contains(((JsonArray)(await ops.RunAsync("member.list", new JsonObject { ["status"] = "ehemalige" }))["data"]!), m => m!["id"]!.GetValue<long>() == id);
        Assert.Equal("failed", Status(await ops.RunAsync("member.list", new JsonObject { ["status"] = "irgendwas" })));

        Assert.Equal("applied", Status(await ops.UndoAsync(changed)));
        var back = (JsonObject)(await ops.RunAsync("member.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("0171 555", back["phone"]!.GetValue<string>());
        Assert.True(back["active"]!.GetValue<bool>());
        Assert.False(back["passive"]!.GetValue<bool>());
        Assert.Equal("", back["note"]!.GetValue<string>());

        Assert.Equal("applied", Status(await ops.UndoAsync(kid)));
        Assert.Equal("applied", Status(await ops.UndoAsync(made)));
        Assert.Equal("failed", Status(await ops.RunAsync("member.read", new JsonObject { ["id"] = id })));
        Assert.Equal("failed", Status(await ops.RunAsync("member.update", new JsonObject { ["id"] = id, ["phone"] = "1" })));
    }

    [Fact]
    public async Task A_meeting_is_created_its_minutes_published_and_undone()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("meeting.create", new JsonObject { ["title"] = "Ohne Termin" })));
        Assert.Equal("failed", Status(await ops.RunAsync("meeting.create", new JsonObject { ["title"] = "Falsche Gruppe", ["starts"] = "2031-03-14T19:30", ["audience"] = "role:Gibtsnicht" })));
        Assert.Equal("failed", Status(await ops.RunAsync("meeting.create", new JsonObject { ["title"] = "Falsche Art", ["starts"] = "2031-03-14T19:30", ["kind"] = "Kaffeekranz" })));

        var made = await ops.RunAsync("meeting.create", new JsonObject
        {
            ["title"] = "JHV per Operation", ["starts"] = "2031-03-14T19:30", ["kind"] = "jahreshauptversammlung", ["audience"] = "alle", ["agenda"] = "1. Begrüßung"
        });
        Assert.Equal("applied", Status(made));
        var id = NewId(made);
        var read = (JsonObject)(await ops.RunAsync("meeting.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("Jahreshauptversammlung", read["kind"]!.GetValue<string>());
        Assert.Equal("all", read["audience"]!.GetValue<string>());
        Assert.Equal("planned", read["status"]!.GetValue<string>());
        Assert.Null(read["invited_at"]);                                                               // no invitation went out

        var minutes = await ops.RunAsync("meeting.update", new JsonObject { ["id"] = id, ["minutes"] = "Alles beschlossen.", ["minutes_status"] = "final" });
        Assert.Equal("applied", Status(minutes));
        read = (JsonObject)(await ops.RunAsync("meeting.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("held", read["status"]!.GetValue<string>());                                      // as in the admin: published minutes = it took place
        Assert.Equal("failed", Status(await ops.RunAsync("meeting.update", new JsonObject { ["id"] = id, ["status"] = "vertagt" })));

        Assert.Equal("applied", Status(await ops.UndoAsync(minutes)));
        read = (JsonObject)(await ops.RunAsync("meeting.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("planned", read["status"]!.GetValue<string>());
        Assert.Equal("", read["minutes"]!.GetValue<string>());
        Assert.Equal("draft", read["minutes_status"]!.GetValue<string>());

        Assert.Equal("applied", Status(await ops.UndoAsync(made)));
        Assert.Equal("failed", Status(await ops.RunAsync("meeting.read", new JsonObject { ["id"] = id })));
    }

    [Fact]
    public async Task A_document_keeps_its_text_once_somebody_answered_and_archiving_is_undone()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("doc.create", new JsonObject { ["title"] = "Ab" })));
        Assert.Equal("failed", Status(await ops.RunAsync("doc.create", new JsonObject { ["title"] = "Falsche Art", ["kind"] = "stempeln" })));

        var made = await ops.RunAsync("doc.create", new JsonObject { ["title"] = "Bildfreigabe per Operation", ["body"] = "Ich bin einverstanden.", ["kind"] = "sign", ["audience"] = "vorstand", ["due"] = "2031-02-01" });
        Assert.Equal("applied", Status(made));
        var id = NewId(made);
        var read = (JsonObject)(await ops.RunAsync("doc.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.Equal("board", read["audience"]!.GetValue<string>());
        Assert.Equal("sign", read["kind"]!.GetValue<string>());

        // without answers the text may change
        Assert.Equal("applied", Status(await ops.RunAsync("doc.update", new JsonObject { ["id"] = id, ["body"] = "Ich bin damit einverstanden." })));

        // once somebody answered, the text is the proof
        _app.Db(db => db.Database.ExecuteSqlRaw("INSERT INTO p_verein_answers (doc_id, member_id, status) VALUES ({0}, 987654, 'accepted')", id));
        var refused = await ops.RunAsync("doc.update", new JsonObject { ["id"] = id, ["body"] = "Ganz anders." });
        Assert.Equal("failed", Status(refused));
        Assert.Equal("Ich bin damit einverstanden.", (await ops.RunAsync("doc.read", new JsonObject { ["id"] = id }))["data"]!["body"]!.GetValue<string>());

        var archived = await ops.RunAsync("doc.update", new JsonObject { ["id"] = id, ["archived"] = true });
        Assert.Equal("applied", Status(archived));
        Assert.Contains(((JsonArray)(await ops.RunAsync("doc.list", new JsonObject { ["archived"] = true }))["data"]!), d => d!["id"]!.GetValue<long>() == id);
        Assert.Equal("applied", Status(await ops.UndoAsync(archived)));
        Assert.False((await ops.RunAsync("doc.read", new JsonObject { ["id"] = id }))["data"]!["archived"]!.GetValue<bool>());

        // the create's undo does not delete a document somebody answered
        Assert.Equal("skipped", Status(await ops.UndoAsync(made)));
        Assert.Equal("applied", Status(await ops.RunAsync("doc.read", new JsonObject { ["id"] = id })));
    }

    [Fact]
    public async Task A_template_is_created_changed_deleted_and_each_step_undone()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("template.create", new JsonObject { ["name"] = "x" })));
        Assert.Equal("failed", Status(await ops.RunAsync("template.create", new JsonObject { ["name"] = "Falsch", ["kind"] = "brief" })));

        var made = await ops.RunAsync("template.create", new JsonObject { ["name"] = "TO per Operation", ["kind"] = "agenda", ["title"] = "Sitzung {{jahr}}", ["body"] = "1. Begrüßung" });
        Assert.Equal("applied", Status(made));
        var id = NewId(made);
        var changed = await ops.RunAsync("template.update", new JsonObject { ["id"] = id, ["body"] = "1. Begrüßung\n2. Kasse" });
        Assert.Equal("applied", Status(changed));
        Assert.Equal("applied", Status(await ops.UndoAsync(changed)));
        Assert.Equal("1. Begrüßung", (await ops.RunAsync("template.read", new JsonObject { ["id"] = id }))["data"]!["body"]!.GetValue<string>());

        var deleted = await ops.RunAsync("template.delete", new JsonObject { ["id"] = id });
        Assert.Equal("applied", Status(deleted));
        Assert.Equal("failed", Status(await ops.RunAsync("template.read", new JsonObject { ["id"] = id })));
        var again = await ops.UndoAsync(deleted);
        Assert.Equal("applied", Status(again));
        var list = (JsonArray)(await ops.RunAsync("template.list", new JsonObject { ["kind"] = "agenda" }))["data"]!;
        Assert.Contains(list, t => t!["name"]!.GetValue<string>() == "TO per Operation" && t["title"]!.GetValue<string>() == "Sitzung {{jahr}}");
        Assert.Equal("applied", Status(await ops.RunAsync("template.remove", new JsonObject { ["id"] = NewId(again) }, allowInternal: true)));
    }

    [Fact]
    public async Task An_upload_is_released_and_the_release_undone_an_unknown_event_is_refused()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("upload.update", new JsonObject { ["id"] = 987654, ["approved"] = true })));
        Assert.Equal("failed", Status(await ops.RunAsync("upload.update", new JsonObject { ["approved"] = true })));

        _app.Db(db => db.Database.ExecuteSqlRaw("INSERT INTO p_verein_uploads (member_id, kind, title, file, file_name, approved, shared, event_id) VALUES (987654, 'photo', 'Ops-Upload', 'u/ops.jpg', 'ops.jpg', 0, 1, 0)"));
        var pending = (JsonArray)(await ops.RunAsync("upload.list", new JsonObject { ["pending"] = true }))["data"]!;
        var id = pending.Single(u => u!["title"]!.GetValue<string>() == "Ops-Upload")!["id"]!.GetValue<long>();

        Assert.Equal("failed", Status(await ops.RunAsync("upload.update", new JsonObject { ["id"] = id, ["event_id"] = 987654 })));
        var released = await ops.RunAsync("upload.update", new JsonObject { ["id"] = id, ["approved"] = true, ["shared"] = false });
        Assert.Equal("applied", Status(released));
        Assert.DoesNotContain((JsonArray)(await ops.RunAsync("upload.list", new JsonObject { ["pending"] = true }))["data"]!, u => u!["id"]!.GetValue<long>() == id);
        Assert.Equal("applied", Status(await ops.UndoAsync(released)));
        var back = ((JsonArray)(await ops.RunAsync("upload.list", new JsonObject { ["pending"] = true }))["data"]!).Single(u => u!["id"]!.GetValue<long>() == id)!;
        Assert.True(back["shared"]!.GetValue<bool>());
    }
}
