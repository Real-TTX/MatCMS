using System.Net;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>A plugin's own block in the sidebar (separator, heading, links with sub-headings) and the settings page of one group.</summary>
public class PluginSidebarTests
{
    private static IServiceProvider Services() => new ServiceCollection().BuildServiceProvider();

    private const string Block = """
        Plugin.Admin.Section("Laden", "ti-store", AdminGroup.Content, 25)
            .Page("orders", "Bestellungen", ctx => PluginResult.Page("x"))
            .Page("rates", "Raten", ctx => PluginResult.Page("x"))
            .Badge(ctx => "4")
            .Link("Bestellungen", "orders", "ti-receipt", badge: true)
            .Link("Produkte", "products", "ti-package")
            .Link("Raten", "rates", "ti-truck", heading: "Konditionen")
            .Link("Zahlung", "_settings?group=Zahlung", "ti-credit-card")
            .Link("Kasse", "_settings?group=Kasse", "ti-cart", heading: "Einstellungen");
        """;

    [Fact]
    public async Task A_section_with_links_is_one_block_whose_links_carry_their_sub_headings_and_the_counter_where_asked()
    {
        using var h = new Harness();
        h.AddScript("laden", Block);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        var entry = (await new AdminNav(h.Registry).GroupAsync(AdminGroup.Content, Services())).Single(e => e.Label == "Laden");

        Assert.Equal("4", entry.Badge);                                                    // the section's line carries its counter, as every plugin's line does
        var links = entry.Links!;
        Assert.Equal(new[] { "Bestellungen", "Produkte", "Raten", "Zahlung", "Kasse" }, links.Select(l => l.Label));
        Assert.Equal("/admin/p/laden/orders", links[0].Url);
        Assert.Equal("/admin/p/laden/_settings?group=Zahlung", links[3].Url);
        Assert.Equal("4", links[0].Badge);
        Assert.All(links.Skip(1), l => Assert.Null(l.Badge));
        // A sub-heading is drawn once, on the first link under it.
        Assert.Equal(new string?[] { null, null, "Konditionen", null, "Einstellungen" }, links.Select(l => l.Heading));
    }

    [Fact]
    public async Task A_plain_section_stays_one_line_and_the_pages_of_a_block_are_still_reachable()
    {
        using var h = new Harness();
        h.AddScript("laden", Block);
        h.AddScript("schlicht", "Plugin.Admin.Section(\"Schlicht\", \"ti-star\", AdminGroup.Design, 90).Page(\"start\", \"Start\", ctx => PluginResult.Page(\"x\")).Badge(ctx => \"2\");");
        await h.Runner.RunAllAsync();
        var plain = (await new AdminNav(h.Registry).GroupAsync(AdminGroup.Design, Services())).Single(e => e.Label == "Schlicht");
        Assert.Null(plain.Links);
        Assert.Equal("2", plain.Badge);
        Assert.NotNull(h.Registry.MatchAdminPage("laden", "orders"));                    // the tab pages are still the pages the links lead to
        Assert.NotNull(h.Registry.MatchAdminPage("laden", "rates"));
    }

    [Theory]
    [InlineData("\"/admin/users\"")]
    [InlineData("\"../x\"")]
    [InlineData("\"https://example.org\"")]
    [InlineData("\"a#b\"")]
    [InlineData("\"\"")]
    public async Task A_link_may_only_name_a_place_inside_its_own_section(string path)
    {
        using var h = new Harness();
        h.AddScript("laden", $"Plugin.Admin.Section(\"Laden\").Page(\"orders\", \"B\", ctx => PluginResult.Page(\"x\")).Link(\"Weg\", {path});");
        await h.Runner.RunAllAsync();
        Assert.NotEmpty(h.Registry.Errors);
        Assert.False(h.Registry.IsLoaded("laden"));
    }

