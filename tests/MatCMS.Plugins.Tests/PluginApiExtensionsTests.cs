using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The API additions the shop needs: atomic counters and gapless sequences, private files, mail templates and customer accounts.
/// (The route options — member-only, antiforgery, webhook, cookies, streams — are checked through HTTP in <see cref="PluginRouteOptionsTests"/>.)
/// </summary>
public class PluginApiExtensionsTests
{
    private const string Counters = """
        Plugin.Data.Table("stock", t => t.Text("name", required: true).Int("qty", def: "0").UniqueIndex("name"));
        Plugin.Data.Table("seq", t => t.Text("name", required: true).Int("value", required: true, def: "0").UniqueIndex("name"));
        Plugin.MailTemplate(new MailTemplateSpec("cnt.hello", "Hello", "Says hello", "Hi {{name}}", "Dear {{name}}") { Placeholders = new[] { new MailPlaceholder("name", "who") } });
        """;

    private static async Task<(Harness H, IPluginContext Ctx)> Start(string key = "cnt", string code = Counters)
    {
        var h = new Harness();
        h.AddScript(key, code);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        return (h, h.Context(key, new RequestData()));
    }

    // ---- atomic counters --------------------------------------------------------------------------------------

    [Fact]
    public async Task Increment_changes_a_number_in_one_step_and_a_guard_refuses_going_below_it()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var t = ctx.Data.Table("stock");
        var id = await t.InsertAsync(new Dictionary<string, object?> { ["name"] = "tasse", ["qty"] = 3 });

