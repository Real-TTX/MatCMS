using System.Net;
using System.Text.Json;
using MatCMS.Content;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The variables of a plugin's page in a block's texts: filled by the kind of field, conditions, "show only when".</summary>
public class BlockScopeTests
{
    private static BlockScope Scope() => new(new Dictionary<string, string>
    {
        ["product.title"] = "Tom & <b>Jerry</b>", ["product.on_sale"] = "1", ["product.empty"] = "", ["product.zero"] = "0", ["product.gallery_html"] = "<i>galerie</i>"
    }, new[] { "product.gallery_html" });

    [Fact]
    public void A_text_value_is_given_as_it_is_to_a_plain_field_and_encoded_in_html_and_only_html_variables_are_raw()
    {
        var s = Scope();
        Assert.Equal("Tom & <b>Jerry</b>", s.Resolve("{{product.title}}", html: false));                     // the block's own markup encodes where it draws it
        Assert.Equal("Tom &amp; &lt;b&gt;Jerry&lt;/b&gt;", s.Resolve("{{product.title}}", html: true));       // a field drawn as HTML never receives markup from a value
        Assert.Equal("<i>galerie</i>", s.Resolve("{{product.gallery_html}}", html: true));                    // the plugin's own HTML piece is what such a field is for
        Assert.Equal("", s.Resolve("{{product.gibtsnicht}}", html: false));                                    // an unknown variable is empty
        Assert.Equal("{{name}} {{ product.title }}", s.Resolve("{{name}} {{ product.title }}", html: false)); // only names with a dot, written exactly, are variables
    }

    [Fact]
    public void A_value_is_never_read_again_so_braces_in_it_do_nothing()
    {
        var s = new BlockScope(new Dictionary<string, string> { ["a.x"] = "{{a.y}}", ["a.y"] = "GEHEIM" }, Array.Empty<string>());
        Assert.Equal("{{a.y}}", s.Resolve("{{a.x}}", html: false));
    }

    [Fact]
    public void If_unless_and_negation_follow_whether_a_variable_has_a_value_and_zero_is_none()
    {
        var s = Scope();
        Assert.Equal("[Sale]", s.Resolve("[{{#if product.on_sale}}Sale{{/if}}]", false));
        Assert.Equal("[]", s.Resolve("[{{#if product.empty}}x{{/if}}]", false));
        Assert.Equal("[]", s.Resolve("[{{#if product.zero}}x{{/if}}]", false));                              // "0" counts as nothing: a count of 0 is a false condition
        Assert.Equal("[no]", s.Resolve("[{{#unless product.empty}}no{{/unless}}]", false));
        Assert.Equal("[ja]", s.Resolve("[{{#if !product.empty}}ja{{/if}}]", false));
        Assert.Equal("[]", s.Resolve("[{{#unless product.on_sale}}x{{/unless}}]", false));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("product.on_sale", true)]
    [InlineData("!product.on_sale", false)]
    [InlineData("product.empty", false)]
    [InlineData("!product.empty", true)]
    [InlineData("product.on_sale !product.empty", true)]
    [InlineData("product.on_sale, product.empty", false)]
    [InlineData("{{product.on_sale}}", true)]
    [InlineData("product.unknown", false)]
    [InlineData("!product.unknown", true)]
    public void A_block_is_shown_only_when_every_variable_of_its_condition_holds(string condition, bool shown) => Assert.Equal(shown, Scope().Visible(condition));

