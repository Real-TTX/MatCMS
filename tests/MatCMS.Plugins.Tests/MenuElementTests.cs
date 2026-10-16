using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Declaring link targets and menu elements: what a plugin may say and what the host refuses.</summary>
public class MenuElementDeclarationTests
{
    [Theory]
    [InlineData("Plugin.MenuElement(new MenuElementSpec(\"Gross\", \"L\"), async (c, t) => null);")]
    [InlineData("Plugin.MenuElement(new MenuElementSpec(\"ok\", \"\"), async (c, t) => null);")]
    [InlineData("Plugin.MenuElement(new MenuElementSpec(\"ok\", \"L\") { Icon = \"Bad Icon\" }, async (c, t) => null);")]
    [InlineData("Plugin.MenuElement(new MenuElementSpec(\"ok\", \"L\") { DefaultMenu = \"a b\" }, async (c, t) => null);")]
    [InlineData("Plugin.MenuElement(new MenuElementSpec(\"ok\", \"L\"), async (c, t) => null); Plugin.MenuElement(new MenuElementSpec(\"ok\", \"M\"), async (c, t) => null);")]
    [InlineData("Plugin.LinkTargets(\"\", async (c, t) => new LinkTarget[0]);")]
    [InlineData("Plugin.LinkTargets(\"G\", async (c, t) => new LinkTarget[0]); Plugin.LinkTargets(\"G\", async (c, t) => new LinkTarget[0]);")]
    public async Task A_bad_declaration_stops_the_plugin_from_loading(string code)
    {
        using var h = new Harness();
        h.AddScript("deklariert", code);
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("deklariert"));
    }

    [Fact]
    public async Task A_good_declaration_is_listed_with_its_key_and_the_choices_for_the_menu_form_carry_it()
    {
        using var h = new Harness();
        h.AddScript("gut", "Plugin.MenuElement(new MenuElementSpec(\"karte\", \"Meine Karte\") { Icon = \"star\", DefaultMenu = \"toolbar\" }, async (c, t) => new MenuEntry(\"K\", \"/k\")); Plugin.LinkTargets(\"Dinge\", async (c, t) => new[] { new LinkTarget(\"Eins\", \"/dinge/eins\") });");
        await h.Runner.RunAllAsync();
        Assert.Equal("gut.karte", Assert.Single(h.Registry.MenuElements).Key);
        var contributions = new PluginContributions(h.Registry);
        Assert.Equal(("Meine Karte", "plugin:gut.karte"), Assert.Single(contributions.MenuElementChoices()));
        Assert.True(contributions.HasMenuElement("gut.karte"));
        Assert.False(contributions.HasMenuElement("gut.nope"));
    }

    [Fact]
    public async Task Link_targets_keep_only_safe_addresses_cap_the_list_and_skip_a_plugin_that_fails()
    {
        using var h = new Harness();
        h.AddScript("ziele", """
            Plugin.LinkTargets("Gut", async (c, t) => new[] { new LinkTarget("Eins", "/eins"), new LinkTarget("Böse", "javascript:alert(1)"), new LinkTarget("Offen", "//evil.example"), new LinkTarget("", "/leer"),
                                                              new LinkTarget("Extern", "https://example.org/x"), new LinkTarget("Leer im Pfad", "/a b") });
            Plugin.LinkTargets("Viele", async (c, t) => System.Linq.Enumerable.Range(0, 500).Select(i => new LinkTarget("T" + i, "/t/" + i)));
            Plugin.LinkTargets("Kaputt", (c, t) => throw new System.InvalidOperationException("ziele kaputt"));
            Plugin.LinkTargets("Leer", async (c, t) => new LinkTarget[0]);
            """);
        await h.Runner.RunAllAsync();
        var groups = await new PluginContributions(h.Registry).LinkTargetsAsync(h.Services);
        Assert.Equal(new[] { "Gut", "Viele" }, groups.Select(g => g.Group).ToArray());
        Assert.Equal(new[] { "/eins", "https://example.org/x" }, groups[0].Items.Select(i => i.Url).ToArray());
        Assert.Equal(300, groups[1].Items.Count);
        Assert.Contains("ziele kaputt", string.Join("\n", h.Registry.LogOf("ziele")));
    }
}