        Assert.Equal(2, await t.IncrementAsync(id, "qty", -1, atLeast: 0));
        Assert.Equal(0, await t.IncrementAsync(id, "qty", -2, atLeast: 0));
        Assert.Null(await t.IncrementAsync(id, "qty", -1, atLeast: 0));            // would be -1: refused, nothing written
        Assert.Equal(0, (await t.GetAsync(id))!.Int("qty"));
        Assert.Equal(5, await t.IncrementAsync(id, "qty", 5));                      // no guard: plain addition
        Assert.Null(await t.IncrementAsync(9999, "qty", 1));                        // no such row
    }

    [Fact]
    public async Task Increment_only_works_on_a_whole_number_column_and_a_declared_one()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var t = ctx.Data.Table("stock");
        var id = await t.InsertAsync(new Dictionary<string, object?> { ["name"] = "x" });
        await Assert.ThrowsAsync<ArgumentException>(() => t.IncrementAsync(id, "name", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => t.IncrementAsync(id, "nope", 1));
        // A column name that looks like SQL is just an unknown column: nothing reaches the database.
        await Assert.ThrowsAsync<ArgumentException>(() => t.IncrementAsync(id, "qty = 0; DROP TABLE x; --", 1));
    }

    [Fact]
    public async Task A_sequence_counts_without_gaps_and_a_failed_transaction_gives_its_number_back()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var seq = ctx.Data.Table("seq");
        Assert.Equal(1, await seq.NextNumberAsync("invoice"));
        Assert.Equal(2, await seq.NextNumberAsync("invoice"));
        Assert.Equal(1, await seq.NextNumberAsync("order"));                        // another sequence of its own

        // The number is taken inside the transaction that uses it: when that fails, nobody ever saw the number and the next caller gets it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Data.InTransactionAsync(async d =>
        {
            Assert.Equal(3, await d.Table("seq").NextNumberAsync("invoice"));
            throw new InvalidOperationException("the order could not be saved");
        }));
        Assert.Equal(3, await seq.NextNumberAsync("invoice"));
    }

    // ---- private files ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Private_files_are_saved_read_and_deleted_outside_the_public_folders()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        await ctx.Files.SaveAsync("products/12/guide.pdf", new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(4, await ctx.Files.LengthAsync("products/12/guide.pdf"));
        await using (var s = (await ctx.Files.OpenAsync("products/12/guide.pdf"))!)
        {
            var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, ms.ToArray());
        }
        // It lies under plugin-files/{key}, not under uploads or plugin-assets.
        var onDisk = Path.Combine(StoragePaths.PluginFilesDir(h.Env, "cnt"), "products", "12", "guide.pdf");
        Assert.True(File.Exists(onDisk));
        Assert.DoesNotContain("uploads", onDisk);
        Assert.DoesNotContain("plugin-assets", onDisk);

        // Replacing leaves no half-written file behind.
        await ctx.Files.SaveAsync("products/12/guide.pdf", new MemoryStream(new byte[] { 9 }));
        Assert.Equal(1, await ctx.Files.LengthAsync("products/12/guide.pdf"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(onDisk)!, "*.part"));

        Assert.True(await ctx.Files.DeleteAsync("products/12/guide.pdf"));
        Assert.False(await ctx.Files.DeleteAsync("products/12/guide.pdf"));
        Assert.Null(await ctx.Files.OpenAsync("products/12/guide.pdf"));
        Assert.Null(await ctx.Files.LengthAsync("nothing.txt"));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("a/../../b")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    [InlineData(".hidden")]
    [InlineData("a/.hidden")]
    [InlineData("a\\b")]
    [InlineData("a/b/c/d/e")]            // five levels
    [InlineData("")]
    [InlineData("name with space.pdf")]
    [InlineData("C:evil")]
    public async Task A_file_name_that_could_leave_the_folder_is_refused(string name)
    {
        var (h, ctx) = await Start();
        using var _h = h;
        Assert.False(ctx.Files.IsValidName(name));
        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Files.SaveAsync(name, new MemoryStream(new byte[] { 1 })));
        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Files.OpenAsync(name));
    }

    [Fact]
    public async Task One_plugins_files_are_not_another_plugins_files()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        await ctx.Files.SaveAsync("same.txt", new MemoryStream(new byte[] { 1 }));
        var other = h.Context("other", new RequestData());
        Assert.Null(await other.Files.OpenAsync("same.txt"));
    }

    // ---- mail -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_declared_mail_template_gets_a_row_the_operator_can_edit_and_is_known_while_the_plugin_is_loaded()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var row = await h.Db.MailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Key == "cnt.hello");
        Assert.NotNull(row);
        Assert.Equal("Hi {{name}}", row!.Subject);
        Assert.NotNull(MatCMS.Shared.MailTemplates.Find("cnt.hello"));              // the editor can list its placeholders
        Assert.Contains(MatCMS.Shared.MailTemplates.Declared, d => d.Key == "cnt.hello");
        Assert.DoesNotContain(MatCMS.Shared.MailTemplates.All, d => d.Key == "cnt.hello");   // never in the core list the cloud seeds

        // An operator's edit is kept when the plugin loads again.
        var tracked = await h.Db.MailTemplates.FirstAsync(t => t.Key == "cnt.hello");
        tracked.Subject = "Our own subject";
        await h.Db.SaveChangesAsync();
        await h.Runner.RunAllAsync();
        Assert.Equal("Our own subject", (await h.Db.MailTemplates.AsNoTracking().FirstAsync(t => t.Key == "cnt.hello")).Subject);

        // Switching the plugin off takes the declaration away (the row stays).
        await h.Runner.UnloadAsync("cnt");
        Assert.Null(MatCMS.Shared.MailTemplates.Find("cnt.hello"));
        Assert.NotNull(await h.Db.MailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Key == "cnt.hello"));
    }

    [Fact]
    public async Task A_plugin_sends_only_its_own_mails_and_a_missing_mail_server_is_an_answer_not_a_crash()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var values = new Dictionary<string, string> { ["name"] = "Anna" };
        var own = await ctx.Mail.SendAsync("cnt.hello", new[] { "anna@example.org" }, values);
        Assert.False(own.Ok);                                                     // no SMTP in the test: the reason comes back
        Assert.Contains("SMTP", own.Error);
        var foreign = await ctx.Mail.SendAsync("form.submission", new[] { "anna@example.org" }, values);
        Assert.False(foreign.Ok);
        Assert.Contains("gehört nicht zum Plugin", foreign.Error);
        Assert.False(await ctx.Mail.IsConfiguredAsync());
    }

    [Fact]
    public async Task A_mail_key_must_start_with_the_plugins_key()
    {
        using var h = new Harness();
        h.AddScript("bad", """Plugin.MailTemplate(new MailTemplateSpec("other.hello", "x", "x", "s", "b"));""");
        await h.Runner.RunAllAsync();
        Assert.Contains(h.Registry.Errors.Values, e => e.Contains("other.hello"));
    }

    // ---- customer accounts ------------------------------------------------------------------------------------

    [Fact]
    public async Task An_account_is_created_found_by_address_and_its_address_is_unique()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var created = await ctx.Members.CreateAsync("  Anna@Example.org ", "correct horse", "Anna");
        Assert.Equal(MemberCreateStatus.Created, created.Status);
        Assert.Equal("anna@example.org", created.Account!.Email);                   // stored lower-case, trimmed
        Assert.Equal("anna@example.org", created.Account.Username);
        Assert.False(created.Account.EmailConfirmed);
        Assert.Equal(created.Account.Id, (await ctx.Members.FindByEmailAsync("ANNA@example.org"))!.Id);
        Assert.Equal("Anna", (await ctx.Members.FindAsync(created.Account.Id))!.DisplayName);

        Assert.Equal(MemberCreateStatus.EmailTaken, (await ctx.Members.CreateAsync("anna@example.org", "another pass", "Anna 2")).Status);
        Assert.Equal(MemberCreateStatus.InvalidEmail, (await ctx.Members.CreateAsync("not-an-address", "correct horse", "x")).Status);
        Assert.Equal(MemberCreateStatus.InvalidEmail, (await ctx.Members.CreateAsync("a b@c.de", "correct horse", "x")).Status);
        Assert.Equal(MemberCreateStatus.WeakPassword, (await ctx.Members.CreateAsync("b@example.org", "short", "x")).Status);
        Assert.Null(await ctx.Members.FindByEmailAsync(""));
        Assert.Null(await ctx.Members.FindByEmailAsync("nobody@example.org"));
    }

    [Fact]
    public async Task An_account_signs_in_with_its_address_or_user_name_and_only_while_active()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        await ctx.Members.CreateAsync("anna@example.org", "correct horse", "Anna");
        var svc = new MemberService(h.Db, h.Services);
        Assert.NotNull(await svc.ValidateAsync("anna@example.org", "correct horse"));
        Assert.NotNull(await svc.ValidateAsync("ANNA@example.org", "correct horse"));
        Assert.Null(await svc.ValidateAsync("anna@example.org", "wrong"));
        // An account an administrator made without an address still signs in by user name, as it always did.
        h.Db.SiteMembers.Add(new SiteMember { Username = "familie", PasswordHash = svc.HashPassword("kurz"), IsActive = true });
        await h.Db.SaveChangesAsync();
        Assert.NotNull(await svc.ValidateAsync("familie", "kurz"));
        // Two accounts without an address do not collide on the empty address.
        h.Db.SiteMembers.Add(new SiteMember { Username = "zweite", PasswordHash = svc.HashPassword("kurz"), IsActive = true });
        await h.Db.SaveChangesAsync();

        var anna = await h.Db.SiteMembers.FirstAsync(m => m.Email == "anna@example.org");
        anna.IsActive = false;
        await h.Db.SaveChangesAsync();
        Assert.Null(await svc.ValidateAsync("anna@example.org", "correct horse"));
    }

    [Fact]
    public async Task A_password_changes_only_with_the_current_one_and_a_long_enough_new_one()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var id = (await ctx.Members.CreateAsync("anna@example.org", "correct horse", "Anna")).Account!.Id;
        Assert.False(await ctx.Members.ChangePasswordAsync(id, "wrong", "a brand new pass"));
        Assert.False(await ctx.Members.ChangePasswordAsync(id, "correct horse", "short"));
        Assert.True(await ctx.Members.ChangePasswordAsync(id, "correct horse", "a brand new pass"));
        var svc = new MemberService(h.Db, h.Services);
        Assert.Null(await svc.ValidateAsync("anna@example.org", "correct horse"));
        Assert.NotNull(await svc.ValidateAsync("anna@example.org", "a brand new pass"));
    }

    [Fact]
    public async Task Confirming_an_address_uses_the_link_once_and_tells_the_plugins()
    {
        var listener = """
            Plugin.Data.Table("seen", t => t.Text("what", required: true));
            Plugin.On("host.member.confirmed", async (ctx, payload) => { await ctx.Data.Table("seen").InsertAsync(new Dictionary<string, object?> { ["what"] = "confirmed:" + payload["email"] }); });
            Plugin.On("host.member.registered", async (ctx, payload) => { await ctx.Data.Table("seen").InsertAsync(new Dictionary<string, object?> { ["what"] = "registered:" + payload["email"] }); });
            """;
        var (h, ctx) = await Start("watch", listener);
        using var _h = h;
        var account = (await ctx.Members.CreateAsync("anna@example.org", "correct horse", "Anna")).Account!;
        var seen = h.Context("watch", new RequestData()).Data.Table("seen");
        Assert.Equal(1, await seen.CountAsync(DataQuery.Everything.Eq("what", "registered:anna@example.org")));

        var svc = new MemberService(h.Db, h.Services);
        var row = await h.Db.SiteMembers.FirstAsync(m => m.Id == account.Id);
        row.ConfirmTokenHash = MemberService.HashToken("the-token");
        row.ConfirmTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        await h.Db.SaveChangesAsync();

        Assert.Null(await svc.ConfirmAsync("wrong-token"));
        Assert.NotNull(await svc.ConfirmAsync("the-token"));
        Assert.Null(await svc.ConfirmAsync("the-token"));                           // used up
        Assert.True((await ctx.Members.FindAsync(account.Id))!.EmailConfirmed);
        Assert.Equal(1, await seen.CountAsync(DataQuery.Everything.Eq("what", "confirmed:anna@example.org")));

        // An expired link does nothing.
        row = await h.Db.SiteMembers.FirstAsync(m => m.Id == account.Id);
        row.EmailConfirmedAt = null;
        row.ConfirmTokenHash = MemberService.HashToken("old"); row.ConfirmTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await h.Db.SaveChangesAsync();
        Assert.Null(await svc.ConfirmAsync("old"));
    }

    [Fact]
    public async Task A_reset_link_sets_a_new_password_once_and_expires()
    {
        var (h, ctx) = await Start();
        using var _h = h;
        var id = (await ctx.Members.CreateAsync("anna@example.org", "correct horse", "Anna")).Account!.Id;
        var svc = new MemberService(h.Db, h.Services);
        var row = await h.Db.SiteMembers.FirstAsync(m => m.Id == id);
        row.ResetTokenHash = MemberService.HashToken("reset-me"); row.ResetTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        await h.Db.SaveChangesAsync();

        Assert.NotNull(await svc.FindByResetTokenAsync("reset-me"));
        Assert.False(await svc.ResetPasswordAsync("reset-me", "short"));            // too weak: the link stays usable
        Assert.NotNull(await svc.FindByResetTokenAsync("reset-me"));
        Assert.True(await svc.ResetPasswordAsync("reset-me", "my new password"));
        Assert.False(await svc.ResetPasswordAsync("reset-me", "my new password"));  // once
        Assert.NotNull(await svc.ValidateAsync("anna@example.org", "my new password"));

        row = await h.Db.SiteMembers.FirstAsync(m => m.Id == id);
        row.ResetTokenHash = MemberService.HashToken("late"); row.ResetTokenExpiresAt = DateTime.UtcNow.AddSeconds(-5);
        await h.Db.SaveChangesAsync();
        Assert.Null(await svc.FindByResetTokenAsync("late"));
        // Asking for a reset for an address nobody has changes nothing and does not throw.
        await svc.RequestResetAsync("nobody@example.org");
    }

    [Fact]
    public async Task Deleting_an_account_tells_the_plugins_first_so_they_can_forget_or_anonymise()
    {
        var listener = """
            Plugin.Data.Table("orders", t => t.Int("member_id").Text("email"));
            Plugin.On("host.member.erase", async (ctx, payload) =>
            {
                var t = ctx.Data.Table("orders");
                await t.UpdateWhereAsync(DataQuery.Everything.Eq("member_id", payload["id"].GetValue<long>()),
                    new Dictionary<string, object?> { ["member_id"] = null, ["email"] = "anonymised" });
            });
            Plugin.On("host.member.export", async (ctx, payload, ct) => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["plugin"] = "watch", ["data"] = new System.Text.Json.Nodes.JsonObject { ["email"] = payload["email"].GetValue<string>() } });
            """;
        var (h, ctx) = await Start("watch", listener);
        using var _h = h;
        var account = (await ctx.Members.CreateAsync("anna@example.org", "correct horse", "Anna")).Account!;
        var orders = h.Context("watch", new RequestData()).Data.Table("orders");
        await orders.InsertAsync(new Dictionary<string, object?> { ["member_id"] = account.Id, ["email"] = "anna@example.org" });

        // The export collects what the plugins hold.
        var svc = new MemberService(h.Db, h.Services);
        var export = await svc.ExportAsync(await h.Db.SiteMembers.FirstAsync(m => m.Id == account.Id));
        Assert.True(export.Count == 1, string.Join(" | ", h.Registry.Log));
        Assert.Equal("anna@example.org", export[0]!["data"]!["email"]!.GetValue<string>());

        Assert.True(await ctx.Members.DeleteAsync(account.Id));
        Assert.Null(await ctx.Members.FindAsync(account.Id));
        var row = (await orders.QueryAsync(DataQuery.Everything)).Single();
        Assert.True(row.IsNull("member_id"));
        Assert.Equal("anonymised", row.Text("email"));
        Assert.False(await ctx.Members.DeleteAsync(account.Id));
    }

    [Fact]
    public async Task A_plugin_context_outside_a_request_has_no_member_and_cannot_sign_in()
    {
        using var h = new Harness();
        var ctx = h.Context("x", new RequestData());
        Assert.Null(ctx.Member);
        var account = new MemberAccount(1, "a", "a", "a@b.de", false, true, Array.Empty<string>(), DateTime.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Members.SignInAsync(account));
    }
}
