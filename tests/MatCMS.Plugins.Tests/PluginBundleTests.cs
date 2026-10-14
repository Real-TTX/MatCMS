using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Export and import of plugin bundles, for both kinds.</summary>
public class PluginBundleTests
{
    private static byte[] Zip(IDictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        return ms.ToArray();
    }

    private static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);

    private static Dictionary<string, string> Manifest(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("plugin.json")!.Open());
        using var doc = JsonDocument.Parse(r.ReadToEnd());
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
    }

    private static List<string> Names(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static async Task<(MatCMS.Models.Plugin? Plugin, bool Updated, string? Error)> Import(Harness h, byte[] bundle)
    {
        await using var ms = new MemoryStream(bundle);
        return await PluginPackager.ImportAsync(ms, h.Env, h.Db);
    }

    private static string RefDll(Harness h) => Path.Combine(AppContext.BaseDirectory, "fixtures", "refplugin", "RefPlugin.dll");

    /// <summary>A valid native bundle, built by hand around the real fixture DLL.</summary>
    private static Dictionary<string, byte[]> NativeEntries(string version = "1", string key = "ref-native", Action<Dictionary<string, object>>? tweak = null)
    {
        var dll = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "refplugin", "RefPlugin.dll"));
        var manifest = new Dictionary<string, object>
        {
            ["Format"] = 2, ["Name"] = "Ref", ["Key"] = key, ["Version"] = version, ["Description"] = "d",
            ["Code"] = "x", ["Mapping"] = "", ["Kind"] = "native", ["Api"] = "2", ["Entry"] = "RefPlugin.dll",
            ["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll)).ToLowerInvariant()
        };
        tweak?.Invoke(manifest);
        return new Dictionary<string, byte[]> { ["plugin.json"] = Json(manifest), ["src/RefPlugin.dll"] = dll };
    }

    // ---- native: export, import, round trip ------------------------------------------------------

    [Fact]
    public async Task A_native_plugin_survives_export_and_import_and_then_runs()
    {
        using var source = new Harness();
        var p = source.AddNative("ref-native");
        var bundle = PluginPackager.Export(p, source.Env);

        // The container shape.
        var manifest = Manifest(bundle);
        Assert.Equal("native", manifest["Kind"]);
        Assert.Equal("2", manifest["Format"]);
        Assert.Equal("RefPlugin.dll", manifest["Entry"]);
        Assert.Equal(p.Sha256, manifest["Sha256"]);
        Assert.Equal(MatCMS.Shared.PluginBundle.NativeGuardCode, manifest["Code"]);
        var names = Names(bundle);
        Assert.Contains("src/RefPlugin.dll", names);
        Assert.DoesNotContain(names, n => n.Contains("MatCMS.Plugin.Abstractions"));

        // Another instance imports it: present, native, verified — and OFF until somebody switches it on.
        using var target = new Harness();
        target.AllowNative(true);                                    // this site's operator allows compiled plugins
        var (plugin, updated, error) = await Import(target, bundle);
        Assert.Null(error);
        Assert.False(updated);
        Assert.Equal(PluginKind.Native, plugin!.Kind);
        Assert.False(plugin.Enabled);
        Assert.Equal(p.Sha256, plugin.Sha256);
        Assert.Equal(2, plugin.Api);
        Assert.True(File.Exists(Path.Combine(StoragePaths.PluginSrc(target.Env, "ref-native", "1"), "RefPlugin.dll")));

        await target.Runner.RunAllAsync();
        Assert.Empty(target.Registry.Routes);                       // imported code does not run by itself

        plugin.Enabled = true;
        target.Db.SaveChanges();
        await target.Runner.ReloadAsync(plugin.Id);
        Assert.Empty(target.Registry.Errors);
        Assert.NotEmpty(target.Registry.Routes);
        Assert.NotEmpty(target.Registry.Sections);
    }

    [Fact]
    public async Task Importing_a_newer_version_puts_it_next_to_the_old_one_and_keeps_only_the_previous()
    {
        using var h = new Harness();
        foreach (var v in new[] { "1", "2", "3" })
        {
            var (_, _, error) = await Import(h, Zip(NativeEntries(v)));
            Assert.Null(error);
        }
        var root = Path.Combine(StoragePaths.Plugins(h.Env), "ref-native");
        var left = Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "2", "3" }, left);                      // current + previous; "1" is gone
        Assert.Equal("3", h.Db.Plugins.Single(x => x.Key == "ref-native").Version);
    }

    [Fact]
    public async Task Importing_over_a_running_native_plugin_leaves_it_running_until_it_is_reloaded()
    {
        using var h = new Harness();
        h.AllowNative(true);
        var (p, _, _) = await Import(h, Zip(NativeEntries("1")));
        p!.Enabled = true; h.Db.SaveChanges();
        await h.Runner.ReloadAsync(p.Id);
        Assert.NotEmpty(h.Registry.Routes);

        var (_, updated, error) = await Import(h, Zip(NativeEntries("2")));
        Assert.Null(error);
        Assert.True(updated);
        // The import switches the plugin OFF (untrusted code from outside), exactly as for scripts.
        Assert.False(h.Db.Plugins.Single(x => x.Key == "ref-native").Enabled);
        await h.Runner.ReloadAsync(p.Id);
        Assert.Empty(h.Registry.Routes);
        Assert.False(h.Native.IsLoaded("ref-native"));
    }

    // ---- native: what is refused ----------------------------------------------------------------

    [Theory]
    [InlineData("src/../evil.dll")]                   // leaves the folder
    [InlineData("src/.hidden.dll")]
    [InlineData("src/run.exe")]                       // not a type a plugin may bring
    [InlineData("src/run.sh")]
    [InlineData("src/MatCMS.Plugin.Abstractions.dll")] // a private copy of the API breaks type identity
    public async Task A_native_bundle_with_a_path_or_file_it_may_not_have_is_refused_whole(string entry)
    {
        using var h = new Harness();
        var entries = NativeEntries();
        entries[entry] = new byte[] { 1, 2, 3 };
        var (plugin, _, error) = await Import(h, Zip(entries));

        Assert.NotNull(error);
        Assert.Null(plugin);
        Assert.Empty(h.Db.Plugins);                                  // nothing half-imported
        Assert.False(Directory.Exists(Path.Combine(StoragePaths.Plugins(h.Env), "ref-native", "1")));
        Assert.False(Directory.Exists(Path.Combine(StoragePaths.Plugins(h.Env), "ref-native", "1.importtmp")));
    }

    [Fact]
    public async Task A_native_bundle_whose_checksum_is_wrong_is_refused()
    {
        using var h = new Harness();
        var (plugin, _, error) = await Import(h, Zip(NativeEntries(tweak: m => m["Sha256"] = new string('0', 64))));
        Assert.Contains("Prüfsumme", error);
        Assert.Null(plugin);
    }

    [Theory]
    [InlineData("Entry", "")]
    [InlineData("Entry", "Other.dll")]                // named, but not in src/
    [InlineData("Entry", "RefPlugin.exe")]
    [InlineData("Version", "")]                       // the version names the folder
    public async Task A_native_bundle_without_a_usable_entry_or_version_is_refused(string field, string value)
    {
        using var h = new Harness();
        var (plugin, _, error) = await Import(h, Zip(NativeEntries(tweak: m => m[field] = value)));
        Assert.NotNull(error);
        Assert.Null(plugin);
    }

    [Fact]
    public async Task A_numeric_Api_field_written_by_a_rewriting_tool_is_still_read()
    {
        // The cloud rewrites plugin.json field by field and turns numbers into numbers: "Api": 2, not "2".
        using var h = new Harness();
        var (plugin, _, error) = await Import(h, Zip(NativeEntries(tweak: m => m["Api"] = 2)));
        Assert.Null(error);
        Assert.Equal(2, plugin!.Api);
    }

    [Fact]
    public async Task An_older_reader_gets_a_plugin_that_says_why_it_cannot_run()
    {
        // An instance from before native plugins ignores Kind/Entry/src/ and imports the manifest's Code as a
        // script. That code must fail with an explanation, not be an empty plugin that quietly does nothing.
        using var h = new Harness();
        var p = h.AddScript("old-reader", MatCMS.Shared.PluginBundle.NativeGuardCode);
        await h.Runner.RunAllAsync();
        Assert.Contains("natives Plugin", h.Registry.Errors[p.Id]);
    }

    // ---- scripts keep their form ----------------------------------------------------------------

    [Fact]
    public void A_script_bundle_keeps_exactly_the_shape_older_readers_and_the_cloud_know()
    {
        using var h = new Harness();
        var p = h.AddScript("plain", "AddHeadHtml(\"x\");");
        p.FilesJson = "{\"menu.csx\":\"// more\"}";
        var bundle = PluginPackager.Export(p, h.Env);

        var manifest = Manifest(bundle);
        Assert.Equal("1", manifest["Format"]);               // not bumped: older readers ignore it anyway
        Assert.Equal("AddHeadHtml(\"x\");", manifest["Code"]);
        Assert.Equal("", manifest["Kind"]);                  // absent-equivalent: a script is what a bundle without Kind is
        Assert.Equal("", manifest["Entry"]);
        Assert.Contains("files/menu.csx", Names(bundle));
        Assert.DoesNotContain(Names(bundle), n => n.StartsWith("src/"));
    }

    [Fact]
    public async Task A_script_bundle_from_before_native_plugins_still_imports_as_a_script()
    {
        using var h = new Harness();
        var legacy = Zip(new Dictionary<string, byte[]>
        {
            ["plugin.json"] = Json(new { Format = 1, Name = "Alt", Key = "alt", Version = "1", Description = "", Code = "AddHeadHtml(\"x\");", Mapping = "" }),
            ["files/menu.csx"] = Encoding.UTF8.GetBytes("// m")
        });
        var (plugin, _, error) = await Import(h, legacy);

        Assert.Null(error);
        Assert.Equal(PluginKind.Script, plugin!.Kind);
        Assert.Equal("", plugin.Entry);
        Assert.Contains("menu.csx", plugin.FilesJson);
    }

    [Fact]
    public async Task Importing_a_script_over_a_native_plugin_of_the_same_key_removes_its_compiled_files()
    {
        using var h = new Harness();
        await Import(h, Zip(NativeEntries("1", "mix")));
        Assert.True(Directory.Exists(Path.Combine(StoragePaths.Plugins(h.Env), "mix")));

        var script = Zip(new Dictionary<string, byte[]>
        {
            ["plugin.json"] = Json(new { Format = 1, Name = "Mix", Key = "mix", Version = "2", Code = "AddHeadHtml(\"x\");" })
        });
        var (plugin, updated, error) = await Import(h, script);

        Assert.Null(error);
        Assert.True(updated);
        Assert.Equal(PluginKind.Script, plugin!.Kind);
        Assert.False(Directory.Exists(Path.Combine(StoragePaths.Plugins(h.Env), "mix")));
    }
}
