using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MatCMS.Models;
using MatCMS.Services;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>Backup and restore of native plugins, and the permission that must never travel with a backup.</summary>
public class PluginBackupTests
{
    private static ContentTransferService Transfer(Harness h) => new(h.Db, h.Env, h.Runner, h.Data);

    private static List<string> Names(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    [Fact]
    public async Task A_backup_carries_a_native_plugin_with_its_files_and_a_restore_brings_it_back()
    {
        using var source = new Harness();
        var p = source.AddNative("ref-native");
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        Assert.Contains("plugins-native/ref-native/1/RefPlugin.dll", Names(backup));

        using var target = new Harness();
        target.AllowNative(true);
        var message = await Transfer(target).ImportAsync(backup);
        Assert.Contains("Plugins", message);

        var row = target.Db.Plugins.Single(x => x.Key == "ref-native");
        Assert.Equal(PluginKind.Native, row.Kind);
        Assert.Equal("RefPlugin.dll", row.Entry);
        Assert.Equal(p.Sha256, row.Sha256);
        Assert.Equal(p.Api, row.Api);
        Assert.True(File.Exists(Path.Combine(StoragePaths.PluginSrc(target.Env, "ref-native", "1"), "RefPlugin.dll")));

        // A restore restores the site as it was: the plugin was ON, and it is running — the files were put down
        // BEFORE the plugins were started, so it did not sit in a "file missing" error.
        Assert.Empty(target.Registry.Errors);
        Assert.True(target.Native.IsLoaded("ref-native"));
        Assert.NotEmpty(target.Registry.Routes);
    }

    [Fact]
    public async Task A_restore_never_takes_the_permission_for_native_plugins_from_the_backup()
    {
        // The source site allows native plugins; its backup says so in its settings.
        using var source = new Harness();
        source.AddNative("ref-native");
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());
        using (var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read))
        using (var r = new StreamReader(zip.GetEntry("content.json")!.Open()))
            Assert.Contains("plugins.allowNative", await r.ReadToEndAsync());   // the premise: it IS in the file

        // The target never allowed it. After the restore it still does not — and the plugin does not run.
        using var target = new Harness();
        await Transfer(target).ImportAsync(backup);