/// <summary>Menu elements on a real site: the shop's cart, account, search and "Shop" are ordinary menu items, drawn per visitor, themed by the template, and what an editor removes stays removed.</summary>
public class MenuElementSiteTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public MenuElementSiteTests(AppFactory app) { _app = app; PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private static string Nav(string html)
    {
        var nav = html[html.IndexOf("class=\"main-nav\"", StringComparison.Ordinal)..];
        return nav[..nav.IndexOf("</nav>", StringComparison.Ordinal)];
    }

    [Fact]
    public async Task The_shop_adds_its_pieces_once_as_menu_items_for_every_language_and_does_not_add_them_a_second_time()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var items = db.MenuItems.Where(m => m.Url.StartsWith("plugin:shop.")).ToList();
        Assert.Equal(new[] { "header:plugin:shop.shop", "toolbar:plugin:shop.account", "toolbar:plugin:shop.cart", "toolbar:plugin:shop.search" },
            items.Where(i => i.Locale == "de").Select(i => i.Menu + ":" + i.Url).OrderBy(x => x).ToArray());
        var before = items.Count;
        await scope.ServiceProvider.GetRequiredService<PluginRunner>().ReloadAsync(db.Plugins.First(p => p.Key == "shop").Id);
        Assert.Equal(before, db.MenuItems.Count(m => m.Url.StartsWith("plugin:shop.")));
        Assert.True(db.SiteSettings.Any(s => s.Key == "menu.seeded.shop.cart"));
    }

    [Fact]
    public async Task The_search_box_is_a_template_part_in_the_icon_strip_and_the_template_can_replace_it()
    {
        var v = _app.Browser();
        var html = Decode(await v.GetStringAsync("/"));
        Assert.Contains("<form class=\"mat-search\" role=\"search\" method=\"get\" action=\"/shop\">", html);
        Assert.Contains("placeholder=\"Produkte suchen\"", html);
        Assert.DoesNotContain("<a class=\"hicon\" href=\"/shop\"", html);                 // the form is drawn as it is, never wrapped in a link
        // The default look comes with the page's stylesheet; the layout carries the elements it shows.
        Assert.Contains("data-els=\"", html);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var tpl = db.Templates.First(t => t.IsActive);
        var before = tpl.PartsJson;
        try
        {
            tpl.PartsJson = "{\"shop.search\":\"<form class=\\\"mein-suchfeld\\\" action=\\\"{{action}}\\\"><input name=\\\"q\\\" placeholder=\\\"{{placeholder}}\\\"></form>\"}"; await db.SaveChangesAsync();
            var own = Decode(await _app.Browser().GetStringAsync("/"));
            Assert.Contains("<form class=\"mein-suchfeld\" action=\"/shop\">", own);
            Assert.DoesNotContain("class=\"mat-search\"", own);

            // A layout of its own places the strip with {{toolbar}}: the box is there too, raw.
            tpl.PartsJson = before; tpl.LayoutHtml = "<header>{{toolbar}}</header><main>{{content}}</main>"; await db.SaveChangesAsync();
            var placed = Decode(await _app.Browser().GetStringAsync("/"));
            Assert.Contains("class=\"mat-search\"", placed);
            Assert.Contains("href=\"/shop/cart\"", placed);
        }
        finally { tpl.PartsJson = before; tpl.LayoutHtml = ""; await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task A_layout_places_an_element_where_it_wants_it_with_element_tokens_and_the_strip_does_not_draw_it_a_second_time()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var tpl = db.Templates.First(t => t.IsActive);
        var before = tpl.LayoutHtml;
        try
        {
            tpl.LayoutHtml = "<header><div class=\"mein-platz\">{{element:shop.search}}</div><span class=\"warenkorb\">{{element:shop.cart}}</span><i>{{element:gibt.es.nicht}}</i>{{toolbar}}</header><main>{{content}}</main>";
            await db.SaveChangesAsync();
            var html = Decode(await _app.Browser().GetStringAsync("/"));
            var head = html[html.IndexOf("<header>", StringComparison.Ordinal)..html.IndexOf("</header>", StringComparison.Ordinal)];
            Assert.Contains("<div class=\"mein-platz\"><form class=\"mat-search\"", head);                 // the part, exactly where the token stands
            Assert.Contains("<span class=\"warenkorb\"><a class=\"hicon\" href=\"/shop/cart\"", head);      // an icon element as its icon link
            Assert.Contains("<i></i>", head);                                                              // an element that does not exist says nothing
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(head, "class=\"mat-search\"").Count);   // placed once: the strip leaves it out
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(head, "href=\"/shop/cart\"").Count);
            Assert.Contains("href=\"/anmelden?returnUrl=%2Fkonto\"", head);                              // what the layout did not place stays in the strip
        }
        finally { tpl.LayoutHtml = before; await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task The_template_editor_offers_the_toolbar_and_every_element_as_placeholders_and_the_starter_body_has_the_icon_strip()
    {
        var admin = await _shop.AdminAsync();
        int id;
        using (var scope = _app.Services.CreateScope()) id = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>().Templates.First(t => t.IsActive).Id;
        var html = Decode(await admin.GetStringAsync($"/admin/templates/edit/{id}"));
        foreach (var token in new[] { "{{toolbar}}", "{{element:shop.search}}", "{{element:shop.cart}}", "{{element:shop.account}}", "{{element:shop.shop}}" }) Assert.Contains(token, html);
        Assert.Matches(@"header-icons.{1,30}{{toolbar}}", html);                              // "Standard einfügen" now has the strip, so the search is where people expect it
    }

    [Fact]
    public async Task The_link_picker_lists_pages_then_what_plugins_offer_and_for_the_menu_form_also_the_elements()
    {
        await _shop.WithContextAsync(async (_, s) =>
        {
            if (await s.CategoryBySlugAsync("pick-oben") is null) await s.SaveCategoryAsync(new Category { Slug = "pick-oben", Name = "Pick Oben" });
            var parent = (await s.CategoryBySlugAsync("pick-oben"))!;
            if (await s.CategoryBySlugAsync("pick-unten") is null) await s.SaveCategoryAsync(new Category { Slug = "pick-unten", Name = "Pick Unten", ParentId = parent.Id });
        });
        await _shop.AddProductAsync("Pick Produkt", 900, category: "pick-unten");
        var admin = await _shop.AdminAsync();

        var json = JsonNode.Parse(await admin.GetStringAsync("/admin/api/link-targets"))!.AsArray();
        string[] Titles(string group) => json.First(g => g!["group"]!.GetValue<string>() == group)!["items"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()).ToArray();
        string[] Urls(string group) => json.First(g => g!["group"]!.GetValue<string>() == group)!["items"]!.AsArray().Select(i => i!["url"]!.GetValue<string>()).ToArray();
        Assert.Equal("Seiten", json[0]!["group"]!.GetValue<string>());
        Assert.Contains("/kontakt", Urls("Seiten"));
        Assert.Contains("/shop/cart", Urls("Shop – Seiten"));
        Assert.Contains("Pick Oben › Pick Unten", Titles("Shop – Kategorien"));               // a subcategory with its path
        Assert.Contains("/shop/categories/pick-unten", Urls("Shop – Kategorien"));
        Assert.Contains("/shop/pick-produkt", Urls("Shop – Produkte"));
        Assert.Contains("/blog", Urls("Blog – Seiten"));
        Assert.DoesNotContain(json, g => g!["group"]!.GetValue<string>() == "Plugin-Elemente");  // block links and buttons get no elements

        var withElements = JsonNode.Parse(await admin.GetStringAsync("/admin/api/link-targets?elements=true"))!.AsArray();
        var els = withElements.First(g => g!["group"]!.GetValue<string>() == "Plugin-Elemente")!["items"]!.AsArray().Select(i => i!["url"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "plugin:shop.account", "plugin:shop.cart", "plugin:shop.categories", "plugin:shop.search", "plugin:shop.shop", "plugin:shop.tags" }, els.OrderBy(x => x).ToArray());

        // Admin only.
        var anon = await _app.Browser().GetAsync("/admin/api/link-targets");
        Assert.NotEqual(HttpStatusCode.OK, anon.StatusCode);
    }

    [Fact]
    public async Task The_menu_form_takes_a_plugin_element_that_exists_and_refuses_one_that_does_not()
    {
        var admin = await _shop.AdminAsync();
        async Task<HttpResponseMessage> Create(string label, string url)
        {
            var page = await admin.GetStringAsync("/admin/menus/create?menu=footer");
            return await admin.PostAsync("/admin/menus/create?menu=footer", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["Menu"] = "footer", ["Locale"] = "de", ["Label"] = label, ["Url"] = url
            }));
        }
        Assert.Equal(HttpStatusCode.Redirect, (await Create("Mein Korb", "plugin:shop.cart")).StatusCode);
        var refused = await Create("Nichts", "plugin:nobody.nothing");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("gibt es nicht", await refused.Content.ReadAsStringAsync());

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        try
        {
            Assert.False(db.MenuItems.Any(m => m.Label == "Nichts"));
            // The footer draws it per visitor, with the element's own text — and no trace of "plugin:".
            var footer = Decode(await _app.Browser().GetStringAsync("/"));
            Assert.DoesNotContain("plugin:", footer);
        }
        finally { db.MenuItems.RemoveRange(db.MenuItems.Where(m => m.Label == "Mein Korb")); await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task An_element_that_is_gone_says_nothing_or_fails_is_left_out_and_never_shows_up_as_a_dead_link_while_the_rest_is_drawn()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<PluginRunner>();
        var plugin = new MatCMS.Models.Plugin { Key = "eltest", Name = "eltest", Version = "1", Kind = MatCMS.Models.PluginKind.Script, Enabled = true, Code = """
            Plugin.MenuElement(new MenuElementSpec("hallo", "Hallo") { Icon = "star" }, async (ctx, ct) => new MenuEntry("Hallo Welt", "/hallo") { Badge = "7" });
            Plugin.MenuElement(new MenuElementSpec("leer", "Leer"), async (ctx, ct) => null);
            Plugin.MenuElement(new MenuElementSpec("kaputt", "Kaputt"), (ctx, ct) => throw new System.InvalidOperationException("element kaputt"));
            Plugin.MenuElement(new MenuElementSpec("boese", "Böse"), async (ctx, ct) => new MenuEntry("Böse", "javascript:alert(1)"));
            Plugin.MenuElement(new MenuElementSpec("drop", "Drop"), async (ctx, ct) => new MenuEntry("Aufklappen", "/auf") { Children = new[] { new MenuEntry("Kind", "/kind"), new MenuEntry("Böses Kind", "javascript:x") } });
            Plugin.MenuElement(new MenuElementSpec("fuss", "Fuß") { DefaultMenu = "footer" }, async (ctx, ct) => new MenuEntry("Im Fuß", "/fuss"));
            """ };
        db.Plugins.Add(plugin); await db.SaveChangesAsync();
        await runner.ReloadAsync(plugin.Id);
        var added = new List<MatCMS.Models.MenuItem>();
        try
        {
            var sort = 500;
            foreach (var key in new[] { "hallo", "leer", "kaputt", "boese", "drop", "nobody.gone" })
            {
                var url = key.Contains('.') ? "plugin:" + key : "plugin:eltest." + key;
                var item = new MatCMS.Models.MenuItem { Menu = "header", Locale = "de", Label = "Stored " + key, Url = url, SortOrder = sort++ };
                db.MenuItems.Add(item); added.Add(item);
            }
            await db.SaveChangesAsync();
            // A child hung under an element that says nothing goes with it.
            var leer = added.First(i => i.Url == "plugin:eltest.leer");
            var orphan = new MatCMS.Models.MenuItem { Menu = "header", Locale = "de", Label = "Waisenkind", Url = "/waise", ParentId = leer.Id, SortOrder = 1 };
            db.MenuItems.Add(orphan); added.Add(orphan); await db.SaveChangesAsync();

            var res = await _app.Browser().GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);                                   // a failing element does not take the page down
            var nav = Nav(Decode(await res.Content.ReadAsStringAsync()));
            Assert.Contains("<a href=\"/hallo\">Hallo Welt <span class=\"hbadge inline\">7</span></a>", nav);
            Assert.Contains("<span class=\"mat-hassub\"><a href=\"/auf\">Aufklappen</a>", nav);
            Assert.Contains("href=\"/kind\">Kind</a>", nav);
            Assert.DoesNotContain("Böses Kind", nav); Assert.DoesNotContain("javascript:", nav);
            Assert.DoesNotContain("Stored", nav); Assert.DoesNotContain("plugin:", nav); Assert.DoesNotContain("Waisenkind", nav);
            Assert.Contains("element kaputt", string.Join("\n", scope.ServiceProvider.GetRequiredService<PluginRegistry>().LogOf("eltest")));

            // The element with a default menu was put into the footer once; deleting it sticks through a reload.
            var fuss = db.MenuItems.Where(m => m.Url == "plugin:eltest.fuss").ToList();
            Assert.NotEmpty(fuss);
            db.MenuItems.RemoveRange(fuss); await db.SaveChangesAsync();
            await runner.ReloadAsync(plugin.Id);
            Assert.Empty(db.MenuItems.Where(m => m.Url == "plugin:eltest.fuss"));
        }
        finally
        {
            db.MenuItems.RemoveRange(db.MenuItems.Where(m => m.Url.StartsWith("plugin:eltest.") || m.Label == "Waisenkind" || m.Label.StartsWith("Stored ")));
            db.SiteSettings.RemoveRange(db.SiteSettings.Where(s => s.Key.StartsWith("menu.seeded.eltest.")));
            plugin.Enabled = false; await db.SaveChangesAsync();
            await runner.ReloadAsync(plugin.Id);
        }
    }

    [Fact]
    public async Task The_blog_has_an_overview_at_its_own_address_and_offers_its_posts_as_link_targets()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var when = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Posts (Title, Slug, Excerpt, ContentHtml, Tags, AttachmentsJson, GalleryJson, GalleryLayout, Locale, IsPublished, PublishedAt, CreatedAt, UpdatedAt) VALUES ('Übersichtsbeitrag', 'uebersichtsbeitrag', 'Kurz.', '<p>x</p>', '', '[]', '[]', 'carousel', 'de', 1, {when}, {when}, {when})");
        var overview = await _app.Browser().GetAsync("/blog");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Contains("Übersichtsbeitrag", Decode(await overview.Content.ReadAsStringAsync()));

        var admin = await _shop.AdminAsync();
        var json = JsonNode.Parse(await admin.GetStringAsync("/admin/api/link-targets"))!.AsArray();
        var posts = json.First(g => g!["group"]!.GetValue<string>() == "Blog – Beiträge")!["items"]!.AsArray().Select(i => i!["url"]!.GetValue<string>()).ToArray();
        Assert.Contains("/blog/uebersichtsbeitrag", posts);
    }
}
