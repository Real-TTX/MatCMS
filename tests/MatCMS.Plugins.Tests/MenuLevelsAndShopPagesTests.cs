using System.Net;
using MatCMS.Content;
using MatCMS.Models;
using MatCMS.Plugin.Shop;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>How deep a menu may go and which parents the editor offers.</summary>
public class MenuLevelsLogicTests
{
    private static MenuItem I(int id, int? parent, int sort = 0) => new() { Id = id, ParentId = parent, SortOrder = sort, Label = "i" + id, Url = "/" + id, Menu = "header", Locale = "de" };

    [Fact]
    public void A_parent_may_be_at_any_level_as_long_as_the_item_and_what_hangs_below_it_stay_within_four_levels()
    {
        var items = new[] { I(1, null), I(2, 1), I(3, 2), I(4, 3), I(5, null, 1) };
        // A new leaf: under levels one to three, not under the fourth (that would be a fifth level).
        Assert.Equal(new[] { "1:0", "2:1", "3:2", "5:0" }, MenuTree.ParentChoices(items).Select(c => $"{c.Item.Id}:{c.Depth}").ToArray());
        // Item 2 with its two levels below it (3, 4) is three levels high: only a top-level item can take it, and never itself or its own descendants.
        Assert.Equal(new[] { "1:0", "5:0" }, MenuTree.ParentChoices(items, 2).Select(c => $"{c.Item.Id}:{c.Depth}").ToArray());
        // A leaf at the bottom can move up under anything that is not itself.
        Assert.DoesNotContain(MenuTree.ParentChoices(items, 4), c => c.Item.Id == 4);
    }
}