        var setting = target.Db.SiteSettings.FirstOrDefault(s => s.Key == SettingKeys.AllowNativePlugins);
        Assert.True(setting is null || setting.Value != "1", "the backup switched on native plugins on the target");
        Assert.False(target.Native.IsLoaded("ref-native"));
        Assert.Empty(target.Registry.Routes);
        Assert.Contains("nicht erlaubt", target.Registry.Errors.Values.Single());
    }

    [Fact]
    public async Task A_restore_keeps_the_permission_the_target_had()
    {
        using var source = new Harness();
        source.AddNative("ref-native");
        source.AllowNative(false);                                   // the source did NOT allow it
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        using var target = new Harness();
        target.AllowNative(true);                                    // the target's operator did
        await Transfer(target).ImportAsync(backup);

        Assert.Equal("1", target.Db.SiteSettings.Single(s => s.Key == SettingKeys.AllowNativePlugins).Value);
        Assert.True(target.Native.IsLoaded("ref-native"));
    }

    [Fact]
    public async Task A_crafted_backup_cannot_park_files_for_a_plugin_it_does_not_declare()
    {
        using var source = new Harness();
        source.AddScript("only-a-script", "AddHeadHtml(\"x\");");
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        // Add entries under plugins-native/ for a plugin the backup's own list does not name as native, and
        // one that tries to leave the folder.
        using var ms = new MemoryStream();
        ms.Write(backup);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var name in new[] { "plugins-native/ghost/1/Ghost.dll", "plugins-native/only-a-script/1/Evil.dll", "plugins-native/only-a-script/1/../../../escape.dll" })
                using (var s = zip.CreateEntry(name).Open()) s.Write(new byte[] { 1, 2, 3 });
        }

        using var target = new Harness();
        await Transfer(target).ImportAsync(ms.ToArray());

        Assert.False(Directory.Exists(Path.Combine(StoragePaths.Plugins(target.Env), "ghost")));
        Assert.False(Directory.Exists(Path.Combine(StoragePaths.Plugins(target.Env), "only-a-script")));
        Assert.False(File.Exists(Path.Combine(target.Root, "escape.dll")));
        Assert.Equal(PluginKind.Script, target.Db.Plugins.Single(x => x.Key == "only-a-script").Kind);
    }

    [Fact]
    public async Task A_backup_from_before_native_plugins_restores_as_scripts()
    {
        // Rows written before the Kind column exist carry no kind; a backup of them says nothing about it either.
        using var source = new Harness();
        source.AddScript("old", "AddHeadHtml(\"x\");");
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        using var stripped = new MemoryStream();
        using (var inZip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read))
        using (var outZip = new ZipArchive(stripped, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in inZip.Entries)
            {
                await using var from = e.Open();
                await using var to = outZip.CreateEntry(e.FullName).Open();
                if (e.FullName == "content.json")
                {
                    var json = await new StreamReader(from).ReadToEndAsync();
                    var cleaned = System.Text.RegularExpressions.Regex.Replace(json, "\"(Kind|Entry|Sha256|Api)\":[^,\\n]*,?\\s*", "");
                    await to.WriteAsync(Encoding.UTF8.GetBytes(cleaned));
                }
                else await from.CopyToAsync(to);
            }

        using var target = new Harness();
        await Transfer(target).ImportAsync(stripped.ToArray());
        Assert.Equal(PluginKind.Script, target.Db.Plugins.Single(x => x.Key == "old").Kind);
        Assert.Empty(target.Registry.Errors);
    }

    private const string ShopCode = "Plugin.Data.Table(\"items\", t => t.Text(\"name\").Int(\"stock\"));";

    [Fact]
    public async Task A_backup_carries_a_plugins_tables_and_a_restore_puts_them_back_before_the_plugin_starts()
    {
        using var source = new Harness();
        source.AddScript("shopx", ShopCode);
        await source.Runner.RunAllAsync();
        await source.Context("shopx", new RequestData()).Data.Table("items").InsertAsync(new Dictionary<string, object?> { ["name"] = "Tasse", ["stock"] = 4 });
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        using var target = new Harness();
        await Transfer(target).ImportAsync(backup);
        Assert.Empty(target.Registry.Errors);
        Assert.True(target.Registry.IsLoaded("shopx"));
        var rows = await target.Context("shopx", new RequestData()).Data.Table("items").QueryAsync(DataQuery.Everything);
        Assert.Equal("Tasse", Assert.Single(rows).Text("name"));
        Assert.Equal(4, rows[0].Int("stock"));
    }

    [Fact]
    public async Task A_backup_that_names_a_core_table_among_a_plugins_data_cannot_write_to_it()
    {
        using var source = new Harness();
        source.AddScript("shopx", ShopCode);
        await source.Runner.RunAllAsync();
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());

        // Rewrite the plugin's data in the backup so it claims the core table of settings.
        using var forged = new MemoryStream();
        using (var inZip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read))
        using (var outZip = new ZipArchive(forged, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var e in inZip.Entries)
            {
                await using var from = e.Open();
                await using var to = outZip.CreateEntry(e.FullName).Open();
                if (e.FullName == "content.json")
                {
                    var doc = System.Text.Json.Nodes.JsonNode.Parse(await new StreamReader(from).ReadToEndAsync())!;
                    var evil = "{\"settings\":{\"adopted\":true,\"schema\":{\"Name\":\"settings\",\"Adopts\":\"SiteSettings\",\"KeyColumn\":\"Id\",\"Columns\":[{\"Name\":\"Value\",\"Type\":\"Text\"}],\"Indexes\":[]},\"rows\":[{\"Id\":1,\"Value\":\"pwned\"}]}}";
                    doc["Plugins"]![0]!["DataJson"] = evil;
                    await to.WriteAsync(Encoding.UTF8.GetBytes(doc.ToJsonString()));
                }
                else await from.CopyToAsync(to);
            }

        using var target = new Harness();
        await Transfer(target).ImportAsync(forged.ToArray());
        Assert.DoesNotContain(target.Db.SiteSettings.AsEnumerable(), s => s.Value == "pwned");
    }

    [Fact]
    public void The_settings_no_outside_source_may_write_name_the_cloud_link_and_the_native_permission()
    {
        Assert.Contains(SettingKeys.AllowNativePlugins, SettingKeys.Protected);
        Assert.Contains(SettingKeys.CloudToken, SettingKeys.Protected);
        foreach (var key in SettingKeys.Cloud) Assert.Contains(key, SettingKeys.Protected);
    }
}

/// <summary>A plugin's private files (<c>appdata/plugin-files/{key}</c>) in backups, restores and deletion.</summary>
public class PluginPrivateFilesBackupTests
{
    private static ContentTransferService Transfer(Harness h) => new(h.Db, h.Env, h.Runner, h.Data);

