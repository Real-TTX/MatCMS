using System.Runtime.CompilerServices;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Xunit;

namespace MatCMS.Plugin.Tests;

public class PluginSystemTests
{
    // ---- one API, two kinds ---------------------------------------------------------------------

    /// <summary>The prefix-independent shape of what a plugin registered.</summary>
    private static List<string> Shape(PluginRegistry r, string key)
    {
        string N(string s) => s.Replace(key, "{key}");
        var lines = new List<string>();
        foreach (var x in r.Routes.Where(x => x.Owner == key && !x.Pattern.Template.EndsWith("/db")).OrderBy(x => x.Pattern.Template, StringComparer.Ordinal))
            lines.Add($"route {N(x.Pattern.Template)} methods={x.Options.Methods} prg={x.Options.PostRedirectGet}");
        foreach (var s in r.Sections.Where(s => s.Owner == key))
        {
            lines.Add($"section '{s.Label}' icon={s.Icon} group={s.Group} order={s.Order} url={N(s.Url)}");
            foreach (var p in s.Pages) lines.Add($"  page {p.Pattern.Template} tab={p.Tab ?? "-"}");
            lines.Add($"  badge={(s.Badge is null ? "-" : "yes")}");
        }
        foreach (var b in r.Blocks.Where(b => b.Owner == key))
        {
            lines.Add($"block {b.Type} '{b.Name}' '{b.Description}'");
            foreach (var f in b.Fields)
                lines.Add($"  field {f.Id} '{f.Label}' {f.Type} ph={f.Placeholder} options={string.Join(",", f.Options.Select(o => o.Value + ":" + o.Label))}");
        }
        foreach (var h in r.HeadHtml.Where(h => h.Contains(key))) lines.Add("head " + N(h));
        var slice = r.SliceOf(key)!;
        foreach (var t in slice.Tables) lines.Add($"table {t.Name} [{string.Join(",", t.Columns.Select(c => c.Name + ":" + c.Type + (c.Required ? "!" : "") + "=" + c.Default))}] ix={t.Indexes.Count}");
        foreach (var s in slice.Settings) lines.Add($"setting {s.Key} {s.Kind} def={s.Default} min={s.Min} max={s.Max}");
        foreach (var kv in slice.Strings.OrderBy(k => k.Key)) lines.Add($"strings {kv.Key}: {string.Join(";", kv.Value.OrderBy(v => v.Key).Select(v => v.Key + "=" + v.Value))}");
        foreach (var j in r.Jobs.Where(j => j.Owner == key)) lines.Add($"job {j.Name} every={j.Schedule.Every} daily={j.Schedule.DailyAt}");
        foreach (var e in r.ListenersOf("ref.echo").Where(e => e.Owner == key)) lines.Add("on " + e.Name);
        lines.Add($"lifecycle install={slice.InstallHandlers.Count} update={slice.UpdateHandlers.Count} load={slice.LoadHandlers.Count}");
        return lines;
    }

    private static string Describe(string key, PluginResult r)
    {
        string N(string? s) => (s ?? "").Replace(key, "{key}");
        // A component tree is compared by what it draws: two plugins that describe the same screen must draw the same HTML.
        var drawn = r.Tree is null ? "" : N(MatCMS.Shared.Web.UiRenderer.Render(r.Tree));
        return $"{r.Kind}|{r.StatusCode}|{r.ContentType}|title={N(r.Title)}|desc={N(r.Description)}|target={N(r.Target)}|" +
               $"body={N(r.Body?.ToJsonString())}|content={N(r.Content)}|tree={drawn}";
    }

