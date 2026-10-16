using System.Net;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The rules of a dashboard layout: only an order and a set of hidden ids, tolerant of widgets that come and go.</summary>
public class DashboardLayoutLogicTests
{
    private static DashboardLayout.Widget W(string id, bool wide = false, bool present = true) => new(id, id.ToUpperInvariant(), wide, "_Dash" + id, Present: present);

    private static readonly IReadOnlyList<DashboardLayout.Widget> Defaults = new[] { W("tiles", wide: true), W("attention"), W("website"), W("recent"), W("card:shop"), W("errors", wide: true), W("quickstart", wide: true) };

    [Fact]
    public void Without_a_layout_the_default_order_stands_and_nothing_is_hidden()
    {
        var (ordered, hidden) = DashboardLayout.Apply(Defaults, null);
        Assert.Equal(Defaults.Select(w => w.Id), ordered.Select(w => w.Id));
        Assert.Empty(hidden);
    }

    [Fact]
    public void A_layout_orders_and_hides_and_what_it_does_not_know_is_added_at_the_end_while_what_is_gone_is_skipped()
    {
        var cfg = DashboardLayout.Parse("""{"order":["website","gone","tiles","attention"],"hidden":["attention","gone2"]}""");
        var (ordered, hidden) = DashboardLayout.Apply(Defaults, cfg);
        Assert.Equal(new[] { "website", "tiles", "attention", "recent", "card:shop", "errors", "quickstart" }, ordered.Select(w => w.Id).ToArray());   // "gone" skipped, the rest after
        Assert.Equal(new[] { "attention" }, hidden.ToArray());                                                                                       // "gone2" skipped
    }

    [Fact]
    public void A_layout_is_written_as_the_order_posted_and_the_unticked_ids_are_the_hidden_ones_and_junk_is_dropped()
    {
        var json = DashboardLayout.Serialize(new[] { "a", "b", "a", "../evil", "<script>", "", "c:d" }, new[] { "a", "c:d" });
        var cfg = DashboardLayout.Parse(json)!;
        Assert.Equal(new[] { "a", "b", "c:d" }, cfg.Order.ToArray());
        Assert.Equal(new[] { "b" }, cfg.Hidden.ToArray());
        Assert.DoesNotContain("evil", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"order\":\"x\"}")]
    public void Unreadable_or_empty_layouts_count_as_none(string? json)
    {
        var cfg = DashboardLayout.Parse(json);
        Assert.True(cfg is null || cfg.Order.Count == 0);
    }

    [Fact]
    public void Rows_put_consecutive_cards_together_and_every_wide_widget_alone()
    {
        var rows = DashboardLayout.Rows(new[] { W("tiles", wide: true), W("a"), W("b"), W("c"), W("errors", wide: true), W("d"), W("quickstart", wide: true) });
        Assert.Equal(new[] { "tiles", "a,b,c", "errors", "d", "quickstart" }, rows.Select(r => string.Join(",", r.Select(w => w.Id))).ToArray());
    }
}

