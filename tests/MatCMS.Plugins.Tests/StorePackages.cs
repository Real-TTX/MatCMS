using System.Security.Cryptography;
using MatCMS.Data;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using MatCMS.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The test run's own store key: trusted IN ADDITION to the real MatCMS Store key (never instead of it), so a test can sign native code the way
/// <c>tools/StoreSign sign-code</c> does. ONE key for the whole run — two classes each adding their own would race on <see cref="NativeTrust.ExtraKeys"/>.
/// </summary>
internal static class TestStoreKey
{
    public static readonly ECDsa Key;
    public static readonly string KeyId;

    static TestStoreKey()
    {
        Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Key.ExportSubjectPublicKeyInfo();
        KeyId = StoreSignature.KeyId(spki);
        NativeTrust.ExtraKeys = new Dictionary<string, string>(NativeTrust.ExtraKeys ?? new Dictionary<string, string>()) { [KeyId] = Convert.ToBase64String(spki) };
    }

    /// <summary>The code signature of the files in <paramref name="srcDir"/> for this key and version.</summary>
    public static string Sign(string key, string version, string srcDir) =>
        StoreSignature.SignCode(key, version, StoreSignature.TreeHash(srcDir), Key, KeyId);
}

/// <summary>
/// The plugins that left the image (the shop and the AI bot, docs/builtin-extraction.md) as a site has them from the MatCMS Store: their BUILT DLL
/// in the plugin folder, signed (so they run without "Native Plugins erlauben", like the store's package), the bot's assets beside it.
/// </summary>
internal static class StorePackages
{
    /// <summary>The version the tests install under (any version works: the signature is made for it).</summary>
    public const string Version = "2.0";

    private static string Fixture(string path) => Path.Combine(AppContext.BaseDirectory, "fixtures", path);

    /// <summary>
    /// Puts the shop and the bot into this application, both OFF — what a fresh site with the two packages has, and what the built-in rows were before.
    /// A row that is already there (a test that restored a backup, say) keeps its on/off and settings.
    /// </summary>
    public static async Task InstallAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        await InstallAsync(db, env, "shop", "Shop", "Shop.dll");
        await InstallAsync(db, env, "bot", "KI-Bot", "Bot.dll");
    }

    /// <summary>Installs one of them into a database and plugin folder (also used with <see cref="Harness"/>).</summary>
    public static async Task<Models.Plugin> InstallAsync(AppDbContext db, IWebHostEnvironment env, string key, string name, string dll, bool enabled = false)
    {
        var src = StoragePaths.PluginSrc(env, key, Version);
        Directory.CreateDirectory(src);
        File.Copy(Fixture(Path.Combine(key, dll)), Path.Combine(src, dll), overwrite: true);
        var assets = Fixture(Path.Combine(key, "assets"));
        if (Directory.Exists(assets))
        {
            var dest = StoragePaths.PluginAssetDir(env, key);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(assets)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        }
        var row = await db.Plugins.FirstOrDefaultAsync(p => p.Key == key);
        if (row is null)
        {
            row = new Models.Plugin { Key = key, Enabled = enabled, ConfigJson = "{}" };
            db.Plugins.Add(row);
        }
        row.Name = name; row.Version = Version; row.Kind = Models.PluginKind.Native; row.Entry = dll; row.Code = ""; row.FilesJson = "{}";
        row.Sha256 = NativePluginHost.HashOf(Path.Combine(src, dll));
        row.Api = typeof(IPlugin).Assembly.GetName().Version!.Major;
        row.CodeSignature = TestStoreKey.Sign(key, Version, src);
        await db.SaveChangesAsync();
        return row;
    }
}
