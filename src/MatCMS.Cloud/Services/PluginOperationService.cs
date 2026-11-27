using System.Text.Json;
using System.Text.Json.Nodes;
using MatCMS.Cloud.Models;
using MatCMS.Shared;

namespace MatCMS.Cloud.Services;

/// <summary>
/// The operations a connected site's plugins describe, listed and run GENERICALLY — every plugin, script or native, without a tool per
/// operation. The site reports its catalogue on the heartbeat (<see cref="HeartbeatRequest.PluginOperations"/>, kept in
/// <see cref="Instance.PluginOperationsJson"/>); running one enqueues a content op whose kind is the operation's QUALIFIED name
/// (<c>shop.product.create</c>), applied by the site through the plugin on its next beat and reported back like every content op.
/// One service for both surfaces (MCP <c>list_plugin_operations</c>/<c>run_plugin_operation</c>, REST <c>/api/v1/instances/{id}/plugin-operations</c>).
/// <para>The rules, checked HERE and not left to the caller: only a name the site listed (internal operations — undo helpers — are never
/// listed, and the site refuses them anyway); a WRITE needs the key's restore right (writing to a live site is the "restore" of content);
/// <see cref="PluginOperationInfo.PersonalData"/> needs it too, READS included (orders, bookings, member lists); a name that is a core content
/// op kind is refused, so a plugin whose key happens to be "page" can never turn this into a second door to the core's ops.</para>
/// </summary>
public sealed class PluginOperationService
{
    /// <summary>The most entries kept of one site's catalogue.</summary>
    public const int MaxOperations = 400;

    /// <summary>The kinds <c>ContentOpApplier</c> handles itself; never reachable through here.</summary>
    private static readonly HashSet<string> CoreKinds = new(StringComparer.Ordinal)
    {
        "page.create", "form.create", "setting.set", "cleanup.analyze", "cleanup.apply", "pages.list", "page.read", "page.updateBlocks",
    };

    private readonly InstanceService _instances;

    public PluginOperationService(InstanceService instances) => _instances = instances;

    /// <summary>The site's last reported catalogue (null = the site never reported one: older than this cloud's protocol, or not beaten yet).</summary>
    public static List<PluginOperationInfo>? Catalog(Instance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.PluginOperationsJson)) return null;
        try { return JsonSerializer.Deserialize<List<PluginOperationInfo>>(instance.PluginOperationsJson) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>The catalogue as a caller sees it: each entry says whether THIS key may run it.</summary>
    public static object ListDto(Instance instance, ApiKey key)
    {
        var ops = Catalog(instance);
        return new
        {
            instanceId = instance.PublicId,
            reported = ops is not null,
            reportedAt = instance.PluginOperationsAt,
            note = ops is null
                ? "Diese Website hat noch keine Plugin-Aktionen gemeldet (ältere MatCMS-Version oder noch kein Heartbeat). Website aktualisieren."
                : "Ausführen mit run_plugin_operation(name, args) — wird beim nächsten Heartbeat (~1 Minute) angewendet; Ergebnis über get_content_op(opId).",
            operations = (ops ?? new()).Select(o => new
            {
                name = o.Name, plugin = o.Plugin, pluginName = o.PluginName, description = o.Description, arguments = o.Arguments,
                writes = o.Writes, personalData = o.PersonalData, allowed = Allowed(o, key) is null,
            }),
        };
    }

    /// <summary>Why this key may not run the operation, or null when it may.</summary>
    public static string? Allowed(PluginOperationInfo op, ApiKey key)
    {
        if ((op.Writes || op.PersonalData) && !key.CanRestore)
            return op.PersonalData
                ? "Diese Aktion liest oder ändert personenbezogene Daten – dafür braucht der Schlüssel das Wiederherstellen-Recht."
                : "Diese Aktion ändert die Website – dafür braucht der Schlüssel das Wiederherstellen-Recht.";
        return null;
    }

    public sealed record RunResult(bool Ok, string? Error, ContentOp? Op = null, PluginOperationInfo? Info = null);

    /// <summary>Checks the name against the site's catalogue and the key's rights, then queues the operation.</summary>
    public async Task<RunResult> RunAsync(Instance instance, ApiKey key, string? name, string? argsJson, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new(false, "name fehlt (der qualifizierte Name aus list_plugin_operations, z. B. shop.product.create).");
        if (CoreKinds.Contains(name)) return new(false, $"„{name}“ ist keine Plugin-Aktion.");
        var ops = Catalog(instance);
        if (ops is null) return new(false, "Diese Website hat noch keine Plugin-Aktionen gemeldet (ältere MatCMS-Version oder noch kein Heartbeat).");
        var op = ops.FirstOrDefault(o => o.Name == name);
        if (op is null)
            return new(false, $"Die Website bietet keine Aktion „{name}“ an (oder ihr Plugin ist aus). list_plugin_operations zeigt, was es gibt.");
        if (Allowed(op, key) is { } refusal) return new(false, refusal);

        var args = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson.Trim();
        try
        {
            if (JsonNode.Parse(args) is not JsonObject) return new(false, "args muss ein JSON-Objekt sein, z. B. {\"title\": \"…\"}.");
        }
        catch (JsonException) { return new(false, "args ist kein gültiges JSON."); }
        if (args.Length > 1_000_000) return new(false, "args ist zu groß (höchstens 1 MB).");

        // A write is queued as an overwrite: it changes live content (so the profile's "Backup vor KI-Änderung" takes its restore point first).
        var queued = await _instances.EnqueueContentOpAsync(instance, op.Name, args, overwrite: op.Writes,
            reason: $"KI: {(op.PluginName.Length > 0 ? op.PluginName : op.Plugin)} – {op.Name}", ct);
        return new(true, null, queued, op);
    }
}
