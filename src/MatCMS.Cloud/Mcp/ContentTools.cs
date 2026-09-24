using System.ComponentModel;
using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools that CHANGE a connected site's content (Stage 2, Increment 1: creating pages). They do NOT
/// touch the site directly — the cloud can't reach in. A tool ENQUEUES a <see cref="ContentOp"/>; the
/// instance pulls it on its next heartbeat and applies it through its OWN validated writer
/// (<c>BlockGenerator</c> + the add-only page writer), then reports the outcome. So every write here is
/// asynchronous ("queued as op N, applied within ~a minute") and add-only, and the cloud ships intent —
/// never code. Gated on the key's restore right: writing to a live site is the "restore" of content.
/// </summary>
[McpServerToolType]
public class ContentTools
{
    private static async Task<Instance> ResolveWritableAsync(McpContext me, AppDbContext db, string instanceId, CancellationToken ct)
    {
        var key = me.Key;
        if (!key.CanRestore)
            throw new McpException("Dieser Schlüssel darf keine Inhalte ändern (fehlendes Wiederherstellen-Recht).");
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (instance is null || !ApiKeyService.CanAccess(key, instance))
            throw new McpException("Instanz nicht gefunden.");
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");
        return instance;
    }

    [McpServerTool(Name = "create_page"), Description(
        "Create a NEW page on a connected MatCMS site (add-only: if the slug already exists the instance skips it, never overwrites). " +
        "You supply the page's blocks; the instance re-validates them (only known block types and text fields survive) and publishes the page. " +
        "This is queued and applied on the site's next heartbeat (~a minute) — check the returned opId's outcome via get_content_op or the instance log. Requires a key with the restore right.")]
    public static async Task<object> CreatePage(
        McpContext me, AppDbContext db, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The page title shown to visitors, e.g. \"Über uns\".")] string title,
        [Description("URL slug, lowercase, no spaces, e.g. \"ueber-uns\". Must be unique on the site.")] string slug,
        [Description("A JSON array of blocks: [{\"type\":\"hero\",\"data\":{\"title\":\"…\",\"text\":\"…\"}}, …]. Use block types the site knows (e.g. hero, richtext, cards). Unknown types/fields are dropped by the instance.")] string blocksJson,
        [Description("Whether to add the page to the site navigation (default true).")] bool showInNav = true,
        [Description("Optional navigation label; falls back to the title.")] string? navLabel = null,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, db, instanceId, ct);

        // Validate the AI-supplied blocks are at least well-formed JSON here (fail fast with a clear
        // message); the real content validation is the instance's own BlockGenerator, which is the only
        // thing that decides what is actually applied.
        JsonElement blocks;
        try
        {
            blocks = JsonSerializer.Deserialize<JsonElement>(blocksJson);
        }
        catch (JsonException)
        {
            throw new McpException("blocksJson ist kein gültiges JSON. Erwartet: ein Array von {\"type\":…,\"data\":{…}}.");
        }
        if (blocks.ValueKind != JsonValueKind.Array)
            throw new McpException("blocksJson muss ein JSON-Array sein.");

        var payload = JsonSerializer.Serialize(new
        {
            title = (title ?? "").Trim(),
            slug = (slug ?? "").Trim(),
            blocks,
            showInNav,
            navLabel,
        });

        var op = await instances.EnqueueContentOpAsync(instance, "page.create", payload,
            overwrite: false, reason: "KI: Seite anlegen", ct);
        return new
        {
            ok = true,
            opId = op.Id,
            queued = true,
            message = "Seite eingereiht — die Website legt sie beim nächsten Kontakt (~1 Min.) an. Ergebnis via get_content_op abrufbar.",
        };
    }

    [McpServerTool(Name = "get_content_op"), Description(
        "Get the status/outcome of a content operation (e.g. from create_page) by its opId: pending, or done with outcome (applied / skipped-exists / failed) and a detail message.")]
    public static async Task<object> GetContentOp(
        McpContext me, AppDbContext db,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The opId returned by the write tool (e.g. create_page).")] int opId,
        CancellationToken ct)
    {
        var key = me.Key;
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (instance is null || !ApiKeyService.CanAccess(key, instance))
            throw new McpException("Instanz nicht gefunden.");

        var op = await db.ContentOps.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == opId && o.InstanceId == instance.Id, ct);
        if (op is null) throw new McpException("Vorgang nicht gefunden.");
        return new
        {
            opId = op.Id,
            kind = op.Kind,
            done = op.DoneAt != null,
            outcome = op.Outcome,
            detail = op.Detail,
            createdAt = op.CreatedAt,
            doneAt = op.DoneAt,
        };
    }
}
