using System.ComponentModel;
using System.Text.Json;
using MatCMS.Cloud.Api;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>The notification matrix as MCP tools — same service and rights as <c>/api/v1/notifications</c>.</summary>
[McpServerToolType]
public class NotificationTools
{
    [McpServerTool(Name = "get_notifications"), Description(
        "Who is notified about what: the matrix rows (g:admins, g:operators, u:<userId>, e:<address>) with their subscribed events (offline, update, updateFailed, migration, nodeOffline, removal), plus the users that can be added as rows. Operators only ever receive events of their own instances and never fleet-only events. Requires an all-instances key.")]
    public static async Task<object> GetNotifications(McpContext me, NotificationService notify, AppDbContext db)
    {
        if (!me.Key.AllInstances) throw new McpException("Benachrichtigungen betreffen die ganze Cloud — nur mit einem Schlüssel für alle Instanzen.");
        return await NotificationApi.DescribeAsync(notify, db);
    }

    [McpServerTool(Name = "set_notifications"), Description(
        "Replace the whole notification matrix. rowsJson is a JSON array like [{\"key\":\"g:admins\",\"events\":[\"offline\",\"update\"]},{\"key\":\"e:ops@example.com\",\"events\":[\"nodeOffline\"]}]. Rows not listed are removed — read get_notifications first and send the complete list back. Unknown events/keys are dropped. Requires an all-instances key with the profile-management right.")]
    public static async Task<object> SetNotifications(McpContext me, NotificationService notify, AppDbContext db,
        [Description("The complete matrix as a JSON array of {key, events}.")] string rowsJson)
    {
        if (!me.Key.AllInstances || !me.Key.CanManageProfiles)
            throw new McpException("Benachrichtigungen ändern braucht einen Schlüssel für alle Instanzen mit dem Recht „Profile verwalten“.");
        List<NotifyRow>? rows;
        try { rows = JsonSerializer.Deserialize<List<NotifyRow>>(rowsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (Exception ex) { throw new McpException("rowsJson ist kein gültiges JSON-Array: " + ex.Message); }
        await notify.SaveAsync(new NotifyMatrix { Rows = rows ?? new() });
        return await NotificationApi.DescribeAsync(notify, db);
    }
}
