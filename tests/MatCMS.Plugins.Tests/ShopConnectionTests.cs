using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

public class ShopConnectionUnitTests
{
    [Fact]
    public void An_endpoint_covers_the_events_when_it_lists_all_of_them_or_everything()
    {
        Assert.True(ShopConnectionCheck.Covers(new[] { "charge.refunded", "checkout.session.completed", "checkout.session.expired", "x" }, ShopConnectionCheck.StripeEvents));
        Assert.False(ShopConnectionCheck.Covers(new[] { "checkout.session.completed" }, ShopConnectionCheck.StripeEvents));
        Assert.True(ShopConnectionCheck.Covers(new[] { "*" }, ShopConnectionCheck.StripeEvents));
        Assert.True(ShopConnectionCheck.Covers(new[] { "checkout.SESSION.completed", "checkout.session.expired", "charge.refunded" }, ShopConnectionCheck.StripeEvents));
        Assert.True(ShopConnectionCheck.SameUrl("https://a.de/shop/webhook/stripe/", "HTTPS://A.de/shop/webhook/stripe"));
        Assert.False(ShopConnectionCheck.SameUrl("https://a.de/x", "https://b.de/x"));
    }
}

/// <summary>"Verbindung prüfen" for Stripe, against the stand-in: the page asks, the stand-in answers like Stripe does in each situation.</summary>
[Collection("shop-stripe")]
public class ShopStripeConnectionTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopStripeConnectionTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync()
    {
        _shop = await ShopApp.StartAsync(_app, stripe: true);
        StripeClient.HandlerFactory = () => _shop.Stripe;
        _shop.Stripe.Livemode = false; _shop.Stripe.WebhookEndpoints = new JsonArray(); _shop.Stripe.BalanceError = null; _shop.Stripe.HooksError = null;
    }

    public Task DisposeAsync() { StripeClient.HandlerFactory = null; return Task.CompletedTask; }

    private async Task<string> CheckAsync(string method)
    {
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync("/admin/p/shop/payment");
        var res = await admin.PostAsync("/admin/p/shop/payment", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "check", ["method"] = method }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    // The origin a request to the test application has (a context made outside a request has none).
    private static Task<string> OriginAsync() => Task.FromResult("http://localhost");

    [Fact]
    public async Task A_working_key_without_a_webhook_is_connected_with_a_remark_and_names_the_address_to_enter()
    {
        var html = await CheckAsync("stripe");
        Assert.Contains("Der Anbieter akzeptiert die Zugangsdaten", html); Assert.Contains("Testmodus", html);
        Assert.Contains("kein Webhook mit dieser Adresse", html); Assert.Contains((await OriginAsync()).TrimEnd('/') + "/shop/webhook/stripe", html);
        Assert.Contains("Verbunden, aber mit Hinweisen", html);
        Assert.Contains(_shop.Stripe.Calls, c => c.Path == "/v1/balance");
        Assert.DoesNotContain(_shop.Stripe.Calls, c => c.Method == HttpMethod.Post);                // reads only
    }

    [Fact]
    public async Task A_set_up_webhook_with_all_events_makes_everything_green_and_a_missing_event_is_named()
    {
        var url = (await OriginAsync()).TrimEnd('/') + "/shop/webhook/stripe";
        _shop.Stripe.WebhookEndpoints = new JsonArray(new JsonObject { ["url"] = url, ["status"] = "enabled", ["enabled_events"] = new JsonArray("checkout.session.completed", "checkout.session.expired", "charge.refunded") });
        var good = await CheckAsync("stripe");
        Assert.Contains("Alles in Ordnung", good); Assert.DoesNotContain("kein Webhook mit dieser Adresse", good);

        _shop.Stripe.WebhookEndpoints = new JsonArray(new JsonObject { ["url"] = url, ["status"] = "enabled", ["enabled_events"] = new JsonArray("checkout.session.completed") });
        Assert.Contains("Dem Webhook fehlen Ereignisse", await CheckAsync("stripe"));

        _shop.Stripe.WebhookEndpoints = new JsonArray(new JsonObject { ["url"] = url, ["status"] = "disabled", ["enabled_events"] = new JsonArray("*") });
        Assert.Contains("Der Webhook ist deaktiviert", await CheckAsync("stripe"));
    }

    [Fact]
    public async Task A_key_of_the_other_mode_a_rejected_key_and_a_restricted_key_are_told_apart()
    {
        _shop.Stripe.Livemode = true;                                                              // the test key answers as a live one
        var mismatch = await CheckAsync("stripe");
        Assert.Contains("anderen Modus", mismatch); Assert.Contains("Die Verbindung funktioniert nicht", mismatch);

        _shop.Stripe.Livemode = false;
        _shop.Stripe.BalanceError = (HttpStatusCode.Unauthorized, "Invalid API Key provided: sk_test_****");
        var rejected = await CheckAsync("stripe");
        Assert.Contains("Abgelehnt: Invalid API Key", rejected); Assert.Contains("Die Verbindung funktioniert nicht", rejected);

        _shop.Stripe.BalanceError = (HttpStatusCode.Forbidden, "This API call cannot be made with a restricted key");
        _shop.Stripe.HooksError = (HttpStatusCode.Forbidden, "restricted");
        var restricted = await CheckAsync("stripe");
        Assert.Contains("kein Leserecht", restricted); Assert.Contains("bitte selbst prüfen", restricted); Assert.DoesNotContain("Die Verbindung funktioniert nicht", restricted);
    }

    [Fact]
    public async Task The_payment_page_carries_the_checklist_and_the_webhook_address_even_without_a_check()
    {
        var html = WebUtility.HtmlDecode(await (await _shop.AdminAsync()).GetStringAsync("/admin/p/shop/payment"));
        Assert.Contains("Testlauf-Checkliste", html); Assert.Contains("4242 4242 4242 4242", html); Assert.Contains("Stripe prüfen", html); Assert.Contains("PayPal prüfen", html);
    }
}

