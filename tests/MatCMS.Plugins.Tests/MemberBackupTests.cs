using System.IO.Compression;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Member accounts travel in a backup WITH their numbers, because plugin data (orders, a club's member area, forum posts, a guest area) refers to
/// accounts by number. Before, a backup carried no accounts: restored into another site, the numbers pointed at whoever got them there next — a
/// guest opened another household's photos. A restore only ADDS accounts, never changes one, and reports a number it cannot give back.
/// </summary>
public class MemberBackupTests
{
    private static ContentTransferService Transfer(Harness h) => new(h.Db, h.Env, h.Runner, h.Data);

    /// <summary>Accounts "name" … in this order, so they get the numbers 1, 2, …; the ones in <paramref name="drop"/> are deleted again (a gap).</summary>
    private static void Accounts(Harness h, string[] names, params string[] drop)
    {
        foreach (var n in names)
            h.Db.SiteMembers.Add(new SiteMember { Username = n, DisplayName = n.ToUpperInvariant(), PasswordHash = "hash-" + n, Email = n + "@example.org" });
        h.Db.SaveChanges();
        foreach (var n in drop) h.Db.SiteMembers.Remove(h.Db.SiteMembers.Single(m => m.Username == n));
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
    }

    private static int NewAccount(Harness h, string name)
    {
        var m = new SiteMember { Username = name, PasswordHash = "x" };
        h.Db.SiteMembers.Add(m);
        h.Db.SaveChanges();
        return m.Id;
    }

    private static async Task<string> ContentAsync(byte[] backup)
    {
        using var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("content.json")!.Open());
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task A_moved_site_keeps_its_member_numbers_and_no_new_account_takes_an_old_one()
    {
        using var source = new Harness();
        Accounts(source, new[] { "anna", "bert", "carla" }, "bert");          // numbers 1 and 3 — 2 is a gap
        source.Db.SiteRoles.Add(new SiteRole { Name = "Vorstand" });
        var carla = source.Db.SiteMembers.Single(m => m.Username == "carla");
        carla.RolesCsv = "Vorstand";
        carla.EmailConfirmedAt = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        carla.ResetTokenHash = "pending-reset";                               // a one-time link does not travel
        source.Db.SaveChanges();

        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());
        Assert.Contains("\"Members\"", await ContentAsync(backup));
        Assert.Equal(2, Transfer(source).Inspect(backup).Members);

        using var target = new Harness();
        var message = await Transfer(target).ImportAsync(backup);
        Assert.Contains("2 Mitgliederkonten übernommen", message);

        target.Db.ChangeTracker.Clear();
        var anna = target.Db.SiteMembers.Single(m => m.Username == "anna");
        var c = target.Db.SiteMembers.Single(m => m.Username == "carla");
        Assert.Equal(1, anna.Id);
        Assert.Equal(3, c.Id);                                                // the SAME numbers plugin data refers to
        Assert.Equal("hash-carla", c.PasswordHash);
        Assert.Equal("carla@example.org", c.Email);
        Assert.Equal("Vorstand", c.RolesCsv);
        Assert.NotNull(c.EmailConfirmedAt);
        Assert.Equal("", c.ResetTokenHash);
        Assert.True(target.Db.SiteRoles.Any(r => r.Name == "Vorstand"));

        // the next account made there gets a number of its own — never one the backup's data still refers to
        Assert.True(NewAccount(target, "neu") > 3);
    }

    [Fact]
    public async Task A_restore_only_adds_and_reports_a_number_it_cannot_give_back()
    {
        using var source = new Harness();
        Accounts(source, new[] { "anna", "x2", "x3", "x4", "x5", "x6", "bert" }, "x2", "x3", "x4", "x5", "x6");   // anna #1, bert #7
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        // the target already has a DIFFERENT person under number 1, and bert under another number
        using var target = new Harness();
        target.Db.SiteMembers.Add(new SiteMember { Username = "zoe", PasswordHash = "hash-zoe", Email = "zoe@example.org" });
        target.Db.SiteMembers.Add(new SiteMember { Username = "bert", PasswordHash = "hash-bert-here", Email = "bert@example.org" });
        target.Db.SaveChanges();
        target.Db.ChangeTracker.Clear();

        var message = await Transfer(target).ImportAsync(backup);
        Assert.Contains("0 Mitgliederkonten übernommen", message);
        Assert.Contains("2 NICHT übernommen", message);

        target.Db.ChangeTracker.Clear();
        // nobody was changed, nobody was merged
        Assert.Equal("hash-zoe", target.Db.SiteMembers.Single(m => m.Id == 1).PasswordHash);
        Assert.Equal("zoe", target.Db.SiteMembers.Single(m => m.Id == 1).Username);
        Assert.Equal("hash-bert-here", target.Db.SiteMembers.Single(m => m.Username == "bert").PasswordHash);
        Assert.False(target.Db.SiteMembers.Any(m => m.Username == "anna"));

        // bert's old number 7 was not given back — but no account made here later may take it either
        Assert.True(NewAccount(target, "neu") > 7);
    }

    [Fact]
    public async Task Restoring_a_site_onto_itself_changes_no_account()
    {
        using var site = new Harness();
        Accounts(site, new[] { "anna", "bert" });
        var backup = await Transfer(site).ExportAsync(new ContentTransferService.BackupOptions());

        // since the backup: anna changed her password, a new account came
        var anna = site.Db.SiteMembers.Single(m => m.Username == "anna");
        anna.PasswordHash = "hash-anna-new";
        site.Db.SaveChanges();
        var carl = NewAccount(site, "carl");
        site.Db.ChangeTracker.Clear();

        var message = await Transfer(site).ImportAsync(backup);
        Assert.Contains("0 Mitgliederkonten übernommen, 2 schon vorhanden", message);
        site.Db.ChangeTracker.Clear();
        Assert.Equal("hash-anna-new", site.Db.SiteMembers.Single(m => m.Username == "anna").PasswordHash);
        Assert.True(site.Db.SiteMembers.Any(m => m.Id == carl));
    }

    [Fact]
    public async Task A_backup_without_the_switch_carries_no_accounts_and_a_restore_leaves_them_alone()
    {
        using var source = new Harness();
        Accounts(source, new[] { "anna" });
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions { Members = false });
        Assert.DoesNotContain("hash-anna", await ContentAsync(backup));

        using var target = new Harness();
        Accounts(target, new[] { "zoe" });
        var message = await Transfer(target).ImportAsync(backup);
        Assert.DoesNotContain("Mitgliederkonten", message);
        target.Db.ChangeTracker.Clear();
        Assert.Equal("zoe", Assert.Single(await target.Db.SiteMembers.ToListAsync()).Username);
    }
}