    private static async Task<string> Answer(Harness h, string key, PluginRegistry.RouteEntry route, Dictionary<string, string> values,
        string method, string path, Dictionary<string, string>? form = null)
    {
        var query = new Dictionary<string, string>();
        var qi = path.IndexOf('?');
        if (qi >= 0)
        {
            foreach (var pair in path[(qi + 1)..].Split('&')) { var kv = pair.Split('=', 2); query[kv[0]] = kv.Length > 1 ? kv[1] : ""; }
            path = path[..qi];
        }
        var ctx = h.Context(key, new RequestData
        {
            Method = method, Path = path, Route = values, Query = query, Form = form ?? new Dictionary<string, string>()
        });
        return Describe(key, await route.Handler(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Script_and_native_register_the_same_things_and_answer_the_same()
    {
        using var h = new Harness();
        h.AddScript("ref-script", Harness.FixtureScript());
        h.AddNative("ref-native");
        await h.Runner.RunAllAsync();

        Assert.Empty(h.Registry.Errors);

        // 1. The same registrations.
        var script = Shape(h.Registry, "ref-script");
        var native = Shape(h.Registry, "ref-native");
        Assert.NotEmpty(script);
        Assert.Equal(script, native);

        // 2. The same answers, for every route and every admin page.
        var asked = new List<(string Method, string Path, Dictionary<string, string>? Form)>
        {
            ("GET", "/{key}", null),
            ("GET", "/{key}/tasse", null),
            ("GET", "/{key}/api/42", null),
            ("POST", "/{key}/danke", new() { ["name"] = "Max" }),
            ("GET", "/{key}/items", null),
            ("POST", "/{key}/items", new() { ["name"] = "Tasse" }),
            ("GET", "/{key}/items", null),
            ("GET", "/{key}/greet", null),
            ("GET", "/{key}/echo?v=hey", null),
        };
        foreach (var (method, template, form) in asked)
        {
            var answers = new List<string>();
            foreach (var key in new[] { "ref-script", "ref-native" })
            {
                var path = template.Replace("{key}", key);
                var m = h.Registry.MatchRoute(path.Split('?')[0], method);
                Assert.True(m is not null, $"{key}: {method} {path} matched no route");
                answers.Add(await Answer(h, key, m!.Value.Route, m.Value.Values, method, path, form));
            }
            Assert.Equal(answers[0], answers[1]);
        }
        foreach (var page in new[] { "", "products", "orders", "ui", "products/7" })
        {
            var answers = new List<string>();
            foreach (var key in new[] { "ref-script", "ref-native" })
            {
                var m = h.Registry.MatchAdminPage(key, page);
                Assert.True(m is not null, $"{key}: admin page '{page}' not found");
                var ctx = h.Context(key, new RequestData { Path = "/" + page, Route = m!.Value.Values });
                answers.Add(Describe(key, await m.Value.Page.Handler(ctx, CancellationToken.None)));
            }
            Assert.Equal(answers[0], answers[1]);
        }

        // 3. The same block output, through the pipeline's own entry point.
        var blocks = new List<string>();
        foreach (var key in new[] { "ref-script", "ref-native" })
        {
            var block = h.Registry.Blocks.Single(b => b.Owner == key);
            blocks.Add(block.Render(new PluginRequest { Services = null!, Registry = h.Registry, Data = "{\"who\":\"Ann <b>\"}" }));
        }
        Assert.Equal(blocks[0], blocks[1]);
        Assert.Equal("<p>Hallo Ann &lt;b&gt;</p>", blocks[0]);
    }

    // ---- native: load, unload, update -----------------------------------------------------------

    // A test that checks unloading must not keep anything of the plugin in its own frame: a Debug build holds
    // every temporary (a matched route, an awaited Task and the state machine behind it) until the method ends,
    // and that alone keeps the load context alive. So everything that touches a plugin object lives in a
    // NoInlining helper that returns plain values — the same discipline NativePluginHost applies to itself.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string CallDbRoute(Harness h, string key, string dbFile)
    {
        async Task<string> Run()
        {
            var m = h.Registry.MatchRoute("/" + key + "/db", "GET")!.Value;
            var ctx = h.Context(key, new RequestData { Path = "/" + key + "/db", Query = new Dictionary<string, string> { ["db"] = dbFile } });
            return (await m.Route.Handler(ctx, CancellationToken.None)).Content;
        }
        return Run().GetAwaiter().GetResult();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasRoute(Harness h, string path) => h.Registry.MatchRoute(path, "GET") is not null;

    /// <summary>Identifies WHICH load of a plugin a route belongs to (its assembly), without holding the route.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int LoadIdentity(Harness h, string key) =>
        RuntimeHelpers.GetHashCode(h.Registry.Routes.First(r => r.Owner == key && r.Pattern.Template.EndsWith("/db")).Handler.Method.DeclaringType!.Assembly);

    [Fact]
    public async Task A_native_plugin_with_its_own_EF_context_and_JSON_can_be_unloaded_and_freed()
    {
        using var h = new Harness();
        h.AddNative("ref-native");
        await h.Runner.RunAllAsync();
        Assert.True(h.Native.IsLoaded("ref-native"));

        var dbFile = Path.Combine(h.Root, "ref.db");
        Assert.Equal("{\"Id\":1,\"Name\":\"a\"}", CallDbRoute(h, "ref-native", dbFile));

        // The point of the whole design: off means gone — registrations at once, memory with them.
        var freed = h.Native.Unload("ref-native", h.Registry);
        Assert.True(freed, "the plugin's load context is still alive after unloading — something in the host holds it " +
                           "(EF's provider cache? reflection-based JSON over a plugin type?) — see EfInternals");
        Assert.False(h.Native.IsLingering("ref-native"));
        Assert.False(HasRoute(h, "/ref-native/tasse"));
        Assert.Empty(h.Registry.Sections);
    }

    [Fact]
    public async Task A_plugin_can_be_loaded_again_after_it_was_unloaded()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-native");
        for (var i = 0; i < 3; i++)
        {
            await h.Runner.ReloadAsync(p.Id);
            Assert.True(HasRoute(h, "/ref-native/tasse"));
            await h.Runner.UnloadAsync("ref-native");
            Assert.False(HasRoute(h, "/ref-native/tasse"));
            Assert.False(h.Native.IsLingering("ref-native"));
        }
    }

    [Fact]
    public async Task A_failed_native_update_keeps_the_running_version()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-native", "1");
        await h.Runner.RunAllAsync();
        var running = h.Registry.Routes.Count(r => r.Owner == "ref-native");
        Assert.True(running > 0);

        // Version 2 points at a folder that does not exist.
        p.Version = "2";
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);

        Assert.True(h.Registry.Errors.ContainsKey(p.Id));
        Assert.Equal(running, h.Registry.Routes.Count(r => r.Owner == "ref-native"));
        Assert.True(h.Native.IsLoaded("ref-native"));
    }

    [Fact]
    public async Task A_native_update_replaces_the_old_version_in_one_step()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-native", "1");
        await h.Runner.RunAllAsync();
        var before = LoadIdentity(h, "ref-native");

        // Version 2 is the same DLL in its own folder, next to the first (never written over it).
        var from = StoragePaths.PluginSrc(new Fake(h.Root), "ref-native", "1");
        var to = StoragePaths.PluginSrc(new Fake(h.Root), "ref-native", "2");
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        p.Version = "2";
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);

