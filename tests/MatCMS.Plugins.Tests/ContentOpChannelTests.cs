using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The cloud's content channel as the SITE applies it (<see cref="ContentOpApplier"/>): a read → edit round trip of a page
/// must not lose what the model did not touch, a new page in the navigation must really be in the menu, and the setting keys
/// a steered model could do the most damage with are refused.
/// </summary>
public class ContentOpChannelTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;
    public ContentOpChannelTests(AppFactory app) => _app = app;

    private static int _op = 9000;

    private async Task<ContentOpReport> ApplyAsync(string kind, JsonObject payload)
    {
        using var scope = _app.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<ContentOpApplier>();
        return await applier.ApplyAsync(new PendingContentOp { OpId = Interlocked.Increment(ref _op), Kind = kind, PayloadJson = payload.ToJsonString(), Overwrite = true });
    }

    private async Task<(int pageId, int heroId, int imageId, int cardsId)> SeedPageAsync(string slug)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var page = new Page { Title = "Test " + slug, Slug = slug, Locale = Localizer.DefaultCulture, IsPublished = true, TranslationGroup = Guid.NewGuid().ToString("N") };
        var hero = new ContentBlock { BlockType = "hero", SortOrder = 0, DataJson = new JsonObject { ["heading"] = "Alt", ["subheading"] = "Unter", ["image"] = "/uploads/held.jpg", ["buttonUrl"] = "/kontakt", ["align"] = "center" }.ToJsonString() };
        var image = new ContentBlock { BlockType = "image", SortOrder = 1, DataJson = new JsonObject { ["src"] = "/uploads/bild.jpg" }.ToJsonString() };
        var cards = new ContentBlock { BlockType = "cards", SortOrder = 2, DataJson = "{}" };
        page.Blocks.AddRange([hero, image, cards]);
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        db.ContentBlocks.Add(new ContentBlock { PageId = page.Id, ParentId = cards.Id, BlockType = "card", SortOrder = 0, DataJson = new JsonObject { ["title"] = "Karte" }.ToJsonString() });
        await db.SaveChangesAsync();
        return (page.Id, hero.Id, image.Id, cards.Id);
    }

    [Fact]
    public async Task A_read_edit_round_trip_changes_only_the_sent_text_and_keeps_pictures_links_and_nested_blocks()
    {
        var (pageId, heroId, imageId, cardsId) = await SeedPageAsync("rundreise");

        var read = await ApplyAsync("page.read", new JsonObject { ["slug"] = "rundreise" });
        var blocks = JsonNode.Parse(read.ResultJson!)!["blocks"]!.AsArray();
        Assert.Equal(3, blocks.Count);
        Assert.Equal(heroId, blocks[0]!["id"]!.GetValue<int>());
        Assert.Contains("heading", blocks[0]!["editable"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(1, blocks[2]!["children"]!.GetValue<int>());

        // What a model sends back after changing the heading: the hero by id with ONE field, the others by id only, in a new order.
        var edit = await ApplyAsync("page.updateBlocks", new JsonObject
        {
            ["slug"] = "rundreise",
            ["removeMissing"] = false, // what the current cloud always sends
            ["blocks"] = new JsonArray(
                new JsonObject { ["id"] = cardsId },
                new JsonObject { ["id"] = heroId, ["data"] = new JsonObject { ["heading"] = "Neu", ["image"] = "javascript:alert(1)" } }),
        });
        Assert.NotEqual("failed", edit.Outcome);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await db.ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId).ToListAsync();
        var hero = JsonNode.Parse(after.Single(b => b.Id == heroId).DataJson)!;
        Assert.Equal("Neu", hero["heading"]!.GetValue<string>());
        Assert.Equal("Unter", hero["subheading"]!.GetValue<string>());
        Assert.Equal("/uploads/held.jpg", hero["image"]!.GetValue<string>());   // not a text field: never written by a content op
        Assert.Equal("/kontakt", hero["buttonUrl"]!.GetValue<string>());
        Assert.Equal("center", hero["align"]!.GetValue<string>());
        // The block it did not name is still there (moved behind the named ones), and the nested card stayed with its parent.
        Assert.Contains(after, b => b.Id == imageId);
        Assert.Contains(after, b => b.ParentId == cardsId && b.BlockType == "card");
        var order = after.Where(b => b.ParentId == null).OrderBy(b => b.SortOrder).Select(b => b.Id).ToList();
        Assert.Equal(new[] { cardsId, heroId, imageId }, order);
        Assert.Equal("partial", edit.Outcome); // the refused image field is named in the detail
        Assert.Contains("image", edit.Detail);
    }

    [Fact]
    public async Task Blocks_are_removed_only_when_asked_and_new_blocks_are_added()
    {
        var (pageId, heroId, imageId, cardsId) = await SeedPageAsync("entfernen");
        var edit = await ApplyAsync("page.updateBlocks", new JsonObject
        {
            ["slug"] = "entfernen",
            ["removeMissing"] = true,
            ["blocks"] = new JsonArray(
                new JsonObject { ["id"] = heroId },
                new JsonObject { ["type"] = "richtext", ["data"] = new JsonObject { ["body"] = "<p>Hallo</p><script>x()</script>" } }),
        });
        Assert.Equal("applied", edit.Outcome);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await db.ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId).ToListAsync();
        Assert.DoesNotContain(after, b => b.Id == imageId);
        Assert.DoesNotContain(after, b => b.Id == cardsId || b.ParentId == cardsId);
        var added = Assert.Single(after, b => b.BlockType == "richtext");
        Assert.DoesNotContain("<script", added.DataJson);
    }

    [Fact]
    public async Task A_new_page_in_the_navigation_gets_a_header_menu_entry_once()
    {
        var payload = new JsonObject
        {
            ["title"] = "Menütest", ["slug"] = "menuetest", ["showInNav"] = true,
            ["blocks"] = new JsonArray(new JsonObject { ["type"] = "richtext", ["data"] = new JsonObject { ["body"] = "<p>x</p>" } }),
        };
        Assert.Equal("applied", (await ApplyAsync("page.create", payload)).Outcome);
        Assert.Equal("skipped-exists", (await ApplyAsync("page.create", (JsonObject)payload.DeepClone())).Outcome);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var url = MatCMS.Services.Assistant.AssistantTools.PublicUrl("menuetest", Localizer.DefaultCulture);
        Assert.Single(await db.MenuItems.Where(m => m.Menu == "header" && m.Url == url).ToListAsync());
    }

    [Theory]
    [InlineData("code.head")]
    [InlineData("code.bodyEnd")]
    [InlineData("security.require2fa")]
    [InlineData("sso.enabled")]
    [InlineData("members.selfRegistration")]
    public async Task Script_and_sign_in_settings_are_refused(string key)
    {
        var r = await ApplyAsync("setting.set", new JsonObject { ["key"] = key, ["value"] = "<script>steal()</script>" });
        Assert.Equal("failed", r.Outcome);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SiteSettings.AnyAsync(s => s.Key == key && s.Value.Contains("steal")));
    }

    [Fact]
    public async Task Ids_in_any_form_name_the_existing_block_and_field_names_follow_the_block()
    {
        var (pageId, heroId, imageId, cardsId) = await SeedPageAsync("ids");
        var edit = await ApplyAsync("page.updateBlocks", new JsonObject
        {
            ["slug"] = "ids",
            ["removeMissing"] = true,
            ["blocks"] = new JsonArray(
                new JsonObject { ["id"] = heroId.ToString(), ["data"] = new JsonObject { ["HEADING"] = "Groß", ["subheading"] = "" } }, // a string id, other casing, a cleared field
                new JsonObject { ["Id"] = imageId },
                new JsonObject { ["id"] = imageId },     // twice: the second one is reported, not a copy
                new JsonObject { ["id"] = cardsId }),
        });
        Assert.Equal("partial", edit.Outcome);
        Assert.Contains("doppelt", edit.Detail);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = await db.ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId && b.ParentId == null).ToListAsync();
        Assert.Equal(3, after.Count); // nothing new, nothing lost
        var hero = JsonNode.Parse(after.Single(b => b.Id == heroId).DataJson)!.AsObject();
        Assert.Equal("Groß", hero["heading"]!.GetValue<string>());
        Assert.False(hero.ContainsKey("HEADING"));
        Assert.False(hero.ContainsKey("subheading"));
        Assert.Equal("/uploads/held.jpg", hero["image"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_older_cloud_without_removeMissing_still_replaces_the_page_instead_of_doubling_it()
    {
        var (pageId, _, _, _) = await SeedPageAsync("altcloud");
        // What a cloud before protocol 19 sends: the whole new page, no ids, no removeMissing.
        await ApplyAsync("page.updateBlocks", new JsonObject
        {
            ["slug"] = "altcloud",
            ["blocks"] = new JsonArray(new JsonObject { ["type"] = "richtext", ["data"] = new JsonObject { ["body"] = "<p>Neu</p>" } }),
        });
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var top = await db.ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId && b.ParentId == null).ToListAsync();
        Assert.Equal("richtext", Assert.Single(top).BlockType);
    }

    [Fact]
    public async Task The_raw_HTML_block_is_neither_editable_nor_creatable()
    {
        int pageId, htmlId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var page = new Page { Title = "Html", Slug = "rohhtml", Locale = Localizer.DefaultCulture, IsPublished = true, TranslationGroup = Guid.NewGuid().ToString("N") };
            var html = new ContentBlock { BlockType = "html", DataJson = new JsonObject { ["html"] = "<b>alt</b>" }.ToJsonString() };
            page.Blocks.Add(html);
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            (pageId, htmlId) = (page.Id, html.Id);
        }
        var read = JsonNode.Parse((await ApplyAsync("page.read", new JsonObject { ["slug"] = "rohhtml" })).ResultJson!)!;
        Assert.Empty(read["blocks"]![0]!["editable"]!.AsArray());

        await ApplyAsync("page.updateBlocks", new JsonObject
        {
            ["slug"] = "rohhtml",
            ["removeMissing"] = false,
            ["blocks"] = new JsonArray(
                new JsonObject { ["id"] = htmlId, ["data"] = new JsonObject { ["html"] = "<script>steal()</script>" } },
                new JsonObject { ["type"] = "html", ["data"] = new JsonObject { ["html"] = "<script>x()</script>" } }),
        });
        using var check = _app.Services.CreateScope();
        var blocks = await check.ServiceProvider.GetRequiredService<AppDbContext>().ContentBlocks.AsNoTracking().Where(b => b.PageId == pageId).ToListAsync();
        Assert.Single(blocks);
        Assert.Equal("<b>alt</b>", JsonNode.Parse(blocks[0].DataJson)!["html"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_cloud_address_push_sets_only_an_https_address()
    {
        Assert.Equal("failed", (await ApplyAsync("setting.set", new JsonObject { ["key"] = "site.canonicalUrl", ["value"] = "https://evil.example" })).Outcome);
        Assert.Equal("failed", (await ApplyAsync("site.address", new JsonObject { ["canonicalUrl"] = "http://plain.example", ["behindHttpsProxy"] = "1" })).Outcome);
        Assert.Equal("failed", (await ApplyAsync("site.address", new JsonObject { ["canonicalUrl"] = "https://x.example/pfad", ["behindHttpsProxy"] = "1" })).Outcome);
        Assert.Equal("applied", (await ApplyAsync("site.address", new JsonObject { ["canonicalUrl"] = "https://www.example.de/", ["behindHttpsProxy"] = "1" })).Outcome);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("https://www.example.de", (await db.SiteSettings.SingleAsync(s => s.Key == SettingKeys.CanonicalUrl)).Value);
        Assert.Equal("1", (await db.SiteSettings.SingleAsync(s => s.Key == SettingKeys.BehindHttpsProxy)).Value);
        // Taken back again, so the other tests of this application see the site as before.
        Assert.Equal("applied", (await ApplyAsync("site.address", new JsonObject { ["canonicalUrl"] = "", ["behindHttpsProxy"] = "" })).Outcome);
    }

    [Theory]
    [InlineData("smtp.host")]
    [InlineData("mail.transport")]
    [InlineData("ContactRecipient")]
    [InlineData("site.domainRoutes")]
    public async Task Mail_and_address_settings_are_refused(string key)
    {
        var r = await ApplyAsync("setting.set", new JsonObject { ["key"] = key, ["value"] = "evil.example" });
        Assert.Equal("failed", r.Outcome);
    }

    [Fact]
    public async Task An_ordinary_setting_is_still_set()
    {
        var r = await ApplyAsync("setting.set", new JsonObject { ["key"] = "seo.description", ["value"] = "Kurz und gut" });
        Assert.Equal("applied", r.Outcome);
    }
}