    [Fact]
    public async Task A_link_needs_a_label_and_only_one_link_shows_the_counter()
    {
        using var h = new Harness();
        h.AddScript("a", "Plugin.Admin.Section(\"A\").Page(\"o\", \"B\", ctx => PluginResult.Page(\"x\")).Link(\" \", \"o\");");
        h.AddScript("b", "Plugin.Admin.Section(\"B\").Page(\"o\", \"B\", ctx => PluginResult.Page(\"x\")).Link(\"x\", \"o\", badge: true).Link(\"y\", \"o\", badge: true);");
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("a"));
        Assert.False(h.Registry.IsLoaded("b"));
    }

    // ---- settings by group ---------------------------------------------------------------------------------

    private const string Groups = """
        Plugin.Settings
            .Text("name", "Name", def: "x")
            .Bool("live", "Live", def: true)
            .Text("key", "Schlüssel")
            .Text("other", "Anderes");
        """;

    [Fact]
    public async Task Settings_belong_to_the_group_named_before_them_and_a_group_form_shows_and_saves_only_its_own()
    {
        using var h = new Harness();
        // The group is named on the first setting of it; the ones after belong to it until the next name.
        h.AddScript("g", """
            Plugin.Settings.Add(new SettingSpec("name", "Name", SettingKind.Text) { Group = "Eins" });
            Plugin.Settings.Add(new SettingSpec("live", "Live", SettingKind.Bool) { Default = "true" });
            Plugin.Settings.Add(new SettingSpec("key", "Schlüssel", SettingKind.Text) { Group = "Zwei" });
            Plugin.Settings.Add(new SettingSpec("other", "Anderes", SettingKind.Text));
            """);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        var slice = h.Registry.SliceOf("g")!;
        Assert.Equal(new[] { ("Eins", 2), ("Zwei", 2) }, PluginSettingsUi.Groups(slice).Select(g => (g.Group, g.Count)));
        Assert.Equal(new string?[] { "Eins", "Eins", "Zwei", "Zwei" }, PluginSettingsUi.WithGroups(slice).Select(x => x.Group));

        var form = PluginSettingsUi.Build(slice, new Dictionary<string, string>(), "de", "Speichern", "Entfernen", "gesetzt", onlyGroup: "Zwei");
        var html = MatCMS.Shared.Web.UiRenderer.Render(form, new MatCMS.Shared.Web.UiRenderOptions { Target = MatCMS.Shared.Web.UiTarget.Admin });
        Assert.Contains("name=\"s_key\"", html); Assert.Contains("name=\"s_other\"", html);
        Assert.DoesNotContain("name=\"s_name\"", html); Assert.DoesNotContain("name=\"s_live\"", html);

        // Saving the group "Zwei" posts only its fields; the switch of group "Eins" must not read as "off", the text must not be wiped.
        var stored = new Dictionary<string, string> { ["name"] = "Laden", ["live"] = "true" };
        var provider = h.Services.GetRequiredService<IDataProtectionProvider>();
        var (config, errors) = PluginSettingsUi.Apply(slice, stored, new Dictionary<string, string> { ["s_key"] = "abc", ["s_other"] = "xyz" }, provider, k => k, "Zwei");
        Assert.Empty(errors);
        Assert.Equal("true", config["live"]);
        Assert.Equal("Laden", config["name"]);
        Assert.Equal("abc", config["key"]);
        // Without a group the same post is the whole form: the missing switch is "off".
        Assert.Equal("false", PluginSettingsUi.Apply(slice, stored, new Dictionary<string, string> { ["s_key"] = "abc" }, provider, k => k).Config["live"]);
    }
}

