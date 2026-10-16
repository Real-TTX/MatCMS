using System.Net;
using System.Text.Json;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The rule of an automatic collection and which categories a product belongs to.</summary>
public class ShopCollectionUnitTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static Product P(Func<Product, Product>? f = null) => (f ?? (p => p))(new Product { Id = 1, Slug = "p", Title = "P", PriceCents = 1500, CreatedUtc = Now.AddDays(-3), Tags = "Neuware, Bio" });

    [Fact]
    public void A_rule_keeps_only_valid_clauses_in_a_normal_spelling()
    {
        Assert.Equal("tag:Neuware; sale; new:30; price<20", ShopCollectionRule.Clean("  TAG: Neuware ;SALE; new:30 ; Price<20,00 ; unfug ; price<abc ; new:0 ; new:99999 ; ;"));
        Assert.Equal("", ShopCollectionRule.Clean("alles; nichts"));
        Assert.Equal(5, ShopCollectionRule.Clean("sale;featured;instock;new:1;new:2;new:3;new:4").Split(';').Length);   // at most five
        Assert.False(ShopCollectionRule.Any(""));
    }

    [Fact]
    public void Every_clause_must_hold_and_a_rule_without_a_valid_clause_picks_nothing()
    {
        Assert.True(ShopCollectionRule.Matches("tag:neuware", P(), Now));                                         // any case
        Assert.False(ShopCollectionRule.Matches("tag:Gebraucht", P(), Now));
        Assert.True(ShopCollectionRule.Matches("new:7", P(), Now)); Assert.False(ShopCollectionRule.Matches("new:2", P(), Now));
        Assert.True(ShopCollectionRule.Matches("price<20", P(), Now)); Assert.False(ShopCollectionRule.Matches("price<15", P(), Now)); Assert.True(ShopCollectionRule.Matches("price>10", P(), Now));
        Assert.False(ShopCollectionRule.Matches("sale", P(), Now)); Assert.True(ShopCollectionRule.Matches("sale", P(p => p with { CompareCents = 2000 }), Now));
        Assert.True(ShopCollectionRule.Matches("featured", P(p => p with { Featured = true }), Now));
        Assert.False(ShopCollectionRule.Matches("instock", P(p => p with { TrackStock = true, Stock = 0 }), Now)); Assert.True(ShopCollectionRule.Matches("instock", P(), Now));
        Assert.True(ShopCollectionRule.Matches("tag:Neuware; price<20; new:7", P(), Now));
        Assert.False(ShopCollectionRule.Matches("tag:Neuware; price<10", P(), Now));                                // one clause fails
        Assert.False(ShopCollectionRule.Matches("", P(), Now)); Assert.False(ShopCollectionRule.Matches("unfug", P(), Now));
    }

    [Fact]
    public void A_product_counts_once_per_category_by_its_main_category_a_further_one_or_a_rule_and_each_category_counts_what_is_below_it()
    {
        var cats = new[]
        {
            new Category { Id = 1, Slug = "kaffee", Name = "Kaffee" }, new Category { Id = 2, Slug = "bohnen", Name = "Bohnen", ParentId = 1 },
            new Category { Id = 3, Slug = "geschenke", Name = "Geschenke" }, new Category { Id = 4, Slug = "neu", Name = "Neu", Rule = "tag:Neuware" }, new Category { Id = 5, Slug = "leer", Name = "Leer" }
        };
        var products = new[]
        {
            P(p => p with { Id = 1, Category = "bohnen", ExtraCategories = "geschenke" }),              // main below kaffee, also in geschenke, tag Neuware → in neu by rule
            P(p => p with { Id = 2, Category = "kaffee", ExtraCategories = "bohnen", Tags = "" }),      // main and further both under kaffee: once
            P(p => p with { Id = 3, Category = "", Tags = "" })
        };
        var c = ShopMembership.Counts(cats, products, Now);
        Assert.Equal(2, c["kaffee"]); Assert.Equal(2, c["bohnen"]); Assert.Equal(1, c["geschenke"]); Assert.Equal(1, c["neu"]);
        Assert.False(c.ContainsKey("leer"));
        Assert.Equal(new[] { "a", "b" }, ShopMembership.SplitSlugs("a, b;a"));
    }
}

