using System.Text.Json.Nodes;
using MatCMS.Services.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Settings, events, lifecycle, strings and jobs: what a plugin declares besides routes and screens, and the guarantees
/// the host gives for each.</summary>
public class PluginServicesTests
{
    // ---- settings -------------------------------------------------------------------------------------

    private const string SettingsPlugin = """
        Plugin.Settings
            .Text("title", "Titel", def: "Hallo", required: true)
            .Number("tax", "Steuersatz", def: 19, min: 0, max: 100)
            .Bool("live", "Live", def: false)
            .Choice("mode", "Modus", "a", new[] { new FieldOption("a", "A"), new FieldOption("b", "B") })
            .Secret("apikey", "API-Schlüssel");
        """;

    private static async Task<Harness> WithSettings()
    {
        var h = new Harness();
        h.AddScript("cfg", SettingsPlugin);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        return h;
    }

    private static (Dictionary<string, string> Config, Dictionary<string, string> Errors) Save(Harness h, Dictionary<string, string> form, string stored = "{}")
    {
        var slice = h.Registry.SliceOf("cfg")!;
        var provider = h.Services.GetRequiredService<IDataProtectionProvider>();
        var (config, errors) = PluginSettingsUi.Apply(slice, PluginSettingsAccess.ParseConfig(stored), form, provider, k => k);
        if (errors.Count == 0)
        {
            var row = h.Db.Plugins.Single(p => p.Key == "cfg");
            row.ConfigJson = PluginSettingsUi.ToJson(config);
            h.Db.SaveChanges();
        }
        return (config, errors);
    }

    [Fact]
    public async Task Settings_have_their_declared_defaults_until_something_is_saved_and_then_the_saved_values()
    {
        using var h = await WithSettings();
        var s = h.Context("cfg", new RequestData()).Settings;
        Assert.Equal("Hallo", s.Get("title"));
        Assert.Equal(19, s.GetNumber("tax"));
        Assert.False(s.GetBool("live"));
        Assert.Equal("a", s.Get("mode"));
        Assert.Equal("", s.Get("apikey"));
        Assert.Equal(7, s.GetInt("unknown", 7));

        var (_, errors) = Save(h, new() { ["s_title"] = " Shop ", ["s_tax"] = "7", ["s_live"] = "true", ["s_mode"] = "b", ["s_apikey"] = "sk_live_123" });
        Assert.Empty(errors);
        var s2 = h.Context("cfg", new RequestData()).Settings;
        Assert.Equal("Shop", s2.Get("title"));
        Assert.Equal(7, s2.GetInt("tax"));
        Assert.True(s2.GetBool("live"));
        Assert.Equal("b", s2.Get("mode"));
        Assert.Equal("sk_live_123", s2.Get("apikey"));
    }

