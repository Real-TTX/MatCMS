using MatCMS.Models;
using MatCMS.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>"The site's name as sender": one address for many sites (a cloud profile's SMTP), and each mail still says whose it is.</summary>
public class MailSenderNameTests
{
    private static void Set(Harness h, string key, string value)
    {
        h.Db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        h.Db.SaveChanges();
    }

    [Fact]
    public async Task With_the_switch_the_sites_name_is_the_sender_name_and_the_address_stays()
    {
        using var h = new Harness();
        Set(h, SettingKeys.SiteName, "Schalksteinnarren Besigheim");
        Set(h, SettingKeys.SmtpFromEmail, "noreply@matthix.de");
        Set(h, SettingKeys.SmtpFromName, "MatCMS");
        var mail = new EmailService(h.Db, h.Services, NullLogger<EmailService>.Instance);

        Assert.Equal("MatCMS", (await mail.GetConfigAsync()).FromName);

        Set(h, SettingKeys.SmtpFromSiteName, "1");
        var cfg = await mail.GetConfigAsync();
        Assert.Equal("Schalksteinnarren Besigheim", cfg.FromName);
        Assert.Equal("noreply@matthix.de", cfg.FromEmail);
    }
}
