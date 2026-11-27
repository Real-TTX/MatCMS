using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>
/// Reading profiles over the operator API and MCP. A profile's join code is an enrolment secret (with AutoApprove a site that
/// sends it is approved at once and receives the configuration), so it is the profile right's; and a key limited to some
/// instances sees only their profiles. Before, any valid key — even one scoped to a single site — read every join code.
/// </summary>
public sealed class ProfileReadAccessTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly ProfileOpsService _ops;

    public ProfileReadAccessTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        // The read paths use neither auth nor the instance service.
        _ops = new ProfileOpsService(_db, new ProfileService(_db, protector), null!, protector, null!);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private (Profile own, Profile other, Instance mine, Instance foreign) Seed()
    {
        var own = new Profile { Name = "Eigenes", JoinCode = "OWNCODE1" };
        var other = new Profile { Name = "Fremdes", JoinCode = "OTHERCD2" };
        _db.Profiles.AddRange(own, other);
        _db.SaveChanges();
        var mine = new Instance { PublicId = "mine", Name = "Meine Seite", ProfileId = own.Id, TokenHash = "a" };
        var neighbour = new Instance { PublicId = "neighbour", Name = "Nachbar", ProfileId = own.Id, TokenHash = "b" };
        var foreign = new Instance { PublicId = "foreign", Name = "Fremde Seite", ProfileId = other.Id, TokenHash = "c" };
        _db.Instances.AddRange(mine, neighbour, foreign);
        _db.SaveChanges();
        return (own, other, mine, foreign);
    }

    private static ApiKey Key(bool all = false, bool manage = false, params Instance[] scope) => new()
    {
        Name = "test", AllInstances = all, CanManageProfiles = manage,
        Instances = scope.Select(i => new ApiKeyInstance { InstanceId = i.Id }).ToList(),
    };

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task A_scoped_key_sees_only_its_instances_profile_without_join_code_and_without_other_sites()
    {
        var (own, other, mine, _) = Seed();
        var key = Key(scope: mine);

        var list = Json(await _ops.ListAsync(key));
        var only = Assert.Single(list.EnumerateArray());
        Assert.Equal(own.Id, only.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, only.GetProperty("joinCode").ValueKind);

        var r = await _ops.GetAsync(own.Id, key);
        Assert.True(r.Ok);
        var p = Json(r.Data!);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("JoinCode").ValueKind);
        var instances = p.GetProperty("instances").EnumerateArray().Select(i => i.GetProperty("PublicId").GetString()).ToList();
        Assert.Equal(new[] { "mine" }, instances); // the neighbour in the same profile is not this key's

        // The other profile answers like one that does not exist — read, effective and recommended alike.
        Assert.False((await _ops.GetAsync(other.Id, key)).Ok);
        Assert.False((await _ops.EffectiveAsync(other.Id, key)).Ok);
        Assert.False((await _ops.RecommendedAsync(other.Id, key)).Ok);
        // The plugin download asks the same question before it streams a bundle.
        Assert.True(await _ops.MayReadAsync(own.Id, key));
        Assert.False(await _ops.MayReadAsync(other.Id, key));
    }

    [Fact]
    public async Task An_all_instances_key_without_the_profile_right_sees_every_profile_but_no_join_code()
    {
        Seed();
        var list = Json(await _ops.ListAsync(Key(all: true)));
        Assert.Equal(2, list.GetArrayLength());
        Assert.All(list.EnumerateArray(), x => Assert.Equal(JsonValueKind.Null, x.GetProperty("joinCode").ValueKind));
    }

    [Fact]
    public async Task The_profile_right_and_the_admin_see_the_join_codes()
    {
        var (own, _, _, _) = Seed();
        foreach (var viewer in new[] { Key(manage: true), null })
        {
            var list = Json(await _ops.ListAsync(viewer));
            Assert.Contains(list.EnumerateArray(), x => x.GetProperty("joinCode").GetString() == "OWNCODE1");
            Assert.Equal("OWNCODE1", Json((await _ops.GetAsync(own.Id, viewer)).Data!).GetProperty("JoinCode").GetString());
        }
    }

    [Fact]
    public void Content_op_rules_refuse_script_and_sign_in_keys_and_keep_order_results_behind_restore()
    {
        foreach (var k in new[] { "code.head", "CODE.bodyEnd", " security.require2fa", "sso.enabled", "members.selfRegistration",
                     "smtp.host", "mail.transport", "site.canonicalUrl", "site.domainRoutes", "ContactRecipient" })
            Assert.True(ContentOpRules.IsRefusedSetting(k), k);
        foreach (var k in new[] { "seo.description", "site.title", "codex.note" })
            Assert.False(ContentOpRules.IsRefusedSetting(k), k);

        Assert.True(ContentOpRules.ResultNeedsOnlyReadScope("page.read"));
        Assert.False(ContentOpRules.ResultNeedsOnlyReadScope("order.list"));
        Assert.False(ContentOpRules.ResultNeedsOnlyReadScope("setting.set"));
    }
}
