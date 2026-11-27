using System.Text.Json.Nodes;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The guest area's operations (plugins/moments, MomentsOps) — what the site's AI assistant, the cloud's MCP and the site's MCP server work with:
/// every operation is declared with the right flags, a write answers with an undo that really undoes it, bad input is refused, and a household's
/// code, user name or password never comes out of an answer.
/// </summary>
public class MomentsOpsTests : IClassFixture<MomentsApp>
{
    private readonly MomentsApp _app;
    public MomentsOpsTests(MomentsApp app) => _app = app;

    // (short name, writes, personal data, internal)
    private static readonly (string Name, bool Writes, bool Personal, bool Internal)[] Expected =
    {
        ("album.list", false, false, false), ("album.read", false, false, false), ("album.create", true, false, false), ("album.update", true, false, false),
        ("album.delete", true, false, false), ("album.remove", true, false, true), ("album.restore", true, false, true), ("album.recreate", true, false, true),
        ("guest.list", false, true, false), ("guest.read", false, true, false), ("guest.create", true, true, false), ("guest.update", true, true, false),
        ("guest.remove", true, true, true), ("guest.restore", true, true, true),
        ("photo.list", false, true, false), ("photo.read", false, true, false), ("photo.update", true, true, false), ("photo.restore", true, true, true)
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

        /// <summary>The name a caller uses: qualified ("moments.album.list") where the host qualifies names, else the short one.</summary>
        public string Name(string shortName) => C.HasOperation("moments." + shortName) ? "moments." + shortName : shortName;

        public async Task<JsonObject> RunAsync(string shortName, JsonObject args, bool allowInternal = false) =>
            (JsonObject)(await C.RunOperationAsync(_scope.ServiceProvider, Name(shortName), args, default, allowInternal))!;

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

    private long InsertPhoto(long guestId, long albumId, string status) => _app.Db(db =>
    {
        db.Database.ExecuteSqlRaw("INSERT INTO p_moments_photos (guest_id, file, file_name, status, album_id, featured, caption, taken) VALUES ({0}, 'ops.jpg', 'ops.jpg', {1}, {2}, 0, '', '')",
            guestId, status, albumId);
        return db.Database.SqlQueryRaw<long>("SELECT MAX(id) AS Value FROM p_moments_photos").ToList().Single();
    });

    [Fact]
    public async Task Every_operation_is_declared_with_its_flags()
    {
        using var ops = await OpsAsync();
        foreach (var (name, writes, personal, isInternal) in Expected)
        {
            Assert.True(ops.C.HasOperation(ops.Name(name)), name + " is missing");
            var spec = ops.Registry.Operations.Single(o => o.Owner == "moments" && o.Name == name).Spec;
            Assert.NotNull(spec);
            Assert.False(string.IsNullOrWhiteSpace(spec!.Description), name + " has no description");
            Assert.True(spec.Writes == writes, name + ": Writes");
            Assert.True(spec.PersonalData == personal, name + ": PersonalData");
            Assert.True(spec.Internal == isInternal, name + ": Internal");
        }
        Assert.Empty(ops.Registry.Operations.Where(o => o.Owner == "moments").Select(o => o.Name).Except(Expected.Select(e => e.Name)));
        // the assistant sees the albums, never the households or the guests' photos
        Assert.Contains(ops.C.AssistantOperations, o => o.Owner == "moments" && o.Name == "album.create");
        Assert.DoesNotContain(ops.C.AssistantOperations, o => o.Owner == "moments" && (o.Name.StartsWith("guest.") || o.Name.StartsWith("photo.")));
    }

    [Fact]
    public async Task Internal_operations_are_refused_without_the_hosts_permission()
    {
        using var ops = await OpsAsync();
        foreach (var name in Expected.Where(e => e.Internal).Select(e => e.Name))
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.RunAsync(name, new JsonObject { ["id"] = 1 }));
    }

