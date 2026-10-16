using System.Net;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Attributes as pure functions: reading what a form or an address says, where an attribute applies, what a filter matches and what it can still offer.</summary>
public class ShopAttributeLogicTests
{
    private static ShopAttribute A(long id, string slug, string kind, string options = "", string category = "", string unit = "") =>
        new() { Id = id, Slug = slug, Name = slug, Kind = kind, Options = options, Category = category, Unit = unit };

    private static readonly ShopAttribute Colour = A(1, "farbe", AttributeKind.Choice, "Rot\nBlau\nGrün");
    private static readonly ShopAttribute Weight = A(2, "gewicht", AttributeKind.Number, unit: "kg");
    private static readonly ShopAttribute Organic = A(3, "bio", AttributeKind.Bool);

    [Fact]
    public void A_value_is_one_clean_line_without_commas_and_options_are_cut_the_same_way()
    {
        Assert.Equal("Rot Blau", ShopAttributes.CleanValue("  Rot,  Blau  "));
        Assert.Equal(80, ShopAttributes.CleanValue(new string('x', 200)).Length);
        Assert.Equal(new[] { "Rot", "blau", "Grün" }, ShopAttributes.SplitOptions("Rot\r\nblau,\nROT\n\n Grün ").ToArray());       // a repeat (any case) is dropped, the first spelling stays
    }

    [Fact]
    public void What_a_form_posts_becomes_values_that_fit_the_kind()
    {
        Assert.Equal(new[] { "Rot", "Blau" }, ShopAttributes.ReadValues(Colour, "rot, nope ,BLAU, rot", true).ToArray());          // only its own options, in their spelling
        Assert.Empty(ShopAttributes.ReadValues(Colour, "", true));
        Assert.Equal(new[] { "12.5" }, ShopAttributes.ReadValues(Weight, "12,5", true).ToArray());                                    // a German decimal sign
        Assert.Empty(ShopAttributes.ReadValues(Weight, "viel", true));
        Assert.Equal(new[] { "1" }, ShopAttributes.ReadValues(Organic, null, true).ToArray());
        Assert.Empty(ShopAttributes.ReadValues(Organic, null, false));                                                               // an unticked box is "no value"
        Assert.Equal(new[] { "Holz Eiche" }, ShopAttributes.ReadValues(A(4, "material", AttributeKind.Text), "  Holz   Eiche ", true).ToArray());
    }

    [Fact]
    public void An_attribute_applies_everywhere_or_to_one_category_and_what_is_below_it()
    {
        var cats = new[] { new Category { Id = 1, Slug = "kaffee" }, new Category { Id = 2, Slug = "bohnen", ParentId = 1 }, new Category { Id = 3, Slug = "tee" } };
        var scoped = A(9, "roestung", AttributeKind.Choice, "Hell", category: "kaffee");
        Assert.True(ShopAttributes.AppliesTo(Colour, cats, "tee"));
        Assert.True(ShopAttributes.AppliesTo(Colour, cats, ""));
        Assert.True(ShopAttributes.AppliesTo(scoped, cats, "kaffee"));
        Assert.True(ShopAttributes.AppliesTo(scoped, cats, "bohnen"));
        Assert.False(ShopAttributes.AppliesTo(scoped, cats, "tee"));
        Assert.False(ShopAttributes.AppliesTo(scoped, cats, ""));                                                                    // a product without a category has only the global ones
    }

    [Fact]
    public void An_address_names_filters_and_unknown_values_are_dropped()
    {
        var q = new Dictionary<string, string> { ["a.farbe"] = "blau, nope ,Rot", ["a.gewicht.min"] = "1,5", ["a.gewicht.max"] = "x", ["a.bio"] = "ja" };
        var filters = ShopAttributes.ReadFilters(new[] { Colour, Weight, Organic }, k => q.GetValueOrDefault(k, ""));
        Assert.Equal(new[] { "Blau", "Rot" }, filters.Single(f => f.Attr == Colour).Values.ToArray());
        var w = filters.Single(f => f.Attr == Weight);
        Assert.Equal((1.5, (double?)null), (w.Min!.Value, w.Max));
        Assert.Equal(new[] { "1" }, filters.Single(f => f.Attr == Organic).Values.ToArray());
        Assert.Equal("a.farbe=Blau,Rot", ShopAttributes.ToQuery(filters).Select(kv => kv.Key + "=" + kv.Value).First());
        Assert.Empty(ShopAttributes.ReadFilters(new[] { Colour }, _ => ""));
    }

