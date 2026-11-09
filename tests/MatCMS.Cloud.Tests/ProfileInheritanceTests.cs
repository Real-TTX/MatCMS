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
/// Profiles inherit from profiles: a child adds and overrides, never takes away; each profile's rows count only while its own
/// switch for that group is on; the nearest profile with the mail group decides how mail goes out. And the sender name: the
/// address stays, the NAME can be the site's own or one given to a single instance.
/// </summary>
public sealed class ProfileInheritanceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;

    public ProfileInheritanceTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _profiles = new ProfileService(_db, new SecretProtector(new EphemeralDataProtectionProvider()));
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private Profile Add(string name, int? parent = null, Action<Profile>? set = null)
    {
        var p = new Profile { Name = name, JoinCode = ProfileService.NewJoinCode(), ParentId = parent };
        set?.Invoke(p);
        _db.Profiles.Add(p);
        _db.SaveChanges();
        return p;
    }

    private void Setting(Profile p, string key, string? value)
    {
        _db.ProfileSettings.Add(new ProfileSetting { ProfileId = p.Id, Key = key, Value = value });
        _db.SaveChanges();
    }

    private Task<InstanceConfig> ConfigAsync(Profile p, Dictionary<string, string>? origins = null) =>
        _profiles.BuildConfigAsync(_db.Profiles.AsNoTracking().Single(x => x.Id == p.Id), default, origins);

    [Fact]
    public async Task A_child_gets_everything_of_its_parent_and_wins_per_key()
    {
        var standard = Add("Standard", set: p => p.SyncSettings = true);
        Setting(standard, "sso.enabled", "1");
        Setting(standard, "antispam.level", "1");
        var club = Add("Vereine", standard.Id, p => p.SyncSettings = true);
        Setting(club, "antispam.level", "2");
        Setting(club, "members.selfRegistration", "1");

        var origins = new Dictionary<string, string>();
        var c = await ConfigAsync(club, origins);
        Assert.Equal("1", c.Settings!["sso.enabled"]);
        Assert.Equal("2", c.Settings["antispam.level"]);
        Assert.Equal("1", c.Settings["members.selfRegistration"]);
        Assert.Equal("Standard", origins["setting:sso.enabled"]);
        Assert.Equal("Vereine", origins["setting:antispam.level"]);

        // the parent itself is unchanged by its child
        var s = await ConfigAsync(standard);
        Assert.Equal("1", s.Settings!["antispam.level"]);
        Assert.False(s.Settings.ContainsKey("members.selfRegistration"));
    }

    [Fact]
    public async Task A_group_switched_off_in_a_profile_rolls_nothing_of_it_out_even_to_a_child()
    {
        var standard = Add("Standard");                         // SMTP stored, but the group is OFF
        Setting(standard, "smtp.host", "mail.example.org");
        var child = Add("Kind", standard.Id, p => p.SyncSettings = true);
        Setting(child, "sso.enabled", "1");

        var c = await ConfigAsync(child);
        Assert.DoesNotContain(c.Settings!.Keys, k => k.StartsWith("smtp."));
        Assert.Equal("smtp", c.MailTransport);
    }

    [Fact]
    public async Task The_relay_of_the_parent_reaches_the_child_and_a_child_can_send_with_its_own_address_instead()
    {
        var standard = Add("Standard", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Cloud; });
        var plain = Add("Normal", standard.Id);
        Assert.Equal("cloud", (await ConfigAsync(plain)).MailTransport);

        // a child that needs the right sender address sends through its own SMTP
        var own = Add("Eigene Adresse", standard.Id, p => { p.SyncSmtp = true; p.MailSource = MailSources.Own; });
        Setting(own, "smtp.host", "smtp.verein.de");
        Setting(own, "smtp.fromEmail", "info@verein.de");
        var c = await ConfigAsync(own);
        Assert.Equal("smtp", c.MailTransport);
        Assert.Equal("smtp.verein.de", c.Settings!["smtp.host"]);
        Assert.Equal("info@verein.de", c.Settings["smtp.fromEmail"]);
    }

    [Fact]
    public async Task Own_smtp_below_own_smtp_only_overrides_what_it_fills_in()
    {
        var standard = Add("Standard", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Own; });
        Setting(standard, "smtp.host", "smtp.matthix.de");
        Setting(standard, "smtp.user", "mailer");
        Setting(standard, "smtp.fromEmail", "noreply@matthix.de");
        Setting(standard, "smtp.fromName", "MatCMS");
        var child = Add("Kunde", standard.Id, p => { p.SyncSmtp = true; p.MailSource = MailSources.Own; });
        Setting(child, "smtp.host", "");                        // left empty in the form = not this profile's to say
        Setting(child, "smtp.fromEmail", "post@kunde.de");

        var c = await ConfigAsync(child);
        Assert.Equal("smtp.matthix.de", c.Settings!["smtp.host"]);
        Assert.Equal("mailer", c.Settings["smtp.user"]);
        Assert.Equal("post@kunde.de", c.Settings["smtp.fromEmail"]);
        Assert.Equal("MatCMS", c.Settings["smtp.fromName"]);

        // a profile on its own still rolls out exactly what it stores, empty fields included (as before inheritance)
        var alone = Add("Allein", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Own; });
        Setting(alone, "smtp.user", "");
        Assert.Equal("", (await ConfigAsync(alone)).Settings!["smtp.user"]);
    }

    [Fact]
    public async Task A_child_with_the_relay_drops_the_smtp_values_of_its_parent()
    {
        var standard = Add("Standard", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Own; });
        Setting(standard, "smtp.host", "smtp.matthix.de");
        var child = Add("Relay", standard.Id, p => { p.SyncSmtp = true; p.MailSource = MailSources.Cloud; });

        var c = await ConfigAsync(child);
        Assert.Equal("cloud", c.MailTransport);
        Assert.DoesNotContain(c.Settings!.Keys, k => k.StartsWith("smtp."));
    }

    [Fact]
    public async Task Users_and_plugins_add_up_and_the_childs_version_wins()
    {
        var standard = Add("Standard", set: p => { p.SyncUsers = true; p.SyncPlugins = true; });
        _db.ProfileUsers.Add(new ProfileUser { ProfileId = standard.Id, Username = "support", PasswordHash = "h1" });
        _db.ProfilePlugins.Add(new ProfilePlugin { ProfileId = standard.Id, Key = "forum", Name = "Forum", Version = "1.0", Bundle = new byte[] { 1 } });
        _db.ProfilePlugins.Add(new ProfilePlugin { ProfileId = standard.Id, Key = "verein", Name = "Verein", Version = "1.12", Bundle = new byte[] { 2 } });
        var child = Add("Vereine", standard.Id, p => p.SyncPlugins = true);
        _db.ProfilePlugins.Add(new ProfilePlugin { ProfileId = child.Id, Key = "verein", Name = "Verein", Version = "1.13", Bundle = new byte[] { 3 } });
        _db.SaveChanges();

        var c = await ConfigAsync(child);
        Assert.Equal("support", Assert.Single(c.Users!).Username);
        Assert.Equal("1.0", c.Plugins!.Single(p => p.Key == "forum").Version);
        Assert.Equal("1.13", c.Plugins!.Single(p => p.Key == "verein").Version);

        // the instance downloads the bundle the config promised: the child's own, else the parent's
        Assert.Equal(new byte[] { 3 }, await _profiles.FindPluginBundleAsync(child.Id, "verein"));
        Assert.Equal(new byte[] { 1 }, await _profiles.FindPluginBundleAsync(child.Id, "forum"));
    }

    [Fact]
    public async Task A_change_to_the_parent_reaches_the_instances_of_every_child()
    {
        var standard = Add("Standard");
        var child = Add("Kind", standard.Id);
        var grandchild = Add("Enkel", child.Id);
        var other = Add("Anderes");
        int Rev(Profile p) => _db.Profiles.AsNoTracking().Single(x => x.Id == p.Id).Revision;
        var before = (Rev(child), Rev(grandchild), Rev(other));

        await _profiles.TouchAsync(standard.Id);

        Assert.Equal(before.Item1 + 1, Rev(child));
        Assert.Equal(before.Item2 + 1, Rev(grandchild));
        Assert.Equal(before.Item3, Rev(other));
    }

    [Fact]
    public async Task No_loop_no_self_no_endless_chain_and_no_parent_deleted_from_under_a_child()
    {
        var a = Add("A");
        var b = Add("B", a.Id);
        var c = Add("C", b.Id);
        Assert.NotNull(await _profiles.SetParentAsync(a.Id, c.Id));      // C → B → A → C would be a loop
        Assert.NotNull(await _profiles.SetParentAsync(a.Id, a.Id));
        Assert.Null(await _profiles.SetParentAsync(c.Id, null));
        Assert.Null(await _profiles.SetParentAsync(c.Id, b.Id));

        var d = Add("D", c.Id);
        var e = Add("E", d.Id);                                           // A-B-C-D-E: five levels, the maximum
        var f = Add("F");
        Assert.NotNull(await _profiles.SetParentAsync(f.Id, e.Id));
        Assert.DoesNotContain(await _profiles.ParentChoicesAsync(a.Id), p => p.Id == c.Id);

        Assert.Equal(new[] { "B" }, await _profiles.ChildNamesAsync(a.Id));
        // Deleting needs neither accounts nor instances; the two services stay out of the way.
        var ops = new ProfileOpsService(_db, _profiles, null!, new SecretProtector(new EphemeralDataProtectionProvider()), null!);
        Assert.False((await ops.DeleteAsync(a.Id)).Ok);
    }

    [Fact]
    public async Task The_sender_name_is_the_sites_own_or_the_one_given_to_the_instance_and_never_the_address()
    {
        var standard = Add("Standard", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Cloud; p.MailFromSiteName = true; });
        var child = Add("Kind", standard.Id);
        var eff = await ProfileInheritance.EffectiveAsync(_db, child);
        var inst = new Instance { Name = "schalksteinnarren" };

        Assert.Equal("Schalksteinnarren Besigheim", ProfileInheritance.SenderName(inst, eff, "Schalksteinnarren Besigheim"));
        Assert.Equal("schalksteinnarren", ProfileInheritance.SenderName(inst, eff, null));           // an older site sends no name
        inst.MailFromName = "Narrenzunft\r\nBcc: x@evil.example";                                       // a header cannot be smuggled in
        Assert.Equal("Narrenzunft  Bcc: x@evil.example", ProfileInheritance.SenderName(inst, eff, "egal"));

        // without the switch the cloud's own name stays — unless the instance was given one
        var off = await ProfileInheritance.EffectiveAsync(_db, Add("Ohne", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Cloud; }));
        Assert.Null(ProfileInheritance.SenderName(new Instance { Name = "x" }, off, "Seite"));
    }

    [Fact]
    public async Task Rolled_out_smtp_carries_the_site_name_switch_and_an_instances_own_name()
    {
        var standard = Add("Standard", set: p => { p.SyncSmtp = true; p.MailSource = MailSources.Own; p.MailFromSiteName = true; });
        Setting(standard, "smtp.host", "smtp.matthix.de");
        Setting(standard, "smtp.fromName", "MatCMS");
        Assert.Equal("1", (await ConfigAsync(standard)).Settings![ProfileService.SmtpFromSiteNameKey]);

        var inst = new Instance { Name = "verein", PublicId = "p1", TokenHash = "t", ProfileId = standard.Id, MailFromName = "Narrenzunft Besigheim" };
        _db.Instances.Add(inst);
        _db.SaveChanges();
        var loaded = _db.Instances.Include(i => i.Profile).AsNoTracking().Single(i => i.Id == inst.Id);
        var c = await _profiles.BuildConfigForAsync(loaded);
        Assert.Equal("Narrenzunft Besigheim", c.Settings!["smtp.fromName"]);
        Assert.Equal("0", c.Settings[ProfileService.SmtpFromSiteNameKey]);
        Assert.Equal("smtp.matthix.de", c.Settings["smtp.host"]);
    }
}
