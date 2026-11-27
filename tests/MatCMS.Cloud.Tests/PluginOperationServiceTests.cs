using System.Text.Json;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Xunit;

namespace MatCMS.Cloud.Tests;

/// <summary>The generic plugin operations: only what the site listed, writes and personal data only with the restore right, never a core
/// content op kind, an older site that never reported a catalogue is told so. The refusals happen before anything is queued.</summary>
public class PluginOperationServiceTests
{
    private static Instance Site(params PluginOperationInfo[] ops) => new()
    {
        PublicId = "abc", PluginOperationsJson = JsonSerializer.Serialize(ops.ToList()), PluginOperationsAt = DateTime.UtcNow,
    };

    private static readonly PluginOperationInfo List = new() { Name = "shop.product.list", Plugin = "shop", PluginName = "Shop", Writes = false };
    private static readonly PluginOperationInfo Create = new() { Name = "shop.product.create", Plugin = "shop", PluginName = "Shop", Writes = true };
    private static readonly PluginOperationInfo Orders = new() { Name = "shop.order.list", Plugin = "shop", PluginName = "Shop", Writes = false, PersonalData = true };
    private static readonly PluginOperationInfo CoreLookalike = new() { Name = "page.read", Plugin = "page", Writes = false };

    // The refusals never reach the queue, so no InstanceService is needed for them.
    private static readonly PluginOperationService Service = new(null!);

    [Fact]
    public void Writes_and_personal_data_need_the_restore_right_reads_do_not()
    {
        var reader = new ApiKey { CanRestore = false };
        var restorer = new ApiKey { CanRestore = true };
        Assert.Null(PluginOperationService.Allowed(List, reader));
        Assert.NotNull(PluginOperationService.Allowed(Create, reader));
        Assert.NotNull(PluginOperationService.Allowed(Orders, reader));              // a READ of orders, still personal data
        Assert.Null(PluginOperationService.Allowed(Create, restorer));
        Assert.Null(PluginOperationService.Allowed(Orders, restorer));

        var dto = JsonSerializer.SerializeToNode(PluginOperationService.ListDto(Site(List, Create, Orders), reader))!;
        Assert.True(dto["reported"]!.GetValue<bool>());
        var allowed = dto["operations"]!.AsArray().ToDictionary(o => o!["name"]!.GetValue<string>(), o => o!["allowed"]!.GetValue<bool>());
        Assert.True(allowed["shop.product.list"]);
        Assert.False(allowed["shop.product.create"]);
        Assert.False(allowed["shop.order.list"]);
    }

    [Fact]
    public async Task Only_listed_operations_run_never_a_core_kind_and_an_unreported_site_says_why()
    {
        var key = new ApiKey { CanRestore = true };
        var site = Site(List, Create, CoreLookalike);
        Assert.Contains("keine Aktion", (await Service.RunAsync(site, key, "shop.product.remove", "{}")).Error);   // internal: never listed
        Assert.Contains("keine Plugin-Aktion", (await Service.RunAsync(site, key, "page.read", "{}")).Error);        // listed, but a core kind
        Assert.Contains("Wiederherstellen-Recht", (await Service.RunAsync(site, new ApiKey(), "shop.product.create", "{}")).Error);
        Assert.Contains("JSON-Objekt", (await Service.RunAsync(site, key, "shop.product.list", "[1,2]")).Error);
        Assert.Contains("gültiges JSON", (await Service.RunAsync(site, key, "shop.product.list", "{nope")).Error);
        Assert.Contains("name fehlt", (await Service.RunAsync(site, key, " ", "{}")).Error);

        var old = new Instance { PublicId = "old" };
        Assert.Contains("noch keine Plugin-Aktionen", (await Service.RunAsync(old, key, "shop.product.list", "{}")).Error);
        Assert.False(JsonSerializer.SerializeToNode(PluginOperationService.ListDto(old, key))!["reported"]!.GetValue<bool>());
    }

    [Fact]
    public void No_core_kind_is_ever_a_plugin_operation_and_only_plain_plugin_reads_are_readable_without_restore()
    {
        foreach (var k in new[] { "page.create", "page.updateBlocks", "pages.list", "setting.set", "site.address", "form.create", "cleanup.apply", "cleanup.analyze" })
            Assert.True(PluginOperationService.IsCoreKind(k), k);
        Assert.False(PluginOperationService.IsCoreKind("shop.product.create"));
        Assert.False(PluginOperationService.IsCoreKind("site-x.thing.list"));

        var site = Site(List, Create, Orders, new() { Name = "page.read", Plugin = "page", Writes = false });
        Assert.True(PluginOperationService.ResultReadableWithoutRestore(site, "shop.product.list"));
        Assert.False(PluginOperationService.ResultReadableWithoutRestore(site, "shop.product.create"));   // a write's answer
        Assert.False(PluginOperationService.ResultReadableWithoutRestore(site, "shop.order.list"));       // personal data
        Assert.False(PluginOperationService.ResultReadableWithoutRestore(site, "order.list"));            // not listed under that name
        Assert.True(PluginOperationService.ResultReadableWithoutRestore(site, "page.read"));              // the core read, by ContentOpRules
        Assert.False(PluginOperationService.ResultReadableWithoutRestore(site, "setting.set"));
    }

    [Fact]
    public void The_catalogue_fingerprint_changes_with_what_an_agent_reads_and_only_then()
    {
        var a = new List<PluginOperationInfo> { List, Create };
        var b = new List<PluginOperationInfo> { List, new() { Name = Create.Name, Plugin = "shop", Writes = true, Description = "neu" } };
        Assert.Equal(PluginOperationInfo.Hash(a), PluginOperationInfo.Hash(new List<PluginOperationInfo> { List, Create }));
        Assert.NotEqual(PluginOperationInfo.Hash(a), PluginOperationInfo.Hash(b));
    }
}
