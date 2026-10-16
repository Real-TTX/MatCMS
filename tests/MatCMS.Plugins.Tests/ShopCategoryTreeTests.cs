using System.Net;
using MatCMS.Plugin.Shop;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The category tree as pure functions: order, depth, what is below what, and the ways a broken tree must not break it.</summary>
public class ShopCategoryTreeLogicTests
{
    private static Category C(long id, string slug, long parent = 0, int sort = 0, string? name = null) => new() { Id = id, Slug = slug, Name = name ?? slug.ToUpperInvariant(), ParentId = parent, Sort = sort };

    private static readonly IReadOnlyList<Category> Tree = new[]
    {
        C(1, "kaffee", 0, 1), C(2, "bohnen", 1, 2), C(3, "gemahlen", 1, 1), C(4, "tee", 0, 2), C(5, "espresso", 2, 0), C(6, "zubehoer", 0, 3)
    };

    [Fact]
    public void A_tree_is_listed_depth_first_in_sort_order_with_its_depth()
    {
        var flat = ShopCategories.Flatten(Tree).Select(t => $"{t.Depth}:{t.Cat.Slug}").ToArray();
        Assert.Equal(new[] { "0:kaffee", "1:gemahlen", "1:bohnen", "2:espresso", "0:tee", "0:zubehoer" }, flat);
        Assert.Equal(new[] { "kaffee", "tee", "zubehoer" }, ShopCategories.Roots(Tree).Select(c => c.Slug).ToArray());
        Assert.Equal(new[] { "gemahlen", "bohnen" }, ShopCategories.Children(Tree, 1).Select(c => c.Slug).ToArray());
        Assert.Empty(ShopCategories.Children(Tree, 6));
    }

    [Fact]
    public void A_category_means_itself_and_everything_below_it()
    {
        Assert.Equal(new[] { "bohnen", "espresso", "gemahlen", "kaffee" }, ShopCategories.SlugsUnder(Tree, "kaffee").OrderBy(s => s).ToArray());
        Assert.Equal(new[] { "bohnen", "espresso" }, ShopCategories.SlugsUnder(Tree, "bohnen").OrderBy(s => s).ToArray());
        Assert.Equal(new[] { "tee" }, ShopCategories.SlugsUnder(Tree, "tee").ToArray());
        Assert.Equal(new[] { "gibts-nicht" }, ShopCategories.SlugsUnder(Tree, "gibts-nicht").ToArray());       // a product may still carry a slug that is gone
        Assert.Equal(new long[] { 1, 2, 3, 5 }, ShopCategories.IdsUnder(Tree, 1).OrderBy(i => i).ToArray());
    }

    [Fact]
    public void The_path_runs_from_the_top_and_the_label_shows_it()
    {
        Assert.Equal(new[] { "kaffee", "bohnen", "espresso" }, ShopCategories.Path(Tree, "espresso").Select(c => c.Slug).ToArray());
        Assert.Equal("KAFFEE › BOHNEN › ESPRESSO", ShopCategories.Label(Tree, Tree.First(c => c.Slug == "espresso")));
        Assert.Equal("TEE", ShopCategories.Label(Tree, Tree.First(c => c.Slug == "tee")));
        Assert.Empty(ShopCategories.Path(Tree, "nope"));
    }

    [Fact]
    public void Counts_roll_up_to_every_ancestor_and_a_slug_without_a_category_counts_for_itself()
    {
        var total = ShopCategories.RollUp(Tree, new Dictionary<string, int> { ["espresso"] = 2, ["gemahlen"] = 1, ["tee"] = 4, ["alt"] = 3 });
        Assert.Equal(3, total["kaffee"]);
        Assert.Equal(2, total["bohnen"]);
        Assert.Equal(2, total["espresso"]);
        Assert.Equal(1, total["gemahlen"]);
        Assert.Equal(4, total["tee"]);
        Assert.Equal(3, total["alt"]);
        Assert.False(total.ContainsKey("zubehoer"));
    }

    [Fact]
    public void A_missing_parent_makes_a_category_top_level_and_a_loop_is_cut_without_hanging()
    {
        var odd = new[] { C(1, "waise", 99), C(2, "a", 3), C(3, "b", 2), C(4, "selbst", 4) };
        var flat = ShopCategories.Flatten(odd);
        Assert.Equal(4, flat.Count);                                                     // everybody is listed, once
        Assert.Equal(4, flat.Select(t => t.Cat.Id).Distinct().Count());
        Assert.Contains(ShopCategories.Roots(odd), c => c.Slug == "waise");
        Assert.Contains(ShopCategories.Roots(odd), c => c.Slug == "selbst");
        Assert.NotEmpty(ShopCategories.Path(odd, "a"));                                  // terminates
        Assert.Contains("a", ShopCategories.SlugsUnder(odd, "b"));
    }
}

