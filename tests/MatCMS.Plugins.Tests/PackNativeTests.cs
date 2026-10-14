using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using MatCMS.Models;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Skips a test, with a reason, when there is no PowerShell to run <c>plugins/pack-native.ps1</c> — or no
/// script, as in a checkout that does not carry <c>plugins/</c>. A plain xunit 2 skip: decided at discovery.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactIfPackNativeAttribute : FactAttribute
{
    public FactIfPackNativeAttribute()
    {
        if (PackNative.Shell() is null) Skip = "Weder 'powershell' noch 'pwsh' ist im PATH — pack-native.ps1 kann nicht ausgeführt werden.";
        else if (PackNative.Script() is null) Skip = "plugins/pack-native.ps1 wurde über dem Testordner nicht gefunden.";
        else if (!Directory.Exists(PackNative.FixtureDir)) Skip = "Das gebaute RefPlugin-Fixture fehlt (fixtures/refplugin).";
    }
}

/// <summary>Runs the real <c>pack-native.ps1</c> on the built reference plugin (<c>tests/Fixtures/RefPlugin</c>, copied
/// next to the test assembly by the test project's build) and gives the bundle to the host's real
/// <see cref="PluginPackager.ImportAsync"/>. The template and the publish step are NOT rebuilt here — too slow and too
/// fragile for a unit test; they are covered by running them by hand (see plugins/README.md).</summary>
internal static class PackNative
{
    public static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "refplugin");

    /// <summary>The PowerShell to use: Windows PowerShell where it exists (that is what the script is written for and
    /// what a Windows developer has), otherwise pwsh.</summary>
    public static string? Shell()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "powershell.exe", "pwsh.exe" } : new[] { "pwsh", "powershell" };
        foreach (var name in names)
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try { if (File.Exists(Path.Combine(dir.Trim('"'), name))) return name; } catch { /* a malformed PATH entry */ }
            }
        return null;
    }

    public static string? Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "plugins", "pack-native.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A fresh copy of the built fixture, so a test may add to it or take from it.</summary>
    public static string CopyFixture(string into)
    {
        var dir = Path.Combine(into, "publish");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(FixtureDir)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    public static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Shell()!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script()! }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { /* gone already */ } throw new TimeoutException("pack-native.ps1 hat nicht geendet."); }
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    public static string[] Meta(string key = "packed-ref", string version = "1.0.0", string description = "Käse — für Prüfung") =>
        new[] { "-Key", key, "-Name", "Gepacktes Referenz-Plugin", "-Version", version, "-Description", description };
}

