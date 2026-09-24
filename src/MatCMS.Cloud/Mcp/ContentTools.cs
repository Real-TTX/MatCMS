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

    [McpServerTool(Name = "create_post"), Description(
        "Create a NEW blog post (\"Beitrag\") on a connected site (add-only: an existing slug is skipped). " +
        "The body and teaser are HTML and are SANITISED by the instance before storage (scripts/handlers removed). " +
        "Queued and applied on the next heartbeat (~a minute); check get_content_op(opId). Requires a key with the restore right.")]
    public static async Task<object> CreatePost(
        McpContext me, AppDbContext db, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The post heading, e.g. \"Sommer-Angebote 2026\".")] string title,
        [Description("Short teaser shown on cards/listings (plain text or simple HTML).")] string excerpt,
        [Description("The post body as HTML (will be sanitised: only safe formatting/links/images survive).")] string contentHtml,
        [Description("URL slug, lowercase, no spaces. Falls back to a slug of the title if empty.")] string? slug = null,
        [Description("Comma-separated tags, e.g. \"angebot,sommer\".")] string? tags = null,
        [Description("Publish immediately (true) or save as draft (false). Default true.")] bool publish = true,
        CancellationToken ct = default)
    {
        var instance = await ResolveWritableAsync(me, db, instanceId, ct);
        var payload = JsonSerializer.Serialize(new
        {
            title = (title ?? "").Trim(),
            slug = (slug ?? "").Trim(),
            excerpt = excerpt ?? "",
            contentHtml = contentHtml ?? "",
            tags = (tags ?? "").Trim(),
            publish,
        });
        var op = await instances.EnqueueContentOpAsync(instance, "post.create", payload,
            overwrite: false, reason: "KI: Beitrag anlegen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Beitrag eingereiht — wird beim nächsten Kontakt angelegt. Ergebnis via get_content_op." };
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
            // For a read op (list_pages/get_page): the content the instance serialized back, as raw JSON.
            result = op.ResultJson is null ? null : (object?)JsonSerializer.Deserialize<JsonElement>(op.ResultJson),
            createdAt = op.CreatedAt,
            doneAt = op.DoneAt,
        };
    }

    // Read scope only — reading a site's content is not a write, so it does NOT require the restore right.
    private static async Task<Instance> ResolveReadableAsync(McpContext me, AppDbContext db, string instanceId, CancellationToken ct)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (instance is null || !ApiKeyService.CanAccess(me.Key, instance))
            throw new McpException("Instanz nicht gefunden.");
        if (instance.Status != InstanceStatus.Approved)
            throw new McpException("Instanz ist nicht freigegeben.");
        return instance;
    }

    [McpServerTool(Name = "list_pages"), Description(
        "Ask a connected site for its list of pages (slug, title, locale, published). Because the cloud cannot reach into a site, this is QUEUED: it returns an opId; the site answers on its next heartbeat (~a minute). Poll get_content_op(opId) — its `result` holds the page list once done.")]
    public static async Task<object> ListPages(
        McpContext me, AppDbContext db, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        CancellationToken ct)
    {
        var instance = await ResolveReadableAsync(me, db, instanceId, ct);
        var op = await instances.EnqueueContentOpAsync(instance, "pages.list", "{}", overwrite: false, reason: "KI: Seiten lesen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Angefragt — Ergebnis via get_content_op(opId)." };
    }

    [McpServerTool(Name = "get_page"), Description(
        "Ask a connected site for one page's blocks (to inspect before editing). QUEUED like list_pages: returns an opId; poll get_content_op(opId) whose `result` holds { title, slug, blocks:[{type,data}] } once the site has answered (~a minute).")]
    public static async Task<object> GetPage(
        McpContext me, AppDbContext db, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The page slug to read, e.g. \"ueber-uns\".")] string slug,
        CancellationToken ct)
    {
        var instance = await ResolveReadableAsync(me, db, instanceId, ct);
        var payload = JsonSerializer.Serialize(new { slug = (slug ?? "").Trim() });
        var op = await instances.EnqueueContentOpAsync(instance, "page.read", payload, overwrite: false, reason: "KI: Seite lesen", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Angefragt — Ergebnis via get_content_op(opId)." };
    }

    [McpServerTool(Name = "update_page_blocks"), Description(
        "Replace the blocks of an EXISTING page on a connected site (overwrite). Read it first with get_page, edit the blocks, then send the full new block list here. The site re-validates the blocks (unknown types/fields dropped) and replaces the page's content. QUEUED and applied on the next heartbeat; check get_content_op(opId). Requires a key with the restore right (this overwrites live content).")]
    public static async Task<object> UpdatePageBlocks(
        McpContext me, AppDbContext db, InstanceService instances,
        [Description("The instance id, as returned by list_instances.")] string instanceId,
        [Description("The slug of the existing page to update.")] string slug,
        [Description("The FULL new JSON array of blocks (replaces all current blocks): [{\"type\":…,\"data\":{…}}, …].")] string blocksJson,
        CancellationToken ct)
    {
        var instance = await ResolveWritableAsync(me, db, instanceId, ct);

        JsonElement blocks;
        try { blocks = JsonSerializer.Deserialize<JsonElement>(blocksJson); }
        catch (JsonException) { throw new McpException("blocksJson ist kein gültiges JSON-Array."); }
        if (blocks.ValueKind != JsonValueKind.Array)
            throw new McpException("blocksJson muss ein JSON-Array sein.");

        var payload = JsonSerializer.Serialize(new { slug = (slug ?? "").Trim(), blocks });
        var op = await instances.EnqueueContentOpAsync(instance, "page.updateBlocks", payload,
            overwrite: true, reason: "KI: Seitenblöcke ändern", ct);
        return new { ok = true, opId = op.Id, queued = true, message = "Änderung eingereiht — wird beim nächsten Kontakt angewendet. Ergebnis via get_content_op." };
    }
}
