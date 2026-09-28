using System.ComponentModel;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools that manage the GLOBAL STORE — the cloud-wide catalogue of templates, components and
/// mail-templates that profiles select from. Unlike the content tools these act directly on the cloud DB
/// (there is no instance round-trip), through the same <see cref="StoreService"/> the REST API uses, so a
/// change here bumps every profile that selected the entry and rolls out on those instances' next beat.
/// Reading needs only a valid key; writing needs the key's <see cref="ApiKey.CanManageStore"/> right.
/// <para>Plugin bundles are binary and stay on the REST API (<c>POST /api/v1/store/plugins</c>); only
/// removing a store plugin is offered here.</para>
/// </summary>
[McpServerToolType]
public class StoreTools
{
    private static void RequireStore(McpContext me)
    {
        if (!me.Key.CanManageStore)
            throw new McpException("Dieser Schlüssel darf den Store nicht verwalten (fehlendes Store-Recht).");
    }

    [McpServerTool(Name = "list_store"), Description(
        "List the cloud-wide store catalogue: templates (themes), components (reusable blocks), mail templates and plugins, each with how many profiles use it. Read-only; any valid key.")]
    public static async Task<object> ListStore(McpContext me, AppDbContext db, CancellationToken ct)
    {
        _ = me.Key; // ensures an authenticated key (throws otherwise)
        var templates = await db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => new
        { t.Name, t.Description, t.AccentColor, t.HeadingFont, t.BodyFont, usedBy = db.ProfileStoreTemplates.Count(x => x.StoreTemplateId == t.Id) }).ToListAsync(ct);
        var components = await db.StoreComponents.AsNoTracking().OrderBy(c => c.Name).Select(c => new
        { c.Type, c.Name, c.Description, c.Icon, usedBy = db.ProfileStoreComponents.Count(x => x.StoreComponentId == c.Id) }).ToListAsync(ct);
        var mailTemplates = await db.StoreMailTemplates.AsNoTracking().OrderBy(m => m.Key).Select(m => new
        { m.Key, m.Name, m.Subject, m.Enabled, m.IsHtml, usedBy = db.ProfileStoreMailTemplates.Count(x => x.StoreMailTemplateId == m.Id) }).ToListAsync(ct);
        var plugins = await db.StorePlugins.AsNoTracking().OrderBy(p => p.Name).Select(p => new
        { p.Key, p.Name, p.Version, usedBy = db.ProfileStorePlugins.Count(x => x.StorePluginId == p.Id) }).ToListAsync(ct);
        return new { templates, components, mailTemplates, plugins };
    }

    // ---- Templates ------------------------------------------------------------
    [McpServerTool(Name = "upsert_store_template"), Description(
        "Create or update a THEME (template) in the store, by name. Only the fields you pass are changed; omitted fields keep their current value (on a new template, their defaults). Colours are hex like \"#de7e11\". This rolls out to every profile that uses the theme on their instances' next heartbeat. Requires the store right.")]
    public static async Task<object> UpsertStoreTemplate(
        McpContext me, StoreService store,
        [Description("Theme name — the identity. Same name updates in place.")] string name,
        [Description("Optional description.")] string? description = null,
        [Description("Accent/brand colour, hex e.g. \"#de7e11\".")] string? accentColor = null,
        [Description("Secondary colour, hex.")] string? secondaryColor = null,
        [Description("Heading font family, e.g. \"Geologica\".")] string? headingFont = null,
        [Description("Body font family, e.g. \"Inter\".")] string? bodyFont = null,
        [Description("Heading text colour, hex.")] string? headingColor = null,
        [Description("Body text colour, hex.")] string? textColor = null,
        [Description("Page background colour, hex.")] string? backgroundColor = null,
        [Description("Extra CSS appended to the theme.")] string? customCss = null,
        CancellationToken ct = default)
    {
        RequireStore(me);
        if (string.IsNullOrWhiteSpace(name)) throw new McpException("name ist erforderlich.");
        var created = await store.UpsertTemplateAsync(new StoreTemplateInput(
            name, description, accentColor, secondaryColor, headingFont, bodyFont, null, headingColor, textColor,
            backgroundColor, null, null, null, null, null, null, customCss, null, null, null, null, null, null), ct);
        return new { ok = true, created, name = name.Trim() };
    }

    [McpServerTool(Name = "delete_store_template"), Description(
        "Remove a theme (template) from the store by name. Instances that already installed it keep it; only future rollouts stop. Requires the store right.")]
    public static async Task<object> DeleteStoreTemplate(
        McpContext me, StoreService store,
        [Description("Theme name to remove.")] string name, CancellationToken ct = default)
    {
        RequireStore(me);
        if (!await store.DeleteTemplateAsync(name, ct)) throw new McpException("Template nicht gefunden.");
        return new { ok = true };
    }

    // ---- Components -----------------------------------------------------------
    [McpServerTool(Name = "upsert_store_component"), Description(
        "Create or update a reusable block (component) in the store, by type. fieldsJson is the field definition array (must be valid JSON). Rolls out to every profile that uses it. Requires the store right.")]
    public static async Task<object> UpsertStoreComponent(
        McpContext me, StoreService store,
        [Description("Component type — the identity, lowercase, no spaces (e.g. \"pricing\").")] string type,
        [Description("Display name.")] string name,
        [Description("Optional description.")] string? description = null,
        [Description("Optional icon (Tabler icon name or emoji).")] string? icon = null,
        [Description("Field definitions as a JSON array; must be valid JSON. Omit to keep the current value.")] string? fieldsJson = null,
        [Description("The component's HTML template. Omit to keep the current value.")] string? templateHtml = null,
        CancellationToken ct = default)
    {
        RequireStore(me);
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name)) throw new McpException("type und name sind erforderlich.");
        if (!string.IsNullOrWhiteSpace(fieldsJson) && !IsValidJson(fieldsJson!)) throw new McpException("fieldsJson ist kein gültiges JSON.");
        var created = await store.UpsertComponentAsync(new StoreComponentInput(type, name, description, icon, fieldsJson, templateHtml), ct);
        return new { ok = true, created, type = type.Trim().ToLowerInvariant() };
    }

    [McpServerTool(Name = "delete_store_component"), Description(
        "Remove a component from the store by type. Instances that already installed it keep it. Requires the store right.")]
    public static async Task<object> DeleteStoreComponent(
        McpContext me, StoreService store,
        [Description("Component type to remove.")] string type, CancellationToken ct = default)
    {
        RequireStore(me);
        if (!await store.DeleteComponentAsync(type, ct)) throw new McpException("Komponente nicht gefunden.");
        return new { ok = true };
    }

    // ---- Mail templates -------------------------------------------------------
    [McpServerTool(Name = "upsert_store_mail_template"), Description(
        "Create or update a mail template in the store, by key (the key names WHAT the mail is, e.g. \"form.submission\"). Rolls out to every profile that uses it. Requires the store right.")]
    public static async Task<object> UpsertStoreMailTemplate(
        McpContext me, StoreService store,
        [Description("Mail template key — the identity, e.g. \"form.submission\".")] string key,
        [Description("The mail subject line.")] string subject,
        [Description("Display name for the catalogue.")] string? name = null,
        [Description("Optional description.")] string? description = null,
        [Description("The mail body.")] string? body = null,
        [Description("Whether the mail is enabled (default keeps current / true for new).")] bool? enabled = null,
        [Description("Whether the body is HTML (default keeps current).")] bool? isHtml = null,
        CancellationToken ct = default)
    {
        RequireStore(me);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(subject)) throw new McpException("key und subject sind erforderlich.");
        var created = await store.UpsertMailTemplateAsync(new StoreMailTemplateInput(key, name, description, subject, body, enabled, isHtml), ct);
        return new { ok = true, created, key = key.Trim() };
    }

    [McpServerTool(Name = "delete_store_mail_template"), Description(
        "Remove a mail template from the store by key. Requires the store right.")]
    public static async Task<object> DeleteStoreMailTemplate(
        McpContext me, StoreService store,
        [Description("Mail template key to remove.")] string key, CancellationToken ct = default)
    {
        RequireStore(me);
        if (!await store.DeleteMailTemplateAsync(key, ct)) throw new McpException("Mail-Template nicht gefunden.");
        return new { ok = true };
    }

    // ---- Plugins (delete only; upload is binary → REST) -----------------------
    [McpServerTool(Name = "delete_store_plugin"), Description(
        "Remove a plugin from the store by key. Instances that already installed it keep it. Uploading a plugin is done over REST (POST /api/v1/store/plugins) because it is a binary bundle. Requires the store right.")]
    public static async Task<object> DeleteStorePlugin(
        McpContext me, StoreService store,
        [Description("Plugin key to remove.")] string key, CancellationToken ct = default)
    {
        RequireStore(me);
        if (!await store.DeletePluginAsync(key, ct)) throw new McpException("Plugin nicht gefunden.");
        return new { ok = true };
    }

    private static bool IsValidJson(string s)
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(s); return true; } catch { return false; }
    }
}
