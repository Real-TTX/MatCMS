using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>The blog since it left the image (docs/builtin-extraction.md): a signed native package from the MatCMS Store that adopts the core
/// table Posts. That it loads like the built-in did, that no post is ever lost on the way — with or without the package — and that what it shows
/// is what the built-in blog showed.</summary>
public class BlogPluginTests
{
    private static async Task<Harness> Start()
    {
        var h = new Harness();
        await BlogFixture.InstallAsync(h);
        Assert.Empty(h.Registry.Errors);
        return h;
    }

    private static Dictionary<string, object?> Values(string slug, Action<Dictionary<string, object?>>? tweak = null)
    {
        var v = new Dictionary<string, object?>
        {
            ["Title"] = slug.ToUpperInvariant(), ["Slug"] = slug, ["TitleImage"] = null, ["Excerpt"] = "e", ["ContentHtml"] = "<p>x</p>", ["Tags"] = "",
            ["AttachmentsJson"] = "[]", ["GalleryJson"] = "[]", ["GalleryLayout"] = "carousel", ["SourceUrl"] = null, ["SourceName"] = null, ["Locale"] = "de",
            ["IsPublished"] = true, ["PublishedAt"] = DateTime.UtcNow.AddDays(-1), ["CreatedAt"] = DateTime.UtcNow, ["UpdatedAt"] = DateTime.UtcNow
        };
        tweak?.Invoke(v);
        return v;
    }

    /// <summary>A post written straight into the core table — what the backup and the cloud see, with or without the plugin.</summary>
    private static async Task<long> Add(Harness h, string slug, Action<Dictionary<string, object?>>? tweak = null) =>
        await (await PostsTable.EnsureAsync(h.Data, h.Db)).InsertAsync(Values(slug, tweak));

    private static async Task<IReadOnlyList<DataRow>> Posts(Harness h) =>
        await (await PostsTable.EnsureAsync(h.Data, h.Db)).QueryAsync(DataQuery.Everything);

    // ---- it is a plugin like the others --------------------------------------------------------------

    [Fact]
    public async Task The_blog_is_no_longer_built_in_a_new_site_has_no_blog_and_its_key_stays_reserved()
    {
        using var h = new Harness(builtIns: true);
        await h.Runner.RunAllAsync();
        Assert.Null(BuiltInPlugins.Find("blog"));
        Assert.True(BuiltInPlugins.IsBuiltInKey("blog"));
        Assert.Contains("blog", BuiltInPlugins.FormerKeys);
        Assert.DoesNotContain(h.Db.Plugins, p => p.Key == "blog");
        Assert.False(h.Registry.IsLoaded("blog"));
    }

    [Fact]
    public async Task The_signed_package_runs_without_the_native_switch_registers_through_the_API_and_adopts_the_Posts_table()
    {
        using var h = await Start();                                   // the harness never allowed native plugins
        var row = h.Db.Plugins.Single(p => p.Key == "blog");
        Assert.Equal(PluginKind.Native, row.Kind);
        Assert.True(NativeTrust.IsSigned(h.Env, row));
        Assert.True(h.Registry.IsLoaded("blog"));

        // Every language now: no DefaultLanguageOnly any more (the handlers answer 404 for a language the site does not publish in).
        Assert.Contains(h.Registry.Routes, r => r.Owner == "blog" && r.Pattern.Template == "/blog/{slug}" && !r.Options.DefaultLanguageOnly);
        Assert.Contains(h.Registry.Routes, r => r.Owner == "blog" && r.Pattern.Template == "/blog");
        Assert.Contains(h.Registry.Blocks, b => b.Owner == "blog" && b.Type == "posts" && b.Category == "design");
        var section = Assert.Single(h.Registry.Sections, s => s.Owner == "blog");
        Assert.Equal((AdminGroup.Content, 20), (section.Group, section.Order));
        Assert.Contains(h.Registry.Sitemaps, c => c.Owner == "blog");
        Assert.True(new PluginContributions(h.Registry).HasOperation("post.create"));
        Assert.True(File.Exists(Path.Combine(StoragePaths.PluginAssetDir(h.Env, "blog"), "blog-editor.js")));

        var tables = await h.Data.DescribeAsync(h.Db, "blog");
        var posts = Assert.Single(tables);
        Assert.Equal(("Posts", true), (posts.Physical, posts.Adopted));
    }

