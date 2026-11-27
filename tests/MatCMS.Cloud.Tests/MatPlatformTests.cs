using MailKit.Security;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Platform;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>
/// The cloud as an app of the platform contract (MatDesign docs/PLATFORM.md): its definition is valid, the people of MatIdentity become the right
/// accounts (never an existing one by its address alone), a site bound through cms.cloud lands in the default profile, and the mail goes through
/// MatSMTP while its variables are set.
/// </summary>
public sealed class MatPlatformTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;

    public MatPlatformTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static string Hash(string password) => "hash:" + password;

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid(string language)
    {
        var sprite = Path.Combine(RepoRoot(), "src", "MatCMS.Shared.Web", "wwwroot", "lib", "matui", "matui-icons.svg");
        var definition = MatPlatformBuilder.Build(CloudPlatform.Describe, language);
        Assert.Empty(MatPlatformValidator.Validate(definition, MatPlatformValidator.ReadSprite(sprite)));
        Assert.Contains(definition.Provides!, p => p.Capability == CloudPlatform.CmsCloud && p.Version == 1);
        Assert.Contains(definition.Consumes!, c => c.Capability == MatCapabilities.IdentityOidc && c.Optional == true);
        Assert.Contains(definition.Consumes!, c => c.Capability == MatCapabilities.MailSmtp && c.Optional == true);
        Assert.Equal(["admin", "operator"], definition.Roles!.Select(r => r.Key));
    }

    [Fact]
    public async Task The_first_person_through_MatIdentity_is_admin_the_next_an_operator()
    {
        _db.Users.Add(new User { Username = "admin", Email = "admin@localhost", PasswordHash = "x" });   // the seeded account
        await _db.SaveChangesAsync();

        var first = await IdentityAccounts.ResolveAsync(_db, new("sub-1", "owner@example.org", "Owner", "owner", null), Hash);
        Assert.Equal(IdentityAccounts.Kind.SignIn, first.Kind);
        Assert.Equal(User.RoleAdmin, first.User!.Role);
        Assert.Equal("sub-1", first.User.ExternalSubject);

        var second = await IdentityAccounts.ResolveAsync(_db, new("sub-2", "colleague@example.org", null, "colleague", null), Hash);
        Assert.Equal(User.RoleOperator, second.User!.Role);

        // the same person again is the same account; MatIdentity's role is followed when it sends one
        var again = await IdentityAccounts.ResolveAsync(_db, new("sub-2", "colleague@example.org", null, "colleague", "admin"), Hash);
        Assert.Equal(second.User.Id, again.User!.Id);
        Assert.Equal(User.RoleAdmin, again.User.Role);
    }

    [Fact]
    public async Task An_account_with_the_same_address_is_linked_only_after_its_password()
    {
        var local = new User { Username = "pat", Email = "Pat@Example.org", PasswordHash = "x", Role = User.RoleOperator };
        _db.Users.Add(local);
        await _db.SaveChangesAsync();

        var person = new IdentityAccounts.Person("sub-9", "pat@example.org", "Pat", "pat.mi", "admin");
        var outcome = await IdentityAccounts.ResolveAsync(_db, person, Hash);
        Assert.Equal(IdentityAccounts.Kind.Link, outcome.Kind);
        Assert.Equal(local.Id, outcome.User!.Id);
        Assert.Null((await _db.Users.FindAsync(local.Id))!.ExternalSubject);   // not entered by the address
        Assert.Equal(1, await _db.Users.CountAsync());                          // and no second account beside it

        var linked = await IdentityAccounts.LinkAsync(_db, local, person);
        Assert.Equal("sub-9", linked!.ExternalSubject);
        Assert.Equal(User.RoleAdmin, linked.Role);

        // an account that already belongs to another person is refused
        var other = await IdentityAccounts.ResolveAsync(_db, new("sub-10", "pat@example.org", null, null, null), Hash);
        Assert.Equal(IdentityAccounts.Kind.Refused, other.Kind);
    }

    [Fact]
    public async Task A_site_bound_through_cms_cloud_lands_in_the_default_profile()
    {
        _db.Profiles.Add(new Profile { Name = "Andere", JoinCode = "OTHER" });
        _db.Profiles.Add(new Profile { Name = "Standard", JoinCode = "STD", IsDefault = true, AutoApprove = true });
        await _db.SaveChangesAsync();

        var instances = Instances();
        var result = await instances.RegisterBoundAsync(new RegisterRequest { SiteName = "Verein", Url = "https://verein.example.org" }, "matcms");
        Assert.NotNull(result.Token);
        var instance = await _db.Instances.Include(i => i.Profile).SingleAsync();
        Assert.Equal("Standard", instance.Profile!.Name);
        Assert.Equal(InstanceStatus.Approved, instance.Status);
        Assert.NotNull(await instances.AuthenticateAsync(instance.PublicId, result.Token));
    }

    [Fact]
    public async Task Without_a_default_profile_a_bound_site_is_refused()
    {
        var result = await Instances().RegisterBoundAsync(new RegisterRequest { SiteName = "x" }, "matcms");
        Assert.Null(result.Instance);
        Assert.Empty(_db.Instances);
    }

    [Fact]
    public async Task The_mail_goes_through_MatSMTP_while_its_variables_are_set()
    {
        _db.CloudSettings.AddRange(
            new CloudSetting { Key = SettingKeys.SmtpHost, Value = "smtp.example.org" },
            new CloudSetting { Key = SettingKeys.SmtpFromEmail, Value = "cloud@example.org" },
            new CloudSetting { Key = SettingKeys.SmtpFromName, Value = "Meine Cloud" });
        await _db.SaveChangesAsync();
        var secrets = new SecretProtector(new EphemeralDataProtectionProvider());

        var own = new EmailService(_db, secrets, NullLogger<EmailService>.Instance, MatSmtpSettings.From(Config()));
        Assert.False(own.IsRelayed);
        Assert.Equal("smtp.example.org", (await own.GetConfigAsync()).Host);

        var relayed = new EmailService(_db, secrets, NullLogger<EmailService>.Instance, MatSmtpSettings.From(Config(new()
        {
            ["MATSMTP_HOST"] = "matos-matsmtp-matsmtp", ["MATSMTP_PORT"] = "2025", ["MATSMTP_USERNAME"] = "matcms-cloud",
            ["MATSMTP_PASSWORD"] = "secret", ["MATSMTP_FROM"] = "cloud@mail.example.org", ["MATSMTP_TLS"] = "none",
        })));
        Assert.True(relayed.IsRelayed);
        var cfg = await relayed.GetConfigAsync();
        Assert.Equal(("matos-matsmtp-matsmtp", 2025, "matcms-cloud", "secret"), (cfg.Host, cfg.Port, cfg.User, cfg.Password));
        Assert.Equal("cloud@mail.example.org", cfg.FromEmail);
        Assert.Equal("Meine Cloud", cfg.FromName);              // the sender NAME stays the cloud's
        Assert.Equal(SecureSocketOptions.None, cfg.Mode);       // inside the Docker network: as MatSMTP says, no opportunistic STARTTLS
    }

    private InstanceService Instances()
    {
        var config = Config(new() { ["MatCmsCloud:Docker:Endpoint"] = "" });
        var protector = new SecretProtector(new EphemeralDataProtectionProvider());
        return new InstanceService(_db, new DockerHostService(config, NullLogger<DockerHostService>.Instance),
            new ReleaseWatcher(new GhcrClient(new NoHttp(), NullLogger<GhcrClient>.Instance), NullLogger<ReleaseWatcher>.Instance),
            new ProfileService(_db, protector), new CloudContext(_db), null!);
    }

    private static IConfiguration Config(Dictionary<string, string?>? values = null)
        => new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatCMS.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("MatCMS.slnx not found");
    }
}

/// <summary>No registry is asked in these tests.</summary>
internal sealed class NoHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
