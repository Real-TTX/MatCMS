using System.Net;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Template parts: HTML files a plugin draws a page with and a site's template may replace — the engine, the declaration, the template editor, and the shop's three parts.</summary>
public class PartRendererTests
{
    private static string R(string html, Dictionary<string, string> values, params string[] htmlNames) => PartRenderer.Render(html, values, htmlNames);

    [Fact]
    public void Values_are_encoded_unless_the_placeholder_is_html_and_unknown_or_missing_ones_are_empty()
    {
        var v = new Dictionary<string, string> { ["t"] = "<b>\"Tom\" & Jerry</b>", ["h"] = "<i>fertig</i>" };
        Assert.Equal("&lt;b&gt;&quot;Tom&quot; &amp; Jerry&lt;/b&gt;|<i>fertig</i>||", R("{{t}}|{{h}}|{{gibtsnicht}}|{{t_fehlt}}", v, "h"));
        Assert.Equal("", R("", v));
        Assert.Equal("x", R("x", v));
    }

    [Fact]
    public void A_value_is_never_read_again_so_braces_in_it_do_nothing()
    {
        var v = new Dictionary<string, string> { ["a"] = "{{b}}{{#if c}}x{{/if}}", ["b"] = "SECRET", ["c"] = "1" };
        Assert.Equal("{{b}}{{#if c}}x{{/if}}", R("{{a}}", v, "a"));                           // html or not, what was put in stays as it is
        Assert.Equal("{{b}}{{#if c}}x{{/if}}", R("{{a}}", v));
    }

    [Fact]
    public void If_keeps_its_inside_only_for_a_value_and_unless_only_for_none_also_around_other_placeholders()
    {
        var v = new Dictionary<string, string> { ["sale"] = "-20 %", ["empty"] = "", ["blank"] = "  " };
        Assert.Equal("[-20 %]", R("[{{#if sale}}{{sale}}{{/if}}]", v));
        Assert.Equal("[]", R("[{{#if empty}}x{{/if}}]", v));
        Assert.Equal("[]", R("[{{#if blank}}x{{/if}}]", v));                                  // blank is empty
        Assert.Equal("[]", R("[{{#if missing}}x{{/if}}]", v));
        Assert.Equal("[kein Angebot]", R("[{{#unless empty}}kein Angebot{{/unless}}]", v));
        Assert.Equal("[]", R("[{{#unless sale}}kein Angebot{{/unless}}]", v));
        Assert.Equal("a|b", R("{{#if sale}}a{{/if}}|{{#unless sale}}x{{/unless}}b", v));
        // Nested conditions of different kinds work; the inside spans lines.
        Assert.Equal("ok", R("{{#if sale}}{{#unless empty}}ok{{/unless}}{{/if}}", v));
        Assert.Equal("1\n2", R("{{#if sale}}1\n2{{/if}}", v));
    }
}

