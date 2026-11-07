using System.Text.RegularExpressions;
using MatCMS.Content;
using MatCMS.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>A form that nobody set up mails its submissions to the admins — an inquiry must not sit unseen in the back office.</summary>
public class FormNotifyTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public FormNotifyTests(AppFactory app) => _app = app;

    [Fact]
    public void A_form_never_set_up_sends_one_switched_off_does_not()
    {
        Assert.True(FormNotify.IsUnset(false, ""));
        Assert.True(FormNotify.Sends(false, null));
        Assert.False(FormNotify.Sends(false, new FormNotify().Serialize()));   // saved with the box unticked = off
        Assert.True(FormNotify.Sends(true, new FormNotify().Serialize()));
        Assert.False(FormNotify.IsUnset(true, ""));
    }

    [Fact]
    public void Nobody_chosen_means_the_admins_otherwise_only_who_was_chosen()
    {
        var admins = () => new[] { "chef@example.com", "", "CHEF@example.com" };
        Assert.Equal(["chef@example.com"], new FormNotify().Recipients([], admins));

        var chosen = new FormNotify { Emails = ["laden@example.com"] };
        Assert.Equal(["laden@example.com", "rick@example.com"], chosen.Recipients(["rick@example.com", "Laden@example.com"], admins));
    }

    [Fact]
    public async Task The_settings_page_shows_the_default_ticked_with_the_admins_and_saving_it_off_sticks()
    {
        int formId, adminId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            admin.Email = "admin@example.com";
            var form = await db.Forms.FirstAsync(f => f.Slug == "kontakt");
            form.NotifyEnabled = false; form.NotifyJson = "";
            await db.SaveChangesAsync();
            formId = form.Id; adminId = admin.Id;
        }

        var c = _app.Browser();
        await AppFactory.LoginAsync(c);
        var html = await c.GetStringAsync($"/admin/forms/edit/{formId}");
        Assert.Matches("name=\"Settings.NotifyEnabled\" value=\"true\"\\s+checked", html);
        Assert.Matches($"name=\"Settings.NotifyUserIds\" value=\"{adminId}\"\\s+checked", html);

        var res = await AppFactory.PostAsync(c, $"/admin/forms/edit/{formId}?handler=Settings", new()
        {
            ["Settings.SuccessMessage"] = "", ["Settings.SubmitLabel"] = "", ["Settings.NotifyEmails"] = "", ["Settings.SpamLevel"] = "-1",
            ["__RequestVerificationToken"] = AppFactory.Token(html) ?? ""
        });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);

        html = await c.GetStringAsync($"/admin/forms/edit/{formId}");
        Assert.DoesNotMatch("name=\"Settings.NotifyEnabled\" value=\"true\"\\s+checked", html);
        using (var scope = _app.Services.CreateScope())
        {
            var form = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Forms.AsNoTracking().FirstAsync(f => f.Id == formId);
            Assert.False(FormNotify.Sends(form.NotifyEnabled, form.NotifyJson));
        }
    }
}
