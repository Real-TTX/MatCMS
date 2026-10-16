using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The shop's operations for the cloud's agent interface: read orders, move one along, set variants, define attributes, give a product its values.</summary>
public class ShopOpsTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopOpsTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonObject> RunAsync(string op, JsonObject args)
    {
        using var scope = _app.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, op, args))!;
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();
    private static string Message(JsonObject a) => a["message"]!.GetValue<string>();

    private async Task<string> PlaceOrderAsync(string title, long price, int qty = 1)
    {
        var id = await _shop.AddProductAsync(title, price);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, (await _shop.ProductAsync(id)).Slug, qty);
        return ShopApp.NumberOf(await _shop.CheckoutAsync(c, email: "ops@example.org"));
    }

    [Fact]
    public async Task All_operations_are_offered_by_the_shop()
    {
        var contributions = _app.Services.GetRequiredService<PluginContributions>();
        foreach (var op in new[] { "product.create", "coupon.create", "order.list", "order.update", "variant.set", "attribute.create", "product.attributes" })
            Assert.True(contributions.HasOperation(op), op);
    }

    [Fact]
    public async Task Orders_can_be_read_whole_by_number_and_filtered_by_status_and_a_bad_filter_is_refused()
    {
        var number = await PlaceOrderAsync("Ops Lesbar", 1500, 2);
        var one = await RunAsync("order.list", new JsonObject { ["number"] = number });
        Assert.Equal("applied", Status(one));
        var order = (JsonObject)((JsonArray)one["data"]!["orders"]!)[0]!;
        Assert.Equal(number, order["number"]!.GetValue<string>()); Assert.Equal("ops@example.org", order["email"]!.GetValue<string>());
        var item = (JsonObject)((JsonArray)order["items"]!)[0]!;
        Assert.Equal("Ops Lesbar", item["title"]!.GetValue<string>()); Assert.Equal(2, item["qty"]!.GetValue<int>());

        var open = await RunAsync("order.list", new JsonObject { ["status"] = OrderStatus.AwaitingPayment, ["limit"] = 500 });
        Assert.Equal("applied", Status(open));
        Assert.Contains(((JsonArray)open["data"]!["orders"]!).OfType<JsonObject>(), x => x["number"]!.GetValue<string>() == number);
        Assert.True(((JsonArray)open["data"]!["orders"]!).Count <= ShopOps.MaxOrders);

        Assert.Equal("failed", Status(await RunAsync("order.list", new JsonObject { ["status"] = "kaputt" })));
        Assert.Equal("failed", Status(await RunAsync("order.list", new JsonObject { ["since"] = "gestern" })));
        Assert.Equal("failed", Status(await RunAsync("order.list", new JsonObject { ["number"] = "GIBT-ES-NICHT" })));
    }

    [Fact]
    public async Task An_order_moves_along_paid_shipped_completed_and_a_move_it_cannot_make_is_refused()
    {
        var number = await PlaceOrderAsync("Ops Weg", 2000);
        Assert.Equal("failed", Status(await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "complete" })));            // not paid yet
        Assert.Equal("applied", Status(await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "paid" })));
        var shipped = await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "ship", ["carrier"] = "dhl", ["tracking"] = "00340434161094042557", ["notify"] = false });
        Assert.Equal("applied", Status(shipped));
        var o = (await _shop.OrderAsync(number))!;
        Assert.Equal((OrderStatus.Shipped, "00340434161094042557"), (o.Status, o.Tracking));
        Assert.Equal("applied", Status(await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "note", ["text"] = "Per Telefon bestätigt" })));
        Assert.Contains(await _shop.EventsAsync(o.Id), e => e.Text.Contains("Per Telefon bestätigt"));
        Assert.Equal("applied", Status(await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "complete" })));
        Assert.Equal(OrderStatus.Completed, (await _shop.OrderAsync(number))!.Status);
        Assert.Equal("failed", Status(await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "cancel" })));                // a finished order stays
        Assert.Equal("failed", Status(await RunAsync("order.update", new JsonObject { ["number"] = number, ["action"] = "dance" })));
        Assert.Equal("failed", Status(await RunAsync("order.update", new JsonObject { ["number"] = "nope", ["action"] = "paid" })));
    }

    [Fact]
    public async Task Variants_are_set_by_name_updated_in_place_and_switched_off_not_deleted_on_replace()
    {
        var id = await _shop.AddProductAsync("Ops Hemd", 3000);
        var slug = (await _shop.ProductAsync(id)).Slug;
        var first = await RunAsync("variant.set", new JsonObject
        {
            ["product"] = slug, ["options"] = new JsonObject { ["Größe"] = new JsonArray("S", "M", "L") },
            ["variants"] = new JsonArray(new JsonObject { ["name"] = "S", ["stock"] = 3, ["sku"] = "H-S" }, new JsonObject { ["name"] = "M", ["delta"] = "2,00" }, new JsonObject { ["name"] = "L", ["delta_cents"] = 400, ["image"] = "javascript:x" })
        });
        Assert.Equal("applied", Status(first)); Assert.Contains("3 Variante(n) neu", Message(first));
        var v1 = await _shop.WithContextAsync((_, s) => s.VariantsAsync(id));
        Assert.Equal(3, v1.Count); Assert.Equal(200, v1.First(v => v.Name == "M").PriceDeltaCents); Assert.Equal(400, v1.First(v => v.Name == "L").PriceDeltaCents);
        Assert.True(v1.First(v => v.Name == "S").TrackStock); Assert.False(v1.First(v => v.Name == "M").TrackStock); Assert.Equal("", v1.First(v => v.Name == "L").Image);
        var opt = Assert.Single(ShopVariants.OptionsFromJson((await _shop.ProductAsync(id)).VariantOptions));
        Assert.Equal("Größe", opt.Name); Assert.Equal(new[] { "S", "M", "L" }, opt.Values);

        var idOfM = v1.First(v => v.Name == "M").Id;
        var second = await RunAsync("variant.set", new JsonObject { ["product"] = slug, ["replace"] = true, ["variants"] = new JsonArray(new JsonObject { ["name"] = "m", ["delta_cents"] = 250 }, new JsonObject { ["name"] = "XL" }) });
        Assert.Contains("1 Variante(n) neu, 1 aktualisiert, 2 abgeschaltet", Message(second));
        var v2 = await _shop.WithContextAsync((_, s) => s.VariantsAsync(id));
        Assert.Equal(idOfM, v2.First(v => v.Name.Equals("m", StringComparison.OrdinalIgnoreCase)).Id);          // updated in place, its id is the same
        Assert.Equal(250, v2.First(v => v.Id == idOfM).PriceDeltaCents);
        Assert.Equal(2, v2.Count(v => v.Active)); Assert.Equal(4, v2.Count);                                      // S and L are off, not gone

        Assert.Equal("failed", Status(await RunAsync("variant.set", new JsonObject { ["product"] = slug, ["variants"] = new JsonArray(new JsonObject { ["name"] = "A" }, new JsonObject { ["name"] = "a" }) })));
        Assert.Equal("failed", Status(await RunAsync("variant.set", new JsonObject { ["product"] = "nirgends", ["variants"] = new JsonArray(new JsonObject { ["name"] = "A" }) })));
        Assert.Equal("failed", Status(await RunAsync("variant.set", new JsonObject { ["product"] = slug })));
    }

    [Fact]
    public async Task Attributes_are_defined_add_only_and_a_product_gets_valid_values_only()
    {
        var made = await RunAsync("attribute.create", new JsonObject { ["name"] = "Ops Farbe", ["kind"] = "choice", ["options"] = new JsonArray("Rot", "Blau", "Grün") });
        Assert.Equal("applied", Status(made));
        Assert.Equal("skipped-exists", Status(await RunAsync("attribute.create", new JsonObject { ["name"] = "Ops Farbe", ["kind"] = "text" })));
        Assert.Equal("applied", Status(await RunAsync("attribute.create", new JsonObject { ["name"] = "Ops Gewicht", ["kind"] = "number", ["unit"] = "kg", ["filterable"] = false })));
        Assert.Equal("applied", Status(await RunAsync("attribute.create", new JsonObject { ["name"] = "Ops Bio", ["kind"] = "bool" })));
        Assert.Equal("failed", Status(await RunAsync("attribute.create", new JsonObject { ["name"] = "Ops Leer", ["kind"] = "choice" })));                      // a choice needs options
        Assert.Equal("failed", Status(await RunAsync("attribute.create", new JsonObject { ["name"] = "Ops Weg", ["kind"] = "text", ["category"] = "gibt-es-nicht" })));
        Assert.Equal("failed", Status(await RunAsync("attribute.create", new JsonObject { ["name"] = " " })));

        var id = await _shop.AddProductAsync("Ops Tasse", 1000);
        var slug = (await _shop.ProductAsync(id)).Slug;
        var set = await RunAsync("product.attributes", new JsonObject
        {
            ["product"] = slug,
            ["values"] = new JsonObject { ["ops-farbe"] = new JsonArray("rot", "Lila", "Blau"), ["ops-gewicht"] = "1,5", ["ops-bio"] = true, ["unbekannt"] = "x" }
        });
        Assert.Equal("applied", Status(set)); Assert.Contains("3 Eigenschaft(en) gesetzt", Message(set)); Assert.Contains("keine Eigenschaft", Message(set));
        var values = await _shop.WithContextAsync(async (_, s) =>
        {
            var attrs = (await s.AttributesAsync()).ToDictionary(a => a.Slug);
            var v = await s.ProductValuesAsync(id);
            return (Colour: v[attrs["ops-farbe"].Id], Weight: v[attrs["ops-gewicht"].Id], Bio: v[attrs["ops-bio"].Id]);
        });
        Assert.Equal(new[] { "Rot", "Blau" }, values.Colour);                                              // the options' own spelling; "Lila" is no option
        Assert.Equal(new[] { "1.5" }, values.Weight); Assert.Equal(new[] { "1" }, values.Bio);

        var bad = await RunAsync("product.attributes", new JsonObject { ["product"] = slug, ["values"] = new JsonObject { ["ops-farbe"] = "Lila" } });
        Assert.Equal("failed", Status(bad));
        Assert.Equal("failed", Status(await RunAsync("product.attributes", new JsonObject { ["product"] = "nirgends", ["values"] = new JsonObject { ["ops-bio"] = true } })));
    }

    [Fact]
    public async Task A_read_answer_carries_its_data_to_the_content_op_report()
    {
        var number = await PlaceOrderAsync("Ops Bericht", 900);
        using var scope = _app.Services.CreateScope();
        var applier = ActivatorUtilities.CreateInstance<MatCMS.Services.ContentOpApplier>(scope.ServiceProvider);
        var report = await applier.ApplyAsync(new MatCMS.Shared.PendingContentOp { OpId = 1, Kind = "order.list", PayloadJson = new JsonObject { ["number"] = number }.ToJsonString() }, default);
        Assert.Equal("applied", report.Outcome);
        Assert.Contains(number, report.ResultJson);
    }
}
