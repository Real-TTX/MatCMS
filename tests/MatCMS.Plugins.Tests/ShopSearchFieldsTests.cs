using System.Net;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>What the search looks at is the operator's choice ("Durchsuchen", a multiple choice): brand and attribute values found by their names, the older
/// single choice still honoured until the new list is saved.</summary>
public class ShopSearchFieldsTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopSearchFieldsTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.SearchFields] = "brand,attributes" });
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void A_stored_list_keeps_only_known_fields_in_their_order_and_the_old_choice_still_means_what_it_meant()
    {
        Assert.Equal(new[] { "sku", "brand" }, ShopSearchFields.Parse("brand, nonsense,sku,,brand"));
        Assert.Empty(ShopSearchFields.Parse(""));
        Assert.Equal(new[] { "sku" }, ShopSearchFields.Legacy("title"));
        Assert.Contains("description", ShopSearchFields.Legacy("all"));
        Assert.DoesNotContain("description", ShopSearchFields.Legacy("text"));
    }

    [Fact]
    public async Task Brand_and_attribute_values_are_found_by_their_names_and_the_summary_is_not_when_unticked()
    {
        await _shop.WithContextAsync((_, s) => s.SaveBrandAsync(new Brand { Slug = "sfmarke", Name = "Sfmarkenname", Active = true }));
        var attr = await _shop.WithContextAsync((_, s) => s.SaveAttributeAsync(new ShopAttribute { Slug = "sf-farbe", Name = "Sf Farbe", Kind = AttributeKind.Choice, Options = "Sfkirschrot\nSfblau" }));
        var id = await _shop.AddProductAsync("Sf Laufwerk", 5000);
        await _shop.WithContextAsync(async (_, s) =>
        {
            var p = (await s.ProductAsync(id))!;
            await s.SaveProductAsync(p with { Brand = "sfmarke", Summary = "mit Sfzimt" });
            await s.SetProductAttrAsync(id, attr, new[] { "Sfkirschrot" });
        });
        var v = _app.Browser();
        Assert.Contains("Sf Laufwerk", WebUtility.HtmlDecode(await v.GetStringAsync("/shop?q=Sfmarkenname")));
        Assert.Contains("Sf Laufwerk", WebUtility.HtmlDecode(await v.GetStringAsync("/shop?q=Sfkirschrot")));
        Assert.DoesNotContain("Sf Laufwerk", WebUtility.HtmlDecode(await v.GetStringAsync("/shop?q=Sfzimt")));      // the summary is not ticked
        Assert.Contains("Sf Laufwerk", WebUtility.HtmlDecode(await v.GetStringAsync("/shop?q=Laufwerk")));          // the name always
        // The suggestions while typing follow the same choice.
        Assert.Contains("Sf Laufwerk", await v.GetStringAsync("/shop/suggest?q=Sfmarkenname"));
    }

    [Fact]
    public async Task The_settings_form_draws_a_box_per_field_and_stores_only_declared_values()
    {
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupCatalog");
        for (var i = 0; i < ShopSearchFields.All.Length; i++) Assert.Contains($"name=\"s_catalog.searchFields~{i}\"", page);
        // A forged value under the field's own name is never stored — the boxes post by position.
        var res = await admin.PostAsync("/admin/p/shop/_settings?group=set.groupCatalog", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save-settings", ["s_catalog.searchFields"] = "evil", ["s_catalog.searchFields~0"] = "true", ["s_catalog.searchFields~5"] = "true", ["s_catalog.searchFields~6"] = "true",
            ["s_catalog.showSearch"] = "true", ["s_catalog.showSort"] = "true", ["s_catalog.showCategories"] = "true", ["s_catalog.showTags"] = "true", ["s_catalog.showFilters"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("sku,brand,attributes", await _shop.WithContextAsync((ctx, _) => Task.FromResult(ctx.Settings.Get(Keys.SearchFields))));
    }
}
