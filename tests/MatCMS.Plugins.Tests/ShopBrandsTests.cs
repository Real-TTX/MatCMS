using System.Net;
using MatCMS.Plugin.Shop;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Brands: the back office, a brand's own page (details only there, structured data), the page of all brands, the product page, the catalogue filter, the gallery block,
/// the take-over from an attribute "Marke", the CSV column and what deleting does.</summary>
public class ShopBrandsTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopBrandsTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private Task<long> BrandAsync(string slug, string name, Func<Brand, Brand>? change = null) =>
        _shop.WithContextAsync((_, s) => s.SaveBrandAsync((change ?? (b => b))(new Brand { Slug = slug, Name = name })));

    private Task SetBrandAsync(long productId, string slug) => _shop.WithContextAsync((_, s) => s.SetProductBrandAsync(productId, slug));

    private async Task<HttpResponseMessage> SaveFormAsync(HttpClient admin, long id, Dictionary<string, string> fields)
    {
        var page = await admin.GetStringAsync($"/admin/p/shop/brands/{id}");
        fields["__RequestVerificationToken"] = AppFactory.Token(page)!; fields["action"] = "save";
        return await admin.PostAsync($"/admin/p/shop/brands/{id}", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task A_brand_is_made_in_the_back_office_and_its_details_are_cleaned()
    {
        var admin = await _shop.AdminAsync();
        Assert.Contains("name=\"details\"", await admin.GetStringAsync("/admin/p/shop/brands/0"));
        var res = await SaveFormAsync(admin, 0, new()
        {
            ["name"] = "Form Marke", ["summary"] = "Kurz und gut.", ["details"] = "<p>Seit 1976.</p><script>alert(1)</script>", ["website"] = "www.form-marke.de",
            ["logo"] = "/uploads/form-logo.png", ["image"] = "javascript:alert(1)", ["active"] = "true", ["sort"] = "3"
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var b = (await _shop.WithContextAsync((_, s) => s.BrandBySlugAsync("form-marke")))!;
        Assert.Equal("Kurz und gut.", b.Summary);
        Assert.Contains("Seit 1976.", b.DetailsHtml);
        Assert.DoesNotContain("<script", b.DetailsHtml);
        Assert.Equal("https://www.form-marke.de", b.Website);
        Assert.Equal("", b.Image);                                                    // a picture address that is not one is dropped
        Assert.True(b.Active);

        // A website that is not an address is refused (the form comes back), no name likewise.
        Assert.Equal(HttpStatusCode.OK, (await SaveFormAsync(admin, b.Id, new() { ["name"] = "Form Marke", ["website"] = "javascript:alert(1)" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaveFormAsync(admin, 0, new() { ["name"] = " " })).StatusCode);
        Assert.Contains("Form Marke", Decode(await admin.GetStringAsync("/admin/p/shop/brands")));
    }

    [Fact]
    public async Task A_brand_page_shows_details_products_and_tells_search_engines_what_it_is()
    {
        await BrandAsync("seite-marke", "Seite Marke", b => b with
        {
            Summary = "Analoge Laufwerke aus Bergisch Gladbach.", DetailsHtml = "<p>Nur hier: die ganze Geschichte.</p>", Logo = "/uploads/seite-logo.png", Website = "https://seite-marke.example",
            SeoDescription = "Die Seite der Marke."
        });
        var id = await _shop.AddProductAsync("Seite Marke Laufwerk", 99000);
        await SetBrandAsync(id, "seite-marke");
        await _shop.AddProductAsync("Fremdes Laufwerk", 50000);

        var v = _app.Browser();
        var page = Decode(await v.GetStringAsync("/shop/brands/seite-marke"));
        Assert.Contains("<h1>Seite Marke</h1>", page);
        Assert.Contains("Nur hier: die ganze Geschichte.", page);
        Assert.Contains("Seite Marke Laufwerk", page);
        Assert.DoesNotContain("Fremdes Laufwerk", page);
        Assert.Contains("\"@type\":\"Brand\"", page);
        Assert.Contains("\"sameAs\":[\"https://seite-marke.example\"]", page);
        Assert.Contains("BreadcrumbList", page);
        Assert.Contains("Die Seite der Marke.", page);                                 // the description search engines get
        Assert.Contains("/shop?brand=seite-marke", page);                              // "filter the shop by it"

        // The page of all brands: the short text, never the details.
        var all = Decode(await v.GetStringAsync("/shop/brands"));
        Assert.Contains("href=\"/shop/brands/seite-marke\"", all);
        Assert.Contains("Analoge Laufwerke aus Bergisch Gladbach.", all);
        Assert.DoesNotContain("Nur hier: die ganze Geschichte.", all);

        // The product names its brand: a link above the title and in its structured data.
        var product = Decode(await v.GetStringAsync("/shop/seite-marke-laufwerk"));
        Assert.Contains("class=\"shop-brandlink\" href=\"/shop/brands/seite-marke\"", product);
        Assert.Contains("\"brand\":{\"@type\":\"Brand\",\"name\":\"Seite Marke\"", product);
        Assert.Contains("<th>Marke</th><td><a href=\"/shop/brands/seite-marke\">Seite Marke</a></td>", product);   // also for a page laid out before brands existed

        Assert.Contains("/shop/brands/seite-marke<", await v.GetStringAsync("/sitemap.xml"));
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/shop/brands/gibt-es-nicht")).StatusCode);
    }

    [Fact]
    public async Task Highlighted_brands_carry_the_shops_badge_or_their_own_and_stand_in_their_own_section()
    {
        await BrandAsync("prem-marke", "Prem Marke", b => b with { Featured = true });
        await BrandAsync("eigen-marke", "Eigen Marke", b => b with { Badge = "Exklusiv" });
        await BrandAsync("normal-marke", "Normal Marke");
        foreach (var slug in new[] { "prem-marke", "eigen-marke", "normal-marke" }) await SetBrandAsync(await _shop.AddProductAsync("Ding " + slug, 1000), slug);
        var all = Decode(await _app.Browser().GetStringAsync("/shop/brands"));
        Assert.Contains("<h2>Unsere Premium-Partner</h2>", all);
        Assert.Contains("<h2>Alle Marken</h2>", all);
        // The highlighted one: framed, the shop's badge, and in the top section.
        Assert.Matches("class=\"shop-brand card featured\" href=\"/shop/brands/prem-marke\"><span class=\"badge\">Premium-Partner</span>", all);
        Assert.True(all.IndexOf("/shop/brands/prem-marke", StringComparison.Ordinal) < all.IndexOf("<h2>Alle Marken</h2>", StringComparison.Ordinal));
        // A brand's own badge, without being highlighted; a plain one has none.
        Assert.Contains("href=\"/shop/brands/eigen-marke\"><span class=\"badge\">Exklusiv</span>", all);
        Assert.DoesNotContain("href=\"/shop/brands/normal-marke\"><span class=\"badge\">", all);
        // The brand's own page says it too.
        Assert.Contains("<span class=\"shop-brandbadge\">Premium-Partner</span><h1>Prem Marke</h1>", Decode(await _app.Browser().GetStringAsync("/shop/brands/prem-marke")));
        Assert.Equal("Exklusiv", ShopBrands.CleanBadge("  Exklusiv  "));
        Assert.Equal(ShopBrands.MaxBadge, ShopBrands.CleanBadge(new string('x', 99)).Length);
    }

    [Fact]
    public async Task A_hidden_brand_has_no_page_and_its_products_are_still_sold()
    {
        await BrandAsync("weg-marke", "Weg Marke", b => b with { Active = false });
        var id = await _shop.AddProductAsync("Weg Marke Kabel", 4900);
        await SetBrandAsync(id, "weg-marke");
        var v = _app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/shop/brands/weg-marke")).StatusCode);
        var product = Decode(await v.GetStringAsync("/shop/weg-marke-kabel"));
        Assert.DoesNotContain("/shop/brands/weg-marke", product);
        Assert.Contains("In den Warenkorb", product);
    }

    [Fact]
    public async Task The_catalogue_filters_by_brand_and_shows_it_as_a_chip()
    {
        await BrandAsync("filt-eins", "Filt Eins");
        await BrandAsync("filt-zwei", "Filt Zwei");
        await SetBrandAsync(await _shop.AddProductAsync("Filt Eins Gerät", 1000), "filt-eins");
        await SetBrandAsync(await _shop.AddProductAsync("Filt Zwei Gerät", 2000), "filt-zwei");
        var v = _app.Browser();
        var all = Decode(await v.GetStringAsync("/shop?q=Filt"));
        Assert.Contains("href=\"/shop?brand=filt-eins&q=Filt\"", all);              // the group offers both, carrying the search
        var one = Decode(await v.GetStringAsync("/shop?brand=filt-eins&q=Filt"));
        Assert.Contains("Filt Eins Gerät", one);
        Assert.DoesNotContain("Filt Zwei Gerät", one);
        Assert.Contains("Marke: Filt Eins", one);                                      // the chip that takes it off
        var both = Decode(await v.GetStringAsync("/shop?brand=filt-eins,filt-zwei&q=Filt"));
        Assert.Contains("Filt Eins Gerät", both);
        Assert.Contains("Filt Zwei Gerät", both);
        // An unknown brand filters nothing away.
        Assert.Contains("Filt Zwei Gerät", Decode(await v.GetStringAsync("/shop?brand=unbekannt&q=Filt")));
    }

    [Fact]
    public async Task The_gallery_block_shows_logos_cards_or_a_pick()
    {
        await BrandAsync("gal-alpha", "Gal Alpha", b => b with { Logo = "/uploads/gal-alpha.png", Summary = "Alpha kurz.", Featured = true });
        await BrandAsync("gal-beta", "Gal Beta", b => b with { DetailsHtml = "<p>Beta Details geheim.</p>" });
        var v = _app.Browser();

        var logos = await BlockAsync(v, """{"source":"all","style":"logos","showName":"no","grey":"yes"}""");
        Assert.Contains("class=\"shop-brands grey\"", logos);
        Assert.Contains("href=\"/shop/brands/gal-alpha\"", logos);
        Assert.Contains("<img src=\"/thumb/640/gal-alpha.png\" alt=\"Gal Alpha\"", logos);
        Assert.Contains("<span class=\"ph\">Gal Beta</span>", logos);                  // no logo: the name stands in

        var cards = await BlockAsync(v, """{"source":"featured","style":"cards"}""");
        Assert.Contains("Alpha kurz.", cards);
        Assert.DoesNotContain("gal-beta", cards);

        var pick = await BlockAsync(v, """{"source":"pick","brands":"gal-beta, gal-alpha","style":"cards"}""");
        Assert.True(pick.IndexOf("gal-beta", StringComparison.Ordinal) < pick.IndexOf("gal-alpha", StringComparison.Ordinal));
        Assert.DoesNotContain("Beta Details geheim.", pick);                          // the details are only on the brand's page
    }

    private async Task<string> BlockAsync(HttpClient visitor, string json)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatCMS.Data.AppDbContext>();
        var page = db.Pages.First(p => p.Slug == "home");
        var row = new MatCMS.Models.ContentBlock { PageId = page.Id, BlockType = "shop_brands", SortOrder = 900, DataJson = json };
        db.ContentBlocks.Add(row); await db.SaveChangesAsync();
        try
        {
            var html = Decode(await visitor.GetStringAsync("/"));
            var at = html.IndexOf("<div class=\"shop shop-block\">", StringComparison.Ordinal);
            return at < 0 ? html : html[at..html.IndexOf("</section>", at, StringComparison.Ordinal)];
        }
        finally { db.ContentBlocks.Remove(row); await db.SaveChangesAsync(); }
    }

    [Fact]
    public async Task Brands_kept_as_an_attribute_are_taken_over_with_one_button()
    {
        var (attrId, p1, p2) = await _shop.WithContextAsync(async (_, s) =>
        {
            var a = await s.SaveAttributeAsync(new ShopAttribute { Slug = "marke", Name = "Marke", Kind = AttributeKind.Choice, Options = "Über Alt\nUnbenutzt Alt", Filterable = true });
            var x = await s.SaveProductAsync(new Product { Slug = "ueber-alt-geraet", Title = "Über Alt Gerät", PriceCents = 100 });
            var y = await s.SaveProductAsync(new Product { Slug = "neu-alt-geraet", Title = "Neu Alt Gerät", PriceCents = 100 });
            await s.SetProductAttrAsync(x, a, new[] { "Über Alt" });
            await s.SetProductAttrAsync(y, a, new[] { "Neu Alt" });
            return (a, x, y);
        });
        var admin = await _shop.AdminAsync();
        var list = Decode(await admin.GetStringAsync("/admin/p/shop/brands"));
        Assert.Contains("Aus „Marke“ übernehmen", list);
        var res = await admin.PostAsync("/admin/p/shop/brands", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = AppFactory.Token(list)!, ["action"] = "takeover" }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        await _shop.WithContextAsync(async (_, s) =>
        {
            Assert.NotNull(await s.BrandBySlugAsync("unbenutzt-alt"));                      // an option nobody uses yet is a brand too
            var ueber = (await s.ResolveBrandAsync("Über Alt", false))!;
            Assert.Equal(ueber.Slug, (await s.ProductAsync(p1))!.Brand);
            Assert.Equal((await s.ResolveBrandAsync("Neu Alt", false))!.Slug, (await s.ProductAsync(p2))!.Brand);
            Assert.False((await s.AttributeAsync(attrId))!.Filterable);                     // the brand filter replaces it
        });

        // A link from before (menus, search results) leads to the brand filter for good.
        var slug = (await _shop.WithContextAsync((_, s) => s.ResolveBrandAsync("Über Alt", false)))!.Slug;
        var old = await _app.Browser().GetAsync("/shop?a.marke=" + Uri.EscapeDataString("Über Alt") + "&sort=name");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal($"/shop?brand={slug}&sort=name", old.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_csv_import_takes_a_brand_by_name_and_makes_a_new_one()
    {
        await BrandAsync("csv-bekannt", "CSV Bekannt");
        var report = await _shop.WithContextAsync((ctx, s) => ShopExport.ImportProductsAsync(ctx, s, ShopConfig.From(ctx.Settings),
            "slug;title;price;marke\r\ncsv-eins;CSV Eins;10,00;csv bekannt\r\ncsv-zwei;CSV Zwei;20,00;CSV Ganz Neu\r\n", null, false, default));
        Assert.Null(report.Fatal);
        Assert.Equal(2, report.Created);
        await _shop.WithContextAsync(async (_, s) =>
        {
            Assert.Equal("csv-bekannt", (await s.ProductBySlugAsync("csv-eins"))!.Brand);
            var made = (await s.ResolveBrandAsync("CSV Ganz Neu", false))!;
            Assert.Equal(made.Slug, (await s.ProductBySlugAsync("csv-zwei"))!.Brand);
        });
    }

    [Fact]
    public async Task Deleting_a_brand_keeps_its_products_without_it()
    {
        var bid = await BrandAsync("del-marke", "Del Marke");
        var pid = await _shop.AddProductAsync("Del Marke Teil", 700);
        await SetBrandAsync(pid, "del-marke");
        var admin = await _shop.AdminAsync();
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/p/shop/brands"))!;
        var res = await admin.PostAsync("/admin/p/shop/brands", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["action"] = "delete", ["id"] = bid.ToString() }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("", (await _shop.ProductAsync(pid)).Brand);
        Assert.Null(await _shop.WithContextAsync((_, s) => s.BrandBySlugAsync("del-marke")));
    }
}
