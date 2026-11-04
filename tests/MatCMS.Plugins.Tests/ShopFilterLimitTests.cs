using System.Net;
using MatCMS.Plugin.Shop;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Several keywords at once (a product must carry all of them) and the filter limit: a group shows a few entries and "Weitere …" opens the rest, fetched only then.</summary>
public class ShopFilterLimitTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopFilterLimitTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    // Its own application (class fixture), so the limit set here is the shop's from the start.
    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app, new Dictionary<string, string> { [Keys.FilterLimit] = "2" });
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    private async Task<long> ProductAsync(string title, string tags, string brand = "")
    {
        var id = await _shop.AddProductAsync(title, 1000);
        await _shop.WithContextAsync(async (_, s) =>
        {
            var p = (await s.ProductAsync(id))!;
            await s.SaveProductAsync(p with { Tags = ShopTags.Normalise(tags), Brand = brand });
        });
        return id;
    }

    [Fact]
    public async Task Several_keywords_narrow_to_the_products_that_carry_all_of_them()
    {
        await ProductAsync("Mt Beide", "Mtrot, Mtgross");
        await ProductAsync("Mt Nur Rot", "Mtrot");
        await ProductAsync("Mt Nur Gross", "Mtgross");
        var v = _app.Browser();

        var both = Decode(await v.GetStringAsync("/shop?tag=Mtrot,Mtgross"));
        Assert.Contains("Mt Beide", both);
        Assert.DoesNotContain("Mt Nur Rot", both);
        Assert.DoesNotContain("Mt Nur Gross", both);
        // One chip per keyword, each taking only itself off.
        Assert.Contains("href=\"/shop?tag=Mtgross\" rel=\"nofollow\" aria-label", both);
        Assert.Contains("href=\"/shop?tag=Mtrot\" rel=\"nofollow\" aria-label", both);

        // From one keyword, a click on another ADDS it.
        var one = Decode(await v.GetStringAsync("/shop?tag=Mtrot"));
        Assert.Contains("Mt Nur Rot", one);
        Assert.DoesNotContain("Mt Nur Gross", one);
        Assert.Contains("href=\"/shop?tag=Mtrot,Mtgross\"", one);

        // Case and spaces do not matter, a keyword typed twice counts once.
        Assert.Contains("Mt Beide", Decode(await v.GetStringAsync("/shop?tag=" + Uri.EscapeDataString(" mtgross , MTROT,mtrot"))));
    }

    [Fact]
    public async Task A_group_shows_the_limit_and_the_rest_comes_from_the_dialog_only_when_asked()
    {
        foreach (var slug in new[] { "lim-a", "lim-b", "lim-c" })
            await _shop.WithContextAsync((_, s) => s.SaveBrandAsync(new Brand { Slug = slug, Name = "Lim " + slug[^1..].ToUpperInvariant(), Active = true }));
        await ProductAsync("Lim Eins", "Limw1, Limw2, Limw3, Limw4", "lim-a");
        await ProductAsync("Lim Zwei", "Limw1, Limw2", "lim-b");
        await ProductAsync("Lim Drei", "Limw1", "lim-c");
        var v = _app.Browser();

        var page = Decode(await v.GetStringAsync("/shop?q=Lim"));
        Assert.Contains("class=\"shop-fmore\"", page);
        Assert.Contains("data-more=\"/shop/filters?group=brand&q=Lim\"", page);
        Assert.Contains("data-more=\"/shop/filters?group=tag&q=Lim\"", page);
        Assert.Contains("window.__shopMore", page);                                     // the dialog's script, once
        // The busiest keywords are shown, the rarest is behind "Weitere …".
        Assert.Contains("href=\"/shop?tag=Limw1&q=Lim\"", page);
        Assert.DoesNotContain("href=\"/shop?tag=Limw4&q=Lim\"", page);

        // The dialog's content: every entry of that ONE group, nothing of the page around it.
        var tags = Decode(await v.GetStringAsync("/shop/filters?group=tag&q=Lim"));
        Assert.StartsWith("<div class=\"opts\">", tags);
        foreach (var t in new[] { "Limw1", "Limw2", "Limw3", "Limw4" }) Assert.Contains(t, tags);
        Assert.DoesNotContain("<html", tags);
        var brands = Decode(await v.GetStringAsync("/shop/filters?group=brand&q=Lim"));
        foreach (var b in new[] { "Lim A", "Lim B", "Lim C" }) Assert.Contains(b, brands);
        Assert.Equal(HttpStatusCode.NotFound, (await v.GetAsync("/shop/filters?group=a.nothing")).StatusCode);

        // Without script: ?more=tag shows that group in full on the page itself.
        Assert.Contains("href=\"/shop?tag=Limw4&q=Lim\"", Decode(await v.GetStringAsync("/shop?q=Lim&more=tag")));
    }
}
