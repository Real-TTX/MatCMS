using System.Net;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

public class MenuVisibilityUnitTests
{
    private static MenuItem I(int id, string label, string vis = "", int? parent = null) => new() { Id = id, Label = label, Visibility = vis, ParentId = parent };

    [Fact]
    public void Members_see_the_member_entries_and_guests_the_guest_entries_and_everybody_the_rest()
    {
        var items = new[] { I(1, "Start"), I(2, "Konto", "in"), I(3, "Anmelden", "out") };
        Assert.Equal(new[] { "Start", "Konto" }, MenuVisibility.Filter(items, signedIn: true).Select(i => i.Label));
        Assert.Equal(new[] { "Start", "Anmelden" }, MenuVisibility.Filter(items, signedIn: false).Select(i => i.Label));
    }

    [Fact]
    public void A_hidden_entry_takes_everything_under_it_along_at_any_depth()
    {
        var items = new[] { I(1, "Konto", "in"), I(2, "Bestellungen", "", 1), I(3, "Abmelden", "", 1), I(4, "Alt", "", 3), I(5, "Shop"), I(6, "Neu", "", 5) };
        Assert.Equal(new[] { "Shop", "Neu" }, MenuVisibility.Filter(items, signedIn: false).Select(i => i.Label));
        Assert.Equal(6, MenuVisibility.Filter(items, signedIn: true).Count);
    }

    [Fact]
    public void A_child_can_be_narrower_than_its_parent_and_a_damaged_loop_hides_instead_of_hanging()
    {
        var items = new[] { I(1, "Konto", "in"), I(2, "Nur Gast", "out", 1) };
        Assert.Equal(new[] { "Konto" }, MenuVisibility.Filter(items, signedIn: true).Select(i => i.Label));
        var loop = new[] { I(1, "A", "", 2), I(2, "B", "", 1) };
        Assert.Empty(MenuVisibility.Filter(loop, signedIn: true));
    }

    [Theory]
    [InlineData("in", "in")]
    [InlineData("out", "out")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("admin", "")]
    public void Only_the_two_known_values_are_kept(string? given, string expected) => Assert.Equal(expected, MenuItem.NormaliseVisibility(given));
}

/// <summary>The site's header for a guest and for a signed-in member, through the real pages.</summary>
public class MenuVisibilityTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public MenuVisibilityTests(AppFactory app) { _app = app; }

    [Fact]
    public async Task The_header_shows_each_visitor_their_own_entries_with_sub_entries_under_a_hidden_parent_gone_too()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var konto = new MenuItem { Menu = "header", Label = "VisKonto", Url = "/konto", Visibility = "in", SortOrder = 90, Locale = Localizer.DefaultCulture };
            var anmelden = new MenuItem { Menu = "header", Label = "VisAnmelden", Url = "/anmelden", Visibility = "out", SortOrder = 91, Locale = Localizer.DefaultCulture };
            db.MenuItems.AddRange(konto, anmelden);
            await db.SaveChangesAsync();
            db.MenuItems.AddRange(
                new MenuItem { Menu = "header", Label = "VisBestellungen", Url = "/konto/bestellungen", ParentId = konto.Id, SortOrder = 1, Locale = Localizer.DefaultCulture },
                new MenuItem { Menu = "header", Label = "VisAbmelden", Url = "/anmelden", ParentId = konto.Id, SortOrder = 2, Locale = Localizer.DefaultCulture });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync("vis@example.org", "correct horse", "Vis Member");
        }

        var guest = await _app.Browser().GetStringAsync("/");
        Assert.Contains("VisAnmelden", guest);
        Assert.DoesNotContain("VisKonto", guest); Assert.DoesNotContain("VisBestellungen", guest); Assert.DoesNotContain("VisAbmelden", guest);

        var member = _app.Browser();
        var login = await member.GetStringAsync("/anmelden");
        var res = await member.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "vis@example.org", ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(login)! }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var signedIn = await member.GetStringAsync("/");
        Assert.Contains("VisKonto", signedIn); Assert.Contains("VisBestellungen", signedIn); Assert.Contains("VisAbmelden", signedIn);
        Assert.DoesNotContain("VisAnmelden", signedIn);

        // The back office lists every entry whatever the editor's own state is.
        var admin = _app.Browser(); await AppFactory.LoginAsync(admin);
        var list = await admin.GetStringAsync("/admin/menus");
        Assert.Contains("header", list, StringComparison.OrdinalIgnoreCase);
    }
}