    private static readonly IReadOnlyList<AttrRow> Rows = new[]
    {
        new AttrRow(1, 1, "Rot"), new AttrRow(2, 1, "Blau"), new AttrRow(3, 1, "Rot"), new AttrRow(3, 1, "Blau"), new AttrRow(1, 2, "2"), new AttrRow(2, 2, "10"), new AttrRow(3, 2, "4")
    };

    [Fact]
    public void A_filter_takes_any_value_of_one_attribute_and_all_attributes_together_and_a_number_a_range()
    {
        AttributeFilter F(ShopAttribute a, string[]? v = null, double? min = null, double? max = null) => new(a, v ?? Array.Empty<string>(), min, max);
        var all = new long[] { 1, 2, 3, 4 };
        Assert.Equal(new long[] { 1, 3 }, ShopAttributes.Match(all, new[] { F(Colour, new[] { "Rot" }) }, Rows).OrderBy(x => x).ToArray());
        Assert.Equal(new long[] { 1, 2, 3 }, ShopAttributes.Match(all, new[] { F(Colour, new[] { "rot", "BLAU" }) }, Rows).OrderBy(x => x).ToArray());     // either value
        Assert.Equal(new long[] { 1 }, ShopAttributes.Match(all, new[] { F(Colour, new[] { "Rot" }), F(Weight, max: 3) }, Rows).ToArray());                // both attributes
        Assert.Equal(new long[] { 2, 3 }, ShopAttributes.Match(all, new[] { F(Weight, min: 4) }, Rows).OrderBy(x => x).ToArray());
        Assert.Equal(all.OrderBy(x => x), ShopAttributes.Match(all, Array.Empty<AttributeFilter>(), Rows).OrderBy(x => x));                                  // no filter, no change
    }

    [Fact]
    public void The_facets_count_products_not_rows_in_the_order_of_the_options_and_give_a_number_its_range()
    {
        var f = ShopAttributes.Facets(new[] { Colour, Weight }, Rows);
        Assert.Equal(new[] { ("Rot", 2), ("Blau", 2) }, f[1].Values.ToArray());
        Assert.Equal((2.0, 10.0), (f[2].Min!.Value, f[2].Max!.Value));
        Assert.Empty(f[2].Values);
        Assert.Empty(ShopAttributes.Facets(new[] { Colour }, Array.Empty<AttrRow>())[1].Values);
    }

    [Fact]
    public void A_number_is_shown_with_its_unit_in_the_reader_s_language_and_a_yes_no_as_a_word()
    {
        Assert.Equal("12,5 kg", ShopAttributes.Show(Weight, new[] { "12.5" }, "Ja", "de"));
        Assert.Equal("12.5 kg", ShopAttributes.Show(Weight, new[] { "12.5" }, "Yes", "en"));
        Assert.Equal("Ja", ShopAttributes.Show(Organic, new[] { "1" }, "Ja", "de"));
        Assert.Equal("Rot, Blau", ShopAttributes.Show(Colour, new[] { "Rot", "Blau" }, "Ja", "de"));
    }
}

