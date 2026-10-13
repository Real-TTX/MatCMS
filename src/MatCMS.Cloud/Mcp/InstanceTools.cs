using System.ComponentModel;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools for a remote AI client (ChatGPT, Claude) connected at <c>/mcp</c>. They mirror the ACTIONS of
/// the operator API (<c>/api/v1</c>) — list instances, inspect one, list/request/restore backups — through
/// the SAME <see cref="InstanceService"/> / <see cref="BackupStore"/> the admin UI uses. There is no second
/// implementation and no new authority: every tool is scoped to the caller's key
/// (<see cref="McpContext"/>), and restore is gated on the key's restore right, exactly like the REST calls.
/// <para>Deliberately NOT tools: the raw backup download/upload of <c>/api/v1</c>. Streaming a multi-megabyte
/// ZIP through a chat model makes no sense; those stay REST, for tooling. This is Stage 1 (read + backup
/// lifecycle); changing site content is Stage 2 (a separate, pull-based channel — see docs).</para>
/// </summary>
[McpServerToolType]
public class InstanceTools
{
    // Same opaque answer for "does not exist" and "outside this key's scope" as ApiInstanceAsync, so a
    // scoped key cannot enumerate instances by watching which id errors differently.
    private static async Task<Instance> ResolveAsync(InstanceService instances, ApiKey key, string instanceId, CancellationToken ct)
    {
        var instance = await instances.FindForKeyAsync(instanceId, key, ct: ct);
        if (instance is null)
            throw new McpException("Instanz nicht gefunden.");
        return instance;
    }

    [McpServerTool(Name = "list_instances"), Description("List the MatCMS instances this key may act on, with status, version and whether each is online. Returns canRestore = whether this connection may restore backups.")]
    public static async Task<object> ListInstances(McpContext me, InstanceService instances, CancellationToken ct)
    {
        var key = me.Key;
        var list = await instances.ListForKeyAsync(key, ct);
        return new
        {
            canRestore = key.CanRestore,
            instances = list.Select(i => new
            {
                id = i.PublicId,
                name = i.Name,
                status = i.Status.ToString(),
                hosting = i.Hosting.ToString(),
                version = i.Version,
                url = i.Url,
                online = InstanceService.IsOnline(i),
                lastHeartbeatUtc = i.LastHeartbeatUtc,
            }),
        };
    }

    [McpServerTool(Name = "get_instance"), Description("Get details of one connected MatCMS instance by its id (from list_instances): status, hosting, version, URL, online state and last heartbeat.")]
    public static async Task<object> GetInstance(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var i = await ResolveAsync(instances, me.Key, instanceId, ct);
        return new
        {
            id = i.PublicId,
            name = i.Name,
            status = i.Status.ToString(),
            hosting = i.Hosting.ToString(),
            version = i.Version,
            url = i.Url,
            online = InstanceService.IsOnline(i),
            lastHeartbeatUtc = i.LastHeartbeatUtc,
        };
    }

    [McpServerTool(Name = "list_backups"), Description("List an instance's cloud backups, newest first, with size, origin and restore state (restorePending / restoreDoneAt / restoreError).")]
    public static async Task<object> ListBackups(
        McpContext me, InstanceService instances, BackupStore store,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveAsync(instances, me.Key, instanceId, ct);
        var rows = (await store.ListForInstanceAsync(instance.Id, ct)).Select(b => new
        {
            id = b.Id,
            fileName = b.FileName,
            sizeBytes = b.SizeBytes,
            createdAt = b.CreatedAt,
            uploadedAt = b.UploadedAt,
            origin = b.Origin,
            restorePending = b.RestorePending,
            restoreDoneAt = b.RestoreDoneAt,
            restoreError = b.RestoreError,
        });
        return new { backups = rows };
    }

    [McpServerTool(Name = "request_backup"), Description("Ask an instance to produce a fresh backup. The instance builds it on its next heartbeat and uploads it; poll list_backups for the file. Returns the requestId that ties the arriving file to this request.")]
    public static async Task<object> RequestBackup(
        McpContext me, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveAsync(instances, me.Key, instanceId, ct);
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");

        await instances.RequestBackupAsync(instance, "Backup über MCP angefordert.", ct);
        return new { ok = true, requestId = instance.BackupRequestId };
    }

    [McpServerTool(Name = "restore_backup"), Description("Restore a backup LIVE onto its instance — the one destructive action. Requires a key with the restore right (canRestore from list_instances). The cloud only MARKS it; the instance downloads and applies it on its next heartbeat and reports the outcome, visible via list_backups.")]
    public static async Task<object> RestoreBackup(
        McpContext me, InstanceService instances, BackupStore store,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The backup id to restore, as returned by list_backups.")] int backupId,
        CancellationToken ct)
    {
        var key = me.Key;
        if (!key.CanRestore)
            throw new McpException("Dieser Schlüssel darf nicht wiederherstellen.");

        var instance = await ResolveAsync(instances, key, instanceId, ct);
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");

        var row = await store.FindAsync(instance.Id, backupId, ct: ct);
        if (row is null) throw new McpException("Backup nicht gefunden.");

        await store.RequestRestoreAsync(row, ct);
        return new { ok = true, id = row.Id, message = "Wiederherstellung vorgemerkt — die Instanz spielt sie beim nächsten Kontakt ein." };
    }
}