    [Fact]
    public async Task A_secret_is_stored_encrypted_never_drawn_into_the_form_and_a_blank_field_keeps_it()
    {
        using var h = await WithSettings();
        Save(h, new() { ["s_title"] = "x", ["s_tax"] = "1", ["s_mode"] = "a", ["s_apikey"] = "sk_live_123" });
        var stored = h.Db.Plugins.Single(p => p.Key == "cfg").ConfigJson;
        Assert.DoesNotContain("sk_live_123", stored);
        Assert.Contains("enc1:", stored);

        var slice = h.Registry.SliceOf("cfg")!;
        var html = MatCMS.Shared.Web.UiRenderer.Render(PluginSettingsUi.Build(slice, PluginSettingsAccess.ParseConfig(stored), "de", "Speichern", "Entfernen", "gespeichert"));
        Assert.DoesNotContain("sk_live_123", html);
        Assert.DoesNotContain("enc1:", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("name=\"clear_apikey\"", html);

        // Saved again with the secret left blank: it stays. With the box ticked: it goes.
        Save(h, new() { ["s_title"] = "y", ["s_tax"] = "1", ["s_mode"] = "a", ["s_apikey"] = "" }, stored);
        Assert.Equal("sk_live_123", h.Context("cfg", new RequestData()).Settings.Get("apikey"));
        Save(h, new() { ["s_title"] = "y", ["s_tax"] = "1", ["s_mode"] = "a", ["clear_apikey"] = "true" }, h.Db.Plugins.Single(p => p.Key == "cfg").ConfigJson);
        Assert.Equal("", h.Context("cfg", new RequestData()).Settings.Get("apikey"));
    }

    [Theory]
    [InlineData("s_tax", "101", "tax")]
    [InlineData("s_tax", "-1", "tax")]
    [InlineData("s_tax", "viel", "tax")]
    [InlineData("s_tax", "NaN", "tax")]
    [InlineData("s_mode", "c", "mode")]
    [InlineData("s_title", "", "title")]
    public async Task Invalid_input_is_refused_per_field_and_nothing_is_saved(string field, string value, string errorKey)
    {
        using var h = await WithSettings();
        var form = new Dictionary<string, string> { ["s_title"] = "ok", ["s_tax"] = "1", ["s_mode"] = "a", [field] = value };
        var (_, errors) = Save(h, form);
        Assert.Contains(errorKey, errors.Keys);
        Assert.Equal("{}", h.Db.Plugins.Single(p => p.Key == "cfg").ConfigJson);
    }

    [Fact]
    public async Task Keys_the_plugin_does_not_declare_survive_a_save_and_a_forged_field_cannot_add_one()
    {
        using var h = await WithSettings();
        var (config, _) = Save(h, new() { ["s_title"] = "x", ["s_tax"] = "1", ["s_mode"] = "a", ["other"] = "forged", ["s_unknown"] = "forged" }, "{\"legacy\":\"keep\"}");
        Assert.Equal("keep", config["legacy"]);
        Assert.DoesNotContain("other", config.Keys);
        Assert.DoesNotContain("unknown", config.Keys);
    }

    [Theory]
    [InlineData("Plugin.Settings.Text(\"Bad Key\", \"x\");")]
    [InlineData("Plugin.Settings.Text(\"a\", \"x\").Text(\"a\", \"y\");")]
    [InlineData("Plugin.Settings.Choice(\"a\", \"x\", \"\", new FieldOption[0]);")]
    [InlineData("Plugin.Settings.Add(new SettingSpec(\"n\", \"N\", SettingKind.Number) { Default = \"abc\" });")]
    public async Task A_bad_settings_declaration_stops_the_load(string code)
    {
        using var h = new Harness();
        h.AddScript("bad", code);
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("bad"));
        Assert.NotEmpty(h.Registry.Errors);
    }

    // ---- strings --------------------------------------------------------------------------------------

    [Fact]
    public async Task Texts_come_in_the_pages_language_then_the_default_then_English_then_as_the_key()
    {
        using var h = new Harness();
        h.AddScript("t", """
            Plugin.Strings("de", new Dictionary<string, string> { ["hi"] = "Hallo {0}", ["only.de"] = "nur deutsch", ["shop.title"] = "Laden" });
            Plugin.Strings("en", new Dictionary<string, string> { ["hi"] = "Hello {0}", ["only.en"] = "only english" });
            """);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        var en = h.Context("t", new RequestData(), "en");
        var de = h.Context("t", new RequestData(), "de");
        var fr = h.Context("t", new RequestData(), "fr");
        Assert.Equal("Hello Max", en.T("hi", "Max"));
        Assert.Equal("Hallo Max", de.T("hi", "Max"));
        Assert.Equal("Hallo Max", fr.T("hi", "Max"));          // not translated to French: the site's default language
        Assert.Equal("only english", fr.T("only.en"));         // not in the default either: English
        Assert.Equal("nur deutsch", en.T("only.de"));          // English lacks it: the default
        Assert.Equal("nowhere", en.T("nowhere"));              // nobody has it: the key shows
        Assert.Equal("a {0 b", en.T("a {0 b"));                 // a stray brace does not throw
        var slice = h.Registry.SliceOf("t");
        Assert.Equal("Laden", PluginStrings.Label(slice, "de", "shop.title"));
        Assert.Equal("Ein Text", PluginStrings.Label(slice, "de", "Ein Text"));
    }