/// <summary>Attributes in the shop: made in the back office, typed into a product, filtered by on the catalogue, shown on the product page — and the catalogue's own switches.</summary>
public class ShopAttributeSiteTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopAttributeSiteTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<long> Attr(string slug, string name, string kind, string options = "", string unit = "", string category = "", bool filterable = true) =>
        await _shop.WithContextAsync((_, s) => s.SaveAttributeAsync(new ShopAttribute { Slug = slug, Name = name, Kind = kind, Options = options, Unit = unit, Category = category, Filterable = filterable }));

    private async Task<long> Product(string title, params (long Attr, string[] Values)[] values)
    {
        var id = await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title), Title = title, PriceCents = 1000, Active = true }));
        foreach (var (a, v) in values) await _shop.WithContextAsync((_, s) => s.SetProductAttrAsync(id, a, v));
        return id;
    }

    [Fact]
    public async Task The_catalogue_offers_the_attributes_as_filters_with_counts_narrows_by_them_and_the_product_page_shows_its_values()
    {
        var colour = await Attr("ea-farbe", "EA Farbe", AttributeKind.Choice, "EA Rot\nEA Blau");
        var weight = await Attr("ea-gewicht", "EA Gewicht", AttributeKind.Number, unit: "kg");
        await Attr("ea-intern", "EA Intern", AttributeKind.Text, filterable: false);
        await Product("EA Ding Eins", (colour, new[] { "EA Rot" }), (weight, new[] { "2" }));
        await Product("EA Ding Zwei", (colour, new[] { "EA Blau", "EA Rot" }), (weight, new[] { "10" }));
        await Product("EA Ding Drei", (colour, new[] { "EA Blau" }));
        var v = _app.Browser();

        var all = Decode(await v.GetStringAsync("/shop?q=EA+Ding"));
        Assert.Contains("class=\"shop-filters\"", all);
        Assert.Contains("href=\"/shop?a.ea-farbe=EA%20Rot&q=EA%20Ding\"", all);                                          // a value is a link that switches it on
        Assert.Contains("EA Rot <span>2</span>", all);                                                                      // counted over the products at hand
        Assert.Contains("EA Blau <span>2</span>", all);
        Assert.Contains("name=\"a.ea-gewicht.min\"", all);                                                                  // a number is a from–to
        Assert.DoesNotContain("EA Intern", all);                                                                            // not offered as a filter

        var red = Decode(await v.GetStringAsync("/shop?q=EA+Ding&a.ea-farbe=EA+Rot"));
        Assert.Contains("EA Ding Eins", red); Assert.Contains("EA Ding Zwei", red); Assert.DoesNotContain("EA Ding Drei", red);
        Assert.Contains("class=\"on\"", red);                                                                               // the chosen value is lit
        Assert.Contains("Eigenschafts-Filter zurücksetzen", red);

        var light = Decode(await v.GetStringAsync("/shop?q=EA+Ding&a.ea-gewicht.max=5"));
        Assert.Contains("EA Ding Eins", light);
        Assert.DoesNotContain("EA Ding Zwei", light);                                                                       // 10 kg: too heavy
        Assert.DoesNotContain("EA Ding Drei", light);                                                                       // no weight at all: not in a range
        var none = Decode(await v.GetStringAsync("/shop?q=EA+Ding&a.ea-farbe=EA+Blau&a.ea-gewicht.max=5"));
        Assert.DoesNotContain("EA Ding Eins", none);                                                                        // light but not blue
        Assert.DoesNotContain("EA Ding Zwei", none);                                                                        // blue but too heavy
    }

    [Fact]
    public async Task A_product_page_lists_the_attributes_the_product_has_with_unit_and_words()
    {
        var weight = await Attr("pp-gewicht", "PP Gewicht", AttributeKind.Number, unit: "kg");
        var organic = await Attr("pp-bio", "PP Bio", AttributeKind.Bool);
        var colour = await Attr("pp-farbe", "PP Farbe", AttributeKind.Choice, "Rot\nBlau");
        await Product("PP Ding", (weight, new[] { "12.5" }), (organic, new[] { "1" }), (colour, new[] { "Rot", "Blau" }));
        var page = Decode(await _app.Browser().GetStringAsync("/shop/pp-ding"));
        Assert.Contains("<table class=\"shop-attrs\">", page);
        Assert.Contains("<th>PP Gewicht</th><td>12,5 kg</td>", page);
        Assert.Contains("<th>PP Bio</th><td>Ja</td>", page);
        Assert.Contains("<th>PP Farbe</th><td>Rot, Blau</td>", page);
    }

    [Fact]
    public async Task An_attribute_is_made_in_the_back_office_typed_into_a_product_limited_to_a_category_and_deleted_with_its_values()
    {
        var admin = await _shop.AdminAsync();
        async Task<HttpResponseMessage> Post(string url, Dictionary<string, string> fields)
        {
            var page = await admin.GetStringAsync(url);
            fields["__RequestVerificationToken"] = AppFactory.Token(page)!;
            return await admin.PostAsync(url, new FormUrlEncodedContent(fields));
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/admin/p/shop/attributes")).StatusCode);

        // A choice without options is refused, with options it is saved.
        var bad = await Post("/admin/p/shop/attributes/0", new() { ["action"] = "save", ["name"] = "BO Farbe", ["kind"] = "choice", ["options"] = "" });
        Assert.Contains("mindestens eine Möglichkeit", Decode(await bad.Content.ReadAsStringAsync()));
        Assert.Null(await _shop.WithContextAsync((_, s) => s.AttributeBySlugAsync("bo-farbe")));
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/admin/p/shop/attributes/0", new() { ["action"] = "save", ["name"] = "BO Farbe", ["kind"] = "choice", ["options"] = "Rot\nBlau\nrot", ["filterable"] = "on", ["category"] = "", ["sort"] = "0" })).StatusCode);
        var attr = (await _shop.WithContextAsync((_, s) => s.AttributeBySlugAsync("bo-farbe")))!;
        Assert.Equal("Rot\nBlau", attr.Options);                                                                      // cleaned: no repeat
        Assert.True(attr.Filterable);
        Assert.Contains("BO Farbe", Decode(await admin.GetStringAsync("/admin/p/shop/attributes")));

        // The product form has a field for it; what is posted is kept, what is not an option is not.
        var pid = await Product("BO Produkt");
        var form = Decode(await admin.GetStringAsync($"/admin/p/shop/products/{pid}"));
        Assert.Contains($"name=\"attr_{attr.Id}\"", form);
        Assert.Equal(HttpStatusCode.Redirect, (await Post($"/admin/p/shop/products/{pid}", new()
        {
            ["action"] = "save", ["title"] = "BO Produkt", ["price"] = "10,00", ["tax"] = "19", ["kind"] = "physical", ["category"] = "", ["attrs"] = attr.Id.ToString(), [$"attr_{attr.Id}"] = "blau, gelb"
        })).StatusCode);
        Assert.Equal(new[] { "Blau" }, (await _shop.WithContextAsync((_, s) => s.ProductValuesAsync(pid)))[attr.Id].ToArray());

        // A category-limited attribute is not on the form of a product outside that category.
        var cat = await _shop.WithContextAsync((_, s) => s.SaveCategoryAsync(new Category { Slug = "bo-kat", Name = "BO Kat" }));
        var scoped = await Attr("bo-roestung", "BO Röstung", AttributeKind.Choice, "Hell\nDunkel", category: "bo-kat");
        Assert.DoesNotContain($"name=\"attr_{scoped}\"", Decode(await admin.GetStringAsync($"/admin/p/shop/products/{pid}")));

        // Deleting the attribute takes its values with it.
        Assert.Equal(HttpStatusCode.Redirect, (await Post("/admin/p/shop/attributes", new() { ["action"] = "delete", ["id"] = attr.Id.ToString() })).StatusCode);
        Assert.Empty((await _shop.WithContextAsync((_, s) => s.ProductValuesAsync(pid))).Where(kv => kv.Key == attr.Id));
        Assert.Null(await _shop.WithContextAsync((_, s) => s.AttributeAsync(attr.Id)));
    }

    [Fact]
    public async Task The_catalogue_switches_hide_the_search_the_sorting_the_category_chips_and_the_keywords_and_the_search_looks_where_it_is_told()
    {
        var admin = await _shop.AdminAsync();
        await _shop.WithContextAsync((_, s) => s.SaveCategoryAsync(new Category { Slug = "sw-kat", Name = "SW Kat", Tags = "SWTag" }));
        await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "sw-ding", Title = "SW Ding", Summary = "enthält Zimtstern", DescriptionHtml = "<p>und Lebkuchen</p>", PriceCents = 1000, Category = "sw-kat", Tags = "SWKeyword", Active = true }));
        async Task Save(Dictionary<string, string> extra)
        {
            var page = await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupCatalog");
            var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save-settings" };
            foreach (var kv in extra) fields[kv.Key] = kv.Value;
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/admin/p/shop/_settings?group=set.groupCatalog", new FormUrlEncodedContent(fields))).StatusCode);
        }
        var on = new Dictionary<string, string>
        {
            ["s_catalog.showSearch"] = "true", ["s_catalog.showSort"] = "true", ["s_catalog.showCategories"] = "true", ["s_catalog.showTags"] = "true", ["s_catalog.showFilters"] = "true", ["s_catalog.searchIn"] = "text"
        };
        var v = _app.Browser();
        try
        {
            var normal = Decode(await v.GetStringAsync("/shop"));
            Assert.Contains("class=\"shop-find\"", normal); Assert.Contains("name=\"sort\"", normal); Assert.Contains("class=\"shop-catnav\"", normal); Assert.Contains("aria-label=\"Schlagwörter\"", normal);

            // Everything off: nothing of it above the products.
            await Save(new Dictionary<string, string> { ["s_catalog.searchIn"] = "text" });
            var bare = Decode(await v.GetStringAsync("/shop"));
            Assert.DoesNotContain("class=\"shop-find\"", bare); Assert.DoesNotContain("name=\"sort\"", bare); Assert.DoesNotContain("class=\"shop-catnav\"", bare); Assert.DoesNotContain("aria-label=\"Schlagwörter\"", bare);
            Assert.Contains("SW Ding", bare);

            // The search: by name only, by name + summary + keywords (the default), by everything (the description too).
            await Save(new Dictionary<string, string>(on) { ["s_catalog.searchIn"] = "title" });
            Assert.DoesNotContain("SW Ding", Decode(await v.GetStringAsync("/shop?q=Zimtstern")));
            Assert.Contains("SW Ding", Decode(await v.GetStringAsync("/shop?q=SW+Ding")));
            await Save(new Dictionary<string, string>(on) { ["s_catalog.searchIn"] = "text" });
            Assert.Contains("SW Ding", Decode(await v.GetStringAsync("/shop?q=Zimtstern")));
            Assert.DoesNotContain("SW Ding", Decode(await v.GetStringAsync("/shop?q=Lebkuchen")));
            await Save(new Dictionary<string, string>(on) { ["s_catalog.searchIn"] = "all" });
            Assert.Contains("SW Ding", Decode(await v.GetStringAsync("/shop?q=Lebkuchen")));

            // The page of all categories: the keyword links, the product count, the text and the subcategories of a card are switches too.
            Assert.Contains("href=\"/shop/categories?tag=SWTag\"", Decode(await v.GetStringAsync("/shop/categories")));
            var catPage = new Dictionary<string, string>(on) { ["s_catpage.tags"] = "", ["s_catpage.counts"] = "", ["s_catpage.desc"] = "", ["s_catpage.subs"] = "" };
            var page = await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupCatPage");
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/admin/p/shop/_settings?group=set.groupCatPage", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save-settings" }))).StatusCode);
            var hidden = Decode(await v.GetStringAsync("/shop/categories"));
            Assert.DoesNotContain("href=\"/shop/categories?tag=SWTag\"", hidden);
            Assert.DoesNotContain("class=\"count\"", hidden);
            Assert.Contains("<h3><a href=\"/shop/categories/sw-kat\">", hidden);
        }
        finally
        {
            await Save(on);
            var page = await admin.GetStringAsync("/admin/p/shop/_settings?group=set.groupCatPage");
            await admin.PostAsync("/admin/p/shop/_settings?group=set.groupCatPage", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save-settings", ["s_catpage.tags"] = "true", ["s_catpage.counts"] = "true", ["s_catpage.desc"] = "true", ["s_catpage.subs"] = "true"
            }));
        }
    }
}
