using System.Net;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The shop inside the site: entries in the site's menus (cart with its count, sign-in or account, the shop with its categories), blocks that know the visitor's cart and account.</summary>
public class ShopSiteIntegrationTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopSiteIntegrationTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    /// <summary>The icon strip of a page: the links with class hicon, as drawn.</summary>
    private static List<string> Icons(string html) => System.Text.RegularExpressions.Regex.Matches(html, "<a class=\"hicon\"[^>]*>.*?</a>", System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Value).ToList();

    private static async Task SignInAsync(HttpClient c, string email, string password)
    {
        var page = await c.GetStringAsync("/anmelden");
        var res = await c.PostAsync("/anmelden", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = email, ["password"] = password, ["__RequestVerificationToken"] = AppFactory.Token(page)! }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    private async Task<string> BlockAsync(HttpClient visitor, string type, string json)
    {
        // A block is drawn inside a page; put one on the start page and read the page as the visitor would.
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var page = db.Pages.First(p => p.Slug == "home");
        var row = new MatCMS.Models.ContentBlock { PageId = page.Id, BlockType = type, SortOrder = 900, DataJson = json };
        db.ContentBlocks.Add(row); await db.SaveChangesAsync();
        try { return Decode(await visitor.GetStringAsync("/")); }
        finally { db.ContentBlocks.Remove(row); await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task The_icon_strip_gets_the_cart_with_its_count_and_a_way_to_sign_in_and_the_count_follows_the_visitors_own_cart()
    {
        await _shop.AddProductAsync("Menü Tasse", 1000);
        var v = _app.Browser();
        var empty = Icons(await v.GetStringAsync("/"));
        Assert.Contains(empty, i => i.Contains("href=\"/shop/cart\"") && i.Contains("ti-shopping-cart") && !i.Contains("hbadge"));    // an empty cart shows no counter
        Assert.Contains(empty, i => i.Contains("href=\"/anmelden?returnUrl=%2Fkonto\"") && i.Contains("ti-user\""));

        await _shop.AddToCartAsync(v, "menue-tasse", 2); await _shop.AddToCartAsync(v, "menue-tasse", 1);
        var cart = Icons(await v.GetStringAsync("/")).Single(i => i.Contains("/shop/cart"));
        Assert.Contains("<span class=\"hbadge\">3</span>", cart);
        // Another browser has its own cart.
        Assert.DoesNotContain("hbadge", Icons(await _app.Browser().GetStringAsync("/")).Single(i => i.Contains("/shop/cart")));
        // The shop's own pages carry it too.
        Assert.Contains("<span class=\"hbadge\">3</span>", await v.GetStringAsync("/shop"));
    }

    [Fact]
    public async Task The_icons_work_with_any_template_a_layout_of_its_own_without_a_strip_gets_them_floating_and_one_with_a_strip_does_not()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var tpl = db.Templates.First(t => t.IsActive);
        var before = tpl.LayoutHtml; var map = tpl.MenuMapJson;
        try
        {
            // A layout that never mentions the icon strip: the cart and the way in would be gone.
            tpl.LayoutHtml = "<div class=\"x\"><nav>{{menu:header}}</nav><main>{{content}}</main></div>"; await db.SaveChangesAsync();
            var html = Decode(await _app.Browser().GetStringAsync("/"));
            var overlay = html[html.IndexOf("class=\"mat-toolbar-overlay", StringComparison.Ordinal)..];
            overlay = overlay[..overlay.IndexOf("</div>", StringComparison.Ordinal)];
            Assert.Contains("href=\"/shop/cart\"", overlay);
            Assert.Contains("href=\"/anmelden?returnUrl=%2Fkonto\"", overlay);
            Assert.Contains("data-toolbar=\"1\"", html);

            // A layout with the strip placed by {{toolbar}} — or by a slot that is mapped to the toolbar — has no overlay on top of it.
            tpl.LayoutHtml = "<header>{{toolbar}}</header><main>{{content}}</main>"; await db.SaveChangesAsync();
            var placed = Decode(await _app.Browser().GetStringAsync("/"));
            Assert.DoesNotContain("mat-toolbar-overlay", placed);
            Assert.Contains("href=\"/shop/cart\"", placed);
            tpl.LayoutHtml = "<header>{{menu:icons}}</header><main>{{content}}</main>"; tpl.MenuMapJson = "{\"icons\":\"toolbar\"}"; await db.SaveChangesAsync();
            var mapped = Decode(await _app.Browser().GetStringAsync("/"));
            Assert.DoesNotContain("mat-toolbar-overlay", mapped);
            Assert.Contains("href=\"/shop/cart\"", mapped);

            // The shop's own pages hide their small bar wherever the cart element is on the page, in every one of these.
            var shopPage = await _app.Browser().GetStringAsync("/shop");
            Assert.Contains("body[data-els~=\"shop.cart\"] .shop-nav{display:none}", shopPage);
            Assert.Contains("data-els=\"", Decode(shopPage));
            Assert.Contains("shop.cart", Decode(shopPage).Substring(Decode(shopPage).IndexOf("data-els=\"", StringComparison.Ordinal), 120));
        }
        finally { tpl.LayoutHtml = before; tpl.MenuMapJson = map; await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task Signing_in_turns_the_entry_into_mein_konto_and_signing_out_turns_it_back()
    {
        const string email = "menue-kunde@example.org";
        var v = _app.Browser();
        await _shop.AddProductAsync("Menü Konto Ding", 500);
        await _shop.AddToCartAsync(v, "menue-konto-ding");
        var res = await _shop.CheckoutAsync(v, email: email, extra: new Dictionary<string, string> { ["create_account"] = "1", ["password"] = "ein langes passwort" });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);                                // creating the account at checkout signs the customer in

        var strip = Icons(await v.GetStringAsync("/"));
        Assert.Contains(strip, i => i.Contains("href=\"/konto\"") && i.Contains("ti-user-check"));
        Assert.DoesNotContain(strip, i => i.Contains("/anmelden"));

        var other = _app.Browser();
        await SignInAsync(other, email, "ein langes passwort");
        Assert.Contains(Icons(await other.GetStringAsync("/")), i => i.Contains("href=\"/konto\""));
        Assert.Contains(Icons(await _app.Browser().GetStringAsync("/")), i => i.Contains("/anmelden"));   // a stranger still sees the way in
    }

    [Fact]
    public async Task The_main_menu_gets_the_shop_with_its_categories_as_a_dropdown_and_deleting_that_menu_item_sticks()
    {
        await _shop.WithContextAsync(async (_, s) =>
        {
            foreach (var (slug, name) in new[] { ("menue-kaffee", "Menü Kaffee"), ("menue-leer", "Menü Leere") }) if (await s.CategoryBySlugAsync(slug) is null) await s.AddCategoryAsync(slug, name, 0);
        });
        await _shop.AddProductAsync("Menü Bohnen", 900, category: "menue-kaffee");
        var html = Decode(await _app.Browser().GetStringAsync("/"));
        var nav = html[html.IndexOf("class=\"main-nav\"", StringComparison.Ordinal)..];
        nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];
        Assert.Contains("<span class=\"mat-hassub\"><a href=\"/shop\">Shop</a>", nav);
        Assert.Contains("href=\"/shop/categories/menue-kaffee\">Menü Kaffee</a>", nav);
        Assert.DoesNotContain("Menü Leere", nav);                                                // a category without a product is no dead end

        // The element is an ordinary menu item: the editor deletes it, and it does NOT come back when the plugin is loaded again.
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var items = db.MenuItems.Where(m => m.Url == "plugin:shop.shop").ToList();
        Assert.NotEmpty(items);
        var copies = items.Select(i => new MatCMS.Models.MenuItem { Menu = i.Menu, Locale = i.Locale, Label = i.Label, Url = i.Url, SortOrder = i.SortOrder, Icon = i.Icon }).ToList();
        db.MenuItems.RemoveRange(items); await db.SaveChangesAsync();
        try
        {
            await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(db.Plugins.First(p => p.Key == "shop").Id);
            var off = Decode(await _app.Browser().GetStringAsync("/"));
            Assert.DoesNotContain("href=\"/shop\"", off[off.IndexOf("class=\"main-nav\"", StringComparison.Ordinal)..off.IndexOf("</nav>", off.IndexOf("class=\"main-nav\"", StringComparison.Ordinal), StringComparison.Ordinal)]);
            Assert.Empty(db.MenuItems.Where(m => m.Url == "plugin:shop.shop"));
        }
        finally { db.MenuItems.AddRange(copies); await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task A_menu_entry_a_plugin_sends_may_only_lead_somewhere_sensible_and_a_failing_plugin_does_not_take_the_page_down()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<PluginRunner>();
        var good = new MatCMS.Models.Plugin { Key = "menutest", Name = "menutest", Version = "1", Kind = MatCMS.Models.PluginKind.Script, Enabled = true, Code = """
            Plugin.Menu(async (ctx, menu, ct) => menu == "header" ? new[] {
                new MenuEntry("Gut", "/gut"), new MenuEntry("Böse", "javascript:alert(1)"), new MenuEntry("Offen", "//evil.example"), new MenuEntry("", "/leer"),
                new MenuEntry("Extern", "https://example.org") { Badge = "123456789" } } : new MenuEntry[0]);
            """ };
        var bad = new MatCMS.Models.Plugin { Key = "menukaputt", Name = "menukaputt", Version = "1", Kind = MatCMS.Models.PluginKind.Script, Enabled = true,
            Code = "Plugin.Menu((ctx, menu, ct) => throw new System.InvalidOperationException(\"kaputt\"));" };
        db.Plugins.AddRange(good, bad);
        await db.SaveChangesAsync();
        await runner.ReloadAsync(good.Id); await runner.ReloadAsync(bad.Id);
        try
        {
            var res = await _app.Browser().GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);                                      // the broken plugin does not take the page down
            var html = Decode(await res.Content.ReadAsStringAsync());
            var nav = html[html.IndexOf("class=\"main-nav\"", StringComparison.Ordinal)..];
            nav = nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];
            Assert.Contains("<a href=\"/gut\">Gut</a>", nav);
            Assert.Contains("<a href=\"https://example.org\">Extern <span class=\"hbadge inline\">123456</span></a>", nav);   // a badge is a counter, not a sentence
            Assert.DoesNotContain("javascript:", nav); Assert.DoesNotContain("evil.example", nav); Assert.DoesNotContain("/leer", nav);
            Assert.Contains(scope.ServiceProvider.GetRequiredService<PluginRegistry>().LogOf("menukaputt"), l => l.Contains("kaputt"));
        }
        finally
        {
            good.Enabled = false; bad.Enabled = false; await db.SaveChangesAsync();
            await runner.ReloadAsync(good.Id); await runner.ReloadAsync(bad.Id);
        }
    }

    // ---- blocks -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_category_block_lists_categories_or_keywords_with_counts_and_the_search_block_asks_the_shop()
    {
        await _shop.WithContextAsync(async (_, s) => { if (await s.CategoryBySlugAsync("blk-kat") is null) await s.AddCategoryAsync("blk-kat", "Block Kat", 0); });
        await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "blk-p1", Title = "Blk P1", PriceCents = 100, Category = "blk-kat", Tags = "BlkWort" }));
        await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "blk-p2", Title = "Blk P2", PriceCents = 100, Category = "blk-kat", Tags = "BlkWort, Anderes" }));
        var v = _app.Browser();

        var cats = await BlockAsync(v, "shop_categories", """{"heading":"Stöbern","by":"category","style":"chips","counts":"yes","allLink":"yes"}""");
        Assert.Contains("<h2>Stöbern</h2>", cats);
        Assert.Contains("href=\"/shop/categories/blk-kat\">Block Kat<span>2</span>", cats);
        Assert.Contains("href=\"/shop\">Alle</a>", cats);

        var tags = await BlockAsync(v, "shop_categories", """{"by":"tag","style":"list","counts":"no","allLink":"no"}""");
        Assert.Contains("<ul class=\"shop-linklist\">", tags);
        Assert.Contains("href=\"/shop?tag=BlkWort\">BlkWort ", tags);
        Assert.DoesNotContain("href=\"/shop\">Alle", tags);

        var search = await BlockAsync(v, "shop_search", """{"heading":"Suchen","placeholder":"Was darf es sein?"}""");
        Assert.Contains("<form method=\"get\" action=\"/shop\" class=\"shop-searchbar\">", search);
        Assert.Contains("placeholder=\"Was darf es sein?\"", search);
    }

    [Fact]
    public async Task The_small_cart_block_shows_the_visitors_own_cart_and_the_account_block_knows_who_is_signed_in()
    {
        await _shop.AddProductAsync("Block Warenkorb Ding", 1250);
        var v = _app.Browser();
        var empty = await BlockAsync(v, "shop_cart", """{"heading":"Ihr Korb"}""");
        Assert.Contains("Ihr Warenkorb ist leer", empty);

        await _shop.AddToCartAsync(v, "block-warenkorb-ding", 2);
        var full = await BlockAsync(v, "shop_cart", """{}""");
        Assert.Contains("2 × Block Warenkorb Ding", full);
        Assert.Contains("25,00 €", full);
        Assert.Contains("href=\"/shop/checkout\"", full);
        Assert.DoesNotContain("Block Warenkorb Ding", await BlockAsync(_app.Browser(), "shop_cart", """{}"""));     // somebody else's cart is none of his business

        // Account: a stranger is asked to sign in; a member sees himself and his orders.
        var guest = await BlockAsync(_app.Browser(), "shop_account", """{"heading":"Mein Bereich"}""");
        Assert.Contains("Melden Sie sich an", guest);
        Assert.Contains("href=\"/anmelden?returnUrl=%2Fkonto\"", guest);

        const string email = "block-kunde@example.org";
        var m = _app.Browser();
        await _shop.AddToCartAsync(m, "block-warenkorb-ding");
        var res = await _shop.CheckoutAsync(m, email: email, extra: new Dictionary<string, string> { ["create_account"] = "1", ["password"] = "ein langes passwort" });
        var number = ShopApp.NumberOf(res);
        var mine = await BlockAsync(m, "shop_account", """{"orders":"3"}""");
        Assert.Contains("Hallo ", mine);
        Assert.Contains($"href=\"/shop/order/{number}\"", mine);
        Assert.Contains("Zahlung offen", mine);
        Assert.Contains("href=\"/konto/bestellungen\"", mine);
        Assert.DoesNotContain("Melden Sie sich an", mine);
        // …and nobody else sees that order in his.
        var other = _app.Browser();
        await SignInAsync(other, email, "ein langes passwort");
        Assert.Contains(number, await BlockAsync(other, "shop_account", """{}"""));
        var stranger = await BlockAsync(_app.Browser(), "shop_account", """{}""");
        Assert.DoesNotContain(number, stranger);
    }
}