    // ---- events ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_event_reaches_every_listener_a_failing_one_does_not_stop_the_others_and_answers_are_collected()
    {
        using var h = new Harness();
        h.AddScript("a", """
            Plugin.On("shop.pay", async (ctx, payload, ct) => new System.Text.Json.Nodes.JsonObject { ["method"] = "card", ["amount"] = payload["amount"].GetValue<int>() });
            """);
        h.AddScript("b", """
            Plugin.On("shop.pay", (ctx, payload, ct) => throw new System.InvalidOperationException("kaputt"));
            """);
        h.AddScript("c", """
            Plugin.On("shop.pay", async (ctx, payload, ct) => { payload["amount"] = 999; return new System.Text.Json.Nodes.JsonObject { ["method"] = "invoice" }; });
            Plugin.On("shop.note", async (ctx, payload) => { ctx.Log("note " + payload["x"]); });
            """);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);

        var events = h.Context("a", new RequestData()).Events;
        var answers = await events.CollectAsync("shop.pay", new JsonObject { ["amount"] = 5 });
        Assert.Equal(new[] { "card", "invoice" }, answers.Select(a => a["method"]!.GetValue<string>()));
        // Listener c edited ITS copy of the payload; a (earlier) and anyone later see the original.
        Assert.Equal(5, answers[0]["amount"]!.GetValue<int>());
        Assert.Contains(h.Registry.LogOf("b"), l => l.Contains("kaputt"));