    [Fact]
    public void The_schema_decides_the_encoding_per_field_and_choices_and_unknown_fields_are_left_alone()
    {
        var def = new BlockDefinition
        {
            Fields =
            {
                new BlockField { Id = "text", Type = FieldType.Text }, new BlockField { Id = "body", Type = FieldType.RichText }, new BlockField { Id = "raw", Type = FieldType.Textarea, RawHtml = true },
                new BlockField { Id = "choice", Type = FieldType.Select },
                new BlockField { Id = "items", Type = FieldType.List, ItemFields = { new BlockField { Id = "t", Type = FieldType.Text }, new BlockField { Id = "b", Type = FieldType.RichText } } }
            }
        };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["text"] = "{{product.title}}", ["body"] = "<p>{{product.title}}</p>", ["raw"] = "{{product.title}}{{product.gallery_html}}", ["choice"] = "{{product.title}}", ["other"] = "{{product.title}}",
            ["items"] = new[] { new Dictionary<string, string> { ["t"] = "{{product.title}}", ["b"] = "{{product.title}}" } }
        });
        var o = System.Text.Json.Nodes.JsonNode.Parse(Scope().Apply(def, json))!;
        Assert.Equal("Tom & <b>Jerry</b>", (string)o["text"]!);
        Assert.Equal("<p>Tom &amp; &lt;b&gt;Jerry&lt;/b&gt;</p>", (string)o["body"]!);
        Assert.Equal("Tom &amp; &lt;b&gt;Jerry&lt;/b&gt;<i>galerie</i>", (string)o["raw"]!);
        Assert.Equal("{{product.title}}", (string)o["choice"]!);                                               // a choice holds a value of its list, never text with variables
        Assert.Equal("{{product.title}}", (string)o["other"]!);                                                // not in the schema
        Assert.Equal("Tom & <b>Jerry</b>", (string)o["items"]![0]!["t"]!);
        Assert.Equal("Tom &amp; &lt;b&gt;Jerry&lt;/b&gt;", (string)o["items"]![0]!["b"]!);
        Assert.Same(json, BlockScope.Empty.Apply(def, json));                                                  // no variables, no work: the same text comes back
    }
}

