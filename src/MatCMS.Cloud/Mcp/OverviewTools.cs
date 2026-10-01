using System.ComponentModel;
using MatCMS.Cloud.Api;
using MatCMS.Cloud.Services;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>The dashboard's lists for an AI client — same services and shapes as /api/v1/attention and /api/v1/syncs.</summary>
[McpServerToolType]
public class OverviewTools
{
    [McpServerTool(Name = "get_attention"), Description("What needs attention across the sites this key may see, errors first: offline or stopped sites, sync and proxy errors, outdated protocols, stale backups, available updates, failed moves — and with an all-instances key also nodes, the cloud's own update, SMTP and Docker. Each line has level (err|warn|info), kind, title, text and the instance id if it is about one site.")]
    public static async Task<object> GetAttention(McpContext me, AttentionService attention, CancellationToken ct) =>
        OverviewApi.AttentionDto(await attention.BuildAsync(OverviewApi.Allowed(me.Key), fleet: me.Key.AllInstances, ct));

    [McpServerTool(Name = "list_sync_runs"), Description("How the sites applied their profiles, newest first: time, instance, revision, outcome (error | failed | changes | nochange) and the counts of installed, updated and failed items.")]
    public static async Task<object> ListSyncRuns(McpContext me, SyncHistoryService history,
        [Description("How many runs (default 50, at most 1000).")] int? take = null, CancellationToken ct = default) =>
        OverviewApi.SyncsDto(await history.ListAsync(OverviewApi.Allowed(me.Key), take ?? 50, ct));
}
