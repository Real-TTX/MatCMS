using System.Net;
using System.Net.Http.Headers;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The site's picture of its home page (SitePreview): taken in the admin's browser, posted to /admin/api/site-preview,
/// re-encoded and served at /_preview.webp for the switcher and the cloud's tiles.</summary>
public class SitePreviewTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public SitePreviewTests(AppFactory app) => _app = app;

    private static byte[] Png(int w, int h)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(41, 92, 255));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, byte[] body, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/api/site-preview") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        if (token is not null) req.Headers.Add("RequestVerificationToken", token);
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task A_picture_from_the_admin_is_stored_re_encoded_and_served_with_revalidation()
    {
        var visitor = _app.Browser();
        var admin = _app.Browser();
        await AppFactory.LoginAsync(admin);
        var token = AppFactory.Token(await admin.GetStringAsync("/admin/pages"));
        Assert.NotNull(token);

        Assert.False((await PostAsync(visitor, Png(1280, 2000), token)).IsSuccessStatusCode);          // a visitor is never let in
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(admin, Png(1280, 2000), null)).StatusCode);       // no antiforgery token
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(admin, "not a picture"u8.ToArray(), token)).StatusCode);

        var ok = await PostAsync(admin, Png(1280, 2000), token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var res = await visitor.GetAsync("/_preview.webp");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/webp", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", res.Headers.CacheControl?.ToString());
        using (var shot = Image.Load(await res.Content.ReadAsByteArrayAsync()))
        {
            Assert.Equal(SitePreview.Width, shot.Width);                     // the top of a long page, cut to the preview's proportion
            Assert.Equal(SitePreview.Height, shot.Height);
        }
        var again = new HttpRequestMessage(HttpMethod.Get, "/_preview.webp");
        again.Headers.IfNoneMatch.Add(res.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await visitor.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task A_new_picture_is_due_when_the_content_changed_and_not_right_after_one_was_taken()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preview = scope.ServiceProvider.GetRequiredService<SitePreview>();
        var before = await SitePreview.FingerprintAsync(db);
        db.Pages.Add(new Page { Title = "Neu", Slug = "preview-neu-" + Guid.NewGuid().ToString("N")[..6], Locale = Localizer.DefaultCulture, IsPublished = true, UpdatedAt = DateTime.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync();
        Assert.NotEqual(before, await SitePreview.FingerprintAsync(db));

        Assert.True(await preview.SaveAsync(new MemoryStream(Png(1280, 800)), db));
        var (version, due) = await preview.StateAsync(db);
        Assert.NotNull(version);
        Assert.False(due);                                                   // just taken: not asked again at once
    }
}