/// <summary>The shop in the real application: its sidebar block, no tab bar, and the settings one group at a time.</summary>
public class ShopSidebarTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopSidebarTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static string Sidebar(string html)
    {
        var side = html[html.IndexOf("<nav class=\"admin-nav\"", StringComparison.Ordinal)..];
        return side[..side.IndexOf("</nav>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task The_shop_is_one_line_in_the_main_menu_and_opens_a_menu_of_its_own_with_a_way_back_on_every_one_of_its_pages_and_none_has_a_tab_bar()
    {
        var admin = await _shop.AdminAsync();

        // Elsewhere in the back office the shop is just its line (with the chevron that says "opens a menu"): the sidebar does not grow with a plugin.
        var outside = Sidebar(Decode(await admin.GetStringAsync("/admin/pages")));
        Assert.Contains("href=\"/admin/p/shop\"", outside);
        Assert.DoesNotContain("href=\"/admin/p/shop/orders\"", outside);
        Assert.DoesNotContain("admin-nav-back", outside);

        // Inside, the main menu gives way to the shop's: the way back, the shop's name, its links — on EVERY page of the shop, the start page included.
        foreach (var page in new[] { "", "/dashboard", "/orders", "/products", "/categories", "/customers", "/shipping", "/_settings?group=set.groupStripe" })
        {
            var inside = Sidebar(Decode(await admin.GetStringAsync("/admin/p/shop" + page)));
            Assert.Contains("<a class=\"admin-nav-back\" href=\"/admin\"", inside);
            Assert.Contains("admin-nav-section", inside);
            foreach (var path in new[] { "dashboard", "orders", "products", "categories", "customers", "shipping", "_settings" })
                Assert.Contains($"href=\"/admin/p/shop/{path}", inside);
            Assert.DoesNotContain("href=\"/admin/pages\"", inside);                                // the main menu is not drawn next to it
        }
        Assert.Contains("<a class=\"active\" href=\"/admin/p/shop/dashboard\"", Sidebar(Decode(await admin.GetStringAsync("/admin/p/shop"))));   // the shop's start page is its dashboard
        Assert.Contains("<a class=\"active\" href=\"/admin/p/shop/orders\"", Sidebar(Decode(await admin.GetStringAsync("/admin/p/shop/orders"))));

        var dash = Decode(await admin.GetStringAsync("/admin/p/shop"));
        Assert.Contains("Zu versenden", dash); Assert.Contains("Umsatz, 30 Tage", dash); Assert.Contains("Letzte Bestellungen", dash);

        // The admin's start page: the shop's tile and card carry the shop's name, and orders are not "edited" (they have a card of their own).
        var home = Decode(await admin.GetStringAsync("/admin"));
        Assert.Contains("Shop · Bestellungen", home);
        Assert.Contains("Shop · Neueste Bestellungen", home);
        var from = home.IndexOf("<h2>Zuletzt bearbeitet</h2>", StringComparison.Ordinal);
        var recently = home[from..home.IndexOf("</table>", from, StringComparison.Ordinal)];
        Assert.DoesNotContain("/admin/p/shop/orders/", recently);

        // "Konditionen" stands for all its pages, which are one screen with a sub-menu of six entries.
        foreach (var page in new[] { "shipping", "payment", "coupons", "taxes", "legal", "numbers" })
        {
            var html = Decode(await admin.GetStringAsync("/admin/p/shop/" + page));
            Assert.Contains("<a class=\"active\" href=\"/admin/p/shop/shipping\"", Sidebar(html));
            var sub = html[html.IndexOf("class=\"ui-subnav\"", StringComparison.Ordinal)..];
            sub = sub[..sub.IndexOf("</nav>", StringComparison.Ordinal)];
            Assert.Equal(6, Count(sub, "<a href="));
            Assert.Contains($"<a href=\"/admin/p/shop/{page}\" class=\"active\"", sub);
        }
        foreach (var page in new[] { "products", "categories", "customers" })
        {
            var res = await admin.GetAsync("/admin/p/shop/" + page);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.DoesNotContain("role=\"tablist\"", await res.Content.ReadAsStringAsync());
        }

        // The order list filters by status with a drop-down in its toolbar — never tabs or a row of buttons.
        var orders = Decode(await admin.GetStringAsync("/admin/p/shop/orders"));
        Assert.DoesNotContain("role=\"tablist\"", orders);
        Assert.Contains("<option value=\"todo\">", orders);   // the status is a drop-down in the toolbar
    }

    private static int Count(string text, string part) => System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(part)).Count;

    [Fact]
    public async Task The_condition_pages_are_lists_that_read_the_settings_and_lead_to_the_form_of_their_group()
    {
        var admin = await _shop.AdminAsync();
        var pay = Decode(await admin.GetStringAsync("/admin/p/shop/payment"));
        Assert.Contains("Vorkasse", pay); Assert.Contains("PayPal", pay); Assert.Contains("Stripe", pay);
        Assert.Contains("href=\"/admin/p/shop/_settings?group=set.groupBank\"", pay);
        Assert.Contains("href=\"/admin/p/shop/_settings?group=set.groupPayPal\"", pay);

        var tax = Decode(await admin.GetStringAsync("/admin/p/shop/taxes"));
        Assert.Contains("Frankreich", tax); Assert.Contains("Ihr Land", tax);
        Assert.Contains("Satz des Produkts, überall", tax);

        var legal = Decode(await admin.GetStringAsync("/admin/p/shop/legal"));
        Assert.Contains("/agb", legal); Assert.Contains("Widerrufsbelehrung", legal);

        var numbers = Decode(await admin.GetStringAsync("/admin/p/shop/numbers"));
        Assert.Contains("RE-", numbers); Assert.Contains("GS-", numbers);
    }

    [Fact]
    public async Task The_settings_are_one_group_at_a_time_with_the_groups_as_a_sub_menu_and_saving_a_group_leaves_the_other_settings_alone()
    {
        var admin = await _shop.AdminAsync();
        // Asked for without a group, the first one opens — never one long form.
        var first = await admin.GetAsync("/admin/p/shop/_settings");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/admin/p/shop/_settings?group=set.groupStripe", first.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/admin/p/shop/_settings?group=gibt-es-nicht")).StatusCode);

        var page = Decode(await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupSeller"));
        Assert.Contains("name=\"s_seller.name\"", page);
        Assert.DoesNotContain("name=\"s_stripe.secretKey\"", page);
        Assert.DoesNotContain("name=\"s_bank.enabled\"", page);
        var sub = page[page.IndexOf("class=\"ui-subnav\"", StringComparison.Ordinal)..];
        sub = sub[..sub.IndexOf("</nav>", StringComparison.Ordinal)];
        Assert.Contains("<a href=\"/admin/p/shop/_settings?group=set.groupSeller\" class=\"active\"", sub);
        Assert.Contains("href=\"/admin/p/shop/_settings?group=set.groupBank\"", sub);
        Assert.DoesNotContain("set.groupMenu", sub);                                            // the menu is no setting any more: its pieces are menu items (Admin → Menü)

        var res = await admin.PostAsync("/admin/p/shop/_settings?group=set.groupSeller", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save-settings", ["s_seller.name"] = "Gruppen Laden GmbH", ["s_seller.address"] = "Weg 1", ["s_seller.email"] = "a@example.org",
            ["s_seller.phone"] = "", ["s_seller.vatId"] = "", ["s_seller.taxNumber"] = "", ["s_seller.invoiceFooter"] = ""
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var cfg = await _shop.WithContextAsync((ctx, _) => Task.FromResult(ShopConfig.From(ctx.Settings)));
        Assert.Equal("Gruppen Laden GmbH", cfg.SellerName);
        Assert.True(cfg.BankEnabled);                                                       // the bank switch is of another group: untouched
        Assert.Equal("/agb", cfg.TermsUrl);
    }
}