[Collection("shop-paypal")]
public class ShopPayPalConnectionTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopPayPalConnectionTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync()
    {
        _shop = await ShopApp.StartAsync(_app, paypal: true);
        PayPalClient.HandlerFactory = () => _shop.PayPal;
        PayPalClient.ForgetTokens();
        _shop.PayPal.Webhooks = new JsonArray(); _shop.PayPal.HooksStatus = HttpStatusCode.OK;
    }

    public Task DisposeAsync() { PayPalClient.HandlerFactory = null; return Task.CompletedTask; }

    private async Task<string> CheckAsync()
    {
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync("/admin/p/shop/payment");
        var res = await admin.PostAsync("/admin/p/shop/payment", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "check", ["method"] = "paypal" }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_webhook_id_must_exist_in_the_paypal_app_point_at_this_shop_and_listen_to_the_needed_events()
    {
        var origin = "http://localhost";
        var unknown = await CheckAsync();
        Assert.Contains("Der Anbieter akzeptiert die Zugangsdaten", unknown); Assert.Contains("gibt es in dieser PayPal-App nicht", unknown);

        JsonArray Events(params string[] names) => new(names.Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray());
        _shop.PayPal.Webhooks = new JsonArray(new JsonObject { ["id"] = FakePayPal.WebhookId, ["url"] = "https://anderer-shop.example/shop/webhook/paypal", ["event_types"] = Events(ShopConnectionCheck.PayPalEvents) });
        Assert.Contains("zeigt auf https://anderer-shop.example", await CheckAsync());

        _shop.PayPal.Webhooks = new JsonArray(new JsonObject { ["id"] = FakePayPal.WebhookId, ["url"] = origin + "/shop/webhook/paypal", ["event_types"] = Events("PAYMENT.CAPTURE.COMPLETED") });
        Assert.Contains("Dem Webhook fehlen Ereignisse", await CheckAsync());

        _shop.PayPal.Webhooks = new JsonArray(new JsonObject { ["id"] = FakePayPal.WebhookId, ["url"] = origin + "/shop/webhook/paypal", ["event_types"] = Events(ShopConnectionCheck.PayPalEvents) });
        var good = await CheckAsync();
        Assert.Contains("Alles in Ordnung", good); Assert.Contains("Sandbox", good);
    }

    [Fact]
    public async Task A_forbidden_listing_is_a_remark_not_an_error()
    {
        _shop.PayPal.HooksStatus = HttpStatusCode.Forbidden;
        var html = await CheckAsync();
        Assert.Contains("kein Leserecht", html); Assert.DoesNotContain("Die Verbindung funktioniert nicht", html);
    }
}