/// <summary>A plugin's template part from end to end: the declaration is checked, the active template's version wins, a blank or missing one falls back to the default.</summary>
public class TemplatePartPluginTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public TemplatePartPluginTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [Theory]
    [InlineData("new TemplatePartSpec(\"Gross\", \"L\", \"x\")")]
    [InlineData("new TemplatePartSpec(\"a b\", \"L\", \"x\")")]
    [InlineData("new TemplatePartSpec(\"ok\", \"\", \"x\")")]
    [InlineData("new TemplatePartSpec(\"ok\", \"L\", \" \")")]
    [InlineData("new TemplatePartSpec(\"ok\", \"L\", \"x\") { Placeholders = new[] { new TemplatePlaceholder(\"Bad Name\", \"h\") } }")]
    [InlineData("new TemplatePartSpec(\"ok\", \"L\", \"x\") { Placeholders = new[] { new TemplatePlaceholder(\"a\", \"h\"), new TemplatePlaceholder(\"a\", \"h\") } }")]
    public async Task A_part_with_a_bad_name_label_default_or_placeholder_is_refused_when_the_plugin_loads(string spec)
    {
        using var h = new Harness();
        h.AddScript("teil", $"Plugin.TemplatePart({spec});");
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("teil"));
    }

    [Fact]
    public async Task A_part_declared_twice_is_refused_and_a_good_one_is_listed_with_its_key_and_file_name()
    {
        using var h = new Harness();
        h.AddScript("zwei", "Plugin.TemplatePart(new TemplatePartSpec(\"box\", \"B\", \"x\")); Plugin.TemplatePart(new TemplatePartSpec(\"box\", \"B\", \"y\"));");
        h.AddScript("gut", "Plugin.TemplatePart(new TemplatePartSpec(\"box\", \"Box\", \"<b>{{x}}</b>\") { Placeholders = new[] { new TemplatePlaceholder(\"x\", \"Wert\") } });");
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("zwei"));
        var part = Assert.Single(h.Registry.TemplateParts);
        Assert.Equal("gut.box", part.Key);
        Assert.Equal("gut-box.html", part.FileName);
    }

    [Fact]
    public async Task The_active_templates_version_is_rendered_and_a_blank_one_or_none_gives_the_default_and_a_foreign_part_does_not_exist()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<PluginRunner>();
        var plugin = new MatCMS.Models.Plugin { Key = "teiltest", Name = "teiltest", Version = "1", Kind = MatCMS.Models.PluginKind.Script, Enabled = true, Code = """
            Plugin.TemplatePart(new TemplatePartSpec("box", "Box", "<p class=\"std\">{{name}}</p>{{raw}}") { Placeholders = new[] { new TemplatePlaceholder("name", "Name"), new TemplatePlaceholder("raw", "Roh", true) } });
            Plugin.Route("/teiltest", ctx => PluginResult.Fragment(ctx.RenderPart("box", new Dictionary<string, string> { ["name"] = "<Tom>", ["raw"] = "<u>fett</u>" })));
            Plugin.Route("/teilfremd", ctx => { try { ctx.RenderPart("shop-card", new Dictionary<string, string>()); return PluginResult.Fragment("no"); } catch (System.InvalidOperationException e) { return PluginResult.Fragment("fehler"); } });
            """ };
        db.Plugins.Add(plugin); await db.SaveChangesAsync(); await runner.ReloadAsync(plugin.Id);
        var tpl = db.Templates.First(t => t.IsActive);
        var before = tpl.PartsJson;
        try
        {
            var visitor = _app.Browser();
            tpl.PartsJson = "{}"; await db.SaveChangesAsync();
            Assert.Contains("<p class=\"std\">&lt;Tom&gt;</p><u>fett</u>", await visitor.GetStringAsync("/teiltest"));         // the default; the value encoded, the html placeholder not

            tpl.PartsJson = "{\"teiltest.box\":\"<section id=\\\"meins\\\">{{name}}!</section>\"}"; await db.SaveChangesAsync();
            var own = await visitor.GetStringAsync("/teiltest");
            Assert.Contains("<section id=\"meins\">&lt;Tom&gt;!</section>", own);
            Assert.DoesNotContain("class=\"std\"", own);

            tpl.PartsJson = "{\"teiltest.box\":\"   \"}"; await db.SaveChangesAsync();                                      // blank = none
            Assert.Contains("class=\"std\"", await visitor.GetStringAsync("/teiltest"));
            tpl.PartsJson = "{\"other.box\":\"<i>x</i>\"}"; await db.SaveChangesAsync();                                    // another plugin's key does not leak in
            Assert.Contains("class=\"std\"", await visitor.GetStringAsync("/teiltest"));
            Assert.Equal("fehler", await visitor.GetStringAsync("/teilfremd"));                                              // a part another plugin declared is not this plugin's to render
        }
        finally
        {
            tpl.PartsJson = before; plugin.Enabled = false; await db.SaveChangesAsync(); await runner.ReloadAsync(plugin.Id);
        }
    }
}

