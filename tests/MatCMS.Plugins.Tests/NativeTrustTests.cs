using Xunit;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using MatCMS.Shared;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The MatCMS Store's code signature (docs/builtin-extraction.md, decision 2026-11-11): a signed native plugin runs without
/// "Native Plugins erlauben" and may take a built-in key — and only the files it was signed for run that way.
/// </summary>
public class NativeTrustTests
{
    /// <summary>One test key for the whole run, trusted IN ADDITION to the real store key (never instead of it).</summary>
    private static readonly ECDsa Key = MakeKey(out KeyId);
    private static readonly string KeyId;

    private static ECDsa MakeKey(out string id)
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ec.ExportSubjectPublicKeyInfo();
        id = StoreSignature.KeyId(spki);
        var extra = new Dictionary<string, string>(NativeTrust.ExtraKeys ?? new Dictionary<string, string>()) { [id] = Convert.ToBase64String(spki) };
        NativeTrust.ExtraKeys = extra;
        return ec;
    }

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "refplugin");

    private static string Sign(string key, string version, string srcDir) =>
        StoreSignature.SignCode(key, version, StoreSignature.TreeHash(srcDir), Key, KeyId);

    [Fact]
    public async Task A_signed_native_plugin_runs_without_the_switch()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-signed", allowNative: false);
        p.CodeSignature = Sign("ref-signed", "1", StoragePaths.PluginSrc(h.Env, "ref-signed", "1"));
        h.Db.SaveChanges();

        await h.Runner.RunAllAsync();

        Assert.True(h.Native.IsLoaded("ref-signed"));
        Assert.True(NativeTrust.IsSigned(h.Env, p));
    }

    [Fact]
    public async Task An_unsigned_native_plugin_still_needs_the_switch()
    {
        using var h = new Harness();
        h.AddNative("ref-plain", allowNative: false);
        await h.Runner.RunAllAsync();
        Assert.False(h.Native.IsLoaded("ref-plain"));
    }

    [Fact]
    public async Task A_file_added_after_signing_breaks_the_signature()
    {
        using var h = new Harness();
        var p = h.AddNative("ref-tampered", allowNative: false);
        var src = StoragePaths.PluginSrc(h.Env, "ref-tampered", "1");
        p.CodeSignature = Sign("ref-tampered", "1", src);
        h.Db.SaveChanges();
        // A side DLL slipped in next to the signed ones: the loader would resolve it from this folder.
        File.WriteAllBytes(Path.Combine(src, "Side.dll"), new byte[] { 1, 2, 3 });

        await h.Runner.RunAllAsync();

        Assert.False(h.Native.IsLoaded("ref-tampered"));
        Assert.False(NativeTrust.IsSigned(h.Env, p));
    }

    [Fact]
    public void A_signature_is_bound_to_key_and_version()
    {
        using var h = new Harness();
        var src = Path.Combine(h.Root, "src");
        Directory.CreateDirectory(src);
        foreach (var f in Directory.GetFiles(Fixture)) File.Copy(f, Path.Combine(src, Path.GetFileName(f)));
        var token = Sign("shop", "2.0", src);

        Assert.True(NativeTrust.IsSigned("shop", "2.0", src, token));
        Assert.False(NativeTrust.IsSigned("shop", "2.1", src, token));
        Assert.False(NativeTrust.IsSigned("bot", "2.0", src, token));
        Assert.False(NativeTrust.IsSigned("shop", "2.0", src, "nonsense"));
        Assert.False(NativeTrust.IsSigned("shop", "2.0", src, ""));
    }

    /// <summary>A native bundle of the reference plugin under <paramref name="key"/>, signed or not.</summary>
    private static MemoryStream Bundle(string key, string version, bool sign)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = Directory.GetFiles(Fixture).Where(f => !f.EndsWith(".pdb") && !f.EndsWith(".runtimeconfig.json")).ToList();
            var tree = StoreSignature.TreeHash(files.Select(f => (Path.GetFileName(f), StoreSignature.Sha256Hex(File.ReadAllBytes(f)))));
            var manifest = new Dictionary<string, object>
            {
                ["Format"] = 2, ["Name"] = key, ["Key"] = key, ["Version"] = version, ["Description"] = "", ["Code"] = PluginBundle.NativeGuardCode,
                ["Kind"] = PluginBundle.KindNative, ["Api"] = typeof(IPlugin).Assembly.GetName().Version!.Major.ToString(), ["Entry"] = "RefPlugin.dll",
                ["Sha256"] = NativePluginHost.HashOf(Path.Combine(Fixture, "RefPlugin.dll")),
                ["CodeSignature"] = sign ? StoreSignature.SignCode(key, version, tree, Key, KeyId) : ""
            };
            using (var w = new StreamWriter(zip.CreateEntry(PluginBundle.ManifestEntry).Open())) w.Write(JsonSerializer.Serialize(manifest));
            foreach (var f in files)
                using (var s = zip.CreateEntry(PluginBundle.SourceFolder + Path.GetFileName(f)).Open()) s.Write(File.ReadAllBytes(f));
        }
        ms.Position = 0;
        return ms;
    }

    private static Models.Plugin BuiltInRow(Harness h, string key, bool on)
    {
        var row = new Models.Plugin { Key = key, Name = key, Version = "1.0", Kind = PluginKind.BuiltIn, Enabled = on, ConfigJson = "{\"currency\":\"EUR\"}" };
        h.Db.Plugins.Add(row);
        h.Db.SaveChanges();
        return row;
    }

    [Fact]
    public async Task The_signed_package_takes_the_place_of_a_built_in_and_keeps_its_state()
    {
        using var h = new Harness();
        BuiltInRow(h, "shop", on: true);

        var (plugin, updated, error) = await PluginPackager.ImportAsync(Bundle("shop", "2.0", sign: true), h.Env, h.Db);

        Assert.Null(error);
        Assert.True(updated);
        Assert.Equal(PluginKind.Native, plugin!.Kind);
        Assert.Equal("2.0", plugin.Version);
        Assert.True(plugin.Enabled);                                  // on stays on: nothing changes for the site
        Assert.Equal("{\"currency\":\"EUR\"}", plugin.ConfigJson);    // settings stay
        Assert.True(NativeTrust.IsSigned(h.Env, plugin));

        // ... and the next start does not call it a conflict nor put the built-in back.
        var conflicts = await BuiltInPlugins.EnsureRowsAsync(h.Db);
        Assert.DoesNotContain("shop", conflicts);
        Assert.Equal(PluginKind.Native, h.Db.Plugins.Single(p => p.Key == "shop").Kind);
    }

    [Fact]
    public async Task Nobody_else_may_take_a_built_in_key()
    {
        using var h = new Harness();
        BuiltInRow(h, "bot", on: false);

        var unsigned = await PluginPackager.ImportAsync(Bundle("bot", "2.0", sign: false), h.Env, h.Db);
        Assert.NotNull(unsigned.error);

        // A signature made for another key does not count either.
        var ms = Bundle("bot", "2.0", sign: false);
        var forged = await PluginPackager.ImportAsync(RewriteSignature(ms, Sign("ref", "2.0", Fixture)), h.Env, h.Db);
        Assert.NotNull(forged.error);

        Assert.Equal(PluginKind.BuiltIn, h.Db.Plugins.Single(p => p.Key == "bot").Kind);
    }

    [Fact]
    public async Task An_ordinary_signed_update_still_waits_for_the_operator()
    {
        using var h = new Harness();
        var (first, _, e1) = await PluginPackager.ImportAsync(Bundle("ref-store", "1.0", sign: true), h.Env, h.Db);
        Assert.Null(e1);
        Assert.False(first!.Enabled);
        Assert.False(string.IsNullOrEmpty(first.CodeSignature));
    }

    private static MemoryStream RewriteSignature(MemoryStream bundle, string token)
    {
        using (var zip = new ZipArchive(bundle, ZipArchiveMode.Update, leaveOpen: true))
        {
            var e = zip.GetEntry(PluginBundle.ManifestEntry)!;
            Dictionary<string, object?> m;
            using (var r = new StreamReader(e.Open())) m = JsonSerializer.Deserialize<Dictionary<string, object?>>(r.ReadToEnd())!;
            m["CodeSignature"] = token;
            e.Delete();
            using var w = new StreamWriter(zip.CreateEntry(PluginBundle.ManifestEntry).Open());
            w.Write(JsonSerializer.Serialize(m));
        }
        bundle.Position = 0;
        return bundle;
    }
}