/// <summary>The shop's pages laid out in the block editor: created once per language, drawn by the shop's routes with the request's variables, locked in the page list, previewed with a real example.</summary>
public class ShopBlockPagesTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopBlockPagesTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = _app.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<Page> PageOf(string role) => Db(db => db.Pages.Include(p => p.Blocks).AsNoTracking().FirstAsync(p => p.OwnerPlugin == "shop" && p.PluginRole == role && p.Locale == "de"));

    /// <summary>Replaces a page's blocks with a section holding the given children (type, data).</summary>
    private async Task SetBlocksAsync(string role, params (string Type, Dictionary<string, string> Data)[] children)
    {
        var page = await PageOf(role);
        await Db(async db =>
        {
            db.ContentBlocks.RemoveRange(db.ContentBlocks.Where(b => b.PageId == page.Id));
            await db.SaveChangesAsync();
            var section = new ContentBlock { PageId = page.Id, BlockType = "el-section", SortOrder = 0, DataJson = "{}" };
            db.ContentBlocks.Add(section);
            await db.SaveChangesAsync();
            for (var i = 0; i < children.Length; i++)
                db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, ParentId = section.Id, BlockType = children[i].Type, SortOrder = i, DataJson = JsonSerializer.Serialize(children[i].Data) });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task Every_role_has_a_page_with_a_starting_layout_that_is_not_served_at_its_own_address()
    {
        var pages = await Db(db => db.Pages.Include(p => p.Blocks).AsNoTracking().Where(p => p.OwnerPlugin == "shop").ToListAsync());
        Assert.Equal(new[] { "account", "cart", "catalog", "categories", "checkout", "order", "product", "tags" }, pages.Where(p => p.Locale == "de").Select(p => p.PluginRole!).OrderBy(x => x).ToArray());
        Assert.All(pages, p => { Assert.Equal("_shop-" + p.PluginRole, p.Slug); Assert.True(p.IsPublished); Assert.False(p.ShowInNav); Assert.True(p.Blocks.Count >= 3, p.PluginRole); });
        Assert.Equal(pages.Select(p => p.PluginRole).Distinct().Count(), pages.Select(p => p.TranslationGroup).Distinct().Count());   // one translation group per role

        Assert.Equal(HttpStatusCode.NotFound, (await _app.Browser().GetAsync("/_shop-product")).StatusCode);
        var admin = await _shop.AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/_shop-product")).StatusCode);                    // not even the administrator: it has no address
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/_shop-product?editor=1")).StatusCode);                  // except in the editor's preview, without data
    }

    [Fact]
    public async Task A_product_page_is_drawn_from_its_blocks_with_the_products_values_a_condition_and_the_shops_own_buy_form()
    {
        await _shop.WithContextAsync(async (_, store) =>
        {
            await store.SaveProductAsync(new Product { Slug = "bp-ding", Title = "Bp <i>Ding</i> & Co", Summary = "Kurz & knapp", PriceCents = 1200, Active = true });
            await store.SaveProductAsync(new Product { Slug = "bp-angebot", Title = "Bp Angebot", PriceCents = 800, CompareCents = 1000, Active = true });
        });
        await SetBlocksAsync("product",
            ("el-heading", new() { ["text"] = "{{product.title}} | {{product.price}}", ["level"] = "h1" }),
            ("el-text", new() { ["body"] = "<p>{{product.summary}}</p>" }),
            ("el-heading", new() { ["text"] = "SONDERANGEBOT", ["level"] = "h2", ["_showIf"] = "product.on_sale" }),
            ("el-heading", new() { ["text"] = "NORMALPREIS", ["level"] = "h2", ["_showIf"] = "!product.on_sale" }),
            ("shop_slot", new() { ["slot"] = "product.form_html" }));
        var v = _app.Browser();

        var normal = await v.GetStringAsync("/shop/bp-ding");
        var shown = Decode(normal);
        Assert.Contains("Bp &lt;i&gt;Ding&lt;/i&gt; &amp; Co | 12,00", normal);                                      // the name is text, never markup
        Assert.Contains("<p>Kurz &amp; knapp</p>", normal);
        Assert.Contains("NORMALPREIS", shown); Assert.DoesNotContain("SONDERANGEBOT", shown);
        Assert.Contains("class=\"shop shop-block shop-slot\"", shown);                                               // the shop's piece, in its own wrapper
        Assert.Contains("action=\"/shop/cart\"", shown);                                                             // the buy form: the shop's, whole
        Assert.DoesNotContain("class=\"shop-nav\"", shown);                                                           // a block page brings no small bar of its own
        Assert.Contains("<style>", shown);                                                                            // but the shop's stylesheet

        var sale = Decode(await v.GetStringAsync("/shop/bp-angebot"));
        Assert.Contains("SONDERANGEBOT", sale); Assert.DoesNotContain("NORMALPREIS", sale);
        Assert.Contains("application/ld+json", sale);                                                                 // the structured data stays
    }

    [Fact]
    public async Task A_page_that_is_switched_off_or_has_a_blank_slot_name_falls_back_and_a_slot_only_draws_the_shops_html_variables()
    {
        await _shop.AddProductAsync("Fb Ding", 500);
        await SetBlocksAsync("product", ("shop_slot", new() { ["slot"] = "product.title" }), ("shop_slot", new() { ["slot"] = "product.image_html" }), ("el-heading", new() { ["text"] = "EIGENE ÜBERSCHRIFT" }));
        var v = _app.Browser();
        var page = Decode(await v.GetStringAsync("/shop/fb-ding"));
        Assert.DoesNotContain("<div class=\"shop shop-block shop-slot\">Fb Ding", page);                              // "product.title" is text, not one of the shop's HTML pieces
        Assert.Contains("EIGENE ÜBERSCHRIFT", page);

        await _shop.BlockPagesAsync(false);
        var fallback = Decode(await v.GetStringAsync("/shop/fb-ding"));
        await _shop.BlockPagesAsync(true);
        Assert.DoesNotContain("EIGENE ÜBERSCHRIFT", fallback);                                                        // switched off: the template part draws the page again
        Assert.Contains("action=\"/shop/cart\"", fallback);
        Assert.Contains("Fb Ding", fallback);
    }

    [Fact]
    public async Task The_page_list_locks_a_plugins_pages_and_their_editor_lists_the_variables_and_previews_a_real_product()
    {
        await _shop.AddProductAsync("Ed Ding", 900);
        var admin = await _shop.AdminAsync();
        var product = await PageOf("product");

        var list = Decode(await admin.GetStringAsync("/admin/pages"));
        Assert.Contains("Seite des Plugins", list);
        Assert.DoesNotContain("_shop-product", list);                                      // no address to click on
        var token = AppFactory.Token(list)!;
        var del = await admin.PostAsync($"/admin/pages?handler=Delete&id={product.Id}", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
        Assert.NotNull(await Db(db => db.Pages.FindAsync(product.Id).AsTask()));                                      // it is still there

        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync($"/admin/pages/edit/{product.Id}")).StatusCode);   // the classic editor sends it to editor v2
        var editor = Decode(await admin.GetStringAsync($"/admin/pages/editor/{product.Id}"));
        Assert.Contains("id=\"ev2-vars\"", editor); Assert.DoesNotContain("ev2-vars-open", editor);          // the list sits in the inspector, not in the header bar
        Assert.Contains("data-var=\"product.title\"", editor);
        Assert.Contains("data-var=\"product.image_html\"", editor);
        Assert.Contains("data-var=\"member.name\"", editor);                                                          // what every plugin page can use
        Assert.Contains("<iframe id=\"ev2-preview\" src=\"/shop/", editor);                                           // the preview is a real product's page
        Assert.Contains("?editor=1", editor);
        Assert.Contains("_showIf", editor);                                                                           // the "show only when" field is in the schema
        Assert.Contains("name=\"Meta.Slug\" value=\"_shop-product\"", editor);                                        // the address cannot be edited (hidden field)
    }

    [Fact]
    public async Task The_live_preview_draws_the_unsaved_draft_with_the_variables_of_the_plugins_example()
    {
        await _shop.AddProductAsync("Lp Ding", 700);
        var admin = await _shop.AdminAsync();
        var product = await PageOf("product");
        var sample = await _shop.WithContextAsync((_, store) => store.FindProductsAsync(new ProductQuery { Take = 1 }));
        var draft = JsonSerializer.Serialize(new[]
        {
            new { id = -1, blockType = "el-section", parentId = (int?)null, sortOrder = 0, dataJson = "{}" },
            new { id = -2, blockType = "el-heading", parentId = (int?)-1, sortOrder = 0, dataJson = "{\"text\":\"VORSCHAU {{product.title}}\"}" }
        });
        var edit = await admin.GetStringAsync($"/admin/pages/editor/{product.Id}");
        var res = await admin.PostAsync($"/admin/pages/edit/{product.Id}?handler=RenderPreview", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(edit)!, ["Draft"] = draft }));
        var html = Decode(await res.Content.ReadAsStringAsync());
        Assert.Contains("VORSCHAU " + sample.Items[0].Title, html);
    }

    [Fact]
    public async Task Back_to_default_restores_the_starting_layout_and_the_link_pickers_do_not_offer_the_pages()
    {
        var admin = await _shop.AdminAsync();
        var cart = await PageOf("cart");
        await SetBlocksAsync("cart", ("el-heading", new() { ["text"] = "NUR DAS" }));
        var edit = await admin.GetStringAsync($"/admin/pages/editor/{cart.Id}");
        var res = await admin.PostAsync($"/admin/pages/edit/{cart.Id}?handler=ResetPlugin", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(edit)! }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var after = await PageOf("cart");
        Assert.True(after.Blocks.Count >= 5);
        Assert.DoesNotContain(after.Blocks, b => b.DataJson.Contains("NUR DAS"));

        Assert.DoesNotContain("_shop-", await admin.GetStringAsync("/admin/api/link-targets"));
        Assert.DoesNotContain("_shop-", await admin.GetStringAsync("/admin/api/pages"));
    }

    [Fact]
    public async Task The_cart_and_the_other_pages_use_their_blocks_too_and_keep_their_forms()
    {
        await _shop.AddProductAsync("Wk Ding", 2500);
        var v = _app.Browser();
        await _shop.AddToCartAsync(v, "wk-ding", 2);
        var cart = Decode(await v.GetStringAsync("/shop/cart"));
        Assert.Contains("class=\"shop shop-block shop-slot\"", cart);
        Assert.Contains("class=\"shop-cartlines\"", cart);                                                            // the lines form (a slot)
        Assert.Contains("class=\"shop-cartsum\"", cart);                                                              // the summary (another slot, in the second column)
        Assert.Contains("2 Artikel", cart);
        var checkout = Decode(await v.GetStringAsync("/shop/checkout"));
        Assert.Contains("class=\"shop-checkoutform\"", checkout);
        Assert.Contains("name=\"terms\"", checkout);                                                                  // the checkout form: whole
        Assert.Contains("class=\"shop-fpanel\"", Decode(await v.GetStringAsync("/shop")));
    }
}
