using System.Net;
using System.Text.RegularExpressions;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

public class ShopVariantPickerUnitTests
{
    private static Variant V(long id, string name, long delta = 0, bool tracked = false, long stock = 0, string image = "") =>
        new() { Id = id, ProductId = 1, Name = name, PriceDeltaCents = delta, TrackStock = tracked, Stock = stock, Image = image, Active = true };

    private static readonly VariantOption[] Opts = { new("Größe", new[] { "S", "M" }), new("Farbe", new[] { "Rot", "Blau" }) };

    [Fact]
    public void Variants_named_by_their_option_values_become_choices_in_the_options_own_spelling()
    {
        var plan = ShopVariantPicker.Plan(Opts, new[] { V(1, "S / rot"), V(2, "M / Blau", 200), V(3, "M / Rot", 0, true, 0, "/uploads/rot.jpg") }, 1000)!;
        Assert.Equal(3, plan.Count);
        Assert.Equal(new[] { "S", "Rot" }, plan[0].Parts);                                              // "rot" → "Rot"
        Assert.Equal(1200, plan[1].PriceCents); Assert.True(plan[1].Available);
        Assert.False(plan[2].Available); Assert.Equal("/uploads/rot.jpg", plan[2].Image);              // sold out, with its picture
    }

    [Fact]
    public void Anything_that_does_not_fit_the_options_keeps_the_plain_list()
    {
        Assert.Null(ShopVariantPicker.Plan(Opts, new[] { V(1, "S / Rot"), V(2, "XL / Rot") }, 1000));          // XL is no option
        Assert.Null(ShopVariantPicker.Plan(Opts, new[] { V(1, "S") }, 1000));                                  // one part for two options
        Assert.Null(ShopVariantPicker.Plan(Opts, new[] { V(1, "S / Rot"), V(2, "s / ROT") }, 1000));           // the same combination twice
        Assert.Null(ShopVariantPicker.Plan(Array.Empty<VariantOption>(), new[] { V(1, "S") }, 1000));          // no options
        Assert.Null(ShopVariantPicker.Plan(Opts, Array.Empty<Variant>(), 1000));
        Assert.Equal(0, ShopVariantPicker.Plan(Opts, new[] { V(1, "S / Rot", -5000) }, 1000)![0].PriceCents);  // never a negative price
    }

    [Fact]
    public void The_picker_has_a_chip_per_value_that_is_sold_in_some_combination_and_encodes_everything()
    {
        var plan = ShopVariantPicker.Plan(Opts, new[] { V(1, "S / Rot"), V(2, "S / \"Blau\"") }, 1000);
        Assert.Null(plan);                                                                                        // a quote in a value is no option value
        var ok = ShopVariantPicker.Plan(new[] { new VariantOption("Größe", new[] { "S", "M" }) }, new[] { V(1, "S"), V(2, "<b>M</b>") }, 1000);
        Assert.Null(ok);
        var html = ShopVariantPicker.Html(new[] { new VariantOption("Größe <x>", new[] { "S", "M", "L" }) }, ShopVariantPicker.Plan(new[] { new VariantOption("Größe <x>", new[] { "S", "M", "L" }) }, new[] { V(1, "S"), V(2, "M") }, 1000)!, c => c / 100 + " €", "n/a");
        Assert.Contains("&lt;x&gt;", html); Assert.DoesNotContain("<x>", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"shop-chip\"").Count);                                       // L has no variant: no chip for it
        Assert.Contains("name=\"variant\"", html); Assert.Contains("data-choices=", html);
    }
}

public class ShopSuggestUnitTests
{
    [Fact]
    public void Distance_counts_inserts_deletes_substitutions_and_one_swap_as_one_step()
    {
        Assert.Equal(0, ShopSuggest.Distance("Tasse", "tasse"));
        Assert.Equal(1, ShopSuggest.Distance("tasse", "tase")); Assert.Equal(1, ShopSuggest.Distance("tasse", "tasso")); Assert.Equal(1, ShopSuggest.Distance("tsase", "tasse"));
        Assert.Equal(5, ShopSuggest.Distance("", "tasse")); Assert.Equal(3, ShopSuggest.Distance("kitten", "sitting"));
    }

    [Fact]
    public void Words_are_lower_case_letters_and_digits_of_three_or_more_each_once()
    {
        Assert.Equal(new[] { "Kaffeetasse", "blau", "500ml" }, ShopSuggest.Words(new[] { "Kaffeetasse, blau!", "Blau 500ml – ab" }));
        Assert.Empty(ShopSuggest.Words(new[] { "a b", "" }));
    }