public class PackNativeTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "matcms-packnative-" + Guid.NewGuid().ToString("N"));

    public PackNativeTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ } }

    private static Dictionary<string, JsonElement> ReadManifest(ZipArchive zip)
    {
        using var r = new StreamReader(zip.GetEntry("plugin.json")!.Open());
        using var doc = JsonDocument.Parse(r.ReadToEnd());
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [FactIfPackNative]
    public async Task A_bundle_from_pack_native_is_imported_by_the_host_and_runs()
    {
        var folder = PackNative.CopyFixture(_tmp);
        var zipPath = Path.Combine(_tmp, "out", "packed-ref.zip");
        var (exit, stdout, stderr) = PackNative.Run(new[] { "-Path", folder, "-Out", zipPath }.Concat(PackNative.Meta()).ToArray());
        Assert.True(exit == 0, $"pack-native.ps1 exit {exit}\n{stdout}\n{stderr}");

        // The container shape, as PluginPackager.Export writes it for a native plugin.
        var dllPath = Path.Combine(folder, "RefPlugin.dll");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var manifest = ReadManifest(zip);
            Assert.Equal(2, manifest["Format"].GetInt32());
            // Every new field is a STRING (the cloud rewrites plugin.json field by field and a number would not survive).
            foreach (var field in new[] { "Kind", "Api", "Entry", "Sha256", "Version", "Key" })
                Assert.Equal(JsonValueKind.String, manifest[field].ValueKind);
            Assert.Equal("native", manifest["Kind"].GetString());
            Assert.Equal(typeof(IPlugin).Assembly.GetName().Version!.Major.ToString(), manifest["Api"].GetString());
            Assert.Equal("RefPlugin.dll", manifest["Entry"].GetString());
            Assert.Equal(NativePluginHost.HashOf(dllPath), manifest["Sha256"].GetString());
            Assert.Equal(MatCMS.Shared.PluginBundle.NativeGuardCode, manifest["Code"].GetString());
            Assert.Equal("Käse — für Prüfung", manifest["Description"].GetString());

            var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "plugin.json", "src/RefPlugin.deps.json", "src/RefPlugin.dll" }, names);   // no .pdb, no runtimeconfig
        }

        // The host's own importer: staged, verified (checksum, paths, no private API copy), stored — and OFF.
        using var h = new Harness();
        h.AllowNative(true);
        MatCMS.Models.Plugin? plugin;
        string? error;
        await using (var ms = File.OpenRead(zipPath))
            (plugin, _, error) = await PluginPackager.ImportAsync(ms, h.Env, h.Db);
        Assert.Null(error);
        Assert.Equal("packed-ref", plugin!.Key);
        Assert.Equal(PluginKind.Native, plugin.Kind);
        Assert.False(plugin.Enabled);
        Assert.Equal(NativePluginHost.HashOf(dllPath), plugin.Sha256);
        Assert.Equal("Käse — für Prüfung", plugin.Description);
        Assert.True(File.Exists(Path.Combine(StoragePaths.PluginSrc(h.Env, "packed-ref", "1.0.0"), "RefPlugin.dll")));

        // And it loads and registers what the reference plugin registers.
        plugin.Enabled = true;
        h.Db.SaveChanges();
        await h.Runner.ReloadAsync(plugin.Id);
        Assert.Empty(h.Registry.Errors);
        Assert.NotEmpty(h.Registry.Routes);
        Assert.NotEmpty(h.Registry.Sections);
        h.Native.Unload("packed-ref", h.Registry);
    }

    [FactIfPackNative]
    public void A_folder_with_its_own_copy_of_the_plugin_api_is_refused()
    {
        var folder = PackNative.CopyFixture(_tmp);
        File.Copy(typeof(IPlugin).Assembly.Location, Path.Combine(folder, "MatCMS.Plugin.Abstractions.dll"));
        var zipPath = Path.Combine(_tmp, "own-api.zip");

        var (exit, _, stderr) = PackNative.Run(new[] { "-Path", folder, "-Out", zipPath }.Concat(PackNative.Meta()).ToArray());

        Assert.NotEqual(0, exit);
        Assert.Contains("eigene Kopie", stderr);
        Assert.Contains("MatCMS.Plugin.Abstractions", stderr);
        Assert.False(File.Exists(zipPath));
    }

    [FactIfPackNative]
    public void A_missing_main_file_is_refused()
    {
        var folder = PackNative.CopyFixture(_tmp);
        File.Delete(Path.Combine(folder, "RefPlugin.dll"));
        var (exit, _, stderr) = PackNative.Run(new[] { "-Path", folder, "-Out", Path.Combine(_tmp, "nomain.zip") }.Concat(PackNative.Meta()).ToArray());
        Assert.NotEqual(0, exit);
        Assert.Contains("Hauptdatei fehlt", stderr);

        // The same when the author names the file and it is not there.
        folder = PackNative.CopyFixture(Path.Combine(_tmp, "second"));
        (exit, _, stderr) = PackNative.Run(new[] { "-Path", folder, "-Entry", "Nope.dll", "-Out", Path.Combine(_tmp, "nomain2.zip") }.Concat(PackNative.Meta()).ToArray());
        Assert.NotEqual(0, exit);
        Assert.Contains("Hauptdatei 'Nope.dll' fehlt", stderr);
    }

    [FactIfPackNative]
    public void An_assembly_that_does_not_reference_the_plugin_api_has_no_api_version_and_is_refused()
    {
        var folder = Path.Combine(_tmp, "foreign");
        Directory.CreateDirectory(folder);
        File.Copy(typeof(System.Text.Json.JsonSerializer).Assembly.Location, Path.Combine(folder, "System.Text.Json.dll"));

        var (exit, _, stderr) = PackNative.Run(new[] { "-Path", folder, "-Out", Path.Combine(_tmp, "foreign.zip") }.Concat(PackNative.Meta()).ToArray());

        Assert.NotEqual(0, exit);
        Assert.Contains("Api-Version ist nicht bestimmbar", stderr);
    }

    [FactIfPackNative]
    public void The_bundle_limits_of_PluginBundle_are_enforced_before_the_zip_is_written()
    {
        var folder = PackNative.CopyFixture(_tmp);
        for (var i = 0; i <= MatCMS.Shared.PluginBundle.MaxSourceFiles; i++) File.WriteAllText(Path.Combine(folder, $"n{i}.txt"), "x");
        var zipPath = Path.Combine(_tmp, "many.zip");

        var (exit, _, stderr) = PackNative.Run(new[] { "-Path", folder, "-Out", zipPath }.Concat(PackNative.Meta()).ToArray());

        Assert.NotEqual(0, exit);
        Assert.Contains("Zu viele Dateien", stderr);
        Assert.False(File.Exists(zipPath));
    }

    [FactIfPackNative]
    public void A_key_the_import_would_rewrite_is_refused()
    {
        var folder = PackNative.CopyFixture(_tmp);
        var (exit, _, stderr) = PackNative.Run(new[] { "-Path", folder, "-Out", Path.Combine(_tmp, "key.zip") }.Concat(PackNative.Meta(key: "Mein Plugin")).ToArray());
        Assert.NotEqual(0, exit);
        Assert.Contains("Key 'Mein Plugin' ist ungueltig", stderr);
    }
}
