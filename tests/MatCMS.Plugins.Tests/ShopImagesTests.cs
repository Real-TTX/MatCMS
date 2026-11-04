using System.Net;
using MatCMS.Data;
using MatCMS.Plugin.Shop;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Pictures shown smaller than they are: stored smaller copies (thumbnail widths) and the crop focus of tiles — the product's own, the media
/// library's focal point, the shop's default.</summary>
public class ShopImagesTests : IClassFixture<AppFactory>, IAsyncLifetime
{
    private readonly AppFactory _app;
    private ShopApp _shop = null!;
    public ShopImagesTests(AppFactory app) { _app = app; MatCMS.Services.Plugins.PluginPostLimiter.Reset(); }

    public async Task InitializeAsync() => _shop = await ShopApp.StartAsync(_app);
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(240, 320)]
    [InlineData(320, 320)]
    [InlineData(480, 640)]
    [InlineData(800, 1200)]
    [InlineData(1400, 1920)]
    [InlineData(5000, 1920)]
    public void A_wanted_width_is_served_by_the_next_stored_width(int wanted, int served)
    {
        // The image blocks ask for 240/480/800/1400 — with only 320 and 1200 allowed they used to get the original every time.
        Assert.StartsWith($"/thumb/{served}/x-snap.jpg", ThumbnailService.Url("/uploads/x-snap.jpg", wanted));
    }

    [Fact]
    public void What_cannot_be_scaled_keeps_its_address()
    {
        Assert.Equal("https://cdn.example/a.jpg", ThumbnailService.Url("https://cdn.example/a.jpg", 640));
        Assert.Equal("/uploads/logo.svg", ThumbnailService.Url("/uploads/logo.svg", 640));
        Assert.Equal("/uploads/anim.gif", ThumbnailService.Url("/uploads/anim.gif", 640));
        Assert.Equal("/uploads/a.jpg", ThumbnailService.Url("/uploads/a.jpg", 0));
    }

    [Theory]
    [InlineData("30% 10%", "30% 10%")]
    [InlineData(" 33.6%   0% ", "34% 0%")]
    [InlineData("150% 5%", "100% 5%")]
    [InlineData("center", "")]
    [InlineData("50% 50%;background:url(x)", "")]
    [InlineData("", "")]
    public void A_focus_is_only_ever_two_percentages(string input, string expected) => Assert.Equal(expected, ShopImages.Clean(input));

    private async Task<long> ProductWithAsync(string title, string image, string focus = "")
    {
        var id = await _shop.AddProductAsync(title, 1000);
        await _shop.WithContextAsync(async (_, s) =>
        {
            var p = (await s.ProductAsync(id))!;
            await s.SaveProductAsync(p with { Image = image, ImageFocus = focus });
        });
        return id;
    }

    [Fact]
    public async Task Cards_show_the_small_copy_cropped_at_the_product_focus_else_the_media_focal_point_else_the_default()
    {
        await ProductWithAsync("Fokuskarte Eigen", "/uploads/fokus-eigen.jpg", "30% 10%");
        await ProductWithAsync("Fokuskarte Mediathek", "/uploads/fokus-media.jpg");
        await ProductWithAsync("Fokuskarte Standard", "/uploads/fokus-standard.jpg");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Media.Add(new MatCMS.Models.Media { Url = "/uploads/fokus-media.jpg", FileName = "fokus-media.jpg", FocalX = 80, FocalY = 20 });
            await db.SaveChangesAsync();
        }

        var page = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/shop?q=Fokuskarte"));
        Assert.Contains("/thumb/640/fokus-eigen.jpg", page);
        Assert.DoesNotContain("url('/uploads/fokus-eigen.jpg')", page);                     // a card never loads the original
        Assert.Contains("fokus-eigen.jpg')", page);
        Assert.Matches(@"fokus-eigen\.jpg[^""]*'\);background-position:30% 10%", page);
        Assert.Matches(@"fokus-media\.jpg[^""]*'\);background-position:80% 20%", page);
        Assert.Matches(@"fokus-standard\.jpg[^""]*'\);background-position:50% 50%", page);
    }

    [Fact]
    public async Task The_product_page_shows_a_large_copy_and_keeps_the_original_for_the_lightbox_and_share_tags()
    {
        var id = await ProductWithAsync("Fokus Seite", "/uploads/fokus-seite.jpg");
        await _shop.WithContextAsync(async (_, s) =>
        {
            var p = (await s.ProductAsync(id))!;
            await s.SaveProductAsync(p with { GalleryJson = "[{\"url\":\"/uploads/fokus-seite-2.jpg\",\"alt\":\"\"}]" });
        });
        var page = WebUtility.HtmlDecode(await _app.Browser().GetStringAsync("/shop/fokus-seite"));
        Assert.Contains("class=\"main-img\" src=\"/thumb/1200/fokus-seite.jpg", page);
        Assert.Contains("<img src=\"/thumb/320/fokus-seite-2.jpg", page);                    // the filmstrip
        Assert.Contains("data-full=\"/uploads/fokus-seite-2.jpg\"", page);                    // the lightbox opens the original
        Assert.Matches(@"og:image[^>]*/uploads/fokus-seite\.jpg|/uploads/fokus-seite\.jpg[^>]*og:image", page);          // share tags name the original
    }

    [Fact]
    public async Task The_product_form_offers_the_focus_and_stores_only_a_clean_one()
    {
        var id = await ProductWithAsync("Fokus Formular", "/uploads/fokus-form.jpg");
        var admin = await _shop.AdminAsync();
        async Task SaveAsync(string focus)
        {
            var form = await admin.GetStringAsync($"/admin/p/shop/products/{id}");
            Assert.Contains("name=\"image_focus\"", form);
            Assert.Contains("data-ui-focus", form);
            var res = await admin.PostAsync($"/admin/p/shop/products/{id}", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AppFactory.Token(form)!, ["action"] = "save", ["title"] = "Fokus Formular", ["slug"] = "fokus-formular", ["kind"] = "physical",
                ["price"] = "10,00", ["tax"] = "19", ["image"] = "/uploads/fokus-form.jpg", ["image_focus"] = focus, ["active"] = "true"
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        }
        await SaveAsync("25% 75%");
        Assert.Equal("25% 75%", (await _shop.WithContextAsync((_, s) => s.ProductAsync(id)))!.ImageFocus);
        await SaveAsync("25% 75%\" onmouseover=\"alert(1)");
        Assert.Equal("", (await _shop.WithContextAsync((_, s) => s.ProductAsync(id)))!.ImageFocus);
    }

    [Fact]
    public async Task The_csv_carries_the_focus_and_refuses_a_bad_one()
    {
        await ProductWithAsync("Fokus Csv", "/uploads/fokus-csv.jpg");
        await _shop.WithContextAsync((ctx, s) => ShopExport.ImportProductsAsync(ctx, s, ShopConfig.From(ctx.Settings), "slug;image_focus\nfokus-csv;40% 60%\n", null, false, default));
        Assert.Equal("40% 60%", (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("fokus-csv")))!.ImageFocus);
        await _shop.WithContextAsync((ctx, s) => ShopExport.ImportProductsAsync(ctx, s, ShopConfig.From(ctx.Settings), "slug;image_focus\nfokus-csv;oben\n", null, false, default));
        Assert.Equal("40% 60%", (await _shop.WithContextAsync((_, s) => s.ProductBySlugAsync("fokus-csv")))!.ImageFocus);   // the bad row changed nothing
    }
}
