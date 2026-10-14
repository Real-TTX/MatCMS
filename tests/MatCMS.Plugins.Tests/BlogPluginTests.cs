using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Plugin.Blog;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The blog as a built-in plugin: that it loads like any plugin, that no post is ever lost on the way, and that what it shows
/// is what the hard-wired blog showed.</summary>
public class BlogPluginTests
{
    private static async Task<Harness> Start()
    {
        var h = new Harness(builtIns: true);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        return h;
    }

    private static async Task<long> Add(Harness h, string slug, Action<Dictionary<string, object?>>? tweak = null)
    {
        var values = BlogStore.ToValues(new BlogPost { Title = slug.ToUpperInvariant(), Slug = slug, Excerpt = "e", ContentHtml = "<p>x</p>", PublishedAt = DateTime.UtcNow.AddDays(-1), IsPublished = true });
        tweak?.Invoke(values);
        return await h.Context("blog", new RequestData()).Data.Table("posts").InsertAsync(values);
    }

    // ---- it is a plugin like the others --------------------------------------------------------------

    [Fact]
    public async Task The_blog_starts_enabled_on_a_new_site_registers_through_the_public_API_and_adopts_the_Posts_table()
    {
        using var h = await Start();
        var row = h.Db.Plugins.Single(p => p.Key == "blog");
        Assert.Equal(PluginKind.BuiltIn, row.Kind);
        Assert.True(row.Enabled);
        Assert.True(h.Registry.IsLoaded("blog"));

        Assert.Contains(h.Registry.Routes, r => r.Owner == "blog" && r.Pattern.Template == "/blog/{slug}" && r.Options.DefaultLanguageOnly);
        Assert.Contains(h.Registry.Blocks, b => b.Owner == "blog" && b.Type == "posts" && b.Category == "design");
        var section = Assert.Single(h.Registry.Sections, s => s.Owner == "blog");
        Assert.Equal((AdminGroup.Content, 20), (section.Group, section.Order));
        Assert.Contains(h.Registry.Sitemaps, c => c.Owner == "blog");
        Assert.True(new PluginContributions(h.Registry).HasOperation("post.create"));

        var tables = await h.Data.DescribeAsync(h.Db, "blog");
        var posts = Assert.Single(tables);
        Assert.Equal(("Posts", true), (posts.Physical, posts.Adopted));
    }

    [Fact]
    public async Task Switching_the_blog_off_removes_its_pages_and_keeps_every_post_and_a_restart_does_not_switch_it_back_on()
    {
        using var h = await Start();
        await Add(h, "hallo");
        var row = h.Db.Plugins.Single(p => p.Key == "blog");
        row.Enabled = false;
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(row.Id);

        Assert.False(h.Registry.IsLoaded("blog"));
        Assert.Empty(h.Registry.Routes);
        Assert.Empty(h.Registry.Sections);
        Assert.Equal(1, (await h.Data.DescribeAsync(h.Db, "blog")).Single().Rows);

        await h.Runner.RunAllAsync();                               // what a restart does
        Assert.False(h.Registry.IsLoaded("blog"));
        Assert.False(h.Db.Plugins.Single(p => p.Key == "blog").Enabled);
    }

    [Fact]
    public async Task A_built_in_plugin_cannot_be_overwritten_by_a_bundle_and_an_operators_own_plugin_with_its_key_is_left_alone()
    {
        using var h = new Harness(builtIns: true);
        h.AddScript("blog", "Plugin.Head(\"<meta name=mine>\");");        // somebody's own plugin that took the key first
        await h.Runner.RunAllAsync();
        Assert.Equal(PluginKind.Script, h.Db.Plugins.Single(p => p.Key == "blog").Kind);
        Assert.Contains(h.Registry.LogOf("blog"), l => l.Contains("existiert bereits"));
        Assert.Contains(h.Registry.HeadHtml, x => x.Contains("mine"));

        using var h2 = await Start();
        var bundle = PluginPackager.Export(new MatCMS.Models.Plugin { Key = "blog", Name = "Fake", Version = "9", Code = "Plugin.Head(\"x\");" }, h2.Env);
        var (plugin, _, error) = await PluginPackager.ImportAsync(new MemoryStream(bundle), h2.Env, h2.Db);
        Assert.Null(plugin);
        Assert.Contains("Lieferumfang", error);
        Assert.Equal(PluginKind.BuiltIn, h2.Db.Plugins.Single(p => p.Key == "blog").Kind);
    }

    // ---- no post is lost on the way --------------------------------------------------------------------

    [Fact]
    public void The_migration_that_takes_Post_out_of_the_model_does_nothing_to_the_database()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        var m = new MatCMS.Data.Migrations.BlogLeavesTheModel();
        typeof(Migration).GetMethod("Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(m, new object[] { builder });
        Assert.Empty(builder.Operations);
        Assert.Empty(m.UpOperations);
    }

