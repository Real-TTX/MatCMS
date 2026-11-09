using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>The relay queues a site's mail with the sender NAME the cloud decided and keeps whether it is HTML (it used to drop
/// that, and HTML mails went out as a wall of tags). It accepts a site whose profile only INHERITS the relay.</summary>
public sealed class MailRelayTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;

    public MailRelayTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task An_inherited_relay_queues_with_the_sites_name_and_keeps_html()
    {
        var standard = new Profile { Name = "Standard", JoinCode = "A", SyncSmtp = true, MailSource = MailSources.Cloud, MailFromSiteName = true };
        _db.Profiles.Add(standard);
        _db.SaveChanges();
        var child = new Profile { Name = "Vereine", JoinCode = "B", ParentId = standard.Id };
        _db.Profiles.Add(child);
        _db.SaveChanges();
        var inst = new Instance { Name = "schalk", PublicId = "p", TokenHash = "t", ProfileId = child.Id };
        _db.Instances.Add(inst);
        _db.SaveChanges();

        var spool = new MailSpool(_db);
        var r = await spool.EnqueueAsync(inst, new MailRequest
        {
            To = ["mitglied@example.org"], Subject = "Neuer Beitrag", Body = "<p>Hallo</p>", IsHtml = true, SiteName = "Schalksteinnarren Besigheim"
        });
        Assert.True(r.Queued, r.Error);
        var row = Assert.Single(_db.SpooledMails.AsNoTracking());
        Assert.Equal("Schalksteinnarren Besigheim", row.FromName);
        Assert.True(row.IsHtml);

        // the operator's name for this one site wins
        inst.MailFromName = "Narrenzunft";
        await spool.EnqueueAsync(inst, new MailRequest { To = ["a@example.org"], Subject = "x", Body = "y", SiteName = "egal" });
        Assert.Equal("Narrenzunft", _db.SpooledMails.AsNoTracking().OrderBy(m => m.Id).Last().FromName);
    }
}