    [Fact]
    public async Task An_album_is_created_renamed_made_the_guests_album_deleted_and_every_step_undone()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("album.create", new JsonObject { ["name"] = "  " })));

        var first = await ops.RunAsync("album.create", new JsonObject { ["name"] = "Fotograf (Ops)" });
        Assert.Equal("applied", Status(first));
        var a = NewId(first);
        Assert.Equal("skipped-exists", Status(await ops.RunAsync("album.create", new JsonObject { ["name"] = "fotograf (ops)" })));
        var second = await ops.RunAsync("album.create", new JsonObject { ["name"] = "Gäste (Ops)" });
        var b = NewId(second);
        Assert.Equal("failed", Status(await ops.RunAsync("album.update", new JsonObject { ["id"] = b, ["name"] = "Fotograf (Ops)" })));   // taken

        // a guest's photo without an album comes along when the album becomes the guests' album
        var loose = InsertPhoto(guestId: 424242, albumId: 0, status: "pending");
        var update = await ops.RunAsync("album.update", new JsonObject { ["id"] = b, ["name"] = "Gästebilder (Ops)", ["guests"] = true });
        Assert.Equal("applied", Status(update));
        var read = (JsonObject)(await ops.RunAsync("album.read", new JsonObject { ["id"] = b }))["data"]!;
        Assert.Equal("Gästebilder (Ops)", read["name"]!.GetValue<string>());
        Assert.True(read["guests"]!.GetValue<bool>());
        Assert.Equal(1, read["pending"]!.GetValue<int>());

        Assert.Equal("applied", Status(await ops.UndoAsync(update)));
        read = (JsonObject)(await ops.RunAsync("album.read", new JsonObject { ["id"] = b }))["data"]!;
        Assert.Equal("Gäste (Ops)", read["name"]!.GetValue<string>());
        Assert.False(read["guests"]!.GetValue<bool>());
        Assert.Equal(0, read["pending"]!.GetValue<int>());                                              // the photo it took in is loose again

        // deleting keeps the photos; the undo brings the album back with them
        var photo = InsertPhoto(guestId: 0, albumId: a, status: "approved");
        var deleted = await ops.RunAsync("album.delete", new JsonObject { ["id"] = a });
        Assert.Equal("applied", Status(deleted));
        Assert.Equal("failed", Status(await ops.RunAsync("album.read", new JsonObject { ["id"] = a })));
        Assert.Equal(0, (await ops.RunAsync("photo.read", new JsonObject { ["id"] = photo }))["data"]!["album_id"]!.GetValue<long>());
        var again = await ops.UndoAsync(deleted);
        Assert.Equal("applied", Status(again));
        var a2 = NewId(again);
        Assert.Equal(a2, (await ops.RunAsync("photo.read", new JsonObject { ["id"] = photo }))["data"]!["album_id"]!.GetValue<long>());
        Assert.Contains((JsonArray)(await ops.RunAsync("album.list", new JsonObject()))["data"]!, x => x!["name"]!.GetValue<string>() == "Fotograf (Ops)" && x["photos"]!.GetValue<int>() == 1);

        Assert.Equal("applied", Status(await ops.UndoAsync(second)));
        Assert.Equal("failed", Status(await ops.RunAsync("album.read", new JsonObject { ["id"] = b })));
        Assert.Equal("applied", Status(await ops.RunAsync("album.remove", new JsonObject { ["id"] = a2 }, allowInternal: true)));
        _ = loose;
    }

    [Fact]
    public async Task A_household_is_created_switched_off_and_back_and_its_code_never_leaves()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("guest.create", new JsonObject { ["people"] = "Niemand" })));

        var made = await ops.RunAsync("guest.create", new JsonObject { ["name"] = "Zoe, Fabian & Co", ["people"] = "Zoe und Fabian", ["table"] = "Tisch 3" });
        Assert.Equal("applied", Status(made));
        var id = NewId(made);
        Assert.Equal("skipped-exists", Status(await ops.RunAsync("guest.create", new JsonObject { ["name"] = "zoe fabian & co" })));
        var code = _app.Db(db => db.Database.SqlQueryRaw<string>("SELECT code AS Value FROM p_moments_guests WHERE id = {0}", id).ToList().Single());
        Assert.False(string.IsNullOrEmpty(code));

        var read = await ops.RunAsync("guest.read", new JsonObject { ["id"] = id });
        var data = (JsonObject)read["data"]!;
        Assert.Equal("Zoe Fabian & Co", data["name"]!.GetValue<string>());                         // a name never carries a comma
        Assert.Equal(new[] { "Zoe", "Fabian" }, data["people"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(data["active"]!.GetValue<bool>());
        Assert.False(data["has_password"]!.GetValue<bool>());
        var list = await ops.RunAsync("guest.list", new JsonObject { ["search"] = "zoe" });
        foreach (var answer in new[] { made, read, list })
        {
            Assert.DoesNotContain(code, answer.ToJsonString());
            Assert.DoesNotContain(code, answer.ToJsonString().Replace("-", ""));                    // not even grouped as printed ("K7XM-4QPA")
            Assert.DoesNotContain("\"code\"", answer.ToJsonString());
        }

        // the way in stays with the hosts
        Assert.Equal("failed", Status(await ops.RunAsync("guest.update", new JsonObject { ["id"] = id, ["password"] = "geheim12345" })));
        Assert.Equal("failed", Status(await ops.RunAsync("guest.update", new JsonObject { ["id"] = id, ["code"] = "START12345" })));

        var off = await ops.RunAsync("guest.update", new JsonObject { ["id"] = id, ["active"] = false, ["table"] = "Tisch 9" });
        Assert.Equal("applied", Status(off));
        Assert.Contains((JsonArray)(await ops.RunAsync("guest.list", new JsonObject { ["active"] = false }))["data"]!, g => g!["id"]!.GetValue<long>() == id);
        Assert.Equal("applied", Status(await ops.UndoAsync(off)));
        data = (JsonObject)(await ops.RunAsync("guest.read", new JsonObject { ["id"] = id }))["data"]!;
        Assert.True(data["active"]!.GetValue<bool>());
        Assert.Equal("Tisch 3", data["table"]!.GetValue<string>());
        Assert.Equal(code, _app.Db(db => db.Database.SqlQueryRaw<string>("SELECT code AS Value FROM p_moments_guests WHERE id = {0}", id).ToList().Single()));   // untouched

        Assert.Equal("applied", Status(await ops.UndoAsync(made)));
        Assert.Equal("failed", Status(await ops.RunAsync("guest.read", new JsonObject { ["id"] = id })));
    }

    [Fact]
    public async Task A_photo_is_released_highlighted_and_put_in_an_album_and_the_undo_puts_it_back()
    {
        using var ops = await OpsAsync();
        Assert.Equal("failed", Status(await ops.RunAsync("photo.list", new JsonObject { ["status"] = "irgendwie" })));
        Assert.Equal("failed", Status(await ops.RunAsync("photo.update", new JsonObject { ["id"] = 987654, ["status"] = "approved" })));

        var album = NewId(await ops.RunAsync("album.create", new JsonObject { ["name"] = "Fotobox (Ops)" }));
        var photo = InsertPhoto(guestId: 0, albumId: 0, status: "pending");
        Assert.Contains((JsonArray)(await ops.RunAsync("photo.list", new JsonObject { ["status"] = "pending" }))["data"]!, p => p!["id"]!.GetValue<long>() == photo);
        Assert.Equal("failed", Status(await ops.RunAsync("photo.update", new JsonObject { ["id"] = photo, ["album_id"] = 987654 })));
        Assert.Equal("failed", Status(await ops.RunAsync("photo.update", new JsonObject { ["id"] = photo, ["status"] = "weg" })));

        var changed = await ops.RunAsync("photo.update", new JsonObject { ["id"] = photo, ["status"] = "approved", ["featured"] = true, ["album_id"] = album, ["caption"] = "Der\nerste Tanz" });
        Assert.Equal("applied", Status(changed));
        var read = (JsonObject)(await ops.RunAsync("photo.read", new JsonObject { ["id"] = photo }))["data"]!;
        Assert.Equal("approved", read["status"]!.GetValue<string>());
        Assert.True(read["featured"]!.GetValue<bool>());
        Assert.Equal(album, read["album_id"]!.GetValue<long>());
        Assert.Equal("Der erste Tanz", read["caption"]!.GetValue<string>());

        Assert.Equal("applied", Status(await ops.UndoAsync(changed)));
        read = (JsonObject)(await ops.RunAsync("photo.read", new JsonObject { ["id"] = photo }))["data"]!;
        Assert.Equal("pending", read["status"]!.GetValue<string>());
        Assert.False(read["featured"]!.GetValue<bool>());
        Assert.Equal(0, read["album_id"]!.GetValue<long>());
        Assert.Equal("", read["caption"]!.GetValue<string>());
    }
}