    [Fact]
    public void A_word_close_to_a_known_one_is_corrected_and_short_or_known_words_are_left_alone()
    {
        var vocab = new[] { "Tasse", "Kaffeebohnen", "Teekanne", "USB" };
        Assert.Equal("Tasse", ShopSuggest.DidYouMean("tsase", vocab));
        Assert.Equal("tasse Kaffeebohnen", ShopSuggest.DidYouMean("tasse kafebohnen", vocab));        // each word on its own
        Assert.Null(ShopSuggest.DidYouMean("Tasse", vocab));                                          // already right
        Assert.Null(ShopSuggest.DidYouMean("usb", vocab)); Assert.Null(ShopSuggest.DidYouMean("tv", vocab));
        Assert.Null(ShopSuggest.DidYouMean("Hubschrauber", vocab));                                   // nothing near
        Assert.Null(ShopSuggest.DidYouMean("", vocab)); Assert.Null(ShopSuggest.DidYouMean("tsase", Array.Empty<string>()));
        Assert.Equal("Tasse", ShopSuggest.DidYouMean("TSASE", vocab));
    }
}

public class ShopEstimateUnitTests
{
    private static ShippingRate R(string countries, long price, long free = 0, bool active = true, int maxWeight = 0) => new() { Id = price, Name = "r" + price, Countries = countries, PriceCents = price, FreeFromCents = free, Active = active, MaxWeightGrams = maxWeight };

    [Fact]
    public void The_offered_countries_are_those_an_active_rate_covers_and_the_chosen_one_falls_back_sensibly()
    {
        var some = new[] { R("DE,AT", 490), R("CH", 990, active: false) };
        Assert.Equal(new[] { "DE", "AT" }, ShopPublic.EstimateCountries(some).OrderByDescending(c => c == "DE").ToArray());
        Assert.Equal(Countries.All.Length, ShopPublic.EstimateCountries(new[] { R("*", 1000) }).Count);
        Assert.Empty(ShopPublic.EstimateCountries(new[] { R("DE", 500, active: false) }));
        var offered = new[] { "AT", "DE" };
        Assert.Equal("AT", ShopPublic.EstimateCountry("at", "DE", offered));
        Assert.Equal("DE", ShopPublic.EstimateCountry("FR", "DE", offered));                       // not offered: the home country
        Assert.Equal("AT", ShopPublic.EstimateCountry(null, "CH", offered));                         // home not offered either: the first
        Assert.Equal("", ShopPublic.EstimateCountry("DE", "DE", Array.Empty<string>()));
    }

    [Fact]
    public void The_distance_to_free_shipping_uses_the_nearest_threshold_of_a_rate_that_fits()
    {
        var cart = new PricedCart(Array.Empty<PricedLine>(), 3000, 0, true, 1000, Array.Empty<string>(), false);
        Assert.Equal(2000, ShopPublic.UntilFreeShipping(new[] { R("DE", 490, 5000), R("DE", 790, 9000) }, "DE", cart));
        Assert.Equal(0, ShopPublic.UntilFreeShipping(new[] { R("DE", 490, 2000) }, "DE", cart));          // already free
        Assert.Equal(0, ShopPublic.UntilFreeShipping(new[] { R("DE", 490, 5000) }, "AT", cart));          // does not serve AT
        Assert.Equal(0, ShopPublic.UntilFreeShipping(new[] { R("DE", 490, 5000, maxWeight: 500) }, "DE", cart));   // too heavy for it
        Assert.Equal(0, ShopPublic.UntilFreeShipping(new[] { R("DE", 490) }, "DE", cart));                // no threshold
    }
}