/// <summary>The shop's three parts in the template editor and on the pages: the default is the markup it always had, an own version replaces it, nothing a visitor typed can break out of it.</summary>
public class ShopTemplatePartTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopTemplatePartTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() { _shop = await ShopApp.StartAsync(_app); await _shop.BlockPagesAsync(false); }   // the parts draw the pages here: the shop's block pages are off
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<T> WithTemplateAsync<T>(string partsJson, Func<Task<T>> work)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var tpl = db.Templates.First(t => t.IsActive);
        var before = tpl.PartsJson;
        tpl.PartsJson = partsJson; await db.SaveChangesAsync();
        try { return await work(); }
        finally { tpl.PartsJson = before; await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task The_product_page_the_card_and_the_catalogue_follow_the_templates_own_parts_and_encode_what_a_visitor_or_a_product_contains()
    {
        await _shop.AddProductAsync("<script>x</script> Teilprodukt", 1250);
        var slug = MatCMS.Pages.Admin.Pages.IndexModel.Slugify("<script>x</script> Teilprodukt");
        var visitor = _app.Browser();
        var standard = Decode(await visitor.GetStringAsync("/shop/" + slug));
        Assert.Contains("class=\"shop-product", standard);                                   // the default is the page as it was
        Assert.Contains("name=\"action\" value=\"add\"", standard);

        const string parts = """
            {"shop.product":"<div id=\"meine-seite\"><h2>{{title}}</h2><b>{{price}}</b>{{form_html}}{{#if on_sale}}SALE{{/if}}</div>",
             "shop.card":"<i class=\"meine-karte\" data-url=\"{{url}}\">{{title}}</i>",
             "shop.catalog":"<main class=\"mein-katalog\">{{count_html}}{{grid_html}}</main>"}
            """;
        await WithTemplateAsync(parts.Replace("\n", ""), async () =>
        {
            var page = await visitor.GetStringAsync("/shop/" + slug);
            Assert.Contains("<div id=\"meine-seite\">", page);
            Assert.Contains("<b>12,50 €</b>", Decode(page));
            Assert.Contains("name=\"action\" value=\"add\"", page);                           // the cart form is the shop's, whatever the template says
            Assert.DoesNotContain("<script>x</script>", page.Replace("application/ld+json", ""));      // the product's own title is data, never markup
            Assert.DoesNotContain("class=\"shop-product", page);
            Assert.DoesNotContain("SALE", page);                                                // a placeholder condition that is not met drops its inside

            var list = Decode(await visitor.GetStringAsync("/shop?q=Teilprodukt"));
            Assert.Contains("<main class=\"mein-katalog\">", list);
            Assert.Contains("<i class=\"meine-karte\" data-url=\"/shop/", list);
            Assert.DoesNotContain("class=\"shop-card\"", list);
            Assert.Contains("&lt;script&gt;x&lt;/script&gt; Teilprodukt", await visitor.GetStringAsync("/shop?q=Teilprodukt"));
            return 0;
        });
        // Back at the default once the template's own versions are gone.
        Assert.Contains("class=\"shop-product", await visitor.GetStringAsync("/shop/" + slug));
        Assert.Contains("class=\"shop-card\"", await visitor.GetStringAsync("/shop?q=Teilprodukt"));
    }

    [Fact]
    public async Task The_cards_in_a_page_block_and_in_related_products_use_the_template_card_too()
    {
        await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "teil-karte-a", Title = "Teil Karte A", PriceCents = 100, Category = "", Tags = "TeilWort" }));
        await _shop.WithContextAsync((_, s) => s.SaveProductAsync(new Product { Slug = "teil-karte-b", Title = "Teil Karte B", PriceCents = 100, Tags = "TeilWort" }));
        var visitor = _app.Browser();
        await WithTemplateAsync("{\"shop.card\":\"<em class=\\\"k\\\">{{title}}|{{price}}|{{on_sale}}</em>\"}", async () =>
        {
            var related = Decode(await visitor.GetStringAsync("/shop/teil-karte-a"));
            Assert.Contains("<em class=\"k\">Teil Karte B|1,00 €|</em>", related);
            return 0;
        });
    }

    [Fact]
    public async Task The_template_editor_lists_the_shops_parts_as_files_and_saves_an_own_version_only_when_it_differs_from_the_default()
    {
        var admin = await _shop.AdminAsync();
        int id;
        using (var scope = _app.Services.CreateScope()) id = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>().Templates.First(t => t.IsActive).Id;

        async Task<string> Edit() => Decode(await admin.GetStringAsync($"/admin/templates/edit/{id}"));
        var html = await Edit();
        foreach (var file in new[] { "shop-card.html", "shop-product.html", "shop-catalog.html" }) Assert.Contains(file, html);
        Assert.Contains("name=\"Parts[shop.product]\"", html);
        Assert.Contains("{{form_html}}", html);                                                // the placeholders are offered as chips

        async Task<HttpResponseMessage> Save(string productPart)
        {
            var page = await admin.GetStringAsync($"/admin/templates/edit/{id}");
            var form = new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["Id"] = id.ToString(), ["Name"] = "Test", ["AccentColor"] = "#112233", ["ButtonStyle"] = "solid",
                ["Parts[shop.product]"] = productPart, ["Parts[shop.card]"] = ShopParts.CardDefault, ["Parts[shop.catalog]"] = ""
            };
            return await admin.PostAsync($"/admin/templates/edit/{id}", new FormUrlEncodedContent(form));
        }
        string Stored()
        {
            using var scope = _app.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>().Templates.AsNoTracking().First(t => t.Id == id).PartsJson;
        }
        var before = Stored();
        try
        {
            Assert.Equal(HttpStatusCode.Redirect, (await Save("<div id=\"gespeichert\">{{title}}</div>")).StatusCode);
            var stored = System.Text.Json.JsonDocument.Parse(Stored()).RootElement;
            Assert.Equal("<div id=\"gespeichert\">{{title}}</div>", stored.GetProperty("shop.product").GetString());
            Assert.False(stored.TryGetProperty("shop.card", out _));                           // posted at its default = "unset"
            Assert.False(stored.TryGetProperty("shop.catalog", out _));

            var shown = await Edit();
            Assert.Contains("gespeichert", shown);
            Assert.Contains("<div id=\"gespeichert\">", Decode(await _app.Browser().GetStringAsync("/shop/" + (await FirstSlugAsync()))));

            // Posted at the default again, the stored version is dropped.
            Assert.Equal(HttpStatusCode.Redirect, (await Save(ShopParts.ProductDefault)).StatusCode);
            Assert.False(System.Text.Json.JsonDocument.Parse(Stored()).RootElement.TryGetProperty("shop.product", out _));
        }
        finally
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
            db.Templates.First(t => t.Id == id).PartsJson = before; await db.SaveChangesAsync();
        }
    }

    private async Task<string> FirstSlugAsync()
    {
        await _shop.AddProductAsync("Teil Editor Produkt", 500);
        return "teil-editor-produkt";
    }

    [Fact]
    public async Task A_stored_version_of_a_part_survives_a_save_while_its_plugin_is_off_and_a_part_that_is_too_long_is_refused()
    {
        var admin = await _shop.AdminAsync();
        int id;
        using (var scope = _app.Services.CreateScope()) id = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>().Templates.First(t => t.IsActive).Id;
        string Stored() { using var scope = _app.Services.CreateScope(); return scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>().Templates.AsNoTracking().First(t => t.Id == id).PartsJson; }
        string before = Stored();
        try
        {
            // A part of a plugin that is not loaded (the key is not one of the shop's) is not on the page and cannot be posted: a save must not throw it away.
            using (var scope = _app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
                db.Templates.First(t => t.Id == id).PartsJson = "{\"abwesend.box\":\"<i>bleibt</i>\"}"; await db.SaveChangesAsync();
            }
            var page = await admin.GetStringAsync($"/admin/templates/edit/{id}");
            var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(page)!, ["Id"] = id.ToString(), ["Name"] = "Test", ["AccentColor"] = "#112233", ["ButtonStyle"] = "solid" };
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync($"/admin/templates/edit/{id}", new FormUrlEncodedContent(form))).StatusCode);
            Assert.Contains("abwesend.box", Stored());

            // Too long: refused, nothing written.
            var tooLong = new Dictionary<string, string>(form) { ["Parts[shop.card]"] = new string('x', 300_000) };
            var res = await admin.PostAsync($"/admin/templates/edit/{id}", new FormUrlEncodedContent(tooLong));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("shop-card.html", Decode(await res.Content.ReadAsStringAsync()));
            Assert.DoesNotContain("xxxxxxxxxx", Stored());
        }
        finally
        {
            using var scope = _app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
            db.Templates.First(t => t.Id == id).PartsJson = before; await db.SaveChangesAsync();
        }
    }
}
