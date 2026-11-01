using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The plugins' API (<c>/api/v1/{plugin}/…</c>, site API keys) through the shop's ERP endpoints: keys and rights, products by article number (upsert,
/// partial changes, variants), stock and prices in batches, categories, orders behind their own right, and the admin page that makes keys.
/// </summary>
public class ShopApiTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopApiTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> KeyAsync(params string[] rights)
    {
        using var scope = _app.Services.CreateScope();
        var (_, key) = await scope.ServiceProvider.GetRequiredService<SiteApiKeyService>().CreateAsync("ERP Test", rights, null);
        return key;
    }

    private HttpClient Client(string? key)
    {
        var c = _app.Browser();
        if (key is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    private static async Task<JsonNode> JsonOf(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Without_a_key_or_with_the_wrong_rights_nothing_happens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/api/v1/shop/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("msk_gibtesnicht").GetAsync("/api/v1/shop/products")).StatusCode);

        var read = Client(await KeyAsync("shop:read"));
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/shop/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PutAsync("/api/v1/shop/products/R-1", Body("""{"title":"x","price_cents":100}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/v1/shop/orders")).StatusCode);        // orders are a right of their own
        Assert.Equal(HttpStatusCode.NotFound, (await read.GetAsync("/api/v1/shop/gibtsnicht")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await read.PostAsync("/api/v1/shop/products", Body("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await read.GetAsync("/api/v1/nichtda/x")).StatusCode);

        // A revoked key stops at once.
        var key = await KeyAsync("shop:write");
        using (var scope = _app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<SiteApiKeyService>();
            var row = (await svc.ListAsync()).First(k => k.Hash == SiteApiKeyService.Hash(key));
            await svc.RevokeAsync(row.Id);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(key).GetAsync("/api/v1/shop/products")).StatusCode);

        // The index says what a key may call.
        var index = await JsonOf(await read.GetAsync("/api/v1"));
        Assert.Equal("ERP Test", index["key"]!["name"]!.ToString());
        Assert.Contains(index["routes"]!.AsArray(), r => r!["path"]!.ToString() == "/api/v1/shop/products/{sku}" && r["method"]!.ToString() == "PUT" && !(bool)r["allowed"]!);
    }

    [Fact]
    public async Task An_erp_creates_and_changes_products_by_article_number_with_variants()
    {
        var erp = Client(await KeyAsync("shop:write"));
        var made = await erp.PutAsync("/api/v1/shop/products/ESP-1000", Body("""
            { "title": "Espresso Bohne 1 kg", "price_cents": 2490, "tax_percent": 7, "stock": 120, "gtin": "4012345678901", "summary": "Kräftig.",
              "variants": [ { "sku": "ESP-1000-M", "name": "Gemahlen", "price_cents": 2590, "stock": 40 } ] }
            """));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var p = await JsonOf(made);
        Assert.Equal("espresso-bohne-1-kg", p["slug"]!.ToString());
        Assert.Equal(2490, (long)p["price_cents"]!);
        Assert.Equal(120, (long)p["stock"]!);
        Assert.Equal(7m, (decimal)p["tax_percent"]!);
        Assert.Equal("4012345678901", p["gtin"]!.ToString());
        var v = p["variants"]!.AsArray().Single()!;
        Assert.Equal(100, (long)v["price_delta_cents"]!);           // the variant's own price, kept as the shop keeps it

        // A partial change: only what is sent changes, the editor's texts stay.
        var changed = await JsonOf(await erp.PutAsync("/api/v1/shop/products/ESP-1000", Body("""{ "price": "26,90", "stock": null }""")));
        Assert.Equal(2690, (long)changed["price_cents"]!);
        Assert.Null(changed["stock"]);                                // no longer counted
        Assert.Equal("Kräftig.", changed["summary"]!.ToString());
        Assert.Equal("Espresso Bohne 1 kg", changed["title"]!.ToString());

        // Found by the variant's number too; the shop's page shows it.
        var byVariant = await JsonOf(await erp.GetAsync("/api/v1/shop/products/ESP-1000-M"));
        Assert.Equal("ESP-1000", byVariant["sku"]!.ToString());
        Assert.Contains("Espresso Bohne 1 kg", WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/shop/espresso-bohne-1-kg")));

        // A variant number is not a product of its own; a new product needs a title and a price.
        Assert.Equal(HttpStatusCode.Conflict, (await erp.PutAsync("/api/v1/shop/products/ESP-1000-M", Body("""{"title":"x","price_cents":1}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await erp.PutAsync("/api/v1/shop/products/NEU-1", Body("""{"price_cents":100}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await erp.PutAsync("/api/v1/shop/products/NEU-1", Body("kein json"))).StatusCode);

        // Switching off, never deleting.
        Assert.Equal(HttpStatusCode.OK, (await erp.DeleteAsync("/api/v1/shop/products/ESP-1000")).StatusCode);
        Assert.False((bool)(await JsonOf(await erp.GetAsync("/api/v1/shop/products/ESP-1000")))["active"]!);
    }

    [Fact]
    public async Task Stock_and_prices_go_in_batches_and_every_line_says_how_it_went()
    {
        var erp = Client(await KeyAsync("shop:write"));
        await erp.PutAsync("/api/v1/shop/products/LAG-1", Body("""{ "title": "Lagerartikel", "price_cents": 1000, "stock": 10, "variants": [ { "sku": "LAG-1-B", "name": "Blau", "stock": 5 } ] }"""));
        await erp.PutAsync("/api/v1/shop/products/LAG-2", Body("""{ "title": "Ohne Zählung", "price_cents": 500 }"""));

        var r = await JsonOf(await erp.PostAsync("/api/v1/shop/stock", Body("""
            { "items": [ { "sku": "LAG-1", "stock": 50 }, { "sku": "LAG-1-B", "add": -2 }, { "sku": "LAG-1-B", "add": -100 },
                         { "sku": "LAG-2", "add": 1 }, { "sku": "GIBTSNICHT", "stock": 1 } ] }
            """)));
        Assert.Equal(2, (int)r["ok"]!); Assert.Equal(3, (int)r["failed"]!);
        var items = r["items"]!.AsArray();
        Assert.Equal(50, (long)items[0]!["stock"]!);
        Assert.Equal(3, (long)items[1]!["stock"]!);
        Assert.Contains("unter 0", items[2]!["error"]!.ToString());
        Assert.Contains("nicht gezählt", items[3]!["error"]!.ToString());
        Assert.Contains("unbekannte", items[4]!["error"]!.ToString());

        var pr = await JsonOf(await erp.PostAsync("/api/v1/shop/prices", Body("""{ "items": [ { "sku": "LAG-1", "price_cents": 1200, "compare_cents": 1500 }, { "sku": "LAG-1-B", "price": "13,00" } ] }""")));
        Assert.Equal(2, (int)pr["ok"]!);
        var p = await JsonOf(await erp.GetAsync("/api/v1/shop/products/LAG-1"));
        Assert.Equal(1200, (long)p["price_cents"]!); Assert.Equal(1500, (long)p["compare_cents"]!);
        Assert.Equal(1300, (long)p["variants"]![0]!["price_cents"]!);

        // A page of products changed since a moment; categories by address.
        var since = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var page = await JsonOf(await erp.GetAsync($"/api/v1/shop/products?updated_since={since}&limit=500"));
        Assert.Contains(page["items"]!.AsArray(), x => x!["sku"]!.ToString() == "LAG-1");
        Assert.Equal(HttpStatusCode.Created, (await erp.PutAsync("/api/v1/shop/categories/lager", Body("""{ "name": "Lager" }"""))).StatusCode);
        Assert.Equal("lager", (await JsonOf(await erp.PutAsync("/api/v1/shop/products/LAG-1", Body("""{ "category": "Lager" }"""))))["category"]!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await erp.PutAsync("/api/v1/shop/products/LAG-1", Body("""{ "category": "gibtsnicht" }"""))).StatusCode);
    }

    [Fact]
    public async Task Orders_need_the_personal_data_right_and_can_be_reported_as_shipped()
    {
        await _shop.AddProductAsync("Versandding", 1990);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "versandding");
        await _shop.CheckoutAsync(c, email: "erp-kunde@example.org");

        var erp = Client(await KeyAsync("shop:read", "shop:personal"));
        var list = await JsonOf(await erp.GetAsync("/api/v1/shop/orders?limit=5"));
        var order = list["orders"]!.AsArray().First(o => o!.ToJsonString().Contains("erp-kunde@example.org"))!;
        var number = order["number"]!.ToString();
        Assert.Equal(number, (await JsonOf(await erp.GetAsync($"/api/v1/shop/orders/{Uri.EscapeDataString(number)}")))["number"]!.ToString());

        // Bank transfer: paid first, then shipped — the moves the back office allows, in the same order.
        Assert.Equal(HttpStatusCode.Conflict, (await erp.PostAsync($"/api/v1/shop/orders/{number}/ship", Body("""{ "tracking": "123", "carrier": "dhl", "notify": false }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await erp.PostAsync($"/api/v1/shop/orders/{number}/status", Body("""{ "action": "paid" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await erp.PostAsync($"/api/v1/shop/orders/{number}/ship", Body("""{ "tracking": "00340434161234567890", "carrier": "dhl", "notify": false }"""))).StatusCode);
        var shipped = await _shop.OrderAsync(number);
        Assert.Equal("shipped", shipped!.Status);
        Assert.Contains(await _shop.EventsAsync(shipped.Id), e => e.Actor == "api:ERP Test");
        Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync("/api/v1/shop/orders/GIBTSNICHT")).StatusCode);
    }

    [Fact]
    public async Task The_admin_makes_a_key_and_sees_it_once()
    {
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync("/admin/apikeys/create");
        Assert.Contains("value=\"shop:write\"", page);
        var token = AppFactory.Token(page)!;
        var made = await admin.PostAsync("/admin/apikeys/create", new FormUrlEncodedContent(new[]
        {
            KeyValuePair.Create("name", "Warenwirtschaft"), KeyValuePair.Create("rights", "shop:write"), KeyValuePair.Create("rights", "shop:bogus"),
            KeyValuePair.Create("__RequestVerificationToken", token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, made.StatusCode);
        var list = await admin.GetStringAsync("/admin/apikeys");
        var key = System.Text.RegularExpressions.Regex.Match(list, "value=\"(msk_[A-Za-z0-9]+)\"").Groups[1].Value;
        Assert.StartsWith("msk_", key);
        Assert.Contains("/api/v1/shop/products/{sku}", list);
        Assert.DoesNotContain(key, await admin.GetStringAsync("/admin/apikeys"));      // shown once
        Assert.Equal(HttpStatusCode.OK, (await Client(key).GetAsync("/api/v1/shop/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(key).GetAsync("/api/v1/shop/orders")).StatusCode);
    }
}
