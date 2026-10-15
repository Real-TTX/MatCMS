using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

public class ShopTagsTests
{
    [Fact]
    public void Keywords_are_split_kept_as_typed_and_listed_once()
    {
        Assert.Equal(new[] { "Bio", "Fair Trade", "geschenk" }, ShopTags.Split(" Bio, Fair   Trade ;geschenk,\nbio ,, Geschenk "));
        Assert.Equal("Bio, Fair Trade", ShopTags.Normalise("Bio,Fair Trade,BIO"));
        Assert.Empty(ShopTags.Split(null));
        Assert.Empty(ShopTags.Split(" , ;; "));
    }

    [Fact]
    public void A_keyword_is_never_longer_than_the_limit_and_a_product_never_has_too_many()
    {
        Assert.Equal(ShopTags.MaxLength, ShopTags.Split(new string('x', 100)).Single().Length);
        Assert.Equal(ShopTags.MaxPerProduct, ShopTags.Split(string.Join(",", Enumerable.Range(0, 60).Select(i => "t" + i))).Count);
    }

    [Fact]
    public void Only_known_sort_orders_are_accepted()
    {
        Assert.Equal("price_asc", ProductQuery.NormaliseSort("price_asc"));
        Assert.Equal("", ProductQuery.NormaliseSort("DROP TABLE"));
        Assert.Equal("", ProductQuery.NormaliseSort(null));
    }

    [Fact]
    public void A_block_selection_is_read_as_ids_and_nothing_else()
    {
        Assert.Equal(new long[] { 3, 7, 12 }, ShopBlocks.ParseIds("3, 7,12, 7"));
        Assert.Empty(ShopBlocks.ParseIds("abc, -1, 0"));
    }
}