        await events.EmitAsync("shop.note", new JsonObject { ["x"] = 1 });
        Assert.Contains(h.Registry.LogOf("c"), l => l.Contains("note 1"));
        // An event nobody listens to is not an error.
        Assert.Empty(await events.CollectAsync("nobody.listens"));
    }

    [Fact]
    public async Task Events_that_trigger_each_other_in_a_loop_stop_instead_of_overflowing()
    {
        using var h = new Harness();
        h.AddScript("ping", "Plugin.On(\"ping\", async (ctx, p, ct) => { await ctx.Events.EmitAsync(\"pong\"); return null; });");
        h.AddScript("pong", "Plugin.On(\"pong\", async (ctx, p, ct) => { await ctx.Events.EmitAsync(\"ping\"); ctx.Log(\"tick\"); return null; });");
        await h.Runner.RunAllAsync();
        await h.Context("ping", new RequestData()).Events.EmitAsync("ping");
        Assert.Contains(h.Registry.Log, l => l.Contains("verschachtelte Ereignisse"));
    }

    // ---- lifecycle ------------------------------------------------------------------------------------

    private static string Lifecycle(string version, string extra = "") => $$"""
        Plugin.Data.Table("log", t => t.Text("what"));
        Plugin.Lifecycle.OnInstall(async ctx => await ctx.Data.Table("log").InsertAsync(new Dictionary<string, object?> { ["what"] = "install" }));
        Plugin.Lifecycle.OnUpdate(async (ctx, from) => await ctx.Data.Table("log").InsertAsync(new Dictionary<string, object?> { ["what"] = "update from " + from }));
        Plugin.Lifecycle.OnLoad(async ctx => await ctx.Data.Table("log").InsertAsync(new Dictionary<string, object?> { ["what"] = "load" }));
        {{extra}}
        """;

    private static async Task<List<string>> LogOf(Harness h) =>
        (await h.Context("life", new RequestData()).Data.Table("log").QueryAsync(DataQuery.Everything.OrderBy("id"))).Select(r => r.Text("what")).ToList();

    [Fact]
    public async Task Install_runs_once_update_runs_with_the_version_it_replaces_and_load_runs_every_time()
    {
        using var h = new Harness();
        var p = h.AddScript("life", Lifecycle("1"), version: "1");
        await h.Runner.RunAllAsync();
        Assert.Equal(new[] { "install", "load" }, await LogOf(h));

        await h.Runner.ReloadAsync(p.Id);                       // same version again
        Assert.Equal(new[] { "install", "load", "load" }, await LogOf(h));

        var row = h.Db.Plugins.Single(x => x.Key == "life");
        row.Version = "2";
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.Equal(new[] { "install", "load", "load", "update from 1", "load" }, await LogOf(h));
        Assert.Equal("2", h.Db.Plugins.AsEnumerable().Single(x => x.Key == "life").DataVersion);
    }

    [Fact]
    public async Task A_failing_installer_keeps_the_plugin_from_loading_and_it_is_tried_again_next_time()
    {
        using var h = new Harness();
        var p = h.AddScript("life", Lifecycle("1", "Plugin.Lifecycle.OnInstall(ctx => throw new System.InvalidOperationException(\"no seed\"));"));
        await h.Runner.RunAllAsync();
        Assert.False(h.Registry.IsLoaded("life"));
        Assert.Contains(h.Registry.Errors.Values, e => e.Contains("no seed"));
        Assert.Equal("", h.Db.Plugins.AsEnumerable().Single(x => x.Key == "life").DataVersion);   // not marked installed

        // Fixed and loaded again: installed now.
        var row = h.Db.Plugins.Single(x => x.Key == "life");
        row.Code = Lifecycle("1");
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.True(h.Registry.IsLoaded("life"));
        Assert.Contains("install", await LogOf(h));
    }

    [Fact]
    public async Task A_plugin_without_lifecycle_handlers_leaves_the_data_version_its_own_Migrate_wrote()
    {
        using var h = new Harness();
        var p = h.AddScript("old", "Migrate(\"5\", () => { });", version: "1.3");
        await h.Runner.RunAllAsync();
        Assert.Equal("5", h.Db.Plugins.AsEnumerable().Single(x => x.Key == "old").DataVersion);
        await h.Runner.ReloadAsync(p.Id);
        Assert.Equal("5", h.Db.Plugins.AsEnumerable().Single(x => x.Key == "old").DataVersion);
    }

    // ---- jobs -----------------------------------------------------------------------------------------

    private static PluginJobService Jobs(Harness h) =>
        new(h.Registry, h.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PluginJobService>.Instance);

    /// <summary>Waits until every job has run at least <paramref name="runs"/> time(s) and none is running — a tick only STARTS jobs.</summary>
    private static async Task WaitRan(PluginJobService jobs, Func<PluginJobService.JobStatus, bool> done)
    {
        for (var i = 0; i < 400; i++)
        {
            var s = jobs.Status();
            if (s.Count > 0 && s.All(x => !x.Running) && s.All(done)) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("the jobs did not finish");
    }

    [Fact]
    public void A_never_run_job_waits_a_full_interval_and_a_daily_job_waits_for_its_time()
    {
        var now = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddHours(1), PluginJobService.NextDue(JobSchedule.EveryInterval(TimeSpan.FromHours(1)), null, now));
        Assert.Equal(now.AddHours(1).AddMinutes(5), PluginJobService.NextDue(JobSchedule.EveryInterval(TimeSpan.FromHours(1)), now.AddMinutes(5), now));
        Assert.Equal(new DateTime(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc), PluginJobService.NextDue(JobSchedule.Daily(new TimeOnly(3, 0)), null, now));
        Assert.Equal(new DateTime(2026, 10, 14, 23, 0, 0, DateTimeKind.Utc), PluginJobService.NextDue(JobSchedule.Daily(new TimeOnly(23, 0)), null, now));
        Assert.Equal(new DateTime(2026, 10, 15, 23, 0, 0, DateTimeKind.Utc), PluginJobService.NextDue(JobSchedule.Daily(new TimeOnly(23, 0)), new DateTime(2026, 10, 14, 23, 0, 1, DateTimeKind.Utc), now));
        // Below a minute is a minute: a plugin cannot ask the host to spin.
        Assert.Equal(now.AddMinutes(1), PluginJobService.NextDue(JobSchedule.EveryInterval(TimeSpan.FromMilliseconds(1)), null, now));
    }

    [Fact]
    public async Task A_due_job_runs_once_records_its_run_and_an_unload_removes_it()
    {
        using var h = new Harness();
        h.AddScript("cron", """
            Plugin.Data.Table("c", t => t.Int("n"));
            Plugin.Job("count", JobSchedule.EveryInterval(TimeSpan.FromMinutes(1)), async ctx => await ctx.Data.Table("c").InsertAsync(new Dictionary<string, object?> { ["n"] = 1 }));
            """);
        await h.Runner.RunAllAsync();
        Assert.Empty(h.Registry.Errors);
        var jobs = Jobs(h);
        var t0 = DateTime.UtcNow;

        await jobs.TickAsync(t0);                                 // first sight: scheduled, not run
        await Task.Delay(150);
        Assert.Equal(0, await h.Context("cron", new RequestData()).Data.Table("c").CountAsync());
        Assert.Null(jobs.Status().Single().LastRunUtc);

        await jobs.TickAsync(t0.AddMinutes(2));                   // due
        await WaitRan(jobs, s => s.LastRunUtc is not null);
        Assert.Equal(1, await h.Context("cron", new RequestData()).Data.Table("c").CountAsync());
        Assert.Null(jobs.Status().Single().LastError);
        // The last run is stored: a restarted host does not run everything at once.
        Assert.Contains(h.Db.SiteSettings.AsEnumerable(), s => s.Key == "plugin.job.cron.count");

        await jobs.TickAsync(t0.AddMinutes(2).AddSeconds(5));     // not due again yet
        await Task.Delay(150);
        Assert.Equal(1, await h.Context("cron", new RequestData()).Data.Table("c").CountAsync());

        // Unloaded: the jobs are gone from the list and nothing runs.
        await h.Runner.UnloadAsync("cron");
        await jobs.TickAsync(t0.AddHours(1));
        Assert.Empty(jobs.Status());
    }

    [Fact]
    public async Task A_job_that_throws_is_recorded_on_its_plugin_and_never_reaches_the_host()
    {
        using var h = new Harness();
        h.AddScript("boom", "Plugin.Job(\"boom\", JobSchedule.EveryInterval(TimeSpan.FromMinutes(1)), ctx => throw new System.InvalidOperationException(\"job failed\"));");
        await h.Runner.RunAllAsync();
        var jobs = Jobs(h);
        var t0 = DateTime.UtcNow;
        await jobs.TickAsync(t0);
        await jobs.TickAsync(t0.AddMinutes(2));
        await WaitRan(jobs, s => s.LastRunUtc is not null);
        Assert.Contains("job failed", jobs.Status().Single().LastError);
        Assert.Contains(h.Registry.LogOf("boom"), l => l.Contains("job failed"));
        Assert.True(h.Registry.IsLoaded("boom"));
    }

    [Fact]
    public async Task A_job_that_is_still_running_is_not_started_a_second_time()
    {
        using var h = new Harness();
        h.AddScript("slow", """
            Plugin.Data.Table("c", t => t.Int("n"));
            Plugin.Job("slow", JobSchedule.EveryInterval(TimeSpan.FromMinutes(1)), async ctx =>
            {
                await ctx.Data.Table("c").InsertAsync(new Dictionary<string, object?> { ["n"] = 1 });
                await System.Threading.Tasks.Task.Delay(700);
            });
            """);
        await h.Runner.RunAllAsync();
        var jobs = Jobs(h);
        var t0 = DateTime.UtcNow;
        await jobs.TickAsync(t0);
        await jobs.TickAsync(t0.AddMinutes(2));
        await Task.Delay(100);
        Assert.True(jobs.Status().Single().Running);
        await jobs.TickAsync(t0.AddMinutes(2));                   // still running: no second start
        Assert.False(await jobs.RunNowAsync("slow", "slow"));     // "run now" refuses too
        await WaitRan(jobs, s => s.LastRunUtc is not null);
        Assert.Equal(1, await h.Context("slow", new RequestData()).Data.Table("c").CountAsync());
        Assert.False(await jobs.RunNowAsync("slow", "nope"));
    }

    // ---- uninstall ------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_plugin_removes_its_tables_but_deactivating_it_keeps_them()
    {
        using var h = new Harness();
        var p = h.AddScript("shopx", "Plugin.Data.Table(\"items\", t => t.Text(\"name\"));");
        await h.Runner.RunAllAsync();
        await h.Context("shopx", new RequestData()).Data.Table("items").InsertAsync(new Dictionary<string, object?> { ["name"] = "a" });

        var row = h.Db.Plugins.Single(x => x.Key == "shopx");
        row.Enabled = false;
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.False(h.Registry.IsLoaded("shopx"));
        Assert.Equal(1, (await h.Data.DescribeAsync(h.Db, "shopx")).Single().Rows);   // off is not gone

        await PluginUninstall.DeleteAsync(h.Db, h.Runner, h.Data, h.Env, h.Db.Plugins.Single(x => x.Key == "shopx"));
        Assert.Empty(await h.Data.DescribeAsync(h.Db, "shopx"));
        Assert.Empty(await PluginDataService.ColumnNamesAsync(h.Db, "p_shopx_items", default));
    }
}

