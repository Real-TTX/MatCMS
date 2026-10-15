using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Stripe, replaced by a hand-written stand-in: it answers like Stripe does, records every call, and never touches a network.</summary>
internal sealed class FakeStripe : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string Path, string Query, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public Dictionary<string, string> Form => Body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "");
    }

    public List<Call> Calls { get; } = new();
    private int _sessions, _coupons;
    /// <summary>What GET /v1/checkout/sessions/{id} answers, by id (a test sets the state it wants to see).</summary>
    public Dictionary<string, JsonObject> Sessions { get; } = new();
    /// <summary>The refunded total the payment intent's charge reports.</summary>
    public long AmountRefunded { get; set; }
    /// <summary>When set, the next session creation fails with this Stripe-style error.</summary>
    public string? FailNextSessionWith { get; set; }
    public string? FailRefundWith { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls) Calls.Add(new Call(request.Method, path, request.RequestUri.Query, body, headers));

        HttpResponseMessage Json(HttpStatusCode code, JsonNode node) => new(code) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        HttpResponseMessage Error(HttpStatusCode code, string message) => Json(code, new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "invalid_request_error", ["code"] = "test" } });

        if (request.Method == HttpMethod.Post && path == "/v1/checkout/sessions")
        {
            if (FailNextSessionWith is { } fail) { FailNextSessionWith = null; return Error(HttpStatusCode.BadRequest, fail); }
            var id = "cs_test_" + Interlocked.Increment(ref _sessions);
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id, ["object"] = "checkout.session", ["url"] = $"https://checkout.stripe.com/c/pay/{id}", ["payment_intent"] = null, ["status"] = "open" });
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/checkout/sessions/", StringComparison.Ordinal))
        {
            var id = path["/v1/checkout/sessions/".Length..];
            return Sessions.TryGetValue(id, out var s) ? Json(HttpStatusCode.OK, s.DeepClone()) : Error(HttpStatusCode.NotFound, "No such checkout.session: " + id);
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/expire", StringComparison.Ordinal))
        {
            var id = path["/v1/checkout/sessions/".Length..^"/expire".Length];
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id, ["status"] = "expired" });
        }
        if (request.Method == HttpMethod.Post && path == "/v1/coupons")
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = "co_test_" + Interlocked.Increment(ref _coupons), ["object"] = "coupon" });
        if (request.Method == HttpMethod.Post && path == "/v1/refunds")
        {
            if (FailRefundWith is { } fail) return Error(HttpStatusCode.BadRequest, fail);
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = "re_test_1", ["status"] = "succeeded" });
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/payment_intents/", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = path["/v1/payment_intents/".Length..], ["latest_charge"] = new JsonObject { ["amount_refunded"] = AmountRefunded } });
        return Error(HttpStatusCode.NotFound, "Unexpected request " + request.Method + " " + path);
    }
}

/// <summary>PayPal, replaced by a hand-written stand-in that keeps the state of its orders (created → approved → captured) and records every call.</summary>
internal sealed class FakePayPal : HttpMessageHandler
{
    public const string ClientId = "AbCdEfGhIjKlMn-test-client-id";
    public const string Secret = "EOxyzSecret-test-1234567890";
    public const string WebhookId = "WH-TEST-WEBHOOK-1";

    public sealed record Call(HttpMethod Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers)
    {
        public JsonObject? Json => string.IsNullOrWhiteSpace(Body) ? null : JsonNode.Parse(Body) as JsonObject;
    }

    public sealed class Order
    {
        public string Id = "";
        public string Status = "PAYER_ACTION_REQUIRED";
        public string Currency = "EUR";
        public string Value = "0.00";
        public string CustomId = "";
        public JsonObject? Capture;
    }

    public List<Call> Calls { get; } = new();
    public Dictionary<string, Order> Orders { get; } = new();
    private int _orders, _captures, _refunds, _tokens;
    public int TokenCalls => _tokens;
    public string? FailNextCreateWith { get; set; }
    public string? FailCaptureWith { get; set; }
    public string? FailRefundWith { get; set; }
    public string VerifyStatus { get; set; } = "SUCCESS";
    /// <summary>When set, a capture is for this amount instead of the order's (a forged or wrong payment).</summary>
    public string? CaptureValueOverride { get; set; }
    public string CaptureStatus { get; set; } = "COMPLETED";
    public string LastRefundId { get; private set; } = "";

    public Order Approve(string id) { Orders[id].Status = "APPROVED"; return Orders[id]; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls) Calls.Add(new Call(request.Method, path, body, headers));