    private static async Task<byte[]> ZipWith(byte[] original, params (string Name, string Content)[] extra)
    {
        using var inZip = new ZipArchive(new MemoryStream(original), ZipArchiveMode.Read);
        var ms = new MemoryStream();
        using (var outZip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in inZip.Entries)
            {
                var copy = outZip.CreateEntry(e.FullName);
                await using var s = copy.Open(); await using var r = e.Open(); await r.CopyToAsync(s);
            }
            foreach (var (name, content) in extra)
            {
                var e = outZip.CreateEntry(name);
                await using var s = e.Open();
                await s.WriteAsync(Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task A_backup_carries_the_private_files_of_a_plugin_and_a_restore_puts_them_back_deep_paths_included()
    {
        using var source = new Harness();
        source.AddScript("shopx", "Plugin.Route(\"/shopx\", ctx => PluginResult.Text(\"x\"));");
        await source.Runner.RunAllAsync();
        var files = source.Context("shopx", new RequestData()).Files;
        await files.SaveAsync("products/12/guide.pdf", new MemoryStream(Encoding.UTF8.GetBytes("PDF-BYTES")));
        await files.SaveAsync("top.txt", new MemoryStream(Encoding.UTF8.GetBytes("T")));

        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());
        using (var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read))
        {
            Assert.NotNull(zip.GetEntry("plugin-files/shopx/products/12/guide.pdf"));
            Assert.NotNull(zip.GetEntry("plugin-files/shopx/top.txt"));
        }
        Assert.Equal(2, Transfer(source).Inspect(backup).PluginAssets);                   // shown in the preview before a restore

        using var target = new Harness();
        var message = await Transfer(target).ImportAsync(backup);
        Assert.Contains("Plugin-Dateien", message);
        var restored = target.Context("shopx", new RequestData()).Files;
        await using var s = (await restored.OpenAsync("products/12/guide.pdf"))!;
        Assert.Equal("PDF-BYTES", await new StreamReader(s).ReadToEndAsync());
    }

    [Fact]
    public async Task The_backup_switch_for_plugin_uploads_also_governs_the_private_files()
    {
        using var source = new Harness();
        await source.Context("shopx", new RequestData()).Files.SaveAsync("a.txt", new MemoryStream(new byte[] { 1 }));
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions { PluginAssets = false });
        using var zip = new ZipArchive(new MemoryStream(backup), ZipArchiveMode.Read);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("plugin-files/"));
    }

    [Theory]
    [InlineData("plugin-files/shopx/../../../escape.txt")]
    [InlineData("plugin-files/../../escape.txt")]
    [InlineData("plugin-files/shopx/.hidden")]
    [InlineData("plugin-files/shopx/a//b.txt")]
    [InlineData("plugin-files/shopx/a/b/c/d/e.txt")]
    [InlineData("plugin-files/shopx/with space.txt")]
    [InlineData("plugin-files//x.txt")]
    [InlineData("plugin-files/shopx")]
    public async Task A_crafted_backup_cannot_write_a_private_file_outside_the_plugins_folder(string entryName)
    {
        using var source = new Harness();
        var backup = await Transfer(source).ExportAsync(new ContentTransferService.BackupOptions());
        var crafted = await ZipWith(backup, (entryName, "evil"));
        using var target = new Harness();
        await Transfer(target).ImportAsync(crafted);

        // Nothing under the plugin-files root but (at most) the folder of a valid key — and nothing outside it.
        var root = StoragePaths.PluginFiles(target.Env);
        var written = Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : Array.Empty<string>();
        Assert.Empty(written);
        Assert.False(File.Exists(Path.Combine(target.Root, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(target.Root)!, "escape.txt")));
    }

    [Fact]
    public async Task Deleting_a_plugin_for_good_removes_its_private_files_but_switching_it_off_does_not()
    {
        using var h = new Harness();
        var row = h.AddScript("shopx", "Plugin.Route(\"/shopx\", ctx => PluginResult.Text(\"x\"));");
        await h.Runner.RunAllAsync();
        var files = h.Context("shopx", new RequestData()).Files;
        await files.SaveAsync("keep.txt", new MemoryStream(new byte[] { 1 }));

        await h.Runner.UnloadAsync("shopx");                                           // off
        await using (var open = await files.OpenAsync("keep.txt")) Assert.NotNull(open);   // (closed again: an open handle would keep a delete from working on Windows)

        await MatCMS.Services.Plugins.PluginUninstall.DeleteAsync(h.Db, h.Runner, h.Data, h.Env, row);
        Assert.False(Directory.Exists(StoragePaths.PluginFilesDir(h.Env, "shopx")));
    }
}