/// <summary>The dashboard in the real application: spacing, the customise panel, a layout per user, an admin's default.</summary>
public class DashboardSiteTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public DashboardSiteTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static readonly string[] Ids = { "tiles", "attention", "website", "recent", "submissions", "card:shop", "errors", "quickstart" };

    private static async Task<HttpResponseMessage> Post(HttpClient admin, string handler, IEnumerable<string>? order = null, IEnumerable<string>? shown = null, bool asDefault = false, bool everybody = false)
    {
        var page = await admin.GetStringAsync("/admin");
        var form = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", AppFactory.Token(page)!) };
        foreach (var o in order ?? Array.Empty<string>()) form.Add(new("id", o));
        foreach (var s in shown ?? Array.Empty<string>()) form.Add(new("show", s));
        if (asDefault) form.Add(new("asDefault", "true"));
        return await admin.PostAsync("/admin?handler=" + handler + (everybody ? "&everybody=true" : ""), new FormUrlEncodedContent(form));
    }

    private static int At(string html, string heading) => html.IndexOf("<h2>" + heading, StringComparison.Ordinal);

    [Fact]
    public async Task The_widgets_stand_in_one_column_with_a_gap_and_the_customise_panel_lists_every_one_of_them()
    {
        var admin = await _shop.AdminAsync();
        var html = Decode(await admin.GetStringAsync("/admin"));
        Assert.Contains("class=\"dash-stack\"", html);                                          // one gap between the rows — the quick start no longer touches what is above it
        Assert.Contains("data-dash-cfg-open", html);
        var panel = html[html.IndexOf("id=\"dash-cfg\"", StringComparison.Ordinal)..];
        panel = panel[..panel.IndexOf("</form>", StringComparison.Ordinal)];
        foreach (var id in Ids.Where(i => i != "card:shop")) Assert.Contains($"name=\"id\" value=\"{id}\"", panel);
        Assert.Contains("name=\"id\" value=\"card:shop\"", panel);                              // a plugin's card is one of them
        Assert.Contains("Shop · Neueste Bestellungen", panel);
        Assert.True(At(html, "Braucht Aufmerksamkeit") < At(html, "Website") && At(html, "Website") < At(html, "Zuletzt bearbeitet"));   // the built-in order
    }

    [Fact]
    public async Task A_user_saves_his_own_order_and_what_he_hides_and_resets_it()
    {
        var admin = await _shop.AdminAsync();
        try
        {
            // Website before attention, "recent" hidden.
            var order = new[] { "website", "tiles", "attention", "recent", "submissions", "card:shop", "errors", "quickstart" };
            var res = await Post(admin, "Layout", order, order.Where(i => i != "recent"));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            var html = Decode(await admin.GetStringAsync("/admin"));
            var cards = html[html.IndexOf("class=\"dash-stack\"", StringComparison.Ordinal)..];
            Assert.True(At(cards, "Website") < At(cards, "Braucht Aufmerksamkeit"));
            Assert.Equal(-1, At(cards, "Zuletzt bearbeitet"));                                  // hidden: not drawn ...
            var panel = html[html.IndexOf("id=\"dash-cfg\"", StringComparison.Ordinal)..];
            Assert.Matches("name=\"show\" value=\"attention\"[^>]*checked", panel);               // ... but still listed, unticked
            Assert.DoesNotMatch("name=\"show\" value=\"recent\"[^>]*checked", panel);

            // Junk in the form is ignored: the page still comes up and keeps the valid part.
            var junk = await Post(admin, "Layout", new[] { "website", "../evil", "<x>", "attention" }, new[] { "website", "attention" });
            Assert.Equal(HttpStatusCode.Redirect, junk.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin")).StatusCode);

            // Reset: the built-in order is back.
            Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "ResetLayout")).StatusCode);
            var back = Decode(await admin.GetStringAsync("/admin"));
            Assert.True(At(back, "Braucht Aufmerksamkeit") < At(back, "Website") && At(back, "Website") < At(back, "Zuletzt bearbeitet"));
        }
        finally { await Post(admin, "ResetLayout"); }
    }

    [Fact]
    public async Task An_admin_can_make_a_layout_the_default_for_everybody_and_a_user_without_his_own_follows_it()
    {
        var admin = await _shop.AdminAsync();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        try
        {
            Assert.True(At(Decode(await admin.GetStringAsync("/admin")), "Letzte Formular-Eingänge") > 0);                // there to begin with
            var order = new[] { "attention", "website", "submissions", "recent", "tiles", "card:shop", "errors", "quickstart" };
            Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "Layout", order, order.Where(i => i != "submissions"), asDefault: true)).StatusCode);
            Assert.True(db.SiteSettings.Any(s => s.Key == SettingKeys.DashboardLayout));

            // Take his own away: he now sees the default (submissions hidden, tiles after the cards).
            await Post(admin, "ResetLayout");
            var html = Decode(await admin.GetStringAsync("/admin"));
            Assert.Equal(-1, At(html, "Letzte Formular-Eingänge"));
            var stack = html[html.IndexOf("class=\"dash-stack\"", StringComparison.Ordinal)..];
            Assert.True(At(stack, "Braucht Aufmerksamkeit") < stack.IndexOf("class=\"stat-grid dash-tiles\"", StringComparison.Ordinal));

            // "Reset for everybody" removes the default.
            Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "ResetLayout", everybody: true)).StatusCode);
            db.ChangeTracker.Clear();
            Assert.False(db.SiteSettings.Any(s => s.Key == SettingKeys.DashboardLayout));
        }
        finally
        {
            db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key == SettingKeys.DashboardLayout));
            foreach (var u in db.Users) u.DashboardJson = null;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task A_widget_that_is_gone_does_not_break_a_stored_layout_and_one_that_is_new_shows_up_at_the_end()
    {
        var admin = await _shop.AdminAsync();
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        try
        {
            // A layout from before: it names a card of a plugin that is not there and knows nothing of the shop's card.
            foreach (var u in db.Users) u.DashboardJson = """{"order":["card:gone","website","attention"],"hidden":["card:gone"]}""";
            await db.SaveChangesAsync();
            var res = await admin.GetAsync("/admin");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var html = Decode(await res.Content.ReadAsStringAsync());
            var stack = html[html.IndexOf("class=\"dash-stack\"", StringComparison.Ordinal)..];
            Assert.True(At(stack, "Website") < At(stack, "Braucht Aufmerksamkeit"));
            Assert.True(At(stack, "Braucht Aufmerksamkeit") < At(stack, "Shop · Neueste Bestellungen"));   // new: after what the layout names
            Assert.DoesNotContain("card:gone\" checked", html);
        }
        finally { foreach (var u in db.Users) u.DashboardJson = null; await db.SaveChangesAsync(); }
    }
}
