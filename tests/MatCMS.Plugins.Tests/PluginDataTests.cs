using System.Text.Json.Nodes;
using MatCMS.Services.Plugins;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Plugin data, layer 1: tables a plugin declares, rows it reads and writes, and what keeps one plugin out of another's
/// (or the CMS's) tables.</summary>
public class PluginDataTests
{
    private const string Items = """
        Plugin.Data.Table("items", t => t
            .Text("name", required: true)
            .Int("stock", def: "0")
            .Bool("active", def: "1")
            .Real("weight")
            .Timestamp("added")
            .Json("meta")
            .Text("note")
            .UniqueIndex("name"));
        """;

    private static async Task<(Harness H, IPluginContext Ctx, IDataTable T)> Start(string key = "shopx", string code = Items)
    {
        var h = new Harness();
        h.AddScript(key, code);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        var ctx = h.Context(key, new RequestData());
        return (h, ctx, ctx.Data.Table("items"));
    }

    private static Dictionary<string, object?> Row(string name, long stock = 1, object? extra = null) =>
        new() { ["name"] = name, ["stock"] = stock, ["note"] = extra };

    [Fact]
    public async Task A_declared_table_exists_and_every_column_type_round_trips()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        var when = new DateTime(2026, 10, 14, 12, 30, 5, DateTimeKind.Utc);
        var id = await t.InsertAsync(new Dictionary<string, object?>
        {
            ["name"] = "Tasse", ["stock"] = 7, ["active"] = false, ["weight"] = 0.25, ["added"] = when,
            ["meta"] = new JsonObject { ["color"] = "rot" }, ["note"] = null
        });
        var row = (await t.GetAsync(id))!;
        Assert.Equal(id, row.Id);
        Assert.Equal("Tasse", row.Text("name"));
        Assert.Equal(7, row.Int("stock"));
        Assert.False(row.Bool("active"));
        Assert.Equal(0.25, row.Real("weight"));
        Assert.Equal(when, row.Timestamp("added"));
        Assert.Equal(DateTimeKind.Utc, row.Timestamp("added")!.Value.Kind);
        Assert.Equal("rot", row.Json("meta")!["color"]!.GetValue<string>());
        Assert.True(row.IsNull("note"));
        // A column the insert did not mention has its declared default.
        var id2 = await t.InsertAsync(new Dictionary<string, object?> { ["name"] = "Teller" });
        var row2 = (await t.GetAsync(id2))!;
        Assert.Equal(0, row2.Int("stock"));
        Assert.True(row2.Bool("active"));
    }

    [Fact]
    public async Task Queries_filter_sort_and_page_and_a_local_time_is_stored_as_utc()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        foreach (var (n, s) in new[] { ("a", 5L), ("b", 1L), ("c", 9L), ("d", 3L), ("e", 7L) })
            await t.InsertAsync(Row(n, s));

        var q = DataQuery.Everything.Where("stock", DataOp.Ge, 3).OrderBy("stock", descending: true);
        Assert.Equal(new[] { "c", "e", "a", "d" }, (await t.QueryAsync(q)).Select(r => r.Text("name")));
        Assert.Equal(new[] { "e", "a" }, (await t.QueryAsync(q.Page(1, 2))).Select(r => r.Text("name")));
        Assert.Equal(4, await t.CountAsync(q));
        Assert.Equal(5, await t.CountAsync());
        Assert.Equal(new[] { "b", "d" }, (await t.QueryAsync(DataQuery.Everything.Where("name", DataOp.In, new[] { "d", "b", "zz" }).OrderBy("name"))).Select(r => r.Text("name")));
        Assert.Equal(new[] { "a", "b" }, (await t.QueryAsync(DataQuery.Everything.Either(new DataFilter("name", DataOp.Eq, "a"), new DataFilter("name", DataOp.Eq, "b")).OrderBy("name"))).Select(r => r.Text("name")));
        Assert.Empty(await t.QueryAsync(DataQuery.Everything.Where("name", DataOp.In, Array.Empty<string>())));
        Assert.NotNull(await t.FindByAsync("name", "c"));
        Assert.Null(await t.FindByAsync("name", "nope"));

        var local = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var id = await t.InsertAsync(new Dictionary<string, object?> { ["name"] = "t", ["added"] = local });
        Assert.Equal(local.ToUniversalTime(), (await t.GetAsync(id))!.Timestamp("added"));
    }

    [Fact]
    public async Task Update_and_delete_touch_only_what_they_name()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        var a = await t.InsertAsync(Row("a", 1));
        var b = await t.InsertAsync(Row("b", 2));
        Assert.Equal(1, await t.UpdateAsync(a, new Dictionary<string, object?> { ["stock"] = 10, ["note"] = "x" }));
        Assert.Equal(10, (await t.GetAsync(a))!.Int("stock"));
        Assert.Equal(2, (await t.GetAsync(b))!.Int("stock"));
        Assert.Equal(0, await t.UpdateAsync(9999, new Dictionary<string, object?> { ["stock"] = 1 }));
        Assert.Equal(1, await t.UpdateWhereAsync(DataQuery.Everything.Eq("name", "b"), new Dictionary<string, object?> { ["stock"] = 0 }));
        Assert.Equal(1, await t.DeleteAsync(a));
        Assert.Null(await t.GetAsync(a));
        Assert.Equal(1, await t.DeleteWhereAsync(DataQuery.Everything.Eq("name", "b")));
        Assert.Equal(0, await t.CountAsync());
        // The key cannot be rewritten through an update.
        var c = await t.InsertAsync(Row("c"));
        await t.UpdateAsync(c, new Dictionary<string, object?> { ["id"] = 777 });
        Assert.NotNull(await t.GetAsync(c));
    }

    [Fact]
    public async Task Nothing_a_visitor_types_can_become_SQL()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        const string hostile = "x'); DROP TABLE p_shopx_items; --";
        var id = await t.InsertAsync(Row(hostile, 1, hostile));
        Assert.Equal(hostile, (await t.GetAsync(id))!.Text("name"));
        Assert.Equal(1, await t.CountAsync(DataQuery.Everything.Eq("name", hostile)));

        // A column name is looked up in the declaration, never pasted into a statement.
        await Assert.ThrowsAsync<ArgumentException>(() => t.QueryAsync(DataQuery.Everything.Eq("name\" = 1 OR \"1", "a")));
        await Assert.ThrowsAsync<ArgumentException>(() => t.QueryAsync(DataQuery.Everything.OrderBy("name; DROP TABLE x")));
        await Assert.ThrowsAsync<ArgumentException>(() => t.InsertAsync(new Dictionary<string, object?> { ["nope"] = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => t.UpdateAsync(id, new Dictionary<string, object?> { ["name') --"] = 1 }));
        Assert.Equal(1, await t.CountAsync());

        // LIKE wildcards in a search term mean themselves.
        await t.InsertAsync(Row("100% Baumwolle"));
        await t.InsertAsync(Row("1000 Dinge"));
        Assert.Equal(1, await t.CountAsync(DataQuery.Everything.Where("name", DataOp.Contains, "0%")));
        Assert.Equal(1, await t.CountAsync(DataQuery.Everything.Where("name", DataOp.StartsWith, "100%")));
        // A value of the wrong type is refused, not coerced into something else.
        await Assert.ThrowsAsync<ArgumentException>(() => t.InsertAsync(new Dictionary<string, object?> { ["name"] = "n", ["stock"] = "viel" }));
    }

    [Fact]
    public async Task A_unique_index_refuses_a_duplicate_and_an_undeclared_table_name_is_an_error()
    {
        var (h, ctx, t) = await Start();
        using var _h = h;
        await t.InsertAsync(Row("same"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.InsertAsync(Row("same")));
        Assert.Throws<InvalidOperationException>(() => ctx.Data.Table("users"));
    }

    [Fact]
    public async Task A_transaction_keeps_what_it_wrote_when_it_returns_and_undoes_it_when_it_throws()
    {
        var (h, ctx, t) = await Start();
        using var _h = h;
        var kept = await ctx.Data.InTransactionAsync(async d => await d.Table("items").InsertAsync(Row("kept")));
        Assert.NotNull(await t.GetAsync(kept));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Data.InTransactionAsync<int>(async d =>
        {
            await d.Table("items").InsertAsync(Row("undone"));
            await d.Table("items").InsertAsync(Row("also undone"));
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal(1, await t.CountAsync());

        // A nested transaction joins the outer one: the outer failure undoes the inner work too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Data.InTransactionAsync(async d =>
        {
            await d.InTransactionAsync(async d2 => { await d2.Table("items").InsertAsync(Row("inner")); });
            throw new InvalidOperationException("outer");
        }));
        Assert.Equal(1, await t.CountAsync());
    }

    [Fact]
    public async Task A_newer_version_with_a_new_column_keeps_the_rows_and_the_database_is_never_dropped_from()
    {
        var (h, ctx, t) = await Start();
        using var _h = h;
        await t.InsertAsync(Row("old", 4));

        var p = h.Db.Plugins.Single(x => x.Key == "shopx");
        p.Code = Items.Replace(".Text(\"note\")", ".Text(\"note\").Text(\"sku\", def: \"n/a\").Int(\"price_cents\", def: \"0\")");
        p.Version = "2";
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.Empty(h.Registry.Errors);

        var t2 = h.Context("shopx", new RequestData()).Data.Table("items");
        var row = (await t2.QueryAsync(DataQuery.Everything)).Single();
        Assert.Equal("old", row.Text("name"));
        Assert.Equal("n/a", row.Text("sku"));
        Assert.Equal(0, row.Int("price_cents"));

        // A column removed from the declaration is NOT dropped: the data stays in the database, just unreachable.
        p.Code = Items.Replace(".Real(\"weight\")", "");
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        var columns = await PluginDataService.ColumnNamesAsync(h.Db, "p_shopx_items", default);
        Assert.Contains("weight", columns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_required_column_without_a_default_cannot_be_added_to_a_table_that_exists_and_the_old_version_keeps_running()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        await t.InsertAsync(Row("old"));
        var p = h.Db.Plugins.Single(x => x.Key == "shopx");
        p.Code = Items.Replace(".Text(\"note\")", ".Text(\"note\").Text(\"must\", required: true)");
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.Contains(h.Registry.Errors.Values, e => e.Contains("must") && e.Contains("Standardwert"));
        Assert.True(h.Registry.IsLoaded("shopx"));
        Assert.Equal(1, await h.Context("shopx", new RequestData()).Data.Table("items").CountAsync());
    }

    [Fact]
    public async Task Two_plugins_cannot_end_up_on_one_table()
    {
        using var h = new Harness();
        // "a-b" and "a_b" both name their table p_a_b_items.
        h.AddScript("a-b", Items);
        h.AddScript("a_b", Items);
        await h.Runner.RunAllAsync();
        Assert.True(h.Registry.IsLoaded("a-b"));
        Assert.False(h.Registry.IsLoaded("a_b"));
        Assert.Contains(h.Registry.Errors.Values, e => e.Contains("gehört dem Plugin 'a-b'"));
    }

    [Theory]
    [InlineData("Plugin.Data.Table(\"Bad Name\", t => t.Text(\"a\"));")]
    [InlineData("Plugin.Data.Table(\"items\", t => t.Text(\"id\"));")]
    [InlineData("Plugin.Data.Table(\"items\", t => t.Text(\"a\").Text(\"A\"));")]
    [InlineData("Plugin.Data.Table(\"items\", t => t.Text(\"a\").Index(\"zz\"));")]
    [InlineData("Plugin.Data.Table(\"items\", t => t.Text(\"a\"));\nPlugin.Data.Table(\"items\", t => t.Text(\"b\"));")]
    [InlineData("Plugin.Data.Table(\"users\", t => t.Adopt(\"Users\"));")]
    public async Task A_bad_declaration_stops_the_load_with_a_reason(string code)
    {
        using var h = new Harness();
        h.AddScript("bad", code);
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("bad"));
        Assert.NotEmpty(h.Registry.Errors);
    }

    [Fact]
    public async Task Only_a_plugin_that_is_part_of_the_CMS_may_adopt_an_existing_table()
    {
        using var h = new Harness();
        h.AddScript("sneaky", "Plugin.Data.Table(\"users\", t => t.Text(\"PasswordHash\").Adopt(\"Users\"));");
        await h.Runner.RunAllAsync();
        Assert.Contains(h.Registry.Errors.Values, e => e.Contains("part of the CMS"));
    }

    [Fact]
    public async Task Describe_lists_tables_with_row_counts_and_drop_removes_them()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        await t.InsertAsync(Row("a"));
        await t.InsertAsync(Row("b"));
        var info = await h.Data.DescribeAsync(h.Db, "shopx");
        var items = Assert.Single(info);
        Assert.Equal(("items", 2L, false), (items.Name, items.Rows, items.Adopted));

        await h.Data.DropAsync(h.Db, "shopx");
        Assert.Empty(await h.Data.DescribeAsync(h.Db, "shopx"));
        Assert.Empty(await PluginDataService.ColumnNamesAsync(h.Db, "p_shopx_items", default));
    }

    [Fact]
    public async Task A_backup_carries_the_tables_and_a_restore_puts_them_back_into_another_site()
    {
        var (h, _, t) = await Start();
        using var _h = h;
        await t.InsertAsync(new Dictionary<string, object?> { ["name"] = "Tasse", ["stock"] = 3, ["added"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ["meta"] = new JsonObject { ["a"] = 1 } });
        var json = await h.Data.ExportAsync(h.Db, "shopx");
        Assert.NotNull(json["items"]);

        using var target = new Harness();
        // The table does not exist there yet: the backup's own schema creates it, before the plugin loads.
        await target.Data.ImportAsync(target.Db, "shopx", (JsonObject)JsonNode.Parse(json.ToJsonString())!, Array.Empty<string>());
        target.AddScript("shopx", Items);
        await target.Runner.RunAllAsync();
        Assert.Empty(target.Registry.Errors);
        var row = (await target.Context("shopx", new RequestData()).Data.Table("items").QueryAsync(DataQuery.Everything)).Single();
        Assert.Equal("Tasse", row.Text("name"));
        Assert.Equal(3, row.Int("stock"));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), row.Timestamp("added"));
        Assert.Equal(1, row.Json("meta")!["a"]!.GetValue<int>());

        // A second restore replaces the rows rather than doubling them.
        await target.Data.ImportAsync(target.Db, "shopx", (JsonObject)JsonNode.Parse(json.ToJsonString())!, Array.Empty<string>());
        Assert.Equal(1, await target.Context("shopx", new RequestData()).Data.Table("items").CountAsync());
    }

    [Fact]
    public async Task A_backup_cannot_name_a_table_of_the_CMS_or_of_another_plugin()
    {
        using var h = new Harness();
        h.AddScript("other", Items);
        await h.Runner.RunAllAsync();
        await h.Context("other", new RequestData()).Data.Table("items").InsertAsync(Row("precious"));
        h.Db.SiteSettings.Add(new MatCMS.Models.SiteSetting { Key = "keep", Value = "me" });
        h.Db.SaveChanges();

        // A hostile backup: claims an adopted core table, and a table "items" whose physical name it tries to aim at another plugin.
        var hostile = JsonNode.Parse("""
            {
              "SiteSettings": { "adopted": true, "schema": { "Name": "SiteSettings", "Adopts": "SiteSettings", "KeyColumn": "Id", "Columns": [ { "Name": "Value", "Type": "Text" } ], "Indexes": [] },
                                "rows": [ { "Id": 1, "Value": "pwned" } ] },
              "items": { "physical": "p_other_items", "adopted": false, "schema": { "Name": "items", "Columns": [ { "Name": "name", "Type": "Text" } ], "Indexes": [] },
                         "rows": [ { "id": 1, "name": "pwned" } ] }
            }
            """)!.AsObject();
        await h.Data.ImportAsync(h.Db, "evil", hostile, Array.Empty<string>());

        Assert.Equal("me", h.Db.SiteSettings.AsEnumerable().Single(s => s.Key == "keep").Value);
        Assert.DoesNotContain(h.Db.SiteSettings.AsEnumerable(), s => s.Value == "pwned");
        Assert.Equal("precious", (await h.Context("other", new RequestData()).Data.Table("items").QueryAsync(DataQuery.Everything)).Single().Text("name"));
        // The hostile plugin got a table of ITS OWN name, nothing else.
        Assert.Single(await h.Data.DescribeAsync(h.Db, "evil"));
    }
}