    [Fact]
    public async Task A_database_from_before_the_change_keeps_its_posts_when_it_is_migrated_to_the_new_model()
    {
        var file = Path.Combine(Path.GetTempPath(), "matcms-blog-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=" + file).Options;
            await using (var db = new AppDbContext(opts))
            {
                await db.Database.GetService<IMigrator>().MigrateAsync("20261014163806_AddPluginKind");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Posts\" (\"Title\",\"Slug\",\"Excerpt\",\"ContentHtml\",\"Tags\",\"AttachmentsJson\",\"Locale\",\"IsPublished\",\"PublishedAt\",\"CreatedAt\",\"UpdatedAt\",\"GalleryJson\",\"GalleryLayout\") " +
                    "VALUES ('Alt','alt','e','<p>x</p>','t','[]','de',1,'2026-01-01 00:00:00','2026-01-01 00:00:00','2026-01-01 00:00:00','[]','carousel')");
            }
            await using (var db = new AppDbContext(opts))
            {
                await db.Database.MigrateAsync();                              // all the way, including BlogLeavesTheModel
                var table = BlogStore.Table(db);
                var post = Assert.Single(await table.QueryAsync(MatCMS.Plugin.DataQuery.Everything));
                Assert.Equal("Alt", post.Text("Title"));
                Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), post.Timestamp("PublishedAt"));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(file); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task A_backup_carries_the_posts_whether_or_not_the_blog_is_on_and_restores_them_into_another_site_also_from_the_old_shape()
    {
        using var source = await Start();
        await Add(source, "eins", v => { v["Tags"] = "a, b"; v["GalleryLayout"] = "grid"; });
        await Add(source, "zwei");
        var row = source.Db.Plugins.Single(p => p.Key == "blog");
        row.Enabled = false;
        source.Db.SaveChanges();
        await source.Runner.ReloadAsync(row.Id);                        // switched off: the posts are still in the backup

        var transfer = new ContentTransferService(source.Db, source.Env, source.Runner, source.Data);
        var backup = await transfer.ExportAsync(new ContentTransferService.BackupOptions());
        string json;
        using (var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read))
            json = await new StreamReader(zip.GetEntry("content.json")!.Open()).ReadToEndAsync();
        Assert.Contains("\"eins\"", json);
        Assert.Contains("\"zwei\"", json);
        Assert.Contains("\"Slug\"", json, StringComparison.OrdinalIgnoreCase);

        using var target = new Harness(builtIns: true);
        await new ContentTransferService(target.Db, target.Env, target.Runner, target.Data).ImportAsync(backup);
        var restored = await BlogStore.AllAsync(BlogStore.Table(target.Db));
        Assert.Equal(new[] { "eins", "zwei" }.OrderBy(x => x), restored.Select(p => p.Slug).OrderBy(x => x));
        Assert.Equal("grid", restored.Single(p => p.Slug == "eins").GalleryLayout);
        Assert.Equal("a, b", restored.Single(p => p.Slug == "eins").Tags);
        // The blog was OFF in the backup and is off after the restore: a restore restores the site as it was.
        Assert.False(target.Registry.IsLoaded("blog"));
    }

    // ---- what the visitor sees ---------------------------------------------------------------------------

    private static BlogPublic.ListRequest Req(string tag = "", int limit = 0, int perPage = 0, string active = "", int page = 1, bool admin = false, string locale = "de") =>
        new(tag, limit, perPage, active, page, admin, locale);

    [Fact]
    public async Task The_list_shows_published_posts_of_the_language_hides_scheduled_ones_from_visitors_and_shows_them_to_admins()
    {
        using var h = await Start();
        await Add(h, "live");
        await Add(h, "draft", v => v["IsPublished"] = false);
        await Add(h, "later", v => v["PublishedAt"] = DateTime.UtcNow.AddDays(3));
        await Add(h, "english", v => v["Locale"] = "en");
        var table = h.Context("blog", new RequestData()).Data.Table("posts");

        Assert.Equal(new[] { "live" }, (await BlogPublic.ListAsync(table, Req())).Posts.Select(p => p.Slug));
        Assert.Equal(new[] { "later", "live" }, (await BlogPublic.ListAsync(table, Req(admin: true))).Posts.Select(p => p.Slug).OrderBy(x => x));
        Assert.Equal(new[] { "english" }, (await BlogPublic.ListAsync(table, Req(locale: "en"))).Posts.Select(p => p.Slug));
    }

    [Fact]
    public async Task The_list_filters_by_tag_limits_pages_and_clamps_the_page_number_like_before()
    {
        using var h = await Start();
        for (var i = 1; i <= 7; i++)
        {
            var n = i;
            await Add(h, "p" + i, v => { v["Tags"] = n % 2 == 0 ? "gerade, news" : "ungerade"; v["PublishedAt"] = DateTime.UtcNow.AddDays(-n); });
        }
        var table = h.Context("blog", new RequestData()).Data.Table("posts");

        var all = await BlogPublic.ListAsync(table, Req(perPage: 3));
        Assert.Equal(new[] { "p1", "p2", "p3" }, all.Posts.Select(p => p.Slug));                  // newest first
        Assert.Equal((1, 3), (all.PageNumber, all.TotalPages));
        Assert.Equal(new[] { "p7" }, (await BlogPublic.ListAsync(table, Req(perPage: 3, page: 99))).Posts.Select(p => p.Slug));   // clamped to the last page
        Assert.Equal(3, (await BlogPublic.ListAsync(table, Req(perPage: 3, page: 99))).PageNumber);

        var news = await BlogPublic.ListAsync(table, Req(tag: "news"));
        Assert.Equal(new[] { "p2", "p4", "p6" }, news.Posts.Select(p => p.Slug));
        Assert.DoesNotContain("news", news.FilterTags, StringComparer.OrdinalIgnoreCase);        // the block's own tag is no chip
        Assert.Equal(new[] { "gerade" }, news.FilterTags);
        Assert.Equal(new[] { "p1", "p2" }, (await BlogPublic.ListAsync(table, Req(limit: 2))).Posts.Select(p => p.Slug));
        Assert.Equal(new[] { "p1", "p3", "p5", "p7" }, (await BlogPublic.ListAsync(table, Req(active: "ungerade"))).Posts.Select(p => p.Slug));
    }

    [Fact]
    public async Task The_posts_block_draws_the_markup_templates_were_written_against_and_encodes_what_it_prints()
    {
        using var h = await Start();
        await Add(h, "hallo", v => { v["Title"] = "A <b> & \"q\""; v["Tags"] = "t1, t2"; v["TitleImage"] = "/uploads/a.jpg"; v["Excerpt"] = "Auszug <i>"; });
        var info = new PluginInfo("blog", "Blog", "1.0", "", 2);
        PluginRequestContext Block(string json) => PluginRequestContext.FromBlock(info, "posts",
            new PluginRequest { Services = h.Services, Registry = h.Registry, Data = json, Path = "/blog-karten", Query = new Dictionary<string, string>() }, h.Registry);

        var cards = (await BlogPublic.PostsBlockAsync(Block("{\"display\":\"cards\",\"columns\":\"2\",\"heading\":\"Neu\"}"), default)).Content;
        Assert.Contains("<section class=\"section posts-block\">", cards);
        Assert.Contains("<div class=\"section__intro\"><h2>Neu</h2></div>", cards);
        Assert.Contains("<div class=\"posts-grid cols-2\">", cards);
        Assert.Contains("href=\"/blog/hallo\"", cards);
        Assert.Contains("background-image:url('/uploads/a.jpg')", cards);
        Assert.Contains("A &lt;b&gt; &amp; &quot;q&quot;", cards);
        Assert.DoesNotContain("<b>", cards);
        Assert.Contains("<span class=\"post-tag\">t1</span>", cards);
        Assert.Contains("Weiterlesen", cards);

        var list = (await BlogPublic.PostsBlockAsync(Block("{\"display\":\"blog\"}"), default)).Content;
        Assert.Contains("<div class=\"container narrow\">", list);
        Assert.Contains("<div class=\"blog-list\">", list);
        Assert.Contains("<a class=\"post-tag\" href=\"/blog-karten?ptag=t1\">t1</a>", list);
        Assert.Contains("Zum Beitrag", list);

        var empty = (await BlogPublic.PostsBlockAsync(Block("{\"tag\":\"nope\"}"), default)).Content;
        Assert.Contains("<p class=\"muted\">", empty);
    }

    // ---- the cloud channel -------------------------------------------------------------------------------

    [Fact]
    public async Task The_clouds_post_create_is_the_blogs_operation_add_only_and_it_sanitises_what_an_AI_wrote()
    {
        using var h = await Start();
        var ops = new PluginContributions(h.Registry);
        var args = new JsonObject { ["title"] = "Neu aus der Cloud", ["contentHtml"] = "<p>Hallo<script>alert(1)</script></p>", ["publish"] = true };

        var first = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", args))!;
        Assert.Equal("applied", first["status"]!.GetValue<string>());
        var post = (await BlogStore.AllAsync(BlogStore.Table(h.Db))).Single();
        Assert.Equal("neu-aus-der-cloud", post.Slug);
        Assert.DoesNotContain("<script", post.ContentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.True(post.IsPublished);

        var again = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", args))!;
        Assert.Equal("skipped-exists", again["status"]!.GetValue<string>());
        Assert.Single(await BlogStore.AllAsync(BlogStore.Table(h.Db)));

        var bad = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", new JsonObject { ["title"] = "  " }))!;
        Assert.Equal("failed", bad["status"]!.GetValue<string>());
    }
}