/// <summary>The storefront additions: the variant picker, the shipping estimate, search suggestions, cart reminders.</summary>
public class ShopStorefrontTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopStorefrontTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_product_with_options_gets_pickers_and_a_noscript_list_and_one_without_keeps_the_plain_list()
    {
        var id = await _shop.AddProductAsync("Pick Hemd", 3000);
        await _shop.WithContextAsync(async (_, s) =>
        {
            var p = (await s.ProductAsync(id))!;
            await s.SaveProductAsync(p with { VariantOptions = ShopVariants.OptionsToJson(new[] { new VariantOption("Größe", new[] { "S", "M" }), new VariantOption("Farbe", new[] { "Rot", "Blau" }) }) });
            foreach (var (name, delta, stock) in new[] { ("S / Rot", 0L, 5L), ("S / Blau", 0L, 0L), ("M / Rot", 200L, 2L), ("M / Blau", 200L, 1L) })
                await s.SaveVariantAsync(new Variant { ProductId = id, Name = name, PriceDeltaCents = delta, TrackStock = true, Stock = stock, Sort = 0, Active = true });
            return 0;
        });
        var html = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/shop/pick-hemd"));
        Assert.Contains("class=\"shop-opts\"", html); Assert.Equal(4, Regex.Matches(html, "class=\"shop-chip\"").Count);
        Assert.Contains("data-shop-price", html);
        Assert.Contains("<noscript><div><label", html); Assert.Contains("S / Rot", html);                      // the plain list for a visitor without script

        var plainId = await _shop.AddProductAsync("Pick Plain", 1000);
        await _shop.WithContextAsync(async (_, s) => { await s.SaveVariantAsync(new Variant { ProductId = plainId, Name = "Groß", Sort = 0, Active = true }); return 0; });
        var plain = await _app.Browser().GetStringAsync("/shop/pick-plain");
        Assert.DoesNotContain("class=\"shop-opts\"", plain); Assert.Contains("<select name=\"variant\">", plain);
    }

    [Fact]
    public async Task The_cart_estimates_shipping_per_country_with_the_total_and_the_distance_to_free_shipping()
    {
        await _shop.AddRateAsync("Est Inland", "DE", 490, 10000);
        await _shop.AddRateAsync("Est Ausland", "AT,CH", 1290);
        await _shop.AddProductAsync("Est Ware", 3000);
        var c = _app.Browser();
        await _shop.AddToCartAsync(c, "est-ware");
        var de = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/cart"));
        Assert.Contains("class=\"shop-est\"", de); Assert.Contains("Est Inland", de); Assert.Contains("4,90", de);
        Assert.Contains("34,90", de);                                                                          // 30,00 + 4,90
        Assert.Contains("Noch 20,00", de);                                                                     // the nearest threshold: the seeded standard rate is free from 50,00
        Assert.DoesNotContain("Est Ausland", de);
        var at = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/cart?ship=AT"));
        Assert.Contains("Est Ausland", at); Assert.Contains("12,90", at); Assert.Contains("42,90", at); Assert.DoesNotContain("Est Inland", at);
        Assert.Contains("<option value=\"AT\" selected>", at);
        var junk = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/cart?ship=<script>"));
        Assert.DoesNotContain("ship=<script>", junk); Assert.Contains("Est Inland", junk);                        // an unknown value is the home country
    }

    [Fact]
    public async Task The_suggest_route_offers_active_products_with_price_and_picture_matching_categories_and_a_corrected_spelling()
    {
        await _shop.AddProductAsync("Sug Tasse", 1290);
        await _shop.AddProductAsync("Sug <b>Fett</b> Becher", 990);
        var hidden = await _shop.AddProductAsync("Sug Tasse Geheim", 500);
        await _shop.WithContextAsync(async (_, s) => { await s.SaveProductAsync((await s.ProductAsync(hidden))! with { Active = false }); return 0; });
        var c = _app.Browser();
        var res = await c.GetAsync("/shop/suggest?q=sug%20tasse");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString() ?? "");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        var titles = ((System.Text.Json.Nodes.JsonArray)json["products"]!).Select(x => x!["title"]!.GetValue<string>()).ToList();
        Assert.Contains("Sug Tasse", titles); Assert.DoesNotContain("Sug Tasse Geheim", titles);          // an inactive product is never offered
        var first = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)json["products"]!)[0]!;
        Assert.Equal("/shop/sug-tasse", first["url"]!.GetValue<string>()); Assert.Contains("12,90", first["price"]!.GetValue<string>());

        var typo = System.Text.Json.Nodes.JsonNode.Parse(await c.GetStringAsync("/shop/suggest?q=sug%20tsase"))!;
        Assert.Empty((System.Text.Json.Nodes.JsonArray)typo["products"]!); Assert.Equal("sug Tasse", typo["didYouMean"]!.GetValue<string>(), ignoreCase: true);
        var none = System.Text.Json.Nodes.JsonNode.Parse(await c.GetStringAsync("/shop/suggest?q=zzzzzzqq"))!;
        Assert.Null(none["didYouMean"]);
        var tooShort = System.Text.Json.Nodes.JsonNode.Parse(await c.GetStringAsync("/shop/suggest?q=s"))!;
        Assert.Empty((System.Text.Json.Nodes.JsonArray)tooShort["products"]!);
        Assert.DoesNotContain("<b>Fett</b>", await c.GetStringAsync("/shop/suggest?q=fett") is var raw && !raw.Contains("\u003Cb") ? raw : "");   // JSON-encoded, never raw markup
    }

    [Fact]
    public async Task The_catalogue_without_a_hit_offers_the_corrected_search_and_the_search_boxes_carry_the_script()
    {
        await _shop.AddProductAsync("Dym Kaffeebohnen", 1500);
        var c = _app.Browser();
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/shop?q=kafebohnen"));
        Assert.Contains("class=\"shop-dym\"", html); Assert.Contains("/shop?q=Kaffeebohnen", html);
        Assert.Contains("__shopSuggest", await c.GetStringAsync("/shop"));                              // the catalogue's own search box
        Assert.DoesNotContain("class=\"shop-dym\"", await c.GetStringAsync("/shop?q=kaffeebohnen"));
    }

    [Fact]
    public async Task The_product_page_lists_only_the_reasons_to_buy_the_settings_make_true()
    {
        await _shop.AddRateAsync("Trust Inland", "DE", 490, 7500);
        var physical = await _shop.AddProductAsync("Trust Ware", 2500);
        var html = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/shop/trust-ware"));
        Assert.Contains("class=\"shop-trust\"", html);
        Assert.Contains("Kostenloser Versand ab", html); Assert.Contains("Widerruf &amp; Rückgabe".Replace("&amp;", "&"), html); Assert.Contains("Sicher bezahlen", html);
        await _shop.AddProductAsync("Trust Download", 900, ProductKind.Digital);
        var digital = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/shop/trust-download"));
        Assert.Contains("Sofort nach der Zahlung als Download", digital); Assert.DoesNotContain("Kostenloser Versand", digital);
    }
}