public class PluginStringFileTests
{
    [Fact]
    public async Task A_script_plugins_language_files_are_its_texts_and_its_own_code_overrides_them()
    {
        using var h = new Harness();
        var p = h.AddScript("lang", "Plugin.Strings(\"de\", new Dictionary<string, string> { [\"over\"] = \"aus dem Code\" });");
        p.FilesJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["strings/de.json"] = "{\"hello\":\"Hallo {0}\",\"over\":\"aus der Datei\"}",
            ["strings/en.json"] = "{\"hello\":\"Hello {0}\"}",
            ["strings/kaputt.json"] = "{ nicht json",            // not a language file by name: ignored
            ["strings/fr.json"] = "{ nicht json"                  // a language file that is broken: reported, plugin still loads
        });
        h.Db.SaveChanges();
        await h.Runner.RunAllAsync();
        Assert.True(h.Registry.IsLoaded("lang"));
        Assert.Empty(h.Registry.Errors);
        Assert.Equal("Hallo Max", h.Context("lang", new RequestData(), "de").T("hello", "Max"));
        Assert.Equal("Hello Max", h.Context("lang", new RequestData(), "en").T("hello", "Max"));
        Assert.Equal("aus dem Code", h.Context("lang", new RequestData(), "de").T("over"));
        Assert.Contains(h.Registry.LogOf("lang"), l => l.Contains("strings/fr.json"));
    }

    [Fact]
    public void A_native_plugins_src_strings_folder_is_read_the_same_way()
    {
        var dir = Path.Combine(Path.GetTempPath(), "matcms-strings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "strings"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "strings", "de.json"), "{\"a\":\"eins\",\"n\":2}");
            File.WriteAllText(Path.Combine(dir, "strings", "readme.json"), "{\"a\":\"ignored\"}");
            var registry = new MatCMS.Services.PluginRegistry();
            var slice = registry.Begin("n", 1);
            PluginStringFiles.FromDirectory(slice, dir);
            Assert.Equal("eins", slice.Strings["de"]["a"]);
            Assert.Equal("2", slice.Strings["de"]["n"]);
            Assert.Single(slice.Strings);
        }
        finally { Directory.Delete(dir, true); }
    }
}