/// <summary>Categories in the shop itself: what the back office writes, what a visitor sees, what a coupon covers.</summary>
public class ShopCategoryTreeTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopCategoryTreeTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<long> NewCategory(string slug, string name, long parent = 0, string image = "", string description = "") =>
        await _shop.WithContextAsync((_, s) => s.SaveCategoryAsync(new Category { Slug = slug, Name = name, ParentId = parent, Image = image, Description = description }));

    private async Task<string> BlockAsync(HttpClient visitor, string type, string json)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var page = db.Pages.First(p => p.Slug == "home");
        var row = new MatCMS.Models.ContentBlock { PageId = page.Id, BlockType = type, SortOrder = 900, DataJson = json };
        db.ContentBlocks.Add(row); await db.SaveChangesAsync();
        try
        {
            // Only the block itself: the page also carries the shop's menu, which lists categories of its own.
            var html = Decode(await visitor.GetStringAsync("/"));
            var at = html.IndexOf("<div class=\"shop shop-block\">", StringComparison.Ordinal);
            return at < 0 ? html : html[at..html.IndexOf("</section>", at, StringComparison.Ordinal)];
        }
        finally { db.ContentBlocks.Remove(row); await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task A_parent_that_would_make_a_loop_or_does_not_exist_is_written_as_top_level()
    {
        var a = await NewCategory("loop-a", "Loop A");
        var b = await NewCategory("loop-b", "Loop B", a);
        await _shop.WithContextAsync((_, s) => s.SaveCategoryAsync(new Category { Id = a, Slug = "loop-a", Name = "Loop A", ParentId = b }));     // a under its own child
        await _shop.WithContextAsync((_, s) => s.SaveCategoryAsync(new Category { Id = b, Slug = "loop-b", Name = "Loop B", ParentId = b }));     // under itself
        var c = await NewCategory("loop-c", "Loop C", 987654);                                                                                    // a parent that is not there
        var saved = await _shop.WithContextAsync(async (_, s) => (await s.CategoryAsync(a), await s.CategoryAsync(b), await s.CategoryAsync(c)));
        Assert.Equal(0, saved.Item1!.ParentId);
        Assert.Equal(0, saved.Item2!.ParentId);
        Assert.Equal(0, saved.Item3!.ParentId);
    }

    [Fact]
    public async Task A_product_filter_and_the_counts_include_what_is_below_and_deleting_moves_everything_up()
    {
        var top = await NewCategory("kt-oben", "KT Oben");
        var mid = await NewCategory("kt-mitte", "KT Mitte", top);
        var low = await NewCategory("kt-unten", "KT Unten", mid);
        await _shop.AddProductAsync("KT Direkt", 500, category: "kt-oben");
        await _shop.AddProductAsync("KT Mitte Ding", 600, category: "kt-mitte");
        await _shop.AddProductAsync("KT Unten Ding", 700, category: "kt-unten");

        var (all, below, facets) = await _shop.WithContextAsync(async (_, s) => (
            await s.FindProductsAsync(new ProductQuery { Category = "kt-oben" }),
            await s.FindProductsAsync(new ProductQuery { Category = "kt-mitte" }),
            (await s.FacetsAsync()).Categories));
        Assert.Equal(3, all.Total);
        Assert.Equal(new[] { "KT Mitte Ding", "KT Unten Ding" }, below.Items.Select(p => p.Title).OrderBy(t => t).ToArray());
        Assert.Equal(3, facets["kt-oben"]);
        Assert.Equal(2, facets["kt-mitte"]);
        Assert.Equal(1, facets["kt-unten"]);

        // Delete the middle one: its child and its product go up to "oben", nothing disappears.
        await _shop.WithContextAsync((_, s) => s.DeleteCategoryAsync(mid));
        var after = await _shop.WithContextAsync(async (_, s) => (await s.CategoryAsync(low), (await s.ProductsAsync(false)).Items.First(p => p.Title == "KT Mitte Ding").Category, (await s.FindProductsAsync(new ProductQuery { Category = "kt-oben" })).Total));
        Assert.Equal(top, after.Item1!.ParentId);
        Assert.Equal("kt-oben", after.Item2);
        Assert.Equal(3, after.Item3);
    }

    [Fact]
    public async Task The_category_list_is_a_tree_and_a_category_is_made_on_its_own_page_with_picture_text_and_parent()
    {
        var admin = await _shop.AdminAsync();
        var page = await admin.GetStringAsync("/admin/p/shop/categories/0");
        Assert.Contains("name=\"description\"", page);
        Assert.Contains("name=\"image\"", page);
        Assert.Contains("name=\"parent\"", page);

        async Task<HttpResponseMessage> Save(long id, Dictionary<string, string> fields)
        {
            var p = await admin.GetStringAsync($"/admin/p/shop/categories/{id}");
            fields["__RequestVerificationToken"] = AppFactory.Token(p)!; fields["action"] = "save";
            return await admin.PostAsync($"/admin/p/shop/categories/{id}", new FormUrlEncodedContent(fields));
        }
        Assert.Equal(HttpStatusCode.Redirect, (await Save(0, new() { ["name"] = "Adm Eltern", ["description"] = "Der erste Text.", ["image"] = "/uploads/eltern.png", ["parent"] = "0", ["sort"] = "1" })).StatusCode);
        var parent = await _shop.WithContextAsync((_, s) => s.CategoryBySlugAsync("adm-eltern"));
        Assert.NotNull(parent);
        Assert.Equal("Der erste Text.", parent!.Description);
        Assert.Equal("/uploads/eltern.png", parent.Image);
        // A picture address that could break out of the style attribute is dropped.
        Assert.Equal(HttpStatusCode.Redirect, (await Save(0, new() { ["name"] = "Adm Kind", ["image"] = "javascript:alert(1)", ["parent"] = parent.Id.ToString(), ["sort"] = "0" })).StatusCode);
        var kid = await _shop.WithContextAsync((_, s) => s.CategoryBySlugAsync("adm-kind"));
        Assert.Equal(parent.Id, kid!.ParentId);
        Assert.Equal("", kid.Image);
        Assert.Equal(HttpStatusCode.OK, (await Save(0, new() { ["name"] = "  ", ["parent"] = "0" })).StatusCode);          // no name: the form comes back, nothing is written

        var list = Decode(await admin.GetStringAsync("/admin/p/shop/categories"));
        Assert.True(list.IndexOf("Adm Eltern", StringComparison.Ordinal) < list.IndexOf("Adm Kind", StringComparison.Ordinal));
        Assert.Contains("padding-left:28px", list);                                                                        // the child is indented under its parent
        Assert.Contains($"/admin/p/shop/categories/0?parent={parent.Id}", list);                                           // "add subcategory"

        // The parent cannot be moved under its own child, and the slug stays.
        var edit = Decode(await admin.GetStringAsync($"/admin/p/shop/categories/{parent.Id}"));
        Assert.DoesNotContain("<option value=\"" + kid.Id + "\"", edit);
        Assert.Contains("adm-eltern", edit);

        // Deleting through the list moves the child up.
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/p/shop/categories"))!;
        var del = await admin.PostAsync("/admin/p/shop/categories", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = "delete", ["id"] = parent.Id.ToString() }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.Equal(0, (await _shop.WithContextAsync((_, s) => s.CategoryBySlugAsync("adm-kind")))!.ParentId);
    }

    [Fact]
    public async Task A_category_page_shows_where_it_sits_its_picture_and_text_its_subcategories_and_the_products_below()
    {
        var top = await NewCategory("seite-oben", "Seite Oben", 0, "/uploads/oben.png", "Alles <b>rund</b> um oben.");
        var kid = await NewCategory("seite-kind", "Seite Kind", top, "/uploads/kind.png");
        await _shop.AddProductAsync("Seite Kinderprodukt", 1500, category: "seite-kind");
        var v = _app.Browser();

        var html = Decode(await v.GetStringAsync("/shop/categories/seite-oben"));
        Assert.Contains("<p class=\"shop-crumbs\"><a href=\"/shop\">Shop</a> › Seite Oben</p>", html);
        Assert.Contains("class=\"shop-cathero\" style=\"background-image:url('/uploads/oben.png')\"", html);
        var raw = await v.GetStringAsync("/shop/categories/seite-oben");
        Assert.Contains("Alles &lt;b&gt;rund&lt;/b&gt; um oben.", raw);                                       // the text is text, never markup
        Assert.DoesNotContain("<b>rund</b>", raw);
        Assert.Contains("class=\"shop-subcat\" href=\"/shop/categories/seite-kind\"", html);
        Assert.Contains("background-image:url('/uploads/kind.png')", html);
        Assert.Contains("Seite Kinderprodukt", html);                                                       // a product of the child is in the parent's list
        Assert.Contains("class=\"on\"", html.Substring(html.IndexOf("shop-catnav", StringComparison.Ordinal)));  // the top-level chip of this branch is lit

        var child = Decode(await v.GetStringAsync("/shop/categories/seite-kind"));
        Assert.Contains("<a href=\"/shop/categories/seite-oben\">Seite Oben</a> › Seite Kind</p>", child);
        Assert.DoesNotContain("class=\"shop-subcat\"", child);

        // The product page's breadcrumbs walk the same path.
        var prod = Decode(await v.GetStringAsync("/shop/seite-kinderprodukt"));
        Assert.Contains("<a href=\"/shop/categories/seite-oben\">Seite Oben</a> › <a href=\"/shop/categories/seite-kind\">Seite Kind</a>", prod);
    }

    [Fact]
    public async Task A_category_has_its_own_address_the_old_query_address_moves_there_for_good_and_an_unknown_category_is_a_404()
    {
        await NewCategory("url-kaffee", "Url Kaffee", 0);
        await _shop.AddProductAsync("Url Bohne", 900, category: "url-kaffee");
        var v = _app.Browser();

        // The old address: a permanent redirect that keeps the other filters.
        var old = await v.GetAsync("/shop?cat=url-kaffee&tag=Bio&sort=name");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal("/shop/categories/url-kaffee?tag=Bio&sort=name", old.Headers.Location!.OriginalString);

        // The new one is the page, and its search form stays on it (no hidden "cat" any more).
        var page = Decode(await v.GetStringAsync("/shop/categories/url-kaffee"));
        Assert.Contains("Url Bohne", page);
        Assert.Contains("<form method=\"get\" action=\"/shop/categories/url-kaffee\" class=\"shop-find\" role=\"search\">", page);
        Assert.DoesNotContain("name=\"cat\"", page);

        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/shop/categories/gibt-es-nicht")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/shop/url-bohne")).StatusCode);                       // a product is still /shop/{slug}

        // The sitemap names the category page.
        Assert.Contains("/shop/categories/url-kaffee", await v.GetStringAsync("/sitemap.xml"));
    }

    [Fact]
    public async Task The_category_block_shows_the_top_level_the_subcategories_of_one_category_tiles_with_pictures_and_a_nested_list()
    {
        var top = await NewCategory("blk-oben", "Blk Oben", 0, "/uploads/b-oben.png");
        await NewCategory("blk-kind", "Blk Kind", top, "/uploads/b-kind.png");
        await _shop.AddProductAsync("Blk Kinddings", 800, category: "blk-kind");
        var v = _app.Browser();

        var chips = await BlockAsync(v, "shop_categories", """{"by":"category","style":"chips","counts":"yes","allLink":"no"}""");
        Assert.Contains("href=\"/shop/categories/blk-oben\">Blk Oben<span>1</span>", chips);
        Assert.DoesNotContain("href=\"/shop/categories/blk-kind\"", chips);                                         // the top level only

        var subs = await BlockAsync(v, "shop_categories", """{"by":"category","parent":"blk-oben","style":"chips","counts":"no","allLink":"no"}""");
        Assert.Contains("href=\"/shop/categories/blk-kind\">Blk Kind", subs);
        Assert.DoesNotContain("href=\"/shop/categories/blk-oben\"", subs);

        var tiles = await BlockAsync(v, "shop_categories", """{"by":"category","style":"tiles","counts":"yes","allLink":"no"}""");
        Assert.Contains("class=\"shop-subcat\" href=\"/shop/categories/blk-oben\"", tiles);
        Assert.Contains("background-image:url('/uploads/b-oben.png')", tiles);

        var list = await BlockAsync(v, "shop_categories", """{"by":"category","style":"list","counts":"no","allLink":"no"}""");
        Assert.Contains("Blk Oben <span", list.Replace("Blk Oben ", "Blk Oben <span"));
        Assert.True(list.IndexOf("blk-oben", StringComparison.Ordinal) < list.IndexOf("blk-kind", StringComparison.Ordinal));
        Assert.Contains("<li><a href=\"/shop/categories/blk-oben\">", list);
        Assert.Contains("<ul class=\"shop-linklist\"><li><a href=\"/shop/categories/blk-kind\">", list);             // the child is a list inside the parent's item
    }

    [Fact]
    public async Task A_coupon_for_a_category_covers_the_subcategories_too()
    {
        await NewCategory("cp-oben", "Cp Oben");
        var oben = (await _shop.WithContextAsync((_, s) => s.CategoryBySlugAsync("cp-oben")))!;
        await NewCategory("cp-kind", "Cp Kind", oben.Id);
        await _shop.AddProductAsync("Cp Kindprodukt", 2000, category: "cp-kind");
        await _shop.AddProductAsync("Cp Fremd", 2000);
        await _shop.AddCouponAsync("CPOBEN", CouponKind.Percent, 5000, c => c with { Category = "cp-oben" });
        var v = _app.Browser();
        await _shop.AddToCartAsync(v, "cp-kindprodukt", 1);
        await _shop.AddToCartAsync(v, "cp-fremd", 1);
        await _shop.ApplyCouponAsync(v, "CPOBEN");
        var order = (await _shop.OrderAsync(ShopApp.NumberOf(await _shop.CheckoutAsync(v))))!;
        Assert.Equal(1000, order.DiscountCents);                                                           // 50 % of the child's 20,00 only, not of the other product
    }
}
