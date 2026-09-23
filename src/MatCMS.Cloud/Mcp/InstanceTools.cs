using System.ComponentModel;
using MatCMS.Cloud.Data;
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
    private static async Task<Instance> ResolveAsync(AppDbContext db, ApiKey key, string instanceId, CancellationToken ct)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (instance is null || !ApiKeyService.CanAccess(key, instance))
            throw new McpException("Instanz nicht gefunden.");
        return instance;
    }

    [McpServerTool(Name = "list_instances"), Description("List the MatCMS instances this key may act on, with status, version and whether each is online. Returns canRestore = whether this connection may restore backups.")]
    public static async Task<object> ListInstances(McpContext me, AppDbContext db, CancellationToken ct)
    {
        var key = me.Key;
        var q = db.Instances.AsNoTracking().AsQueryable();
        if (!key.AllInstances)
        {
            var ids = key.Instances.Select(s => s.InstanceId).ToList();
            q = q.Where(i => ids.Contains(i.Id));
        }
        var list = await q.OrderBy(i => i.Name).ToListAsync(ct);
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
        McpContext me, AppDbContext db,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var i = await ResolveAsync(db, me.Key, instanceId, ct);
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
        McpContext me, AppDbContext db,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveAsync(db, me.Key, instanceId, ct);
        var rows = await db.CloudBackups.AsNoTracking()
            .Where(b => b.InstanceId == instance.Id)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                id = b.Id,
                fileName = b.FileName,
                sizeBytes = b.SizeBytes,
                createdAt = b.CreatedAt,
                uploadedAt = b.UploadedAt,
                origin = b.Origin,
                restorePending = b.RestoreRequestedAt != null && b.RestoreDoneAt == null && b.RestoreError == null,
                restoreDoneAt = b.RestoreDoneAt,
                restoreError = b.RestoreError,
            })
            .ToListAsync(ct);
        return new { backups = rows };
    }

    [McpServerTool(Name = "request_backup"), Description("Ask an instance to produce a fresh backup. The instance builds it on its next heartbeat and uploads it; poll list_backups for the file. Returns the requestId that ties the arriving file to this request.")]
    public static async Task<object> RequestBackup(
        McpContext me, AppDbContext db, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveAsync(db, me.Key, instanceId, ct);
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");

        await instances.RequestBackupAsync(instance, ct);
        instances.Log(instance, InstanceEventKind.BackupRequested, "Backup über MCP angefordert.");
        await db.SaveChangesAsync(ct);
        return new { ok = true, requestId = instance.BackupRequestId };
    }

    [McpServerTool(Name = "restore_backup"), Description("Restore a backup LIVE onto its instance — the one destructive action. Requires a key with the restore right (canRestore from list_instances). The cloud only MARKS it; the instance downloads and applies it on its next heartbeat and reports the outcome, visible via list_backups.")]
    public static async Task<object> RestoreBackup(
        McpContext me, AppDbContext db, BackupStore store,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The backup id to restore, as returned by list_backups.")] int backupId,
        CancellationToken ct)
    {
        var key = me.Key;
        if (!key.CanRestore)
            throw new McpException("Dieser Schlüssel darf nicht wiederherstellen.");

        var instance = await ResolveAsync(db, key, instanceId, ct);
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");

        var row = await db.CloudBackups.FirstOrDefaultAsync(b => b.Id == backupId && b.InstanceId == instance.Id, ct);
        if (row is null) throw new McpException("Backup nicht gefunden.");

        await store.RequestRestoreAsync(row, ct);
        return new { ok = true, id = row.Id, message = "Wiederherstellung vorgemerkt — die Instanz spielt sie beim nächsten Kontakt ein." };
    }
}
