using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>"Bild bearbeiten" in the media library: an edit lands where the picture is used, the original stays, a backup keeps
/// what a picture carries, SVG is code.</summary>
public class MediaEditorTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public MediaEditorTests(AppFactory app) => _app = app;

    private static byte[] Jpeg(int w, int h)
    {
        using var img = new Image<Rgb24>(w, h, new Rgb24(200, 120, 40));
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    /// <summary>A picture in the library and on a page.</summary>
    private (int mediaId, string url, int pageId) Seed(int w = 400, int h = 300)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var name = Guid.NewGuid().ToString("N") + ".jpg";
        Directory.CreateDirectory(StoragePaths.Uploads(env));
        File.WriteAllBytes(Path.Combine(StoragePaths.Uploads(env), name), Jpeg(w, h));
        var m = new Media { Url = "/uploads/" + name, FileName = "foto.jpg", ContentType = "image/jpeg", SizeBytes = 1, Width = w, Height = h };
        db.Media.Add(m);
        var page = new Page { Title = "Mit Bild " + name[..6], Slug = "bild-" + name[..8], Locale = "de" };
        page.Blocks.Add(new ContentBlock { BlockType = "image", DataJson = $"{{\"image\":\"/uploads/{name}\"}}" });
        db.Pages.Add(page);
        db.SaveChanges();
        return (m.Id, m.Url, page.Id);
    }

    private async Task<(HttpClient c, string token)> AdminAsync()
    {
        var c = _app.Browser();
        await AppFactory.LoginAsync(c);
        var html = await c.GetStringAsync("/admin/media");
        return (c, AppFactory.Token(html)!);
    }

    private static MultipartFormDataContent Form(string token, Dictionary<string, string> fields, byte[]? file = null, string name = "bild.jpg")
    {
        var f = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (k, v) in fields) f.Add(new StringContent(v), k);
        if (file is not null) { var p = new ByteArrayContent(file); p.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg"); f.Add(p, "file", name); }
        return f;
    }

    private static async Task<JsonObject> Json(HttpResponseMessage r) => JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();

    [Fact]
    public async Task Applying_an_edit_keeps_the_address_so_the_page_shows_it_and_the_original_can_be_restored()
    {
        var (id, url, _) = Seed();
        var (c, token) = await AdminAsync();
        var thumbBefore = ThumbnailService.Url(url, 320);

        var r = await Json(await c.PostAsync("/admin/media?handler=Edit", Form(token, new() { ["id"] = id.ToString(), ["mode"] = "replace", ["edit"] = "{\"x\":0,\"y\":0,\"width\":200,\"height\":100}" }, Jpeg(200, 100))));
        Assert.True(r["ok"]!.GetValue<bool>(), r["error"]?.ToString());
        Assert.Equal(url, r["media"]!["url"]!.GetValue<string>());                 // same address
        Assert.Equal(200, r["media"]!["width"]!.GetValue<int>());
        Assert.True(r["media"]!["hasOriginal"]!.GetValue<bool>());

        // What a visitor gets at the old address is the edit; the thumbnail address changed with it.
        var served = await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync();
        Assert.Equal(200, Image.Identify(served).Width);
        Assert.NotEqual(thumbBefore, ThumbnailService.Url(url, 320));

        // A second edit starts from the ORIGINAL again (one original, set aside once).
        string original;
        using (var scope = _app.Services.CreateScope())
            original = scope.ServiceProvider.GetRequiredService<AppDbContext>().Media.First(m => m.Id == id).OriginalUrl!;
        await c.PostAsync("/admin/media?handler=Edit", Form(token, new() { ["id"] = id.ToString(), ["mode"] = "replace" }, Jpeg(100, 100)));
        using (var scope = _app.Services.CreateScope())
            Assert.Equal(original, scope.ServiceProvider.GetRequiredService<AppDbContext>().Media.First(m => m.Id == id).OriginalUrl);
        Assert.Equal(400, Image.Identify(await (await c.GetAsync(original)).Content.ReadAsByteArrayAsync()).Width);

        // Reset brings the original back under the same address.
        r = await Json(await c.PostAsync("/admin/media?handler=Reset", Form(token, new() { ["id"] = id.ToString() })));
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(400, Image.Identify(await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync()).Width);
    }

    [Fact]
    public async Task Saving_as_a_copy_leaves_the_picture_alone_and_links_the_copy_to_it()
    {
        var (id, url, _) = Seed();
        var (c, token) = await AdminAsync();
        var r = await Json(await c.PostAsync("/admin/media?handler=Edit", Form(token, new() { ["id"] = id.ToString(), ["mode"] = "copy" }, Jpeg(120, 120))));
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.NotEqual(url, r["media"]!["url"]!.GetValue<string>());
        Assert.Equal(400, Image.Identify(await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync()).Width);
        using var scope = _app.Services.CreateScope();
        var copy = scope.ServiceProvider.GetRequiredService<AppDbContext>().Media.First(m => m.Id == r["media"]!["id"]!.GetValue<int>());
        Assert.Equal(id, copy.SourceMediaId);
        Assert.Contains("(bearbeitet)", copy.FileName);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_refused_and_nothing_changes()
    {
        var (id, url, _) = Seed();
        var (c, token) = await AdminAsync();
        var r = await Json(await c.PostAsync("/admin/media?handler=Edit", Form(token, new() { ["id"] = id.ToString(), ["mode"] = "replace" }, "<script>x</script>"u8.ToArray())));
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal(400, Image.Identify(await (await c.GetAsync(url)).Content.ReadAsByteArrayAsync()).Width);
    }

    [Fact]
    public async Task The_info_says_where_a_picture_is_used_and_the_focal_point_is_rendered_and_kept_in_a_backup()
    {
        var (id, url, pageId) = Seed();
        var (c, token) = await AdminAsync();
        var uses = JsonNode.Parse(await c.GetStringAsync($"/admin/media?handler=Uses&id={id}"))!.AsArray();
        Assert.Contains(uses, u => u!["kind"]!.GetValue<string>() == "page" && u["link"]!.GetValue<string>() == $"/admin/pages/editor/{pageId}");

        var f = await Json(await c.PostAsync("/admin/media?handler=Focal", Form(token, new() { ["id"] = id.ToString(), ["focalX"] = "20", ["focalY"] = "80" })));
        Assert.True(f["ok"]!.GetValue<bool>());

        using (var scope = _app.Services.CreateScope())
        {
            var transfer = scope.ServiceProvider.GetRequiredService<ContentTransferService>();
            var zip = await transfer.ExportAsync(new ContentTransferService.BackupOptions());
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
            using var reader = new StreamReader(archive.GetEntry("content.json")!.Open());
            var content = JsonNode.Parse(await reader.ReadToEndAsync())!;
            var item = content["Media"]!.AsArray().First(m => m!["Url"]!.GetValue<string>() == url)!;
            Assert.Equal(20, item["FocalX"]!.GetValue<int>());
            Assert.Equal(80, item["FocalY"]!.GetValue<int>());
            Assert.Equal(400, item["Width"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task An_svg_is_edited_as_code_and_saved_clean()
    {
        string url;
        int id;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var name = Guid.NewGuid().ToString("N") + ".svg";
            File.WriteAllText(Path.Combine(StoragePaths.Uploads(env), name), "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>");
            var m = new Media { Url = "/uploads/" + name, FileName = "logo.svg", ContentType = "image/svg+xml" };
            db.Media.Add(m);
            db.SaveChanges();
            (url, id) = (m.Url, m.Id);
        }
        var (c, token) = await AdminAsync();
        Assert.Contains("<rect", await c.GetStringAsync($"/admin/media?handler=Svg&id={id}"));
        var r = await Json(await c.PostAsync("/admin/media?handler=Svg", Form(token, new()
        {
            ["id"] = id.ToString(), ["code"] = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 20\" onload=\"x()\"><script>y()</script><circle r=\"5\"/></svg>",
        })));
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(40, r["media"]!["width"]!.GetValue<int>());
        var served = await c.GetStringAsync(url);
        Assert.Contains("<circle", served);
        Assert.DoesNotContain("script", served);
        Assert.DoesNotContain("onload", served);

        var bad = await Json(await c.PostAsync("/admin/media?handler=Svg", Form(token, new() { ["id"] = id.ToString(), ["code"] = "kein svg" })));
        Assert.False(bad["ok"]!.GetValue<bool>());
    }
}
