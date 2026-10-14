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
