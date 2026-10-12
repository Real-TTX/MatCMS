using System.ComponentModel;
using MatCMS.Cloud.Api;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>MCP twin of <see cref="BackupUsersApi"/> — a backup that carries the site's user accounts, for moving a
/// site. Restore-gated: password hashes leave the site.</summary>
[McpServerToolType]
public static class BackupUsersTools
{
    [McpServerTool(Name = "request_backup_with_users"), Description("Ask an instance for a fresh backup that ALSO contains its user accounts (password hashes, roles, 2FA) — only for moving a site to another server; ordinary backups never carry users. Needs the restore right. Poll list_backups for the file (same requestId). Restoring it elsewhere adds missing users and replaces only an untouched default 'admin'; existing accounts stay as they are.")]
    public static async Task<object> RequestBackupWithUsers(
        McpContext me, InstanceService instances, AppDbContext db,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await BackupUsersApi.FindAsync(db, me.Key, instanceId, ct) ?? throw new McpException("Instanz nicht gefunden.");
        if (!me.Key.CanRestore) throw new McpException("Diese Verbindung darf keine Benutzerdaten anfordern (Recht „Wiederherstellen“ fehlt).");
        var (requestId, error) = await BackupUsersApi.RequestAsync(instances, db, instance, "Backup mit Benutzern über MCP angefordert.", ct);
        if (error is not null) throw new McpException(error);
        return new { ok = true, requestId };
    }
}