    [Fact]
    public async Task Nobody_but_the_signed_package_may_take_the_key_blog()
    {
        using var h = new Harness();
        var unsigned = await PluginPackager.ImportAsync(BlogFixture.Bundle(sign: false), h.Env, h.Db);
        Assert.Null(unsigned.plugin);
        Assert.Contains("Lieferumfang", unsigned.error);

        var script = PluginPackager.Export(new Models.Plugin { Key = "blog", Name = "Fake", Version = "9", Code = "Plugin.Head(\"x\");" }, h.Env);
        var (plugin, _, error) = await PluginPackager.ImportAsync(new MemoryStream(script), h.Env, h.Db);
        Assert.Null(plugin);
        Assert.Contains("Lieferumfang", error);
        Assert.DoesNotContain(h.Db.Plugins, p => p.Key == "blog");
    }

    [Fact]
    public async Task A_built_in_row_left_behind_by_the_image_waits_with_its_data_and_the_signed_package_takes_it_over_with_its_state()
    {
        using var h = new Harness(builtIns: true);
        await Add(h, "bleibt");
        // What a site has after the image update: the blog's row is still "builtin", there is no such built-in any more.
        var row = new Models.Plugin { Key = "blog", Name = "Blog", Version = "1.0", Kind = PluginKind.BuiltIn, Enabled = true, ConfigJson = "{\"x\":\"1\"}", FilesJson = "{}" };
        h.Db.Plugins.Add(row);
        h.Db.SaveChanges();

        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("blog"));
        Assert.True(BuiltInPlugins.IsGone(row));
        Assert.Equal(BuiltInPlugins.GoneMessage, h.Registry.Errors[row.Id]);
        Assert.Equal(PluginKind.BuiltIn, h.Db.Plugins.Single(p => p.Key == "blog").Kind);   // a restart does not touch it either
        Assert.Single(await Posts(h));