/// <summary>What a customer and an editor find: the catalogue's filters and order, the page blocks, the lists the editor offers, related products.</summary>
public class ShopCatalogTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopCatalogTests(AppFactory app) { _app = app; PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<long> Add(string title, long cents, string category = "", string tags = "", bool featured = false, bool active = true, string summary = "") =>
        _shop.WithContextAsync(async (_, store) =>
        {
            if (category.Length > 0 && await store.CategoryBySlugAsync(category) is null) await store.AddCategoryAsync(category, char.ToUpper(category[0]) + category[1..], 0);
            return await store.SaveProductAsync(new Product
            {
                Slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title), Title = title, PriceCents = cents, Category = category, Tags = tags, Featured = featured, Active = active, Summary = summary
            });
        });

    private async Task<string> Block(string type, string json)
    {
        using var scope = _app.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<PluginRegistry>();
        var block = registry.Blocks.First(b => b.Type == type);
        return await block.RenderAsync!(new PluginRequest { Services = scope.ServiceProvider, Registry = registry, Data = json, Path = "/seite", Query = new Dictionary<string, string>() }, default);
    }

    private async Task<string> Page(string url) => WebUtility.HtmlDecode(await _app.Browser().GetStringAsync(url));

    // ---- the catalogue ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_keyword_filters_exactly_and_a_longer_word_that_contains_it_does_not_match()
    {
        await Add("Kt Bio-Tee", 500, tags: "Kt-Bio, Tee");
        await Add("Kt Biodynamischer Wein", 900, tags: "Kt-Biodynamisch");
        var page = await Page("/shop?tag=kt-bio");                                       // case does not matter
        Assert.Contains("Kt Bio-Tee", page);
        Assert.DoesNotContain("Kt Biodynamischer Wein", page);
    }

    [Fact]
    public async Task The_catalogue_sorts_by_price_and_by_name_and_ignores_an_unknown_order()
    {
        await Add("Ks Zebra", 3000, tags: "ks-sort"); await Add("Ks Apfel", 1000, tags: "ks-sort"); await Add("Ks Mitte", 2000, tags: "ks-sort");
        static int At(string html, string s) => html.IndexOf(s, StringComparison.Ordinal);
        var asc = await Page("/shop?tag=ks-sort&sort=price_asc");
        Assert.True(At(asc, "Ks Apfel") < At(asc, "Ks Mitte") && At(asc, "Ks Mitte") < At(asc, "Ks Zebra"));
        var desc = await Page("/shop?tag=ks-sort&sort=price_desc");
        Assert.True(At(desc, "Ks Zebra") < At(desc, "Ks Mitte") && At(desc, "Ks Mitte") < At(desc, "Ks Apfel"));
        var name = await Page("/shop?tag=ks-sort&sort=name");
        Assert.True(At(name, "Ks Apfel") < At(name, "Ks Mitte") && At(name, "Ks Mitte") < At(name, "Ks Zebra"));
        Assert.Contains("Ks Apfel", await Page("/shop?tag=ks-sort&sort=%27%3Bdrop"));   // nothing breaks
    }

    [Fact]
    public async Task The_catalogue_shows_the_category_it_is_about_counts_and_a_way_back()
    {
        await Add("Kc Eins", 100, category: "kc-sachen"); await Add("Kc Zwei", 200, category: "kc-sachen"); await Add("Kc Weg", 300, category: "kc-sachen", active: false);
        var page = await Page("/shop?cat=kc-sachen");
        Assert.Contains("<h1>Kc-sachen</h1>", page);
        Assert.Contains("2 Produkte", page);                                              // the hidden one is not counted
        Assert.Contains("Filter zurücksetzen", page);
        Assert.DoesNotContain("Kc Weg", page);
        Assert.Contains("Kc-sachen <span class=\"shop-muted\">2</span>", page);
    }

    [Fact]
    public async Task A_filter_with_no_match_says_so_and_an_empty_category_is_not_offered()
    {
        await _shop.WithContextAsync(async (_, s) => { if (await s.CategoryBySlugAsync("kn-leer") is null) await s.AddCategoryAsync("kn-leer", "Kn Leer", 0); });
        Assert.Contains("Dazu haben wir nichts gefunden.", await Page("/shop?q=gibt-es-garantiert-nicht-xyz"));
        Assert.DoesNotContain("Kn Leer", await Page("/shop"));
    }

    [Fact]
    public async Task A_product_page_shows_its_keywords_its_category_and_related_products_but_not_itself()
    {
        await Add("Kr Haupt", 1000, category: "kr-gruppe", tags: "kr-wort");
        await Add("Kr Gleiche Kategorie", 1100, category: "kr-gruppe");
        await Add("Kr Gleiches Wort", 1200, tags: "kr-wort");
        await Add("Kr Fremd", 1300, category: "kr-andere");
        var page = await Page("/shop/kr-haupt");
        Assert.Contains("href=\"/shop?tag=kr-wort\"", page);
        Assert.Contains("href=\"/shop?cat=kr-gruppe\"", page);                           // the breadcrumb
        Assert.Contains("Ähnliche Produkte", page);
        Assert.Contains("Kr Gleiche Kategorie", page);
        Assert.Contains("Kr Gleiches Wort", page);
        Assert.DoesNotContain("Kr Fremd", page);
    }

    // ---- the page blocks -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_products_block_takes_a_category_a_keyword_the_highlighted_or_a_selection()
    {
        var a = await Add("Kb Alpha", 100, category: "kb-gruppe", tags: "kb-wort");
        var b = await Add("Kb Beta", 200, category: "kb-gruppe", featured: true);
        var c = await Add("Kb Gamma", 300, tags: "kb-wort", featured: true);
        await Add("Kb Delta", 400);

        var byCategory = await Block("shop_products", """{"source":"category","category":"kb-gruppe"}""");
        Assert.Contains("Kb Alpha", byCategory); Assert.Contains("Kb Beta", byCategory); Assert.DoesNotContain("Kb Gamma", byCategory);

        var byTag = await Block("shop_products", """{"source":"tag","tag":"kb-wort"}""");
        Assert.Contains("Kb Alpha", byTag); Assert.Contains("Kb Gamma", byTag); Assert.DoesNotContain("Kb Beta", byTag);

        var featured = await Block("shop_products", """{"source":"featured","limit":"48"}""");
        Assert.Contains("Kb Beta", featured); Assert.Contains("Kb Gamma", featured); Assert.DoesNotContain("Kb Alpha", featured); Assert.DoesNotContain("Kb Delta", featured);

        var picked = await Block("shop_products", $$"""{"source":"pick","products":"{{c}}, {{a}}"}""");
        Assert.Contains("Kb Alpha", picked); Assert.Contains("Kb Gamma", picked); Assert.DoesNotContain("Kb Beta", picked);
        Assert.True(picked.IndexOf("Kb Gamma", StringComparison.Ordinal) < picked.IndexOf("Kb Alpha", StringComparison.Ordinal));   // the order of the selection

        Assert.Equal("", await Block("shop_products", """{"source":"pick","products":""}"""));    // nothing chosen, nothing shown
        Assert.Equal("", await Block("shop_products", """{"source":"tag","tag":"kb-gibt-es-nicht"}"""));
        _ = b;
    }

    [Fact]
    public async Task A_block_saved_before_the_choices_existed_still_shows_its_category()
    {
        await Add("Kl Alt", 100, category: "kl-gruppe"); await Add("Kl Anders", 200);
        var html = await Block("shop_products", """{"category":"kl-gruppe","limit":"4"}""");
        Assert.Contains("Kl Alt", html); Assert.DoesNotContain("Kl Anders", html);
    }

    [Fact]
    public async Task The_products_block_follows_the_layout_columns_order_and_what_to_show()
    {
        await Add("Ko Teuer", 5000, tags: "ko-wort", summary: "Eine Beschreibung"); await Add("Ko Billig", 100, tags: "ko-wort", summary: "Eine Beschreibung");
        var grid = await Block("shop_products", """{"source":"tag","tag":"ko-wort","columns":"3","sort":"price_asc","heading":"Unsere <Auswahl>","allLink":"yes"}""");
        Assert.Contains("<div class=\"shop-grid cols-3\">", grid);
        Assert.True(grid.IndexOf("Ko Billig", StringComparison.Ordinal) < grid.IndexOf("Ko Teuer", StringComparison.Ordinal));
        Assert.Contains("<h2>Unsere &lt;Auswahl&gt;</h2>", grid);                       // encoded
        Assert.Contains("href=\"/shop?tag=ko-wort\"", grid);                              // the link carries the filter
        Assert.Contains("Eine Beschreibung", grid);

        var list = await Block("shop_products", """{"source":"tag","tag":"ko-wort","layout":"list","showSummary":"no","showPrice":"no"}""");
        Assert.Contains("<div class=\"shop-list\">", list);
        Assert.DoesNotContain("Eine Beschreibung", list);
        Assert.DoesNotContain("class=\"shop-price\"", list);                                    // the stylesheet names it; no card uses it
        Assert.DoesNotContain("href=\"/shop?tag=", list);                                  // no link unless asked
    }

    [Fact]
    public async Task The_single_product_block_shows_one_product_large_and_nothing_for_a_hidden_or_missing_one()
    {
        var id = await Add("Ke Einzeln", 1990, summary: "Nur dieses eine");
        var html = await Block("shop_product", $$"""{"product":"{{id}}","button":"Jetzt ansehen"}""");
        Assert.Contains("class=\"shop-feature\"", html);
        Assert.Contains("Ke Einzeln", html); Assert.Contains("Nur dieses eine", html); Assert.Contains("19,90", html);
        Assert.Contains("Jetzt ansehen", html);
        Assert.Contains("href=\"/shop/ke-einzeln\"", html);
        var hidden = await Add("Ke Versteckt", 100, active: false);
        Assert.Equal("", await Block("shop_product", $$"""{"product":"{{hidden}}"}"""));
        Assert.Equal("", await Block("shop_product", """{"product":"999999"}"""));
        Assert.Equal("", await Block("shop_product", """{}"""));
    }

    // ---- what the editor offers ------------------------------------------------------------------------------

    [Fact]
    public async Task The_editor_offers_the_shops_categories_keywords_and_products_and_the_block_speaks_german()
    {
        var id = await Add("Kd Auswahl", 100, category: "kd-gruppe", tags: "kd-wort, kd-zwei");
        await Add("Kd Zweites", 100, tags: "kd-wort");
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var registry = sp.GetRequiredService<PluginRegistry>();
        var defs = sp.GetRequiredService<BlockRegistry>().All.Where(d => d.Type is "shop_products" or "shop_product").ToList();
        Assert.Equal(2, defs.Count);

        var sources = await BlockSchema.LoadSourcesAsync(sp.GetRequiredService<AppDbContext>(), defs, sp.GetRequiredService<PluginContributions>(), sp);
        Assert.Contains(sources["plugin:shop:categories"], o => o.Value == "kd-gruppe" && o.Label == "Kd-gruppe");
        Assert.Contains(sources["plugin:shop:tags"], o => o.Value == "kd-wort" && o.Label == "kd-wort (2)");
        Assert.Contains(sources["plugin:shop:tags"], o => o.Value == "kd-zwei" && o.Label == "kd-zwei (1)");
        Assert.Contains(sources["plugin:shop:products"], o => o.Value == id.ToString() && o.Label == "Kd Auswahl");

        // The schema the editor reads: real wording (not "shop.block.name"), the choices filled in, the conditional fields.
        var t = sp.GetRequiredService<Localizer>();
        var def = defs.First(d => d.Type == "shop_products");
        Assert.NotEqual("shop.block.name", t[def.Name]);
        var schema = System.Text.Json.JsonSerializer.Serialize(BlockSchema.For(def, t, sources), new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.DoesNotContain("shop.block.", schema);
        Assert.Contains("kd-wort (2)", schema);
        Assert.Contains("\"showWhen\":{\"field\":\"source\",\"value\":\"tag\"}", schema);
        Assert.Contains("\"type\":\"MultiSelect\"", schema);
    }

    [Fact]
    public async Task A_plugin_that_is_off_offers_no_choices_and_the_editor_does_not_fail()
    {
        using var scope = _app.Services.CreateScope();
        var contributions = scope.ServiceProvider.GetRequiredService<PluginContributions>();
        Assert.Empty(await contributions.OptionsAsync(scope.ServiceProvider, "plugin:nobody:categories"));
        Assert.Empty(await contributions.OptionsAsync(scope.ServiceProvider, "plugin:shop:nope"));
        Assert.Empty(await contributions.OptionsAsync(scope.ServiceProvider, "garbage"));
        await Task.CompletedTask;
    }

    // ---- the back office and the agent -----------------------------------------------------------------------

    [Fact]
    public async Task The_admin_saves_keywords_and_the_highlight_and_the_agent_can_set_them_too()
    {
        var admin = await _shop.AdminAsync();
        var form = await admin.GetStringAsync("/admin/p/shop/products/0");
        Assert.Contains("name=\"tags\"", form);
        Assert.Contains("name=\"featured\"", form);
        var res = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(form)!, ["action"] = "save", ["title"] = "Ka Verwaltet", ["slug"] = "", ["kind"] = "physical", ["price"] = "9,90", ["tax"] = "19",
            ["tags"] = "Ka-Eins, ka-eins, Ka Zwei", ["featured"] = "true", ["active"] = "true", ["gallery"] = "[]", ["variants"] = "", ["stock"] = "0", ["weight"] = "0", ["max"] = "0", ["sort"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var saved = (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("ka-verwaltet")))!;
        Assert.Equal("Ka-Eins, Ka Zwei", saved.Tags);
        Assert.True(saved.Featured);

        using var scope = _app.Services.CreateScope();
        var answer = await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, "product.create",
            JsonNode.Parse("""{"title":"Ka Per Agent","price_cents":1500,"category":"Ka Neue Gruppe","tags":"Agent, Bio","featured":true}"""));
        Assert.Equal("applied", answer!["status"]!.GetValue<string>());
        var made = (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("ka-per-agent")))!;
        Assert.Equal("ka-neue-gruppe", made.Category);
        Assert.Equal("Agent, Bio", made.Tags);
        Assert.True(made.Featured);
        Assert.NotNull(await _shop.WithContextAsync((_, s) => s.CategoryBySlugAsync("ka-neue-gruppe")));   // made because it was new
    }
}
