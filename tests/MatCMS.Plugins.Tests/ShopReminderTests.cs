using System.Net;
using MatCMS.Plugin;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

public class ShopReminderUnitTests
{
    [Theory]
    [InlineData("kunde@example.org", true)]
    [InlineData("a.b+c@sub.example.co.uk", true)]
    [InlineData("kunde@example", false)]
    [InlineData("@example.org", false)]
    [InlineData("kunde@@example.org", false)]
    [InlineData("kunde @example.org", false)]
    [InlineData("a@b.org, c@d.org", false)]
    [InlineData("a@b.org;c@d.org", false)]
    [InlineData("kunde@example.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_address_must_look_like_exactly_one_address(string? email, bool ok) => Assert.Equal(ok, ShopReminders.ValidEmail(email));

    [Fact]
    public void The_reminder_is_due_after_the_set_hours()
    {
        var cfg = new ShopConfig { RemindHours = 6 };
        Assert.Equal(new DateTime(2026, 10, 16, 18, 0, 0, DateTimeKind.Utc), ShopReminders.DueAt(new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), cfg));
    }
}

/// <summary>The reminder for an abandoned cart: opt-in on the cart page, one mail from the job, a restore link, nothing for carts that do not qualify.</summary>
public class ShopReminderTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopReminderTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { ["cart.remind.enabled"] = "true", ["cart.remind.hours"] = "24" });
    public Task DisposeAsync() { ShopReminders.MailSender = null; return Task.CompletedTask; }

    private sealed record Sent(string To, IReadOnlyDictionary<string, string> Values, IReadOnlyList<IReadOnlyDictionary<string, string>> Items);

    private static async Task<HttpResponseMessage> PostCartAsync(HttpClient c, Dictionary<string, string> form)
    {
        var page = await c.GetStringAsync("/shop/cart");
        form["__RequestVerificationToken"] = AppFactory.Token(page)!;
        return await c.PostAsync("/shop/cart", new FormUrlEncodedContent(form));
    }

    private Task AgeCartAsync(string email, TimeSpan age) => _shop.WithContextAsync(async (ctx, _) =>
    {
        var t = ctx.Data.Table("carts");
        foreach (var r in await t.QueryAsync(DataQuery.Everything.Eq("email", email))) await t.UpdateAsync(r.Id, new Dictionary<string, object?> { ["updated"] = DateTime.UtcNow - age });
        return 0;
    });

    private async Task<List<Sent>> RunJobAsync()
    {
        var sent = new List<Sent>();
        ShopReminders.MailSender = (to, values, items) => { sent.Add(new Sent(to, values, items)); return Task.FromResult(new MailResult(true, null)); };
        await _shop.WithContextAsync(async (ctx, store) => await ShopReminders.RunAsync(ctx, ShopConfig.From(ctx.Settings), store, DateTime.UtcNow, default));
        return sent;
    }

    private Task<IReadOnlyList<DataRow>> CartRowsOf(string email) =>
        _shop.WithContextAsync(async (ctx, _) => await ctx.Data.Table("carts").QueryAsync(DataQuery.Everything.Eq("email", email)));

    [Fact]
    public async Task The_customer_asks_for_a_reminder_with_a_valid_address_and_can_take_it_back()
    {
        await _shop.AddProductAsync("Rem Ding", 2500);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "rem-ding");
        Assert.Contains("class=\"shop-remind\"", await c.GetStringAsync("/shop/cart"));
        var bad = WebUtility.HtmlDecode(await (await PostCartAsync(c, new() { ["action"] = "remind", ["email"] = "keine-adresse" })).Content.ReadAsStringAsync());
        Assert.Contains("gültige E-Mail-Adresse", bad);
        var ok = WebUtility.HtmlDecode(await (await PostCartAsync(c, new() { ["action"] = "remind", ["email"] = "Rem.Kunde@Example.org" })).Content.ReadAsStringAsync());
        Assert.Contains("Erinnerung eingerichtet für Rem.Kunde@Example.org", ok); Assert.Contains("Erinnerung ausschalten", ok);
        var row = Assert.Single(await CartRowsOf("rem.kunde@example.org"));
        Assert.True(row.Bool("remind")); Assert.Equal(48, row.Text("restore_key").Length);
        var off = WebUtility.HtmlDecode(await (await PostCartAsync(c, new() { ["action"] = "remind-off" })).Content.ReadAsStringAsync());
        Assert.Contains("Erinnerung ausgeschaltet", off);
        Assert.Empty(await CartRowsOf("rem.kunde@example.org"));                                         // the address went with it
    }

    [Fact]
    public async Task The_job_sends_one_mail_for_a_cart_that_was_left_and_never_a_second()
    {
        await _shop.AddProductAsync("Rem Job", 1500);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "rem-job", qty: 2);
        await PostCartAsync(c, new() { ["action"] = "remind", ["email"] = "job@example.org" });
        Assert.DoesNotContain(await RunJobAsync(), s => s.To == "job@example.org");                       // too young: touched a moment ago
        await AgeCartAsync("job@example.org", TimeSpan.FromHours(30));
        var mail = Assert.Single((await RunJobAsync()).Where(s => s.To == "job@example.org").ToList());
        Assert.Contains("/shop/cart?restore=", mail.Values["restore_url"]); Assert.Contains("30,00", mail.Values["total"]);
        var item = Assert.Single(mail.Items); Assert.Equal("2", item["qty"]); Assert.Equal("Rem Job", item["title"]);
        Assert.DoesNotContain(await RunJobAsync(), s => s.To == "job@example.org");                       // once
    }

    [Fact]
    public async Task A_cart_that_is_too_old_or_whose_owner_ordered_since_or_who_never_asked_gets_nothing()
    {
        await _shop.AddProductAsync("Rem Alt", 1000);
        var old = _app.Browser(); await _shop.AddToCartAsync(old, "rem-alt");
        await PostCartAsync(old, new() { ["action"] = "remind", ["email"] = "alt@example.org" });
        await AgeCartAsync("alt@example.org", TimeSpan.FromDays(20));                                     // older than two weeks

        var buyer = _app.Browser(); await _shop.AddToCartAsync(buyer, "rem-alt");
        await PostCartAsync(buyer, new() { ["action"] = "remind", ["email"] = "kauft@example.org" });
        await AgeCartAsync("kauft@example.org", TimeSpan.FromHours(30));
        var other = _app.Browser(); await _shop.AddToCartAsync(other, "rem-alt");                         // the same address orders from another browser meanwhile
        await _shop.CheckoutAsync(other, email: "kauft@example.org");

        var quiet = _app.Browser(); await _shop.AddToCartAsync(quiet, "rem-alt");                         // never asked for a reminder
        await _shop.WithContextAsync(async (ctx, _) =>
        {
            var t = ctx.Data.Table("carts");
            foreach (var r in await t.QueryAsync(DataQuery.Everything)) if (r.Text("email").Length == 0) await t.UpdateAsync(r.Id, new Dictionary<string, object?> { ["updated"] = DateTime.UtcNow.AddHours(-48) });
            return 0;
        });

        var sent = await RunJobAsync();
        Assert.DoesNotContain(sent, s => s.To is "alt@example.org" or "kauft@example.org");
        Assert.DoesNotContain(sent, s => string.IsNullOrEmpty(s.To));
    }

    [Fact]
    public async Task The_restore_link_puts_the_cart_back_in_another_browser_and_a_wrong_key_does_nothing()
    {
        await _shop.AddProductAsync("Rem Link", 1800);
        var first = _app.Browser(); await _shop.AddToCartAsync(first, "rem-link", qty: 3);
        await PostCartAsync(first, new() { ["action"] = "remind", ["email"] = "link@example.org" });
        var key = Assert.Single(await CartRowsOf("link@example.org")).Text("restore_key");

        var phone = _app.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await phone.GetAsync("/shop/cart?restore=" + key)).StatusCode);
        var cart = WebUtility.HtmlDecode(await phone.GetStringAsync("/shop/cart"));
        Assert.Contains("Rem Link", cart); Assert.Contains("54,00", cart);

        var stranger = _app.Browser();
        foreach (var wrong in new[] { new string('0', 48), "kurz", key.ToUpperInvariant() + "x", "' OR 1=1 --" })
            Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync("/shop/cart?restore=" + Uri.EscapeDataString(wrong))).StatusCode);   // the ordinary cart page
        var after = await stranger.GetStringAsync("/shop/cart");
        Assert.Contains("shop-cart-empty", after); Assert.DoesNotContain("54,00", after);                  // still an empty cart (the empty page suggests products, so the name may appear)
    }
}
