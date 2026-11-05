using System.Net;
using MatCMS.Data;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>"Vertrag widerrufen" (§ 356a BGB): the page, the review with "Widerruf bestätigen", the stored declaration and the receipt, a member's own orders in the
/// dialog (never anybody else's), a guest who learns nothing about an order he does not hold, the footer link, and the back office following the return.</summary>
public class ShopWithdrawalTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopWithdrawalTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, Dictionary<string, string> fields, string from = "/shop/widerrufen")
    {
        var page = await c.GetStringAsync(from);
        fields["__RequestVerificationToken"] = AppFactory.Token(page)!;
        return await c.PostAsync("/shop/widerrufen", new FormUrlEncodedContent(fields));
    }

    private async Task<(string Number, string Token, long Id)> OrderAsync(HttpClient c, string email, params string[] slugs)
    {
        foreach (var s in slugs) await _shop.AddToCartAsync(c, s);
        var res = await _shop.CheckoutAsync(c, email: email);
        var number = ShopApp.NumberOf(res);
        return (number, ShopApp.TokenOf(res), (await _shop.OrderAsync(number))!.Id);
    }

    [Fact]
    public async Task A_guest_states_name_order_and_mail_sees_it_once_more_and_confirms_and_the_order_says_so()
    {
        await _shop.AddProductAsync("Wd Laufwerk", 99000);
        await _shop.AddProductAsync("Wd Kabel", 4900);
        var c = _app.Browser();
        var (number, token, orderId) = await OrderAsync(c, "wd-gast@example.org", "wd-laufwerk", "wd-kabel");

        // The order's own link fills in the order and offers its items.
        var form = Decode(await c.GetStringAsync($"/shop/widerrufen?order={number}&t={Uri.EscapeDataString(token)}"));
        Assert.Contains("value=\"" + number + "\"", form);
        Assert.Contains("Wd Kabel", form);
        var kabel = (await _shop.ItemsAsync(orderId)).Single(i => i.Title == "Wd Kabel");

        var fields = new Dictionary<string, string> { ["action"] = "review", ["name"] = "Gerda Gast", ["email"] = "wd-gast@example.org", ["order"] = number, ["t"] = token, ["item"] = kabel.Id.ToString() };
        var review = Decode(await (await PostAsync(c, fields)).Content.ReadAsStringAsync());
        Assert.Contains("Widerruf bestätigen", review);
        Assert.Contains("1 × Wd Kabel", review);
        Assert.DoesNotContain("Wd Laufwerk", review);

        fields["action"] = "confirm";
        var done = Decode(await (await PostAsync(c, fields)).Content.ReadAsStringAsync());
        Assert.Contains("Ihr Widerruf ist eingegangen", done);
        Assert.Contains("WR-", done);
        Assert.Contains("Rücksendung", done);

        var w = (await _shop.WithContextAsync((_, s) => s.WithdrawalsOfOrderAsync(orderId))).Single();
        Assert.Equal("1 × Wd Kabel", w.What);
        Assert.Equal(ShopWithdrawals.Received, w.Status);
        var events = await _shop.EventsAsync(orderId);
        Assert.Contains(events, e => e.Kind == "withdrawal" && e.Text.Contains("1 × Wd Kabel"));
        // The receipt was sent (or its failure written down — the test site has no mail server): never silently skipped.
        Assert.Contains(events, e => e.Kind is "mail" or "mail-failed" && e.Text.Contains("Widerruf"));
    }

    [Fact]
    public async Task A_guest_learns_nothing_about_an_order_he_does_not_hold_and_his_declaration_is_kept_anyway()
    {
        await _shop.AddProductAsync("Wd Fremd", 1000);
        var owner = _app.Browser();
        var (number, _, orderId) = await OrderAsync(owner, "wd-besitzer@example.org", "wd-fremd");

        var stranger = _app.Browser();
        var page = Decode(await stranger.GetStringAsync($"/shop/widerrufen?order={number}"));
        Assert.DoesNotContain("Wd Fremd", page);                                          // no items without the key
        Assert.DoesNotContain("wd-besitzer@example.org", page);
        var done = Decode(await (await PostAsync(stranger, new() { ["action"] = "confirm", ["name"] = "X", ["email"] = "x@example.org", ["order"] = number })).Content.ReadAsStringAsync());
        Assert.Contains("Ihr Widerruf ist eingegangen", done);                            // the same answer as for anybody
        Assert.DoesNotContain("Wd Fremd", done);
        // Stored, but not hung on the owner's order: the address does not match.
        Assert.Empty(await _shop.WithContextAsync((_, s) => s.WithdrawalsOfOrderAsync(orderId)));
        Assert.Contains(await _shop.WithContextAsync((_, s) => s.WithdrawalsAsync()), w => w.OrderNumber == number && w.OrderId == 0);

        // Required fields: no name / no address / no order is refused before anything is stored.
        var refused = Decode(await (await PostAsync(stranger, new() { ["action"] = "confirm", ["name"] = "", ["email"] = "kaputt", ["order"] = "" })).Content.ReadAsStringAsync());
        Assert.Contains("Bitte geben Sie Ihren Namen an.", refused);
        Assert.Contains("Bitte geben Sie die Bestellung an.", refused);
    }

    [Fact]
    public async Task A_member_picks_from_his_own_orders_only_and_the_list_is_closed_to_guests()
    {
        await _shop.AddProductAsync("Wd Eigen", 2000);
        await _shop.AddProductAsync("Wd Anderer", 2000);
        var email = $"wd-{Guid.NewGuid():N}"[..12] + "@example.org";
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MemberService>().CreateAsync(email, "correct horse", "Mia Mitglied");
        var member = _app.Browser();
        var login = await member.GetStringAsync("/anmelden");
        await member.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email, ["password"] = "correct horse", ["__RequestVerificationToken"] = AppFactory.Token(login)! }));
        var (mine, _, _) = await OrderAsync(member, email, "wd-eigen");
        var (other, _, _) = await OrderAsync(_app.Browser(), "wd-andere@example.org", "wd-anderer");

        var page = Decode(await member.GetStringAsync("/shop/widerrufen"));
        Assert.Contains("data-wd-pick=\"/shop/widerrufen/orders\"", page);
        Assert.Contains("value=\"Mia Mitglied\"", page);
        var list = Decode(await member.GetStringAsync("/shop/widerrufen/orders"));
        Assert.Contains(mine, list);
        Assert.Contains("Wd Eigen", list);
        Assert.DoesNotContain(other, list);
        // His own order without a key: the items are offered; somebody else's number is not.
        Assert.Contains("Wd Eigen", Decode(await member.GetStringAsync($"/shop/widerrufen?order={mine}")));
        Assert.DoesNotContain("Wd Anderer", Decode(await member.GetStringAsync($"/shop/widerrufen?order={other}")));

        var guest = await _app.Browser().GetAsync("/shop/widerrufen/orders");
        Assert.NotEqual(HttpStatusCode.OK, guest.StatusCode);
    }

    [Fact]
    public async Task The_link_is_in_the_footer_and_the_back_office_follows_the_return()
    {
        var footer = Decode(await _app.Browser().GetStringAsync("/"));
        Assert.Contains("href=\"/shop/widerrufen\"", footer);

        await _shop.AddProductAsync("Wd Admin", 3000);
        var c = _app.Browser();
        var (number, token, orderId) = await OrderAsync(c, "wd-admin@example.org", "wd-admin");
        await PostAsync(c, new() { ["action"] = "confirm", ["name"] = "Anton", ["email"] = "wd-admin@example.org", ["order"] = number, ["t"] = token });
        var w = (await _shop.WithContextAsync((_, s) => s.WithdrawalsOfOrderAsync(orderId))).Single();

        var admin = await _shop.AdminAsync();
        Assert.Contains(w.Number, Decode(await admin.GetStringAsync("/admin/p/shop/withdrawals")));
        Assert.Contains("neue(r) Widerruf(e)", Decode(await admin.GetStringAsync("/admin/p/shop/dashboard")));
        var one = await admin.GetStringAsync($"/admin/p/shop/withdrawals/{w.Id}");
        var res = await admin.PostAsync($"/admin/p/shop/withdrawals/{w.Id}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(one)!, ["action"] = "save", ["status"] = ShopWithdrawals.Returned, ["note"] = "Paket angekommen"
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var after = (await _shop.WithContextAsync((_, s) => s.WithdrawalAsync(w.Id)))!;
        Assert.Equal(ShopWithdrawals.Returned, after.Status);
        Assert.Equal("Paket angekommen", after.Note);
        Assert.Contains(await _shop.EventsAsync(orderId), e => e.Kind == "withdrawal" && e.Text.Contains("Ware zurück"));

        // The order page links to it, with the order filled in.
        Assert.Contains("/shop/widerrufen?order=" + number, Decode(await c.GetStringAsync($"/shop/order/{number}?t={Uri.EscapeDataString(token)}")));
    }
}
