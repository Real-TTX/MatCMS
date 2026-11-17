using System.Net;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The EU notice on the legal guarantee (Directive (EU) 2024/825, Implementing Regulation (EU) 2025/1960): on by default, shown where goods are sold
/// (product page of a physical product, checkout with goods, order mails, the footer link and its page), the Commission's file served unchanged and sandboxed,
/// nothing of it for a download-only product.</summary>
public class ShopGuaranteeTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopGuaranteeTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Sentence = "Ihre gesetzlichen Gewährleistungsrechte";

    [Fact]
    public async Task A_physical_product_and_the_checkout_open_the_full_notice_and_a_download_shows_none()
    {
        await _shop.AddProductAsync("Gw Buch", 1799);
        await _shop.AddProductAsync("Gw Ebook", 499, kind: ProductKind.Digital);
        var c = _app.Browser();

        var book = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/gw-buch"));
        Assert.Contains(Sentence, book);
        Assert.Contains("src=\"/shop/gewaehrleistung/notice/de\"", book);
        Assert.Contains("href=\"https://europa.eu/youreurope/garantien\"", book);   // the clickable link beside the notice

        Assert.DoesNotContain("gewaehrleistung/notice", WebUtility.HtmlDecode(await c.GetStringAsync("/shop/gw-ebook")));   // the footer link stays; the notice itself is not on a download

        await _shop.AddToCartAsync(c, "gw-buch");
        Assert.Contains(Sentence, WebUtility.HtmlDecode(await c.GetStringAsync("/shop/checkout")));
    }

    [Fact]
    public async Task The_notice_is_the_Commissions_file_served_sandboxed_and_has_a_page_and_a_footer_link()
    {
        var c = _app.Browser();
        foreach (var lang in new[] { "de", "en" })
        {
            var res = await c.GetAsync("/shop/gewaehrleistung/notice/" + lang);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("image/svg+xml", res.Content.Headers.ContentType?.MediaType);
            Assert.Contains("sandbox", string.Join(";", res.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal(ShopGuarantee.Svg(lang), await res.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/shop/gewaehrleistung/notice/fr")).StatusCode);

        var page = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/gewaehrleistung"));
        Assert.Contains("src=\"/shop/gewaehrleistung/notice/de\"", page);
        Assert.Contains("href=\"/shop/gewaehrleistung\"", WebUtility.HtmlDecode(await c.GetStringAsync("/")));   // the footer link, added once
    }

    [Fact]
    public async Task The_order_mails_carry_the_link_for_goods_only()
    {
        var (goods, download) = await _shop.WithContextAsync((ctx, _) =>
        {
            var cfg = ShopConfig.From(ctx.Settings);
            var o = new Order { Locale = "de" };
            return Task.FromResult((
                ShopGuarantee.MailText(ctx, cfg, o, new[] { new OrderItem { Kind = ProductKind.Physical } }),
                ShopGuarantee.MailText(ctx, cfg, o, new[] { new OrderItem { Kind = ProductKind.Digital } })));
        });
        Assert.Contains("Ihre gesetzlichen Gewährleistungsrechte (EU-Hinweis):", goods);
        Assert.Contains("/shop/gewaehrleistung", goods);
        Assert.Contains("https://europa.eu/youreurope/garantien", goods);
        Assert.Equal("", download);
    }

    [Fact]
    public void Every_page_language_gets_a_notice_file_and_its_Your_Europe_page()
    {
        Assert.Equal("de", ShopGuarantee.NoticeLanguage("de"));
        Assert.Equal("en", ShopGuarantee.NoticeLanguage("hr"));                       // only German and English files ship; the rest get the English one
        Assert.Equal("https://europa.eu/youreurope/jamstva_hr", ShopGuarantee.InfoUrl("hr"));
        Assert.Equal("https://europa.eu/youreurope/guarantees", ShopGuarantee.InfoUrl("xx"));
        Assert.True(ShopGuarantee.Svg("de").Length > 100_000 && ShopGuarantee.Svg("en").Length > 100_000);
    }
}

/// <summary>A shop that does not sell to consumers switches the notice off: no sentence, no page, no footer entry.</summary>
public class ShopGuaranteeOffTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopGuaranteeOffTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.GuaranteeNotice] = "false" });
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Switched_off_nothing_of_it_is_shown()
    {
        await _shop.AddProductAsync("Gw Aus", 1000);
        var c = _app.Browser();
        var page = WebUtility.HtmlDecode(await c.GetStringAsync("/shop/gw-aus"));
        Assert.DoesNotContain("Gewährleistungsrechte", page);
        Assert.DoesNotContain("gewaehrleistung/notice", page);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/shop/gewaehrleistung")).StatusCode);
        Assert.DoesNotContain("href=\"/shop/gewaehrleistung\"", WebUtility.HtmlDecode(await c.GetStringAsync("/")));
    }
}