/// <summary>The category pages and the multi-level menu in the real application.</summary>
public class MenuLevelsAndShopPagesTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public MenuLevelsAndShopPagesTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private Task<long> Category(string slug, string name, long parent = 0, string tags = "") =>
        _shop.WithContextAsync((_, s) => s.SaveCategoryAsync(new Category { Slug = slug, Name = name, ParentId = parent, Tags = tags }));

    private Task<long> Product(string title, string category, string tags = "") =>
        _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify(title), Title = title, PriceCents = 1000, Category = category, Tags = tags, Active = true }));

    [Fact]
    public async Task The_categories_page_lists_the_top_level_filters_by_a_keyword_on_categories_and_the_keyword_page_lists_the_products_keywords()
    {
        var neu = await Category("cp-neu", "CP Neuware", 0, "CPNeuware");
        await Category("cp-alt", "CP Gebraucht", 0, "CPGebraucht");
        await Category("cp-neu-kind", "CP Neues Kind", neu, "CPGebraucht, CPNeuware");     // a subcategory carries keywords too
        await Product("CP Ding Neu", "cp-neu-kind", "CPBio");
        await Product("CP Ding Alt", "cp-alt", "CPBio");
        var v = _app.Browser();

        var all = Decode(await v.GetStringAsync("/shop/categories"));
        Assert.Contains("href=\"/shop/categories/cp-neu\"", all);
        Assert.Contains("href=\"/shop/categories/cp-alt\"", all);
        Assert.DoesNotContain("<h3><a href=\"/shop/categories/cp-neu-kind\">", all);           // the top level only
        Assert.Contains("href=\"/shop/categories?tag=CPNeuware\"", all);                                       // the keywords of categories, as links
        Assert.DoesNotContain("<ul class=\"shop-linklist\">", all);                                               // the tree as a list is offered to a template, not drawn by default
        Assert.Contains("CP Neuware", all);

        var tagged = Decode(await v.GetStringAsync("/shop/categories?tag=cpneuware"));                         // case does not matter
        Assert.Contains("<h1>Kategorien · cpneuware</h1>", tagged);
        Assert.Contains("<h3><a href=\"/shop/categories/cp-neu\">", tagged);
        Assert.Contains("<h3><a href=\"/shop/categories/cp-neu-kind\">", tagged);              // at any level
        Assert.DoesNotContain("<h3><a href=\"/shop/categories/cp-alt\">", tagged);
        Assert.Contains("Kategorien</a> › cpneuware", tagged);

        var none = Decode(await v.GetStringAsync("/shop/categories?tag=gibt-es-nicht"));
        Assert.Contains("Noch keine Kategorien.", none);

        var tags = Decode(await v.GetStringAsync("/shop/tags"));
        Assert.Contains("href=\"/shop?tag=CPBio\">CPBio <span class=\"shop-muted\">2</span>", tags);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/shop/tags")).StatusCode);
    }

    [Fact]
    public async Task The_link_picker_offers_the_pages_and_the_keywords_and_a_category_keyword_leads_to_its_categories()
    {
        await Category("lt-neu", "LT Neu", 0, "LTNeuware");
        var admin = await _shop.AdminAsync();
        var json = await admin.GetStringAsync("/admin/api/link-targets");
        Assert.Contains("/shop/categories\"", json);
        Assert.Contains("/shop/tags\"", json);
        Assert.Contains("/shop/categories?tag=LTNeuware", json);
        Assert.Contains("Shop – Kategorien nach Schlagwort", json);
    }

    [Fact]
    public async Task A_category_is_tagged_in_the_back_office_and_the_tags_come_back_cleaned()
    {
        var id = await Category("adm-tag", "Adm Tag");
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync($"/admin/p/shop/categories/{id}");
        Assert.Contains("name=\"tags\"", page);
        var res = await admin.PostAsync($"/admin/p/shop/categories/{id}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["action"] = "save", ["name"] = "Adm Tag", ["tags"] = "  Neuware ;neuware, Gebrauchtware ,, ", ["parent"] = "0"
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("Neuware, Gebrauchtware", (await _shop.WithContextAsync((_, s) => s.CategoryAsync(id)))!.Tags);        // split, trimmed, no repeat (first spelling wins)
        Assert.Contains("Neuware, Gebrauchtware", Decode(await admin.GetStringAsync("/admin/p/shop/categories")));        // shown in the list
    }

    [Fact]
    public async Task The_categories_element_is_a_multi_level_dropdown_and_a_plugin_entry_may_nest_as_deep_as_a_menu_goes()
    {
        var a = await Category("ml-a", "ML Alpha");
        var b = await Category("ml-b", "ML Beta", a);
        var c = await Category("ml-c", "ML Gamma", b);
        await Product("ML Tief", "ml-c");
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var item = new MenuItem { Menu = "header", Locale = "de", Label = "Kategorien", Url = "plugin:shop.categories", SortOrder = 700 };
        db.MenuItems.Add(item); await db.SaveChangesAsync();
        try
        {
            var html = Decode(await _app.Browser().GetStringAsync("/"));
            var at = html.IndexOf("<a href=\"/shop/categories\">", StringComparison.Ordinal);
            Assert.True(at >= 0, "the element's heading leads to the page of all categories");
            var tree = html[at..];
            // Level 1 = the heading, then Alpha > Beta > Gamma: three nested dropdowns.
            Assert.Matches("<span class=\"mat-hassub\"><a href=\"/shop/categories\">[^<]*</a><span class=\"mat-sub\"><span class=\"mat-hassub\"><a href=\"/shop/categories/ml-a\">ML Alpha</a><span class=\"mat-sub\"><span class=\"mat-hassub\"><a href=\"/shop/categories/ml-b\">ML Beta</a><span class=\"mat-sub\"><a href=\"/shop/categories/ml-c\">ML Gamma</a>", html);
        }
        finally { db.MenuItems.Remove(item); await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task The_page_carries_the_script_that_folds_the_phone_menu_and_opens_a_dropdown_on_a_tap()
    {
        var html = await _app.Browser().GetStringAsync("/");
        Assert.Contains("/js/site-nav.js", html);                      // adds the arrow buttons to the entries with a submenu; the first tap on a touch screen opens the dropdown
        Assert.Contains("class=\"nav-toggle-label\"", html);
    }

    [Fact]
    public async Task An_empty_cart_offers_a_way_on_and_a_full_one_shows_pictures_a_summary_and_one_big_button()
    {
        await Category("cart-kat", "Cart Kat");
        await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "cart-ding", Title = "Cart Ding", PriceCents = 2500, Category = "cart-kat", Image = "/uploads/ding.png", Featured = true, Active = true }));
        var v = _app.Browser();

        var empty = Decode(await v.GetStringAsync("/shop/cart"));
        Assert.Contains("class=\"shop-cart-empty\"", empty);
        Assert.Contains("Ihr Warenkorb ist leer", empty);
        Assert.Contains("<a class=\"shop-btn big\" href=\"/shop\">Weiter einkaufen</a>", empty);          // the way back, as THE button
        Assert.Contains("href=\"/shop/categories/cart-kat\">Cart Kat</a>", empty);                         // the categories
        Assert.Contains("Cart Ding", empty);                                                                // a product to start with
        Assert.DoesNotContain("class=\"shop-cartsum\"", empty);

        await _shop.AddToCartAsync(v, "cart-ding", 2);
        var full = Decode(await v.GetStringAsync("/shop/cart"));
        Assert.Contains("class=\"shop-cartlines\"", full);
        Assert.Contains("2 Artikel", full);
        Assert.Contains("style=\"background-image:url('/uploads/ding.png')\"", full);                     // the picture
        Assert.Contains("class=\"lineprice\">50,00 €", full);
        Assert.Contains("<aside class=\"shop-cartsum\">", full);
        Assert.Contains("class=\"shop-btn big\" href=\"/shop/checkout\">Zur Kasse", full);
        Assert.DoesNotContain("class=\"shop-cart-empty\"", full);
    }

    [Fact]
    public async Task Several_pictures_are_thumbnails_that_switch_the_main_picture_and_a_single_picture_stays_as_it_was()
    {
        await _shop.WithContextAsync((_, st) => st.SaveProductAsync(new Product { Slug = "gal-viele", Title = "Gal Viele", PriceCents = 1000, Image = "/uploads/a.png", GalleryJson = "[{\"url\":\"/uploads/b.png\",\"alt\":\"\"},{\"url\":\"/uploads/c.png\",\"alt\":\"\"}]", Active = true }));
        await _shop.WithContextAsync((_, st) => st.SaveProductAsync(new Product { Slug = "gal-eins", Title = "Gal Eins", PriceCents = 1000, Image = "/uploads/a.png", Active = true }));
        var many = Decode(await _app.Browser().GetStringAsync("/shop/gal-viele"));
        Assert.Contains("<div class=\"shop-gal\" id=\"gal-", many);
        Assert.Contains("<img class=\"main-img\" src=\"/uploads/a.png\"", many);
        // The main picture is the first thumbnail, then the gallery in its order.
        Assert.Matches("class=\"th on\" href=\"/uploads/a.png\" data-src=\"/uploads/a.png\".*class=\"th\" href=\"/uploads/b.png\".*class=\"th\" href=\"/uploads/c.png\"", many);
        Assert.Contains("shop-lightbox", many);                                               // a click on the main picture opens it large
        var one = Decode(await _app.Browser().GetStringAsync("/shop/gal-eins"));
        Assert.DoesNotContain("class=\"shop-gal\"", one);
        Assert.Contains("<img class=\"main-img\" src=\"/uploads/a.png\"", one);
    }

    [Fact]
    public async Task The_checkout_is_numbered_cards_with_pickable_choices_and_a_summary_with_pictures_and_the_confirmations()
    {
        await _shop.WithContextAsync((_, st) => st.SaveProductAsync(new Product { Slug = "kasse-ding", Title = "Kasse Ding", PriceCents = 2500, Image = "/uploads/k.png", Active = true }));
        var v = _app.Browser();
        await _shop.AddToCartAsync(v, "kasse-ding", 2);
        var page = Decode(await v.GetStringAsync("/shop/checkout"));
        Assert.Contains("class=\"shop-checkout\"", page);
        Assert.Contains("<span class=\"n\">1</span>", page);                                   // the steps are numbered
        Assert.Contains("class=\"shop-opt\"", page);                                            // shipping / payment are cards one picks
        Assert.Contains("class=\"shop-sline\"", page);
        Assert.Contains("style=\"background-image:url('/uploads/k.png')\"><b>2</b>", page);    // the summary line has the picture and the quantity
        Assert.Contains("name=\"terms\"", page);                                                // the confirmation and the button sit in the summary
        Assert.Contains("class=\"shop-btn big\" type=\"submit\"", page);
        Assert.Contains("name=\"email\"", page); Assert.Contains("name=\"b_name\"", page);     // every field the order needs is still there
    }
}
