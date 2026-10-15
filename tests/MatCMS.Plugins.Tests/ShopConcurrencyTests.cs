using System.Net;
using MatCMS.Plugin.Shop;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Many buyers at once. A shop that sells the last piece twice, gives two orders one number or loses an order to "database is locked" is not a shop.</summary>
public class ShopConcurrencyTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopConcurrencyTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Twelve_buyers_at_once_for_five_pieces_get_exactly_five_orders_with_distinct_numbers_and_no_error()
    {
        var pid = await _shop.AddProductAsync("Rennen Stück", 1000, trackStock: true, stock: 5);
        var buyers = new List<HttpClient>();
        for (var i = 0; i < 12; i++)
        {
            var c = _app.Browser();
            await _shop.AddToCartAsync(c, "rennen-stueck");
            buyers.Add(c);
        }
        MatCMS.Services.Plugins.PluginPostLimiter.Reset();

        var results = await Task.WhenAll(buyers.Select((c, i) => Task.Run(() => _shop.CheckoutAsync(c, email: $"kaeufer{i}@example.org"))));

        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);                     // no "database is locked", no crash
        var placed = results.Where(r => r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.StartsWith("/shop/order/")).ToList();
        Assert.Equal(5, placed.Count);
        // Everybody else was told (back to the cart, or the checkout with the reason) — not left with a half-made order.
        Assert.All(results.Except(placed), r => Assert.True(r.StatusCode == HttpStatusCode.Redirect || r.StatusCode == (HttpStatusCode)422));
        var numbers = placed.Select(ShopApp.NumberOf).Select(long.Parse).OrderBy(n => n).ToList();
        Assert.Equal(5, numbers.Distinct().Count());
        Assert.Equal(numbers[0] + 4, numbers[^1]);                                          // no gap: five numbers in a row
        Assert.Equal(0, (await _shop.ProductAsync(pid)).Stock);                             // never negative

        var orders = await _shop.WithContextAsync((_, s) => s.OrdersAsync(OrderStatus.AwaitingPayment, "kaeufer"));
        var items = 0;
        foreach (var o in orders.Items) items += (await _shop.WithContextAsync((_, s) => s.ItemsAsync(o.Id))).Sum(i => i.Qty);
        Assert.Equal(5, items);                                                              // exactly the stock was sold
    }

    [Fact]
    public async Task Simultaneous_clicks_on_mark_paid_pay_the_order_once()
    {
        await _shop.AddProductAsync("Doppelklick Buch", 1000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "doppelklick-buch");
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(c))))!;
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync($"/admin/p/shop/orders/{order.Id}");
        var token = AppFactory.Token(page)!;
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            admin.PostAsync($"/admin/p/shop/orders/{order.Id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = "paid" })))));
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);
        Assert.Single((await _shop.EventsAsync(order.Id)).Where(e => e.Kind == "paid"));
        var invoices = await _shop.WithContextAsync((ctx, _) => ctx.Data.Table("invoices").CountAsync(DataQuery.Everything.Eq("order_id", order.Id)));
        Assert.Equal(1, invoices);
    }

    [Fact]
    public async Task Parallel_downloads_never_exceed_the_limit()
    {
        var id = await _shop.AddProductAsync("Rennen Download", 100, ProductKind.Digital);
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync($"/admin/p/shop/products/{id}");
        var upload = new MultipartFormDataContent
        {
            { new StringContent(AppFactory.Token(page)!), "__RequestVerificationToken" }, { new StringContent("upload-file"), "action" },
            { new ByteArrayContent(new byte[2048]), "file", "daten.zip" }
        };
        await admin.PostAsync($"/admin/p/shop/products/{id}", upload);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "rennen-download");
        var res = await _shop.CheckoutAsync(c, waiver: true);
        var number = ShopApp.NumberOf(res); var token = ShopApp.TokenOf(res);
        var order = (await _shop.OrderAsync(number))!;
        await _shop.AdminActionAsync(admin, order.Id, "paid");
        var file = (await _shop.WithContextAsync((_, s) => s.FilesOfProductAsync(id))).Single();
        await _shop.WithContextAsync(async (ctx, _) =>
        {
            var t = ctx.Data.Table("downloads");
            var row = (await t.QueryAsync(DataQuery.Everything.Eq("order_id", order.Id))).Single();
            await t.UpdateAsync(row.Id, new Dictionary<string, object?> { ["max_downloads"] = 3L });
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => _app.Browser().GetAsync($"/shop/download/{number}/{file.Id}?t={token}"))));
        using var logScope = _app.Services.CreateScope();
        var summary = string.Join(",", results.Select(r => (int)r.StatusCode).OrderBy(x => x)) + " LOG: " + string.Join(" | ", logScope.ServiceProvider.GetRequiredService<MatCMS.Services.PluginRegistry>().LogOf("shop").TakeLast(3));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK) == 3, summary);
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.Gone) == 7, summary);
        Assert.Equal(3, (await _shop.WithContextAsync((_, s) => s.DownloadsOfOrderAsync(order.Id))).Single().Count);
    }
}
