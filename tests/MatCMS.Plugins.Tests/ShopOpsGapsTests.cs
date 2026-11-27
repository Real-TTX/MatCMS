using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The shop's operations beyond products and categories: coupons, attributes and shipping rates are listed and changed, and EVERY write answers with an
/// undo that the host can run (and nobody else) and that puts back what was there.
/// </summary>
public class ShopOpsGapsTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopOpsGapsTests(AppFactory app) { _app = app; PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The name a caller uses: qualified (<c>shop.coupon.list</c>) where the host qualifies names, else the short one.</summary>
    private string Op(string name)
    {
        var c = _app.Services.GetRequiredService<PluginContributions>();
        return c.HasOperation("shop." + name) ? "shop." + name : name;
    }

    private async Task<JsonObject> RunAsync(string op, JsonObject args, bool allowInternal = false)
    {
        using var scope = _app.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, Op(op), args, default, allowInternal))!;
    }

    /// <summary>Runs the undo an answer carries, as the assistant's "Rückgängig" does.</summary>
    private async Task<JsonObject> UndoAsync(JsonObject answer)
    {
        var undo = Assert.IsType<JsonObject>(answer["undo"]);
        using var scope = _app.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>()
            .RunOperationAsync(scope.ServiceProvider, undo["name"]!.GetValue<string>(), undo["args"]?.DeepClone(), default, allowInternal: true))!;
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();

    private static readonly string[] Public = { "coupon.list", "coupon.update", "attribute.list", "attribute.update", "shipping.list", "shipping.save" };
    private static readonly string[] Internal =
        { "coupon.restore", "coupon.remove", "attribute.restore", "attribute.remove", "product.attributes.restore", "variant.restore", "shipping.restore", "shipping.remove" };

    [Fact]
    public void The_new_operations_are_declared_with_the_right_flags()
    {
        var registry = _app.Services.GetRequiredService<PluginRegistry>();
        PluginOperationSpec Spec(string name) => registry.Operations.Single(o => o.Owner == "shop" && o.Name == name).Spec!;
        foreach (var name in Public.Concat(Internal)) Assert.True(_app.Services.GetRequiredService<PluginContributions>().HasOperation(Op(name)), name);
        foreach (var name in new[] { "coupon.list", "attribute.list", "shipping.list" }) Assert.False(Spec(name).Writes, name);
        foreach (var name in new[] { "coupon.update", "attribute.update", "shipping.save" }) Assert.True(Spec(name).Writes, name);
        foreach (var name in Public) { Assert.False(Spec(name).PersonalData, name); Assert.False(Spec(name).Internal, name); Assert.False(string.IsNullOrWhiteSpace(Spec(name).Description), name); }
        foreach (var name in Internal) Assert.True(Spec(name).Internal, name);
        // the assistant is offered the public ones, never an undo helper
        var assistant = _app.Services.GetRequiredService<PluginContributions>().AssistantOperations.Where(o => o.Owner == "shop").Select(o => o.Name).ToList();
        Assert.All(Public, n => Assert.Contains(n, assistant));
        Assert.All(Internal, n => Assert.DoesNotContain(n, assistant));
    }

    [Fact]
    public async Task An_internal_undo_is_refused_without_the_hosts_permission()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("coupon.remove", new JsonObject { ["id"] = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync("variant.restore", new JsonObject { ["productId"] = 1, ["variants"] = new JsonArray() }));
    }

    [Fact]
    public async Task A_coupon_is_listed_changed_and_both_its_creation_and_its_change_are_undone()
    {
        var made = await RunAsync("coupon.create", new JsonObject { ["code"] = "gaps10", ["kind"] = "percent", ["percent"] = 10 });
        Assert.Equal("applied", Status(made));

        var list = await RunAsync("coupon.list", new JsonObject { ["search"] = "GAPS" });
        var row = Assert.Single(((JsonArray)list["data"]!).OfType<JsonObject>(), c => c["code"]!.GetValue<string>() == "GAPS10");
        Assert.Equal((10.0, true), (row["percent"]!.GetValue<double>(), row["active"]!.GetValue<bool>()));

        var changed = await RunAsync("coupon.update", new JsonObject { ["code"] = "gaps10", ["percent"] = 25, ["min_order_cents"] = 3000, ["expires"] = "2031-12-31", ["active"] = false });
        Assert.Equal("applied", Status(changed));
        var after = await _shop.CouponAsync("GAPS10");
        Assert.Equal((2500L, 3000L, false), (after.Value, after.MinOrderCents, after.Active));
        Assert.NotNull(after.ExpiresUtc);

        // bad input is refused and changes nothing
        Assert.Equal("failed", Status(await RunAsync("coupon.update", new JsonObject { ["code"] = "gaps10", ["percent"] = 150 })));
        Assert.Equal("failed", Status(await RunAsync("coupon.update", new JsonObject { ["code"] = "gaps10", ["category"] = "gibt-es-nicht" })));
        Assert.Equal("failed", Status(await RunAsync("coupon.update", new JsonObject { ["code"] = "gaps10", ["expires"] = "morgen" })));
        Assert.Equal("failed", Status(await RunAsync("coupon.update", new JsonObject { ["code"] = "KEIN-SOLCHER", ["active"] = true })));
        Assert.Equal(2500L, (await _shop.CouponAsync("GAPS10")).Value);

        Assert.Equal("applied", Status(await UndoAsync(changed)));
        var back = await _shop.CouponAsync("GAPS10");
        Assert.Equal((1000L, 0L, true, (DateTime?)null), (back.Value, back.MinOrderCents, back.Active, back.ExpiresUtc));

        Assert.Equal("applied", Status(await UndoAsync(made)));
        Assert.Null(await _shop.WithContextAsync((_, s) => s.CouponByCodeAsync("GAPS10")));
    }

    [Fact]
    public async Task Variants_set_by_an_agent_are_undone_to_what_the_product_had()
    {
        var id = await _shop.AddProductAsync("Gaps Hemd", 3000, variants: new[] { ("S", 0L, (long?)4), ("M", 100L, (long?)null) });
        var slug = (await _shop.ProductAsync(id)).Slug;
        var before = await _shop.WithContextAsync((_, s) => s.VariantsAsync(id));

        var set = await RunAsync("variant.set", new JsonObject
        {
            ["product"] = slug, ["replace"] = true, ["options"] = new JsonObject { ["Größe"] = new JsonArray("M", "XL") },
            ["variants"] = new JsonArray(new JsonObject { ["name"] = "M", ["delta_cents"] = 900, ["stock"] = 1 }, new JsonObject { ["name"] = "XL" })
        });
        Assert.Equal("applied", Status(set));
        Assert.Equal(3, (await _shop.WithContextAsync((_, s) => s.VariantsAsync(id))).Count);

        Assert.Equal("applied", Status(await UndoAsync(set)));
        var after = await _shop.WithContextAsync((_, s) => s.VariantsAsync(id));
        Assert.Equal(before.Select(v => (v.Id, v.Name, v.PriceDeltaCents, v.TrackStock, v.Stock, v.Active)), after.Select(v => (v.Id, v.Name, v.PriceDeltaCents, v.TrackStock, v.Stock, v.Active)));
        Assert.Empty(ShopVariants.OptionsFromJson((await _shop.ProductAsync(id)).VariantOptions));   // the option list it had (none)
    }

    [Fact]
    public async Task An_attribute_is_listed_changed_and_its_creation_change_and_product_values_are_undone()
    {
        var made = await RunAsync("attribute.create", new JsonObject { ["name"] = "Gaps Material", ["kind"] = "choice", ["options"] = new JsonArray("Holz", "Metall") });
        Assert.Equal("applied", Status(made));
        var slug = made["data"]!["slug"]!.GetValue<string>();
        Assert.Contains(((JsonArray)(await RunAsync("attribute.list", new JsonObject()))["data"]!).OfType<JsonObject>(), a => a["slug"]!.GetValue<string>() == slug);

        var changed = await RunAsync("attribute.update", new JsonObject { ["attribute"] = slug, ["name"] = "Gaps Werkstoff", ["options"] = new JsonArray("Holz", "Metall", "Glas"), ["filterable"] = false });
        Assert.Equal("applied", Status(changed));
        Assert.Equal("failed", Status(await RunAsync("attribute.update", new JsonObject { ["attribute"] = slug, ["options"] = new JsonArray() })));    // a choice keeps options
        Assert.Equal("failed", Status(await RunAsync("attribute.update", new JsonObject { ["attribute"] = "gibt-es-nicht", ["name"] = "X" })));
        var now = await _shop.WithContextAsync((_, s) => s.AttributeBySlugAsync(slug));
        Assert.Equal(("Gaps Werkstoff", 3, false), (now!.Name, now.OptionList.Count, now.Filterable));

        // values of a product, then undone to none
        var pid = await _shop.AddProductAsync("Gaps Stuhl", 9900);
        var pslug = (await _shop.ProductAsync(pid)).Slug;
        var values = await RunAsync("product.attributes", new JsonObject { ["product"] = pslug, ["values"] = new JsonObject { [slug] = new JsonArray("Glas") } });
        Assert.Equal("applied", Status(values));
        Assert.Equal(new[] { "Glas" }, (await _shop.WithContextAsync((_, s) => s.ProductValuesAsync(pid)))[now.Id]);
        Assert.Equal("applied", Status(await UndoAsync(values)));
        Assert.False((await _shop.WithContextAsync((_, s) => s.ProductValuesAsync(pid))).ContainsKey(now.Id));

        Assert.Equal("applied", Status(await UndoAsync(changed)));
        var back = await _shop.WithContextAsync((_, s) => s.AttributeBySlugAsync(slug));
        Assert.Equal(("Gaps Material", 2, true), (back!.Name, back.OptionList.Count, back.Filterable));

        Assert.Equal("applied", Status(await UndoAsync(made)));
        Assert.Null(await _shop.WithContextAsync((_, s) => s.AttributeBySlugAsync(slug)));
    }

    [Fact]
    public async Task A_shipping_rate_is_made_and_changed_with_the_tabs_rules_and_both_are_undone()
    {
        Assert.Equal("failed", Status(await RunAsync("shipping.save", new JsonObject { ["name"] = "Gaps Kaputt", ["price_cents"] = 500, ["countries"] = "Deutschland" })));
        Assert.Equal("failed", Status(await RunAsync("shipping.save", new JsonObject { ["name"] = "Gaps Ohne Preis" })));
        Assert.Equal("failed", Status(await RunAsync("shipping.save", new JsonObject { ["price_cents"] = 500 })));       // no name

        var made = await RunAsync("shipping.save", new JsonObject { ["name"] = "Gaps Express", ["countries"] = "de, at", ["price_cents"] = 1290, ["free_from_cents"] = 10000 });
        Assert.Equal("applied", Status(made));
        var id = made["data"]!["id"]!.GetValue<long>();
        var listed = ((JsonArray)(await RunAsync("shipping.list", new JsonObject()))["data"]!).OfType<JsonObject>().Single(r => r["id"]!.GetValue<long>() == id);
        Assert.Equal(("DE,AT", 1290L), (listed["countries"]!.GetValue<string>(), listed["priceCents"]!.GetValue<long>()));

        var changed = await RunAsync("shipping.save", new JsonObject { ["id"] = id, ["price_cents"] = 1490, ["active"] = false });
        Assert.Equal("applied", Status(changed));
        var rate = await _shop.WithContextAsync((_, s) => s.RateAsync(id));
        Assert.Equal((1490L, false, "Gaps Express"), (rate!.PriceCents, rate.Active, rate.Name));

        Assert.Equal("applied", Status(await UndoAsync(changed)));
        rate = await _shop.WithContextAsync((_, s) => s.RateAsync(id));
        Assert.Equal((1290L, true), (rate!.PriceCents, rate.Active));

        Assert.Equal("applied", Status(await UndoAsync(made)));
        Assert.Null(await _shop.WithContextAsync((_, s) => s.RateAsync(id)));
    }
}