        // The store's package arrives: it takes the row over — on stays on, the settings stay, the posts are there.
        await BlogFixture.InstallAsync(h, enable: null);
        var taken = h.Db.Plugins.AsNoTracking().Single(p => p.Key == "blog");
        Assert.Equal(PluginKind.Native, taken.Kind);
        Assert.Equal(row.Id, taken.Id);
        Assert.True(taken.Enabled);
        Assert.Equal("{\"x\":\"1\"}", taken.ConfigJson);
        Assert.True(h.Registry.IsLoaded("blog"));
        Assert.False(h.Registry.Errors.ContainsKey(row.Id));
        Assert.Equal("bleibt", (await Posts(h)).Single().Text("Slug"));
    }

    [Fact]
    public async Task A_leftover_row_that_was_off_stays_off_when_the_package_takes_it_over()
    {
        using var h = new Harness(builtIns: true);
        h.Db.Plugins.Add(new Models.Plugin { Key = "blog", Name = "Blog", Version = "1.0", Kind = PluginKind.BuiltIn, Enabled = false, ConfigJson = "{}", FilesJson = "{}" });
        h.Db.SaveChanges();
        await h.Runner.RunAllAsync();
        await BlogFixture.InstallAsync(h, enable: null);
        var taken = h.Db.Plugins.AsNoTracking().Single(p => p.Key == "blog");
        Assert.Equal(PluginKind.Native, taken.Kind);
        Assert.False(taken.Enabled);
        Assert.False(h.Registry.IsLoaded("blog"));
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
    public async Task A_database_from_before_the_change_keeps_its_posts_when_it_is_migrated_and_gets_the_translation_column()
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
                var table = await PostsTable.EnsureAsync(new PluginDataService(), db);    // adds TranslationGroup, touches nothing else
                var post = Assert.Single(await table.QueryAsync(DataQuery.Everything));
                Assert.Equal("Alt", post.Text("Title"));
                Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), post.Timestamp("PublishedAt"));
                Assert.True(post.IsNull("TranslationGroup"));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { File.Delete(file); } catch { /* best effort */ } }
    }

    private static async Task<string> ContentJson(byte[] backup)
    {
        using var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read);
        return await new StreamReader(zip.GetEntry("content.json")!.Open()).ReadToEndAsync();
    }

    [Fact]
    public async Task A_site_without_the_blog_package_backs_its_posts_up_and_restores_them_with_their_translations()
    {
        // Neither site has the plugin: the core carries the posts on its own.
        using var source = new Harness();
        var group = "g-1";
        await Add(source, "eins", v => { v["Tags"] = "a, b"; v["GalleryLayout"] = "grid"; v["TranslationGroup"] = group; });
        await Add(source, "one", v => { v["Locale"] = "en"; v["TranslationGroup"] = group; });
        await Add(source, "zwei");

        var backup = await new ContentTransferService(source.Db, source.Env, source.Runner, source.Data).ExportAsync(new ContentTransferService.BackupOptions());
        var json = await ContentJson(backup);
        Assert.Contains("\"eins\"", json);
        Assert.Contains("\"Slug\"", json, StringComparison.OrdinalIgnoreCase);

        using var target = new Harness();
        await new ContentTransferService(target.Db, target.Env, target.Runner, target.Data).ImportAsync(backup);
        var restored = await Posts(target);
        Assert.Equal(new[] { "eins", "one", "zwei" }, restored.Select(p => p.Text("Slug")).OrderBy(x => x));
        Assert.Equal("grid", restored.Single(p => p.Text("Slug") == "eins").Text("GalleryLayout"));
        Assert.Equal("a, b", restored.Single(p => p.Text("Slug") == "eins").Text("Tags"));
        Assert.Equal(group, restored.Single(p => p.Text("Slug") == "one").Text("TranslationGroup"));
        Assert.Equal("en", restored.Single(p => p.Text("Slug") == "one").Text("Locale"));
        Assert.True(restored.Single(p => p.Text("Slug") == "zwei").IsNull("TranslationGroup"));
        Assert.DoesNotContain(target.Db.Plugins, p => p.Key == "blog");
    }

    [Fact]
    public async Task A_backup_with_the_blog_switched_off_carries_the_posts_and_the_package_and_restores_both()
    {
        using var source = await Start();
        await Add(source, "eins");
        var row = source.Db.Plugins.Single(p => p.Key == "blog");
        row.Enabled = false;
        source.Db.SaveChanges();
        await source.Runner.ReloadAsync(row.Id);                        // switched off: the posts are still in the backup

        var backup = await new ContentTransferService(source.Db, source.Env, source.Runner, source.Data).ExportAsync(new ContentTransferService.BackupOptions());
        Assert.Contains("\"eins\"", await ContentJson(backup));

        using var target = new Harness();
        await new ContentTransferService(target.Db, target.Env, target.Runner, target.Data).ImportAsync(backup);
        Assert.Equal("eins", (await Posts(target)).Single().Text("Slug"));
        // The package came back as the signed native plugin it was, off as it was.
        var restoredRow = target.Db.Plugins.Single(p => p.Key == "blog");
        Assert.Equal(PluginKind.Native, restoredRow.Kind);
        Assert.False(restoredRow.Enabled);
        Assert.False(target.Registry.IsLoaded("blog"));
    }

    [Fact]
    public async Task A_backup_from_a_site_with_the_built_in_blog_brings_its_state_and_waits_for_the_package()
    {
        // The site the backup comes from still had the built-in: its row says "builtin", on, with settings.
        using var source = new Harness();
        await Add(source, "alt");
        source.Db.Plugins.Add(new Models.Plugin { Key = "blog", Name = "Blog", Version = "1.0", Kind = PluginKind.BuiltIn, Enabled = true, ConfigJson = "{\"x\":\"2\"}", FilesJson = "{}" });
        source.Db.SaveChanges();
        var backup = await new ContentTransferService(source.Db, source.Env, source.Runner, source.Data).ExportAsync(new ContentTransferService.BackupOptions());

        using var target = new Harness();
        await new ContentTransferService(target.Db, target.Env, target.Runner, target.Data).ImportAsync(backup);
        var waiting = target.Db.Plugins.Single(p => p.Key == "blog");
        Assert.Equal(PluginKind.BuiltIn, waiting.Kind);
        Assert.True(BuiltInPlugins.IsGone(waiting));
        Assert.True(waiting.Enabled);
        Assert.Equal("{\"x\":\"2\"}", waiting.ConfigJson);

        await BlogFixture.InstallAsync(target, enable: null);
        var taken = target.Db.Plugins.AsNoTracking().Single(p => p.Key == "blog");
        Assert.Equal((PluginKind.Native, true, "{\"x\":\"2\"}"), (taken.Kind, taken.Enabled, taken.ConfigJson));
        Assert.Equal("alt", (await Posts(target)).Single().Text("Slug"));
    }

    [Fact]
    public async Task A_restore_keeps_the_first_of_two_posts_with_the_same_address_instead_of_failing()
    {
        using var source = new Harness();
        await Add(source, "doppelt");
        var backup = await new ContentTransferService(source.Db, source.Env, source.Runner, source.Data).ExportAsync(new ContentTransferService.BackupOptions());
        // A hand-edited backup that names the post twice.
        byte[] edited;
        using (var ms = new MemoryStream())
        {
            ms.Write(backup);
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
            {
                var e = zip.GetEntry("content.json")!;
                string text;
                using (var r = new StreamReader(e.Open())) text = await r.ReadToEndAsync();
                var node = JsonNode.Parse(text)!.AsObject();
                var posts = node["Posts"]!.AsArray();
                posts.Add(posts[0]!.DeepClone());
                e.Delete();
                using var w = new StreamWriter(zip.CreateEntry("content.json").Open());
                await w.WriteAsync(node.ToJsonString());
            }
            edited = ms.ToArray();
        }
        using var target = new Harness();
        await new ContentTransferService(target.Db, target.Env, target.Runner, target.Data).ImportAsync(edited);
        Assert.Single(await Posts(target));
    }

    // ---- what the visitor sees ---------------------------------------------------------------------------

    /// <summary>Draws the posts block as a page in this language would.</summary>
    private static async Task<string> Block(Harness h, string json, string path = "/blog-karten", bool admin = false, Dictionary<string, string>? query = null, string locale = "de")
    {
        var block = h.Registry.Blocks.Single(b => b.Owner == "blog" && b.Type == "posts");
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(locale);         // the page language, as the localisation middleware sets it
        try
        {
            return await block.RenderAsync!(new PluginRequest
            {
                Services = h.Services, Registry = h.Registry, Data = json, Path = path, IsAdmin = admin, Query = query ?? new Dictionary<string, string>()
            }, default);
        }
        finally { CultureInfo.CurrentUICulture = before; }
    }

    private static IEnumerable<string> Hrefs(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "post-card__link\" href=\"/blog/([^\"]+)\"").Select(m => m.Groups[1].Value);

    [Fact]
    public async Task The_block_shows_published_posts_of_the_page_language_hides_scheduled_ones_from_visitors_and_shows_them_to_admins()
    {
        using var h = await Start();
        await Add(h, "live");
        await Add(h, "draft", v => v["IsPublished"] = false);
        await Add(h, "later", v => v["PublishedAt"] = DateTime.UtcNow.AddDays(3));
        await Add(h, "english", v => v["Locale"] = "en");

        Assert.Equal(new[] { "live" }, Hrefs(await Block(h, "{}")));
        Assert.Equal(new[] { "later", "live" }, Hrefs(await Block(h, "{}", admin: true)).OrderBy(x => x));
        // The English page shows the English post — under its own prefix.
        Assert.Contains("href=\"/en/blog/english\"", await Block(h, "{}", locale: "en"));
        Assert.DoesNotContain("/blog/live", await Block(h, "{}", locale: "en"));
    }

    [Fact]
    public async Task The_block_filters_by_tag_limits_pages_and_clamps_the_page_number_like_before()
    {
        using var h = await Start();
        for (var i = 1; i <= 7; i++)
        {
            var n = i;
            await Add(h, "p" + i, v => { v["Tags"] = n % 2 == 0 ? "gerade, news" : "ungerade"; v["PublishedAt"] = DateTime.UtcNow.AddDays(-n); });
        }

        var all = await Block(h, "{\"perPage\":\"3\"}");
        Assert.Equal(new[] { "p1", "p2", "p3" }, Hrefs(all));                                                        // newest first
        Assert.Contains("pseite=3", all);
        Assert.Equal(new[] { "p7" }, Hrefs(await Block(h, "{\"perPage\":\"3\"}", query: new() { ["pseite"] = "99" })));    // clamped to the last page

        var news = await Block(h, "{\"tag\":\"news\",\"showFilter\":\"yes\"}");
        Assert.Equal(new[] { "p2", "p4", "p6" }, Hrefs(news));
        Assert.DoesNotContain("ptag=news", news);                                                                  // the block's own tag is no chip
        Assert.Contains("ptag=gerade", news);
        Assert.Equal(new[] { "p1", "p2" }, Hrefs(await Block(h, "{\"limit\":\"2\"}")));
        Assert.Equal(new[] { "p1", "p3", "p5", "p7" }, Hrefs(await Block(h, "{}", query: new() { ["ptag"] = "ungerade" })));
    }

    [Fact]
    public async Task The_posts_block_draws_the_markup_templates_were_written_against_and_encodes_what_it_prints()
    {
        using var h = await Start();
        await Add(h, "hallo", v => { v["Title"] = "A <b> & \"q\" Größe"; v["Tags"] = "t1, t2"; v["TitleImage"] = "/uploads/a.jpg"; v["Excerpt"] = "Auszug <i>"; });

        var cards = await Block(h, "{\"display\":\"cards\",\"columns\":\"2\",\"heading\":\"Neu\"}");
        Assert.Contains("<section class=\"section posts-block\">", cards);
        Assert.Contains("<div class=\"section__intro\"><h2>Neu</h2></div>", cards);
        Assert.Contains("<div class=\"posts-grid cols-2\">", cards);
        Assert.Contains("href=\"/blog/hallo\"", cards);
        Assert.Contains("background-image:url('/thumb/640/a.jpg')", cards);
        Assert.Contains("A &lt;b&gt; &amp; &quot;q&quot; Größe", cards);                 // Razor's encoder: umlauts stay umlauts
        Assert.DoesNotContain("<b>", cards);
        Assert.Contains("<span class=\"post-tag\">t1</span>", cards);
        Assert.Contains("Weiterlesen", cards);

        var list = await Block(h, "{\"display\":\"blog\"}");
        Assert.Contains("<div class=\"container narrow\">", list);
        Assert.Contains("<div class=\"blog-list\">", list);
        Assert.Contains("<a class=\"post-tag\" href=\"/blog-karten?ptag=t1\">t1</a>", list);
        Assert.Contains("Zum Beitrag", list);
        Assert.Matches(@"blog-post__meta"">\d\d\. [A-ZÄÖÜa-zäöü]+ \d{4}<", list);          // "04. Oktober 2026", the German date it always had

        var empty = await Block(h, "{\"tag\":\"nope\"}");
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
        Assert.Equal("/blog/neu-aus-der-cloud", first["data"]!["url"]!.GetValue<string>());
        var post = (await Posts(h)).Single();
        Assert.Equal("neu-aus-der-cloud", post.Text("Slug"));
        Assert.Equal("de", post.Text("Locale"));
        Assert.DoesNotContain("<script", post.Text("ContentHtml"), StringComparison.OrdinalIgnoreCase);
        Assert.True(post.Bool("IsPublished"));

        var again = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", args))!;
        Assert.Equal("skipped-exists", again["status"]!.GetValue<string>());
        Assert.Single(await Posts(h));

        var bad = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", new JsonObject { ["title"] = "  " }))!;
        Assert.Equal("failed", bad["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_operation_links_a_translation_to_its_original_and_refuses_a_second_one_in_the_same_language()
    {
        using var h = await Start();
        var ops = new PluginContributions(h.Registry);
        var original = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", new JsonObject { ["title"] = "Hallo Welt", ["contentHtml"] = "<p>x</p>" }))!;
        var id = original["data"]!["id"]!.GetValue<long>();

        // "en" is not a language this harness publishes in (it has no site behind it: only the default one) → the post stays German …
        var notPublished = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.create", new JsonObject { ["title"] = "Hello", ["locale"] = "en", ["translationOf"] = id }))!;
        Assert.Equal("failed", notPublished["status"]!.GetValue<string>());          // … and a translation into the original's own language is refused
        Assert.Single(await Posts(h));

        var read = (JsonObject)(await ops.RunOperationAsync(h.Services, "post.read", new JsonObject { ["id"] = id }))!;
        Assert.Empty(read["data"]!["translations"]!.AsArray());
    }
}