        Assert.Empty(h.Registry.Errors);
        Assert.NotEqual(before, LoadIdentity(h, "ref-native"));   // a different load
        Assert.True(HasRoute(h, "/ref-native/tasse"));
        Assert.False(h.Native.IsLingering("ref-native"));    // and the old context was freed
    }

    [Fact]
    public async Task Native_plugins_do_not_run_unless_the_operator_allowed_them()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-native", allowNative: false);

        await h.Runner.RunAllAsync();
        Assert.Contains("nicht erlaubt", h.Registry.Errors[p.Id]);
        Assert.False(h.Native.IsLoaded("ref-native"));
        Assert.Empty(h.Registry.Routes);

        // The switch is live: allowed, the same plugin starts without being touched.
        h.AllowNative(true);
        await h.Runner.ReloadAsync(p.Id);
        Assert.Empty(h.Registry.Errors);
        Assert.True(h.Native.IsLoaded("ref-native"));

        // And off again means off.
        // The plugin stays ENABLED; what changes is the permission. A failed reload normally keeps the running
        // version — but a withdrawn permission is not a failed load, and "off" has to mean off.
        h.AllowNative(false);
        await h.Runner.ReloadAsync(p.Id);
        Assert.False(h.Native.IsLoaded("ref-native"));
        Assert.Empty(h.Registry.Routes);
        Assert.Contains("nicht erlaubt", h.Registry.Errors[p.Id]);
    }

    [Fact]
    public async Task A_changed_DLL_does_not_run()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-native");
        p.Sha256 = new string('0', 64);
        h.Db.SaveChanges();
        await h.Runner.RunAllAsync();

        Assert.Contains("Prüfsumme", h.Registry.Errors[p.Id]);
        Assert.False(h.Native.IsLoaded("ref-native"));
        Assert.Empty(h.Registry.Routes);
    }

    [Fact]
    public async Task A_plugin_built_for_another_API_major_does_not_run()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-native");
        p.Api = 99;
        h.Db.SaveChanges();
        await h.Runner.RunAllAsync();

        Assert.Contains("API 99", h.Registry.Errors[p.Id]);
        Assert.Empty(h.Registry.Routes);
    }

    // ---- scripts: per-plugin loading ------------------------------------------------------------

    [Fact]
    public async Task Reloading_one_plugin_does_not_touch_the_others()
    {
        using var h = new Harness();
        var a = h.AddScript("aaa", "Plugin.Route(\"/aaa\", ctx => PluginResult.Page(\"a1\"));");
        h.AddScript("bbb", "Plugin.Route(\"/bbb\", ctx => PluginResult.Page(\"b1\"));");
        await h.Runner.RunAllAsync();
        var bBefore = h.Registry.Routes.Single(r => r.Owner == "bbb");

        a.Code = "Plugin.Route(\"/aaa\", ctx => PluginResult.Page(\"a2\"));";
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(a.Id);

        Assert.Same(bBefore, h.Registry.Routes.Single(r => r.Owner == "bbb"));
        var m = h.Registry.MatchRoute("/aaa", "GET")!.Value;
        var result = await m.Route.Handler(h.Context("aaa", new RequestData()), CancellationToken.None);
        Assert.Equal("a2", result.Content);
    }

    [Fact]
    public async Task A_script_that_throws_on_its_first_load_registers_nothing()
    {
        using var h = new Harness();
        var p = h.AddScript("boom", "Plugin.Route(\"/boom\", ctx => PluginResult.Page(\"x\"));\nthrow new System.Exception(\"nein\");");
        await h.Runner.RunAllAsync();

        Assert.Contains("nein", h.Registry.Errors[p.Id]);
        Assert.Empty(h.Registry.Routes);                // not even the route it got to before the error
    }

    [Fact]
    public async Task A_script_saved_with_an_error_keeps_the_last_good_version_running()
    {
        using var h = new Harness();
        var p = h.AddScript("live", "Plugin.Route(\"/live\", ctx => PluginResult.Page(\"gut\"));");
        await h.Runner.RunAllAsync();

        p.Code = "Plugin.Route(\"/live\", ctx => PluginResult.Page(\"neu\");";   // a missing parenthesis (a missing final semicolon is legal in a script)
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);

        Assert.True(h.Registry.Errors.ContainsKey(p.Id));
        var m = h.Registry.MatchRoute("/live", "GET")!.Value;
        Assert.Equal("gut", (await m.Route.Handler(h.Context("live", new RequestData()), CancellationToken.None)).Content);

        // Fixing it clears the error and takes over.
        p.Code = "Plugin.Route(\"/live\", ctx => PluginResult.Page(\"neu\"));";
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.Empty(h.Registry.Errors);
        m = h.Registry.MatchRoute("/live", "GET")!.Value;
        Assert.Equal("neu", (await m.Route.Handler(h.Context("live", new RequestData()), CancellationToken.None)).Content);
    }

    [Fact]
    public async Task Switching_a_plugin_off_removes_it_and_on_brings_it_back()
    {
        using var h = new Harness();
        var p = h.AddScript("sw", "Plugin.Route(\"/sw\", ctx => PluginResult.Page(\"x\"));");
        await h.Runner.RunAllAsync();
        Assert.NotNull(h.Registry.MatchRoute("/sw", "GET"));

        p.Enabled = false; h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.Null(h.Registry.MatchRoute("/sw", "GET"));

        p.Enabled = true; h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.NotNull(h.Registry.MatchRoute("/sw", "GET"));
    }

    [Theory]
    [InlineData("/admin/x")]       // the back office
    [InlineData("/login")]
    [InlineData("/uploads/a")]
    [InlineData("/_framework/x")]
    [InlineData("/{slug}")]        // would answer for every page of the site
    [InlineData("/")]
    public async Task A_public_route_cannot_claim_what_belongs_to_the_CMS(string pattern)
    {
        using var h = new Harness();
        var p = h.AddScript("greedy", "Plugin.Route(\"" + pattern + "\", ctx => PluginResult.Page(\"x\"));");
        await h.Runner.RunAllAsync();

        Assert.True(h.Registry.Errors.ContainsKey(p.Id), "the route should have been refused");
        Assert.Empty(h.Registry.Routes);
    }

    [Fact]
    public async Task Older_scripts_that_use_the_PluginContext_members_still_run()
    {
        using var h = new Harness();
        h.AddScript("legacy",
            "AddAdminMenu(\"Alt\", \"/admin/plugin/legacy\", \"ti-star\");\n" +
            "AddAdminPage(\"legacy\", req => \"seite\");\n" +
            "AddPublicPage(\"legacy\", req => \"oeffentlich\");\n" +
            "AddBlock(\"legacy-b\", \"B\", \"d\", req => \"block\");\n" +
            "AddHeadHtml(\"<meta name='legacy'>\");\n");
        await h.Runner.RunAllAsync();

        Assert.Empty(h.Registry.Errors);
        Assert.Contains(h.Registry.AdminMenu, m => m.Label == "Alt" && m.Owner == "legacy");
        Assert.True(h.Registry.TryGetPage("legacy", out var page));
        Assert.Equal("seite", page!(new PluginRequest()));
        Assert.True(h.Registry.TryGetPublicPage("legacy", out _));
        Assert.Single(h.Registry.Blocks);
        Assert.Contains(h.Registry.HeadHtml, x => x.Contains("legacy"));
    }

    [Fact]
    public async Task A_plugin_can_replace_its_own_menu_entry_by_index_like_Leserstimmen_does()
    {
        using var h = new Harness();
        h.AddScript("badge", "AddAdminMenu(\"Stimmen\", \"/admin/plugin/badge\", \"ti-star\");");
        await h.Runner.RunAllAsync();

        var i = Enumerable.Range(0, h.Registry.AdminMenu.Count).First(x => h.Registry.AdminMenu[x].Url == "/admin/plugin/badge");
        h.Registry.AdminMenu[i] = new PluginRegistry.AdminMenuEntry("Stimmen (2)", "/admin/plugin/badge", "ti-star");

        Assert.Equal("Stimmen (2)", h.Registry.AdminMenu.Single(m => m.Url == "/admin/plugin/badge").Label);
    }

    // ---- the contract itself --------------------------------------------------------------------

    [Fact]
    public void The_plugin_API_assembly_depends_on_no_web_framework_and_no_EF()
    {
        var refs = typeof(IPlugin).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, r => r.StartsWith("MatCMS", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/shop/{slug}", "/shop/tasse", true)]
    [InlineData("/shop/{slug}", "/SHOP/tasse", true)]          // literals ignore case
    [InlineData("/shop/{slug}", "/shop", false)]
    [InlineData("/shop/{slug}", "/shop/a/b", false)]
    [InlineData("/shop/{*rest}", "/shop/a/b", true)]
    [InlineData("/shop/{*rest}", "/shop", true)]
    [InlineData("/shop", "/shop/", true)]
    [InlineData("/shop", "/shopx", false)]
    public void Route_patterns_match_as_documented(string pattern, string path, bool expected)
    {
        var p = PluginRoutePattern.TryParse(pattern, out _)!;
        Assert.Equal(expected, p.Match(path) is not null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/a/../b")]
    [InlineData("/a/{x}/{x}")]
    [InlineData("/a/{*rest}/b")]
    [InlineData("/a/b{x}")]
    [InlineData("/a?x=1")]
    public void Route_patterns_that_cannot_be_matched_unambiguously_are_refused(string pattern)
    {
        Assert.Null(PluginRoutePattern.TryParse(pattern, out var why));
        Assert.NotEmpty(why);
    }

    [Fact]
    public void A_literal_route_wins_over_a_parameter_and_a_catch_all_comes_last()
    {
        var r = new PluginRegistry();
        var slice = r.Begin("x", 1);
        void Add(string pattern) => slice.Routes.Add(new PluginRegistry.RouteEntry("x",
            new PluginInfo("x", "x", "1", "", 2), PluginRoutePattern.TryParse(pattern, out _)!, (_, _) => Task.FromResult(PluginResult.Status(200)), new PublicRouteOptions()));
        Add("/shop/{*rest}");
        Add("/shop/{slug}");
        Add("/shop/danke");
        r.Publish(slice);

        Assert.Equal("/shop/danke", r.MatchRoute("/shop/danke", "GET")!.Value.Route.Pattern.Template);
        Assert.Equal("/shop/{slug}", r.MatchRoute("/shop/tasse", "GET")!.Value.Route.Pattern.Template);
        Assert.Equal("/shop/{*rest}", r.MatchRoute("/shop/a/b", "GET")!.Value.Route.Pattern.Template);
    }

    [Theory]
    [InlineData("/ok", "/ok")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("", "/")]
    public void A_redirect_only_ever_goes_to_a_local_path(string target, string expected)
        => Assert.Equal(expected, PluginResults.SafeLocal(target));

    private sealed class Fake(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