        HttpResponseMessage Json(HttpStatusCode code, JsonNode node) => new(code) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        HttpResponseMessage Error(HttpStatusCode code, string name, string message, string issue = "") =>
            Json(code, new JsonObject { ["name"] = name, ["message"] = message, ["details"] = new JsonArray(new JsonObject { ["issue"] = issue, ["description"] = message }) });

        if (path == "/v1/oauth2/token")
        {
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + Secret));
            if (headers.GetValueOrDefault("Authorization") != "Basic " + expected) return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_client", ["error_description"] = "Client Authentication failed" });
            return Json(HttpStatusCode.OK, new JsonObject { ["access_token"] = "A21-token-" + Interlocked.Increment(ref _tokens), ["expires_in"] = 32400, ["token_type"] = "Bearer" });
        }
        if (!(headers.GetValueOrDefault("Authorization") ?? "").StartsWith("Bearer A21-token-", StringComparison.Ordinal)) return Json(HttpStatusCode.Unauthorized, new JsonObject { ["name"] = "AUTHENTICATION_FAILURE", ["message"] = "Authentication failed" });

        if (request.Method == HttpMethod.Post && path == "/v2/checkout/orders")
        {
            if (FailNextCreateWith is { } fail) { FailNextCreateWith = null; return Error(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE_ENTITY", fail, "ISSUE"); }
            var unit = (JsonNode.Parse(body)!["purchase_units"] as JsonArray)![0]!;
            var id = "PP-ORDER-" + Interlocked.Increment(ref _orders);
            Orders[id] = new Order { Id = id, Currency = unit["amount"]!["currency_code"]!.GetValue<string>(), Value = unit["amount"]!["value"]!.GetValue<string>(), CustomId = unit["custom_id"]!.GetValue<string>() };
            return Json(HttpStatusCode.Created, new JsonObject
            {
                ["id"] = id, ["status"] = "PAYER_ACTION_REQUIRED",
                ["links"] = new JsonArray(new JsonObject { ["href"] = "https://www.sandbox.paypal.com/checkoutnow?token=" + id, ["rel"] = "payer-action", ["method"] = "GET" })
            });
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v2/checkout/orders/", StringComparison.Ordinal))
            return Orders.TryGetValue(path["/v2/checkout/orders/".Length..], out var o) ? Json(HttpStatusCode.OK, Describe(o)) : Error(HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND", "The specified resource does not exist.");
        if (request.Method == HttpMethod.Post && path.EndsWith("/capture", StringComparison.Ordinal) && path.StartsWith("/v2/checkout/orders/", StringComparison.Ordinal))
        {
            var id = path["/v2/checkout/orders/".Length..^"/capture".Length];
            if (!Orders.TryGetValue(id, out var order)) return Error(HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND", "The specified resource does not exist.");
            if (FailCaptureWith is { } fail) return Error(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE_ENTITY", fail, "INSTRUMENT_DECLINED");
            if (order.Status == "COMPLETED") return Json(HttpStatusCode.OK, Describe(order));
            if (order.Status != "APPROVED") return Error(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE_ENTITY", "Order not approved.", "ORDER_NOT_APPROVED");
            order.Capture = new JsonObject
            {
                ["id"] = "CAP-" + Interlocked.Increment(ref _captures), ["status"] = CaptureStatus,
                ["amount"] = new JsonObject { ["currency_code"] = order.Currency, ["value"] = CaptureValueOverride ?? order.Value }, ["custom_id"] = order.CustomId
            };
            order.Status = "COMPLETED";
            return Json(HttpStatusCode.Created, Describe(order));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/refund", StringComparison.Ordinal) && path.StartsWith("/v2/payments/captures/", StringComparison.Ordinal))
        {
            if (FailRefundWith is { } fail) return Error(HttpStatusCode.UnprocessableEntity, "UNPROCESSABLE_ENTITY", fail, "CAPTURE_FULLY_REFUNDED");
            {
                LastRefundId = "RF-" + Interlocked.Increment(ref _refunds);
                return Json(HttpStatusCode.Created, new JsonObject { ["id"] = LastRefundId, ["status"] = "COMPLETED" });
            }
        }
        if (request.Method == HttpMethod.Post && path == "/v1/notifications/verify-webhook-signature")
            return Json(HttpStatusCode.OK, new JsonObject { ["verification_status"] = VerifyStatus });
        return Error(HttpStatusCode.NotFound, "NOT_FOUND", "Unexpected request " + request.Method + " " + path);
    }

    public static JsonObject Describe(Order o)
    {
        var unit = new JsonObject { ["custom_id"] = o.CustomId, ["amount"] = new JsonObject { ["currency_code"] = o.Currency, ["value"] = o.Value } };
        if (o.Capture is not null) unit["payments"] = new JsonObject { ["captures"] = new JsonArray(o.Capture.DeepClone()) };
        return new JsonObject { ["id"] = o.Id, ["intent"] = "CAPTURE", ["status"] = o.Status, ["purchase_units"] = new JsonArray(unit) };
    }
}

/// <summary>An application with the shop switched on, plus the helpers the shop tests share (products, settings, a browser that fills a cart and checks out).</summary>
internal sealed class ShopApp
{
    public const string StripeKey = "sk_test_51ABCDEFGHIJKLMNOP";
    public const string WebhookSecret = "whsec_test_signing_secret";

    public AppFactory App { get; }
    public FakeStripe Stripe { get; } = new();
    public FakePayPal PayPal { get; } = new();
    private ShopApp(AppFactory app) => App = app;

    /// <summary>Switches the shop on in the running application. <paramref name="settings"/> are saved as an operator would (secrets encrypted).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<AppFactory, ShopApp> Started = new();
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    /// <summary>
    /// Switches the shop on once per application. A test class gets a new instance for every test, but its fixture — the application — lives for the whole class, so the
    /// first test sets the shop up and the others find it as it is. (The administrator also signs in once: the sign-in is rate limited, as it should be.)
    /// </summary>
    public static async Task<ShopApp> StartAsync(AppFactory app, IDictionary<string, string>? settings = null, bool stripe = false, bool paypal = false)
    {
        await StartGate.WaitAsync();
        try
        {
            if (Started.TryGetValue(app, out var existing)) return existing;
            var created = await StartCoreAsync(app, settings, stripe, paypal);
            Started.Add(app, created);
            return created;
        }
        finally { StartGate.Release(); }
    }

    private static async Task<ShopApp> StartCoreAsync(AppFactory app, IDictionary<string, string>? settings, bool stripe, bool paypal)
    {
        var shop = new ShopApp(app);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Plugins.FirstAsync(p => p.Key == "shop");
            var cfg = new JsonObject
            {
                [Keys.BankEnabled] = "true", [Keys.SellerName] = "Test Shop GmbH", [Keys.SellerAddress] = "Teststraße 1\n12345 Teststadt",
                [Keys.BankDetails] = "IBAN DE00 0000 0000 0000 0000 00", [Keys.TermsUrl] = "/agb", [Keys.WithdrawalUrl] = "/widerruf", [Keys.PrivacyUrl] = "/datenschutz"
            };
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(PluginSettingsAccess.SecretPurpose);
            string Secret(string v) => PluginSettingsAccess.SecretPrefix + protector.Protect(v);
            if (stripe)
            {
                cfg[Keys.StripeEnabled] = "true"; cfg[Keys.StripeSecret] = Secret(StripeKey); cfg[Keys.StripeWebhookSecret] = Secret(WebhookSecret);
            }
            if (paypal)
            {
                cfg[Keys.PayPalEnabled] = "true"; cfg[Keys.PayPalClientId] = FakePayPal.ClientId; cfg[Keys.PayPalSecret] = Secret(FakePayPal.Secret);
                cfg[Keys.PayPalWebhookId] = FakePayPal.WebhookId; cfg[Keys.PayPalSandbox] = "true";
            }
            if (settings is not null) foreach (var kv in settings) cfg[kv.Key] = kv.Value;
            row.ConfigJson = cfg.ToJsonString();
            row.Enabled = true;
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(row.Id);
            var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
            Assert.True(registry.IsLoaded("shop"), "the shop did not load: " + string.Join(" | ", registry.Errors.Values));
        }
        return shop;
    }

    /// <summary>Runs <paramref name="work"/> with a plugin context of the shop (its own scope, its tables): for arranging and inspecting data, not for the flows under test.</summary>
    public async Task<T> WithContextAsync<T>(Func<IPluginContext, ShopStore, Task<T>> work)
    {
        using var scope = App.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        var ctx = PluginRequestContext.ForBackground(new PluginInfo("shop", "Shop", "1.0", "", 2), scope.ServiceProvider, registry);
        return await work(ctx, new ShopStore(ctx.Data));
    }

    public Task WithContextAsync(Func<IPluginContext, ShopStore, Task> work) => WithContextAsync<bool>(async (c, s) => { await work(c, s); return true; });

    public Task<long> AddProductAsync(string title, long priceCents, string kind = ProductKind.Physical, bool trackStock = false, long stock = 0, int taxBp = 1900,
        int weight = 0, string category = "", params (string Name, long Delta, long? Stock)[] variants) =>
        WithContextAsync(async (ctx, store) =>
        {
            var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title);
            var id = await store.SaveProductAsync(new Product { Slug = slug, Title = title, PriceCents = priceCents, Kind = kind, TrackStock = trackStock, Stock = stock, TaxBp = taxBp, WeightGrams = weight, Category = category });
            foreach (var (name, delta, vstock) in variants)
                await store.SaveVariantAsync(new Variant { ProductId = id, Name = name, PriceDeltaCents = delta, TrackStock = vstock is not null, Stock = vstock ?? 0 });
            return id;
        });

    /// <summary>Makes a coupon the way the form would; returns it as stored.</summary>
    public Task<Coupon> AddCouponAsync(string code, string kind = CouponKind.Percent, long value = 1000, Func<Coupon, Coupon>? change = null) =>
        WithContextAsync(async (_, store) =>
        {
            var c = new Coupon { Code = code, Kind = kind, Value = value };
            if (change is not null) c = change(c);
            var (ok, _) = await store.SaveCouponAsync(c);
            Assert.True(ok, "the coupon code was taken: " + code);
            return (await store.CouponByCodeAsync(code))!;
        });

    public Task<Coupon> CouponAsync(string code) => WithContextAsync(async (_, store) => (await store.CouponByCodeAsync(code))!);

    /// <summary>A shipping method for the given countries (the shop starts with one for Germany only).</summary>
    public Task<long> AddRateAsync(string name, string countries, long priceCents, long freeFromCents = 0) =>
        WithContextAsync((_, store) => store.SaveRateAsync(new ShippingRate { Name = name, Countries = countries, PriceCents = priceCents, FreeFromCents = freeFromCents }));

    /// <summary>Types a coupon code into the cart page. Returns the page that comes back (decoded).</summary>
    public async Task<string> ApplyCouponAsync(HttpClient c, string code)
    {
        var page = await c.GetStringAsync("/shop/cart");
        var res = await c.PostAsync("/shop/cart", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "coupon", ["code"] = code }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    }

    public Task<Product> ProductAsync(long id) => WithContextAsync(async (_, store) => (await store.ProductAsync(id))!);
    public Task<IReadOnlyList<OrderItem>> ItemsAsync(long orderId) => WithContextAsync((_, store) => store.ItemsAsync(orderId));
    public Task<Order?> OrderAsync(string number) => WithContextAsync((_, store) => store.OrderByNumberAsync(number));
    public Task<IReadOnlyList<ShopStore.OrderEvent>> EventsAsync(long orderId) => WithContextAsync((_, store) => store.EventsAsync(orderId));

    private HttpClient? _admin;
    public async Task<HttpClient> AdminAsync()
    {
        if (_admin is not null) return _admin;
        var c = App.Browser();
        await AppFactory.LoginAsync(c);
        // The back office is English until its user chooses otherwise; the texts the tests look for are German.
        await c.PostAsync("/set-language?culture=de", new StringContent(""));
        return _admin = c;
    }

    // ---- what a visitor does ---------------------------------------------------------------------------------

    public static async Task<string> GetAsync(HttpClient c, string url) => await c.GetStringAsync(url);

    /// <summary>Puts a product in the cart through the real product page (token and all).</summary>
    public async Task AddToCartAsync(HttpClient c, string slug, int qty = 1, long variantId = 0)
    {
        var page = await c.GetStringAsync("/shop/" + slug);
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "add", ["product"] = ProductIdOf(page), ["qty"] = qty.ToString() };
        if (variantId > 0) form["variant"] = variantId.ToString();
        var res = await c.PostAsync("/shop/cart", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private static string ProductIdOf(string html) => System.Text.RegularExpressions.Regex.Match(html, "name=\"product\" value=\"(\\d+)\"").Groups[1].Value;

    public static Dictionary<string, string> CheckoutForm(string token, string email = "kunde@example.org", string pay = "bank", string country = "DE", bool terms = true, bool waiver = false,
        IDictionary<string, string>? extra = null)
    {
        var f = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["action"] = "place", ["email"] = email, ["phone"] = "", ["b_name"] = "Erika Muster", ["b_line1"] = "Musterweg 1", ["b_line2"] = "",
            ["b_zip"] = "12345", ["b_city"] = "Musterstadt", ["b_country"] = country, ["pay"] = pay, ["note"] = ""
        };
        if (terms) f["terms"] = "1";
        if (waiver) f["waiver"] = "1";
        if (extra is not null) foreach (var kv in extra) f[kv.Key] = kv.Value;
        return f;
    }

    /// <summary>Opens the checkout, fills it in and posts it. Returns the answer (a redirect when the order was placed).</summary>
    public async Task<HttpResponseMessage> CheckoutAsync(HttpClient c, string email = "kunde@example.org", string pay = "bank", string country = "DE", bool terms = true, bool waiver = false,
        IDictionary<string, string>? extra = null)
    {
        var open = await c.GetAsync("/shop/checkout");
        // An empty or corrected cart sends the buyer back to it instead of showing the form.
        if (open.StatusCode == HttpStatusCode.Redirect) return open;
        var page = await open.Content.ReadAsStringAsync();
        return await c.PostAsync("/shop/checkout", new FormUrlEncodedContent(CheckoutForm(AppFactory.Token(page)!, email, pay, country, terms, waiver, extra)));
    }

    /// <summary>The order number of a "placed" redirect: <c>/shop/order/1001?t=…&amp;new=1</c>.</summary>
    public static string NumberOf(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!.OriginalString;
        var m = System.Text.RegularExpressions.Regex.Match(loc, @"^/shop/order/([^?]+)\?t=([^&]+)");
        Assert.True(m.Success, "not an order redirect: " + loc);
        return Uri.UnescapeDataString(m.Groups[1].Value);
    }

    public static string TokenOf(HttpResponseMessage res) => Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(res.Headers.Location!.OriginalString, @"t=([^&]+)").Groups[1].Value);

    // ---- what the shop owner does ----------------------------------------------------------------------------

    /// <summary>Posts an action of the order page in the back office.</summary>
    public async Task<HttpResponseMessage> AdminActionAsync(HttpClient admin, long orderId, string action, IDictionary<string, string>? extra = null)
    {
        var page = await admin.GetStringAsync($"/admin/p/shop/orders/{orderId}");
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = action };
        if (extra is not null) foreach (var kv in extra) form[kv.Key] = kv.Value;
        return await admin.PostAsync($"/admin/p/shop/orders/{orderId}", new FormUrlEncodedContent(form));
    }

    /// <summary>A PayPal notification as PayPal sends it (the fake PayPal answers the signature check).</summary>
    public async Task<HttpResponseMessage> PayPalWebhookAsync(HttpClient c, string eventType, JsonObject resource, string? eventId = null)
    {
        var evt = new JsonObject { ["id"] = eventId ?? "WH-EVT-" + Guid.NewGuid().ToString("N")[..12], ["event_type"] = eventType, ["resource"] = resource.DeepClone() };
        var req = new HttpRequestMessage(HttpMethod.Post, "/shop/webhook/paypal") { Content = new StringContent(evt.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("PAYPAL-AUTH-ALGO", "SHA256withRSA"); req.Headers.Add("PAYPAL-CERT-URL", "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-360caa42-fca2a594-1d93a270");
        req.Headers.Add("PAYPAL-TRANSMISSION-ID", Guid.NewGuid().ToString()); req.Headers.Add("PAYPAL-TRANSMISSION-SIG", "c2lnbmF0dXJl"); req.Headers.Add("PAYPAL-TRANSMISSION-TIME", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        return await c.SendAsync(req);
    }

    /// <summary>A signed Stripe notification, posted to the webhook.</summary>
    public async Task<HttpResponseMessage> WebhookAsync(HttpClient c, string type, JsonObject obj, string? eventId = null, string? secret = null, DateTimeOffset? at = null)
    {
        var evt = new JsonObject { ["id"] = eventId ?? "evt_" + Guid.NewGuid().ToString("N")[..12], ["type"] = type, ["data"] = new JsonObject { ["object"] = obj.DeepClone() } };
        var body = Encoding.UTF8.GetBytes(evt.ToJsonString());
        var req = new HttpRequestMessage(HttpMethod.Post, "/shop/webhook/stripe") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("Stripe-Signature", StripeClient.Sign(body, secret ?? WebhookSecret, at ?? DateTimeOffset.UtcNow));
        return await c.SendAsync(req);
    }

    /// <summary>The session object Stripe sends for a paid Checkout.</summary>
    public static JsonObject PaidSession(string id, Order order, long? amount = null, string status = "paid", string intent = "pi_test_1", string? currency = null) => new()
    {
        ["id"] = id, ["object"] = "checkout.session", ["payment_status"] = status, ["status"] = "complete", ["amount_total"] = amount ?? order.TotalCents,
        ["currency"] = currency ?? order.Currency.ToLowerInvariant(), ["payment_intent"] = intent, ["client_reference_id"] = order.Number
    };
}
