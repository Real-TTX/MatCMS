using System.ComponentModel;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// Every operation a connected site's plugins describe — the shop's products, the blog's posts, the club's events, bookings, the forum … —
/// through two generic tools instead of one tool per operation (the hand-written shop tools in <see cref="ContentTools"/> stay for the clients
/// that use them). Same content-op channel, same rules: <see cref="PluginOperationService"/>. REST twin: <c>Api/PluginOperationsApi.cs</c>.
/// </summary>
[McpServerToolType]
public class PluginOperationTools
{
    private static async Task<Instance> ResolveAsync(McpContext me, InstanceService instances, string instanceId, CancellationToken ct)
    {
        var instance = await instances.FindForKeyAsync(instanceId, me.Key, track: false, ct: ct) ?? throw new McpException("Instanz nicht gefunden.");
        if (instance.Status != InstanceStatus.Approved) throw new McpException("Instanz ist nicht freigegeben.");
        return instance;
    }

    [McpServerTool(Name = "list_plugin_operations", ReadOnly = true), Description(
        "List the operations the plugins of a connected MatCMS site offer (as the site last reported them): qualified name (plugin.operation, e.g. shop.product.create, " +
        "blog.post.list, verein.event.create), the plugin, a description, example arguments, whether it writes and whether it touches personal data, and whether THIS key may run it. " +
        "Answers at once (no queue). Run one with run_plugin_operation.")]
    public static async Task<object> ListPluginOperations(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveAsync(me, instances, instanceId, ct);
        return PluginOperationService.ListDto(instance, me.Key);
    }

    [McpServerTool(Name = "run_plugin_operation"), Description(
        "Run ONE operation of a connected site's plugin by its qualified name from list_plugin_operations, with its arguments as a JSON object. " +
        "QUEUED: returns an opId; the site applies it through the plugin on its next heartbeat (~a minute) — poll get_content_op(opId): outcome applied / skipped-exists / failed, " +
        "and for a read the answer in `result`. Writing operations and everything with personal data (orders, bookings, members) need a key with the restore right. " +
        "Read first (…list / …read), then write; ids and slugs always come from a result.")]
    public static async Task<object> RunPluginOperation(
        McpContext me, InstanceService instances, PluginOperationService ops,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The qualified operation name, e.g. \"shop.product.create\".")] string name,
        [Description("The arguments as a JSON object, shaped like the operation's example arguments, e.g. {\"title\":\"Grüner Tee\",\"price_cents\":790}. Empty = {}.")] string? argsJson = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveAsync(me, instances, instanceId, ct);
        var r = await ops.RunAsync(instance, me.Key, name, argsJson, ct, by: me.Actor);
        if (!r.Ok) throw new McpException(r.Error!);
        return new { ok = true, opId = r.Op!.Id, queued = true, name = r.Info!.Name, writes = r.Info.Writes, message = "Eingereiht — Ergebnis via get_content_op(opId)." };
    }
}
