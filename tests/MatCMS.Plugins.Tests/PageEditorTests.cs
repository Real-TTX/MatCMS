using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The page editor (formerly "editor v2") is the only one: the classic editor's address redirects to it, and what only the classic
/// editor could do — link an existing page as a translation, the translation dialog's block fields — works without it.
/// </summary>
public class PageEditorTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public PageEditorTests(AppFactory app) => _app = app;

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> f)
    {
        using var scope = _app.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<Page> AddPageAsync(string slug, string locale, string? group = null, string blockData = "{\"text\":\"Hallo\"}") => Db(async db =>
    {
        var p = new Page { Title = "T " + slug, Slug = slug, Locale = locale, TranslationGroup = group, IsPublished = true };
        p.Blocks.Add(new ContentBlock { BlockType = "el-heading", SortOrder = 0, DataJson = blockData });
        db.Pages.Add(p);
        await db.SaveChangesAsync();
        return p;
    });

    private async Task<HttpClient> AdminAsync()
    {
        var c = _app.Browser();
        await AppFactory.LoginAsync(c);
        return c;
    }

    [Fact]
    public async Task The_classic_editors_address_redirects_to_the_page_editor_with_the_block()
    {
        var page = await AddPageAsync("ed-redirect", "de");
        var c = await AdminAsync();

        var res = await c.GetAsync($"/admin/pages/edit/{page.Id}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal($"/admin/pages/editor/{page.Id}", res.Headers.Location!.OriginalString, ignoreCase: true);

        var block = page.Blocks.First().Id;
        res = await c.GetAsync($"/admin/pages/edit/{page.Id}?block={block}");
        Assert.Equal($"/admin/pages/editor/{page.Id}?block={block}", res.Headers.Location!.OriginalString, ignoreCase: true);

        var editor = await c.GetStringAsync($"/admin/pages/editor/{page.Id}");
        Assert.DoesNotContain("ev2-classic", editor);                                   // no way back to an editor that is gone
        Assert.DoesNotContain("/admin/pages/edit/", editor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"/Admin/Pages/Editor/{page.Id}?handler=SaveAll", editor, StringComparison.OrdinalIgnoreCase);

        // A form left open in an old tab posts to the old address: it lands on the editor, nothing is carried out.
        var post = await c.PostAsync($"/admin/pages/edit/{page.Id}?handler=SaveAll", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(editor)!, ["Draft"] = "[]"
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Single(await Db(db => db.ContentBlocks.Where(b => b.PageId == page.Id).ToListAsync()));
    }

    [Fact]
    public async Task An_existing_page_in_another_language_can_be_linked_as_a_translation()
    {
        await Db(async db =>
        {
            var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == MatCMS.Services.SettingKeys.Languages);
            if (row is null) db.SiteSettings.Add(new SiteSetting { Key = MatCMS.Services.SettingKeys.Languages, Value = "de,en" });
            else row.Value = "de,en";
            return await db.SaveChangesAsync();
        });
        var de = await AddPageAsync("ed-link", "de", group: "grp-" + Guid.NewGuid().ToString("N"));
        var en = await AddPageAsync("ed-link-en", "en");
        var c = await AdminAsync();

        var editor = await c.GetStringAsync($"/admin/pages/editor/{de.Id}");
        Assert.Contains("handler=LinkTranslation", editor);
        Assert.Contains($"value=\"{en.Id}\"", editor);

        var res = await c.PostAsync($"/admin/pages/editor/{de.Id}?handler=LinkTranslation", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(editor)!, ["targetId"] = en.Id.ToString()
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var linked = await Db(db => db.Pages.AsNoTracking().FirstAsync(p => p.Id == en.Id));
        Assert.Equal(de.TranslationGroup, linked.TranslationGroup);
    }

    [Fact]
    public async Task The_translation_dialog_reads_and_saves_one_block_through_the_compare_page()
    {
        var page = await AddPageAsync("ed-diff", "de", group: "grp-" + Guid.NewGuid().ToString("N"));
        var block = page.Blocks.First().Id;
        var c = await AdminAsync();

        var diff = await c.GetStringAsync($"/admin/pages/diff/{page.Id}");
        Assert.Contains($"/Admin/Pages/Editor/{page.Id}", diff, StringComparison.OrdinalIgnoreCase);   // "back" leads to the page editor
        var got = JsonNode.Parse(await c.GetStringAsync($"/admin/pages/diff/{page.Id}?handler=Block&blockId={block}"))!;
        Assert.Equal("Hallo", got["data"]!["text"]!.GetValue<string>());
        Assert.NotEmpty(got["schema"]!.AsArray());

        var res = await c.PostAsync($"/admin/pages/diff/{page.Id}?handler=SaveBlock&blockId={block}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(diff)!, ["DataJson"] = "{\"text\":\"Hello\"}"
        }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Hello", (await Db(db => db.ContentBlocks.AsNoTracking().FirstAsync(b => b.Id == block))).DataJson);

        res = await c.PostAsync($"/admin/pages/diff/{page.Id}?handler=SaveBlock&blockId={block}", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AppFactory.Token(diff)!, ["DataJson"] = "[1,2]"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);                          // only a JSON object is block data
    }
}
