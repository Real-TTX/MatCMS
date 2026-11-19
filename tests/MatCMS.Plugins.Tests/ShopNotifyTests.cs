using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// Shop 2.3: the operator hears of a bank-transfer order when it comes IN. Before, the notification went out only when an order was paid — and a transfer
/// is marked paid by the operator herself, so a new order reached her only by looking into the back office (nslibera, 2026-11-19).
/// </summary>
public class ShopNotifyTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopNotifyTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.NotifyEmail] = "betreiberin@example.org" });
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_bank_transfer_order_tells_the_operator_when_it_is_placed_with_the_customers_note()
    {
        await _shop.AddProductAsync("Signiertes Buch", 1799);
        var visitor = _app.Browser();
        await _shop.AddToCartAsync(visitor, "signiertes-buch");
        var res = await _shop.CheckoutAsync(visitor, email: "leserin@example.org");
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(res)))!;
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);

        // No mail server in the test: the attempt is written into the order's history with its recipient — that is what is checked.
        var texts = (await _shop.EventsAsync(order.Id)).Where(e => e.Kind is "mail" or "mail-failed").Select(e => e.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("Shop: Bestellbestätigung") && t.Contains("leserin@example.org"));
        Assert.Contains(texts, t => t.StartsWith("Shop: Bestellung eingegangen, Vorkasse") && t.Contains("betreiberin@example.org"));
        Assert.DoesNotContain(texts, t => t.StartsWith("Shop: Neue Bestellung (an den Betreiber)"));   // the "paid" one waits for the money
    }

    [Fact]
    public void The_new_mail_is_a_template_the_operator_can_edit_and_names_the_note()
    {
        var spec = Assert.Single(ShopMail.Specs, s => s.Key == ShopMail.AdminPlaced);
        Assert.StartsWith("shop.", spec.Key);                                                         // a plugin sends only its own keys
        Assert.Contains("{{note}}", spec.Body);
        Assert.Contains("{{admin_url}}", spec.Body);
        Assert.Contains(spec.Placeholders, p => p.Token == "note");
    }
}