/// <summary>Shop package A and B in the shop itself: copying, moved addresses, search-engine fields, further categories, collections, languages.</summary>
public class ShopPackageTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopPackageTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private Dictionary<string, string> Form(string token, string title, string price = "10,00", params (string, string)[] more)
    {
        var d = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["action"] = "save", ["title"] = title, ["slug"] = "", ["kind"] = "physical", ["price"] = price, ["tax"] = "19", ["sku"] = "", ["category"] = "",
            ["summary"] = "", ["description"] = "", ["image"] = "", ["gallery"] = "[]", ["stock"] = "0", ["weight"] = "0", ["max"] = "0", ["sort"] = "0", ["active"] = "true", ["variants.count"] = "0"
        };
        foreach (var (k, v) in more) d[k] = v;
        return d;
    }

    private async Task<(long Id, string Location)> CreateAsync(HttpClient admin, string title, params (string, string)[] more)
    {
        var form = await admin.GetStringAsync("/admin/p/shop/products/0");
        var res = await admin.PostAsync("/admin/p/shop/products/0", new FormUrlEncodedContent(Form(AppFactory.Token(form)!, title, "10,00", more)));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loc = res.Headers.Location!.OriginalString;
        return (long.Parse(loc[(loc.LastIndexOf('/') + 1)..]), loc);
    }

    [Fact]
    public async Task A_changed_address_keeps_the_old_one_working_with_a_permanent_redirect_until_another_product_takes_it()
    {
        var admin = await _shop.AdminAsync();
        var (id, location) = await CreateAsync(admin, "Rd Alt", ("slug", "rd-alt"));
        var v = _app.Browser();
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/shop/rd-alt")).StatusCode);

        var page = await admin.GetStringAsync(location);
        var moved = await admin.PostAsync(location, new FormUrlEncodedContent(Form(AppFactory.Token(page)!, "Rd Neu", "10,00", ("slug", "rd-neu"))));
        Assert.Equal(HttpStatusCode.Redirect, moved.StatusCode);
        var old = await v.GetAsync("/shop/rd-alt");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal("/shop/rd-neu", old.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/shop/rd-neu")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/shop/gibt-es-nie")).StatusCode);

        // A second change keeps BOTH old addresses pointing at the product, and an address another product now uses is that product's again.
        var page2 = await admin.GetStringAsync(location);
        await admin.PostAsync(location, new FormUrlEncodedContent(Form(AppFactory.Token(page2)!, "Rd Neu", "10,00", ("slug", "rd-neuer"))));
        Assert.Equal("/shop/rd-neuer", (await v.GetAsync("/shop/rd-alt")).Headers.Location!.OriginalString);
        Assert.Equal("/shop/rd-neuer", (await v.GetAsync("/shop/rd-neu")).Headers.Location!.OriginalString);
        await CreateAsync(admin, "Rd Andere", ("slug", "rd-alt"));
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/shop/rd-alt")).StatusCode);
    }

    [Fact]
    public async Task A_product_is_copied_switched_off_with_its_variants_and_a_free_address()
    {
        var admin = await _shop.AdminAsync();
        var (id, location) = await CreateAsync(admin, "Kp Original", ("slug", "kp-original"), ("variants.count", "2"),
            ("variants.0.name", "S"), ("variants.0.delta", ""), ("variants.0.stock", "4"), ("variants.0.sku", "KP-S"), ("variants.1.name", "M"), ("variants.1.delta", "+1,50"), ("variants.1.image", "/uploads/m.png"));
        var page = await admin.GetStringAsync(location);
        var res = await admin.PostAsync(location, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "duplicate" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var copyId = long.Parse(res.Headers.Location!.OriginalString.Split('/').Last());
        Assert.NotEqual(id, copyId);
        var copy = await _shop.ProductAsync(copyId);
        Assert.Equal("Kp Original (Kopie)", copy.Title); Assert.Equal("kp-original-kopie", copy.Slug); Assert.False(copy.Active);
        var variants = await _shop.WithContextAsync((_, s) => s.VariantsAsync(copyId, true));
        Assert.Equal(new[] { "S", "M" }, variants.Select(x => x.Name));
        Assert.Equal("/uploads/m.png", variants[1].Image); Assert.Equal(150, variants[1].PriceDeltaCents);
        Assert.Equal("", variants[0].Sku);                                                                          // article numbers are not copied (they would be twice)
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Browser().GetAsync("/shop/kp-original-kopie")).StatusCode);   // off: not in the shop

        // And again: a second copy gets its own address.
        var page2 = await admin.GetStringAsync(location);
        var res2 = await admin.PostAsync(location, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page2)!, ["action"] = "duplicate" }));
        Assert.Equal("kp-original-kopie-2", (await _shop.ProductAsync(long.Parse(res2.Headers.Location!.OriginalString.Split('/').Last()))).Slug);
    }

    [Fact]
    public async Task The_search_engine_title_and_description_and_the_share_tags_come_from_the_product()
    {
        var admin = await _shop.AdminAsync();
        await CreateAsync(admin, "Se Ding", ("slug", "se-ding"), ("summary", "Kurzer Text"), ("image", "/uploads/se.png"), ("seo_title", "Das beste Ding – Shop"), ("seo_description", "Warum man es kaufen sollte."));
        await CreateAsync(admin, "Se Ohne", ("slug", "se-ohne"), ("summary", "Nur ein Kurztext"));
        var v = _app.Browser();
        var own = Decode(await v.GetStringAsync("/shop/se-ding"));
        Assert.Contains("<title>Das beste Ding – Shop", own);
        Assert.Contains("name=\"description\" content=\"Warum man es kaufen sollte.\"", own);
        Assert.Contains("property=\"og:title\" content=\"Das beste Ding – Shop\"", own);
        Assert.Contains("property=\"og:type\" content=\"product\"", own);
        Assert.Contains("property=\"og:image\" content=\"http", own);                                               // an absolute address, a share needs one
        Assert.Contains("<h1", own); Assert.Contains("Se Ding", own);                                                // the page itself still says the product's name
        var plain = Decode(await v.GetStringAsync("/shop/se-ohne"));
        Assert.Contains("<title>Se Ohne", plain);
        Assert.Contains("content=\"Nur ein Kurztext\"", plain);
    }

    [Fact]
    public async Task A_product_in_further_categories_and_an_automatic_collection_show_up_where_they_belong_with_counts()
    {
        await _shop.WithContextAsync(async (_, s) =>
        {
            var kaffee = await s.AddCategoryAsync("fk-kaffee", "Fk Kaffee", 0);
            await s.AddCategoryAsync("fk-geschenk", "Fk Geschenk", 1);
            var neu = await s.AddCategoryAsync("fk-neu", "Fk Neu", 2);
            await s.SaveCategoryAsync(new Category { Id = neu, Slug = "fk-neu", Name = "Fk Neu", Rule = "tag:FkNeuware; price<20" });
            await s.SaveProductAsync(new Product { Slug = "fk-bohne", Title = "Fk Bohne", PriceCents = 900, Active = true, Category = "fk-kaffee", ExtraCategories = "fk-geschenk", Tags = "FkNeuware" });
            await s.SaveProductAsync(new Product { Slug = "fk-teuer", Title = "Fk Teuer", PriceCents = 5000, Active = true, Category = "fk-kaffee", Tags = "FkNeuware" });
            await s.SaveProductAsync(new Product { Slug = "fk-tee", Title = "Fk Tee", PriceCents = 700, Active = true, Category = "", Tags = "" });
        });
        var v = _app.Browser();
        var geschenk = Decode(await v.GetStringAsync("/shop/categories/fk-geschenk"));
        Assert.Contains("Fk Bohne", geschenk); Assert.DoesNotContain("Fk Teuer", geschenk); Assert.DoesNotContain("Fk Tee", geschenk);
        var neu = Decode(await v.GetStringAsync("/shop/categories/fk-neu"));
        Assert.Contains("Fk Bohne", neu);                                                                            // meets both clauses
        Assert.DoesNotContain("Fk Teuer", neu);                                                                      // tag yes, price no
        Assert.DoesNotContain("Fk Tee", neu);                                                                        // price yes, tag no
        var kaffee = Decode(await v.GetStringAsync("/shop/categories/fk-kaffee"));
        Assert.Contains("Fk Bohne", kaffee); Assert.Contains("Fk Teuer", kaffee);
        Assert.Contains("Fk Geschenk <span>1</span>", Decode(await v.GetStringAsync("/shop")));                       // the category list counts the further one
        Assert.Contains("Fk Neu <span>1</span>", Decode(await v.GetStringAsync("/shop")));                            // and the collection's own count

        // The form offers the further categories and saves them; a deleted category leaves them.
        var admin = await _shop.AdminAsync();
        var (id, location) = await CreateAsync(admin, "Fk Form", ("slug", "fk-form"), ("category", "fk-kaffee"), ("xcat_fk-geschenk", "true"), ("xcat_fk-kaffee", "true"));
        Assert.Equal("fk-geschenk", (await _shop.ProductAsync(id)).ExtraCategories);                                  // the main category is never also a further one
        Assert.Contains("name=\"xcat_fk-geschenk\"", await admin.GetStringAsync(location));
        await _shop.WithContextAsync(async (_, s) => await s.DeleteCategoryAsync((await s.CategoryBySlugAsync("fk-geschenk"))!.Id));
        Assert.Equal("", (await _shop.ProductAsync(id)).ExtraCategories);
    }

    [Fact]
    public async Task Product_and_category_words_come_in_the_visitors_language_where_a_translation_exists_and_the_main_language_elsewhere()
    {
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == MatCMS.Services.SettingKeys.Languages);
            if (row is null) db.SiteSettings.Add(new SiteSetting { Key = MatCMS.Services.SettingKeys.Languages, Value = "de,en" }); else row.Value = "de,en";
            await db.SaveChangesAsync();
        }
        var admin = await _shop.AdminAsync();
        var form = await admin.GetStringAsync("/admin/p/shop/products/0");
        Assert.Contains("name=\"tr_en_title\"", form);                                                               // the form has a block for English
        var (id, location) = await CreateAsync(admin, "Tr Kaffee", ("slug", "tr-kaffee"), ("summary", "Frisch geröstet"),
            ("tr_en_title", "Tr Coffee"), ("tr_en_summary", "Freshly roasted"), ("tr_en_description", "<p>Roasted <script>x()</script>slowly</p>"), ("tr_en_seo_title", "Buy Tr Coffee"));
        await CreateAsync(admin, "Tr Tee", ("slug", "tr-tee"));

        var v = _app.Browser();
        var de = Decode(await v.GetStringAsync("/shop/tr-kaffee"));
        Assert.Contains("Tr Kaffee", de); Assert.DoesNotContain("Tr Coffee", de);
        var en = Decode(await v.GetStringAsync("/en/shop/tr-kaffee"));
        Assert.Contains("Tr Coffee", en); Assert.Contains("Freshly roasted", en); Assert.Contains("<title>Buy Tr Coffee", en);
        Assert.Contains("Roasted", en); Assert.DoesNotContain("<script>x()", en);                                    // the translated description is cleaned like the main one
        Assert.Contains("Tr Tee", Decode(await v.GetStringAsync("/en/shop/tr-tee")));                                // no translation: the main language's words
        Assert.Contains("Tr Coffee", Decode(await v.GetStringAsync("/en/shop?q=Tr")));                               // lists too
        Assert.Contains("Tr Kaffee", Decode(await v.GetStringAsync("/shop?q=Tr")));

        // The form shows what was saved; clearing the fields removes the translation.
        Assert.Contains("value=\"Tr Coffee\"", Decode(await admin.GetStringAsync(location)));
        var page = await admin.GetStringAsync(location);
        await admin.PostAsync(location, new FormUrlEncodedContent(Form(AppFactory.Token(page)!, "Tr Kaffee", "10,00", ("slug", "tr-kaffee"), ("tr_en_title", ""), ("tr_en_summary", ""), ("tr_en_description", ""), ("tr_en_seo_title", ""))));
        Assert.Empty(await _shop.WithContextAsync((_, s) => s.TranslationsOfAsync("product", id)));
        Assert.Contains("Tr Kaffee", Decode(await v.GetStringAsync("/en/shop/tr-kaffee")));
    }
}
