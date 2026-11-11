using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using MatCMS.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// ONE test signing key for the whole run, trusted IN ADDITION to the real MatCMS Store key (never instead of it). Every test that needs
/// "signed by the store" signs with it — one key, registered once under a lock, because <see cref="NativeTrust.ExtraKeys"/> is static and test
/// classes run in parallel (two read-modify-writes at once would drop a key).
/// </summary>
internal static class TestStoreKey
{
    private static readonly object Gate = new();
    public static readonly ECDsa Key;
    public static readonly string KeyId;

    static TestStoreKey()
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Key.ExportSubjectPublicKeyInfo();
        KeyId = StoreSignature.KeyId(spki);
        lock (Gate)
        {
            var extra = new Dictionary<string, string>(NativeTrust.ExtraKeys ?? new Dictionary<string, string>()) { [KeyId] = Convert.ToBase64String(spki) };
            NativeTrust.ExtraKeys = extra;
        }
    }

    /// <summary>A code signature for these files under this key and version.</summary>
    public static string SignCode(string key, string version, string treeHex) => StoreSignature.SignCode(key, version, treeHex, Key, KeyId);

    /// <summary>A code signature for the files of a folder.</summary>
    public static string SignDir(string key, string version, string srcDir) => SignCode(key, version, StoreSignature.TreeHash(srcDir));
}

/// <summary>
/// The blog (plugins/blog) as a site gets it since it left the image: the store's code-signed native package, imported through the real
/// <see cref="PluginPackager"/> (assets included) and loaded by the real runner. Signed with <see cref="TestStoreKey"/>, so it runs without
/// "Native Plugins erlauben" and may adopt the core table Posts — exactly what the real package may.
/// </summary>
internal static class BlogFixture
{
    public const string Version = "2.0";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "blog");

    /// <summary>The package: plugin.json, src/Blog.dll, assets/*.</summary>
    public static MemoryStream Bundle(bool sign = true, string version = Version)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var dll = Path.Combine(Dir, "Blog.dll");
            var tree = StoreSignature.TreeHash(new[] { ("Blog.dll", StoreSignature.Sha256Hex(File.ReadAllBytes(dll))) });
            var manifest = new Dictionary<string, object>
            {
                ["Format"] = 2, ["Name"] = "Blog", ["Key"] = "blog", ["Version"] = version, ["Description"] = "Blog", ["Code"] = PluginBundle.NativeGuardCode,
                ["Kind"] = PluginBundle.KindNative, ["Api"] = typeof(IPlugin).Assembly.GetName().Version!.Major.ToString(), ["Entry"] = "Blog.dll",
                ["Sha256"] = NativePluginHost.HashOf(dll), ["Mapping"] = "",
                ["CodeSignature"] = sign ? TestStoreKey.SignCode("blog", version, tree) : ""
            };
            using (var w = new StreamWriter(zip.CreateEntry(PluginBundle.ManifestEntry).Open())) w.Write(JsonSerializer.Serialize(manifest));
            using (var s = zip.CreateEntry(PluginBundle.SourceFolder + "Blog.dll").Open()) s.Write(File.ReadAllBytes(dll));
            foreach (var asset in Directory.GetFiles(Path.Combine(Dir, "assets")))
                using (var s = zip.CreateEntry(PluginBundle.AssetFolder + Path.GetFileName(asset)).Open()) s.Write(File.ReadAllBytes(asset));
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>Imports the signed package, switches it on (a fresh import lands off; a takeover keeps the built-in's on/off unless
    /// <paramref name="enable"/> says otherwise) and loads it.</summary>
    public static async Task<Models.Plugin> InstallAsync(IWebHostEnvironment env, AppDbContext db, PluginRunner runner, PluginRegistry registry, bool? enable = true)
    {
        var (plugin, _, error) = await PluginPackager.ImportAsync(Bundle(), env, db);
        Assert.True(error is null, error);
        if (enable is bool on && plugin!.Enabled != on)
        {
            plugin.Enabled = on;
            await db.SaveChangesAsync();
        }
        await runner.ReloadAsync(plugin!.Id);
        if (plugin.Enabled) Assert.True(registry.IsLoaded("blog"), "blog did not load: " + string.Join(" | ", registry.Errors.Values));
        return plugin;
    }

    /// <summary>The same in a test harness.</summary>
    public static Task<Models.Plugin> InstallAsync(Harness h, bool? enable = true) => InstallAsync(h.Env, h.Db, h.Runner, h.Registry, enable);

    private static readonly SemaphoreSlim Once = new(1, 1);

    /// <summary>The same in a whole application (once per application: a second call finds it installed).</summary>
    public static async Task InstallAsync(IServiceProvider app)
    {
        await Once.WaitAsync();
        try
        {
            using var scope = app.CreateScope();
            var sp = scope.ServiceProvider;
            if (sp.GetRequiredService<PluginRegistry>().IsLoaded("blog")) return;
            await InstallAsync(sp.GetRequiredService<IWebHostEnvironment>(), sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<PluginRunner>(), sp.GetRequiredService<PluginRegistry>());
        }
        finally { Once.Release(); }
    }
}
