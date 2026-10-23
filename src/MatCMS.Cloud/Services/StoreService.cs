using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Writes to the global store — the cloud-wide catalogue of templates, plugins, components and
/// mail-templates that profiles select from. One place for the upsert/delete logic AND the rule that a
/// store change must bump every profile that SELECTED the entry (otherwise the edit never reaches an
/// instance — see the admin <c>Store</c> pages' <c>TouchUsersAsync</c>). Both the operator REST API
/// (<c>StoreApi</c>) and the MCP tools (<c>StoreTools</c>) go through here, so there is one behaviour.
/// <para>Callers validate their inputs and translate failures in their own idiom (REST → 400, MCP →
/// McpException); this service maps a validated input onto the entity and touches the affected profiles.</para>
/// </summary>
public class StoreService
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    public StoreService(AppDbContext db, ProfileService profiles) { _db = db; _profiles = profiles; }

    // ---- Templates (identity = Name) ------------------------------------------
    public async Task<bool> UpsertTemplateAsync(StoreTemplateInput b, CancellationToken ct = default)
    {
        var name = b.Name.Trim();
        var row = await _db.StoreTemplates.FirstOrDefaultAsync(t => t.Name == name, ct);
        var isNew = row is null;
        if (isNew) { row = new StoreTemplate { Name = name }; _db.StoreTemplates.Add(row); }
        row!.Description = b.Description ?? row.Description;
        row.AccentColor = b.AccentColor ?? row.AccentColor;
        row.SecondaryColor = b.SecondaryColor ?? row.SecondaryColor;
        row.HeadingFont = b.HeadingFont ?? row.HeadingFont;
        row.BodyFont = b.BodyFont ?? row.BodyFont;
        row.ButtonStyle = b.ButtonStyle ?? row.ButtonStyle;
        row.HeadingColor = b.HeadingColor ?? row.HeadingColor;
        row.TextColor = b.TextColor ?? row.TextColor;
        row.BackgroundColor = b.BackgroundColor ?? row.BackgroundColor;
        row.AltBackground = b.AltBackground ?? row.AltBackground;
        row.ContainerWidth = b.ContainerWidth ?? row.ContainerWidth;
        row.ButtonRadius = b.ButtonRadius ?? row.ButtonRadius;
        row.HeaderBackground = b.HeaderBackground ?? row.HeaderBackground;
        row.HeaderTextColor = b.HeaderTextColor ?? row.HeaderTextColor;
        row.HeaderPadding = b.HeaderPadding ?? row.HeaderPadding;
        row.CustomCss = b.CustomCss ?? row.CustomCss;
        row.CustomJs = b.CustomJs ?? row.CustomJs;
        row.LayoutHtml = b.LayoutHtml ?? row.LayoutHtml;
        row.MenuMapJson = b.MenuMapJson ?? row.MenuMapJson;
        row.ParametersJson = b.ParametersJson ?? row.ParametersJson;
        row.ParamValuesJson = b.ParamValuesJson ?? row.ParamValuesJson;
        row.PartsJson = b.PartsJson ?? row.PartsJson;
        await _db.SaveChangesAsync(ct);
        await TouchAsync(_db.ProfileStoreTemplates.Where(x => x.StoreTemplateId == row.Id).Select(x => x.ProfileId), ct);
        return isNew;
    }

    public async Task<bool> DeleteTemplateAsync(string name, CancellationToken ct = default)
    {
        var row = await _db.StoreTemplates.FirstOrDefaultAsync(t => t.Name == name, ct);
        if (row is null) return false;
        var picks = await _db.ProfileStoreTemplates.Where(x => x.StoreTemplateId == row.Id).ToListAsync(ct);
        var ids = picks.Select(x => x.ProfileId).Distinct().ToList();
        _db.ProfileStoreTemplates.RemoveRange(picks); // a profile pointing at a gone row must not drop out silently
        _db.StoreTemplates.Remove(row);
        await _db.SaveChangesAsync(ct);
        await TouchAsync(ids, ct);
        return true;
    }

    // ---- Components (identity = Type) -----------------------------------------
    public async Task<bool> UpsertComponentAsync(StoreComponentInput b, CancellationToken ct = default)
    {
        var type = b.Type.Trim().ToLowerInvariant();
        var row = await _db.StoreComponents.FirstOrDefaultAsync(c => c.Type == type, ct);
        var isNew = row is null;
        if (isNew) { row = new StoreComponent { Type = type }; _db.StoreComponents.Add(row); }
        row!.Name = b.Name.Trim();
        row.Description = b.Description ?? row.Description;
        row.Icon = b.Icon ?? row.Icon;
        row.FieldsJson = b.FieldsJson ?? row.FieldsJson;
        row.TemplateHtml = b.TemplateHtml ?? row.TemplateHtml;
        await _db.SaveChangesAsync(ct);
        await TouchAsync(_db.ProfileStoreComponents.Where(x => x.StoreComponentId == row.Id).Select(x => x.ProfileId), ct);
        return isNew;
    }

    public async Task<bool> DeleteComponentAsync(string type, CancellationToken ct = default)
    {
        var t = type.Trim().ToLowerInvariant();
        var row = await _db.StoreComponents.FirstOrDefaultAsync(c => c.Type == t, ct);
        if (row is null) return false;
        var picks = await _db.ProfileStoreComponents.Where(x => x.StoreComponentId == row.Id).ToListAsync(ct);
        var ids = picks.Select(x => x.ProfileId).Distinct().ToList();
        _db.ProfileStoreComponents.RemoveRange(picks);
        _db.StoreComponents.Remove(row);
        await _db.SaveChangesAsync(ct);
        await TouchAsync(ids, ct);
        return true;
    }

    // ---- Mail templates (identity = Key) --------------------------------------
    public async Task<bool> UpsertMailTemplateAsync(StoreMailTemplateInput b, CancellationToken ct = default)
    {
        var mk = b.Key.Trim();
        var row = await _db.StoreMailTemplates.FirstOrDefaultAsync(m => m.Key == mk, ct);
        var isNew = row is null;
        if (isNew) { row = new StoreMailTemplate { Key = mk }; _db.StoreMailTemplates.Add(row); }
        row!.Name = b.Name ?? row.Name;
        row.Description = b.Description ?? row.Description;
        row.Subject = b.Subject;
        row.Body = b.Body ?? row.Body;
        row.Enabled = b.Enabled ?? row.Enabled;
        row.IsHtml = b.IsHtml ?? row.IsHtml;
        await _db.SaveChangesAsync(ct);
        await TouchAsync(_db.ProfileStoreMailTemplates.Where(x => x.StoreMailTemplateId == row.Id).Select(x => x.ProfileId), ct);
        return isNew;
    }

    public async Task<bool> DeleteMailTemplateAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.StoreMailTemplates.FirstOrDefaultAsync(m => m.Key == key, ct);
        if (row is null) return false;
        var picks = await _db.ProfileStoreMailTemplates.Where(x => x.StoreMailTemplateId == row.Id).ToListAsync(ct);
        var ids = picks.Select(x => x.ProfileId).Distinct().ToList();
        _db.ProfileStoreMailTemplates.RemoveRange(picks);
        _db.StoreMailTemplates.Remove(row);
        await _db.SaveChangesAsync(ct);
        await TouchAsync(ids, ct);
        return true;
    }

    // ---- Plugins (raw ZIP bundle, identity = Key) -----------------------------
    public sealed record PluginResult(bool Created, string Key, string Name, string Version);

    /// <summary>Stores a raw plugin ZIP as-is (keyed/named/versioned from its <c>plugin.json</c>), so the
    /// instance still receives the exact archive its own importer expects. Returns null when the bundle is
    /// not a valid plugin — the caller decides how to report that.</summary>
    public async Task<PluginResult?> UpsertPluginAsync(byte[] bundle, CancellationToken ct = default)
    {
        var (pk, pname, pver, pdesc, err) = ReadBundleMeta(bundle);
        if (err is not null || pk is null) return null;
        var row = await _db.StorePlugins.FirstOrDefaultAsync(x => x.Key == pk, ct);
        var isNew = row is null;
        if (isNew) { row = new StorePlugin { Key = pk }; _db.StorePlugins.Add(row); }
        row!.Name = pname ?? pk;
        row.Version = pver ?? "";
        row.Description = pdesc ?? "";
        row.Bundle = bundle;
        row.UploadedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await TouchAsync(_db.ProfileStorePlugins.Where(x => x.StorePluginId == row.Id).Select(x => x.ProfileId), ct);
        return new PluginResult(isNew, row.Key, row.Name, row.Version);
    }

    public async Task<bool> DeletePluginAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.StorePlugins.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) return false;
        var picks = await _db.ProfileStorePlugins.Where(x => x.StorePluginId == row.Id).ToListAsync(ct);
        var ids = picks.Select(x => x.ProfileId).Distinct().ToList();
        _db.ProfileStorePlugins.RemoveRange(picks);
        _db.StorePlugins.Remove(row);
        await _db.SaveChangesAsync(ct);
        await TouchAsync(ids, ct);
        return true;
    }

    // ---- shared helpers ----
    private async Task TouchAsync(IEnumerable<int> profileIds, CancellationToken ct)
    {
        // Materialise + de-dupe first: TouchAsync opens its own work on the same DbContext.
        var ids = profileIds is IQueryable<int> q
            ? await q.Distinct().ToListAsync(ct)
            : profileIds.Distinct().ToList();
        foreach (var pid in ids) await _profiles.TouchAsync(pid);
    }

    // ---- Reading ----------------------------------------------------------------------------------
    // Lists carry what a client needs to pick an entry; the single reads return the whole entry. The overview adds how many
    // profiles selected each entry (UsedBy). All read-only.

    public async Task<StoreOverview> OverviewAsync(CancellationToken ct = default)
    {
        var plugins = await _db.StorePlugins.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new StorePluginRow(p.Key, p.Name, p.Version, p.Description, p.Bundle.Length, _db.ProfileStorePlugins.Count(x => x.StorePluginId == p.Id), null))
            .ToListAsync(ct);
        var templates = await _db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new StoreTemplateRow(t.Name, t.Description, t.AccentColor, t.HeadingFont, t.BodyFont, _db.ProfileStoreTemplates.Count(x => x.StoreTemplateId == t.Id)))
            .ToListAsync(ct);
        var components = await _db.StoreComponents.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new StoreComponentRow(c.Type, c.Name, c.Description, c.Icon, _db.ProfileStoreComponents.Count(x => x.StoreComponentId == c.Id)))
            .ToListAsync(ct);
        var mailTemplates = await _db.StoreMailTemplates.AsNoTracking().OrderBy(m => m.Key)
            .Select(m => new StoreMailTemplateRow(m.Key, m.Name, m.Subject, m.Enabled, m.IsHtml, _db.ProfileStoreMailTemplates.Count(x => x.StoreMailTemplateId == m.Id)))
            .ToListAsync(ct);
        return new StoreOverview(plugins, templates, components, mailTemplates);
    }

    public Task<List<StoreTemplateRow>> ListTemplatesAsync(CancellationToken ct = default) =>
        _db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new StoreTemplateRow(t.Name, t.Description, t.AccentColor, t.HeadingFont, t.BodyFont, null)).ToListAsync(ct);

    public Task<StoreTemplate?> GetTemplateAsync(string name, CancellationToken ct = default) =>
        _db.StoreTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name, ct);

    public Task<List<StoreComponentRow>> ListComponentsAsync(CancellationToken ct = default) =>
        _db.StoreComponents.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new StoreComponentRow(c.Type, c.Name, c.Description, c.Icon, null)).ToListAsync(ct);

    /// <summary>What an instance may browse in the store: plugins, templates and components with the few fields a picker needs.</summary>
    public async Task<StoreCatalog> CatalogAsync() => new StoreCatalog
    {
        Plugins = await _db.StorePlugins.AsNoTracking()
            .Select(p => new CatalogPlugin { Key = p.Key, Name = p.Name, Version = p.Version, Description = p.Description })
            .OrderBy(p => p.Name).ToListAsync(),
        Templates = await _db.StoreTemplates.AsNoTracking()
            .Select(t => new CatalogTemplate { Name = t.Name, Description = t.Description, AccentColor = t.AccentColor })
            .OrderBy(t => t.Name).ToListAsync(),
        Components = await _db.StoreComponents.AsNoTracking()
            .Select(c => new CatalogComponent { Type = c.Type, Name = c.Name, Description = c.Description })
            .OrderBy(c => c.Name).ToListAsync()
    };

    /// <summary>
    /// The store as the PUBLIC may see it (<c>GET /api/public/store</c>, read by the "Store-Einblick" plugin on a website): names, versions,
    /// descriptions and the kind of each plugin (script or native, read from the bundle's own manifest) — never a bundle, a template's code or
    /// anything else an instance downloads. Built from the same rows as the instances' catalogue, so the two cannot disagree.
    /// </summary>
    public async Task<System.Text.Json.Nodes.JsonObject> PublicCatalogAsync(CancellationToken ct = default)
    {
        var plugins = new System.Text.Json.Nodes.JsonArray();
        foreach (var p in await _db.StorePlugins.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct))
        {
            var native = NativeBundleInfo.Read(p.Bundle);
            plugins.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = p.Key, ["name"] = p.Name, ["version"] = p.Version, ["description"] = p.Description,
                ["kind"] = native is null ? "script" : "native", ["api"] = native?.Api ?? "", ["updated"] = p.UploadedAt.ToString("yyyy-MM-dd")
            });
        }
        var templates = new System.Text.Json.Nodes.JsonArray();
        foreach (var t in await _db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Name, t.Description, t.AccentColor }).ToListAsync(ct))
            templates.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["accent"] = t.AccentColor });
        var components = new System.Text.Json.Nodes.JsonArray();
        foreach (var c in await _db.StoreComponents.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Type, c.Name, c.Description }).ToListAsync(ct))
            components.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = c.Type, ["name"] = c.Name, ["description"] = c.Description });
        return new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = 1, ["generated"] = DateTime.UtcNow.ToString("O"), ["plugins"] = plugins, ["templates"] = templates, ["components"] = components
        };
    }

    /// <summary>A component by its type exactly as stored (what the catalogue lists); see <see cref="GetComponentAsync"/> for the
    /// lower-casing lookup of the operator API.</summary>
    public Task<StoreComponent?> GetComponentExactAsync(string type, CancellationToken ct = default) =>
        _db.StoreComponents.AsNoTracking().FirstOrDefaultAsync(x => x.Type == type, ct);

    /// <summary>A component by its type (identity; compared lower-case).</summary>
    public Task<StoreComponent?> GetComponentAsync(string type, CancellationToken ct = default)
    {
        var t = type.ToLowerInvariant();
        return _db.StoreComponents.AsNoTracking().FirstOrDefaultAsync(x => x.Type == t, ct);
    }

    public Task<List<StoreMailTemplateRow>> ListMailTemplatesAsync(CancellationToken ct = default) =>
        _db.StoreMailTemplates.AsNoTracking().OrderBy(m => m.Key)
            .Select(m => new StoreMailTemplateRow(m.Key, m.Name, m.Subject, m.Enabled, m.IsHtml, null)).ToListAsync(ct);

    public Task<StoreMailTemplate?> GetMailTemplateAsync(string key, CancellationToken ct = default) =>
        _db.StoreMailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);

    public Task<List<StorePluginRow>> ListPluginsAsync(CancellationToken ct = default) =>
        _db.StorePlugins.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new StorePluginRow(p.Key, p.Name, p.Version, p.Description, p.Bundle.Length, null, p.UploadedAt)).ToListAsync(ct);

    /// <summary>A plugin with its bundle (the ZIP), for the download.</summary>
    public Task<StorePlugin?> GetPluginAsync(string key, CancellationToken ct = default) =>
        _db.StorePlugins.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key, ct);

    /// <summary>Reads a plugin bundle's manifest (plugin.json) enough to key/name/version it.</summary>
    public static (string? key, string? name, string? version, string? desc, string? error) ReadBundleMeta(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry(PluginBundle.ManifestEntry);
            if (entry is null) return (null, null, null, null, $"Kein {PluginBundle.ManifestEntry} im Bundle.");
            using var r = new StreamReader(entry.Open());
            var json = r.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Get(string p) => root.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var key = Get("Key") ?? Get("key");
            if (string.IsNullOrWhiteSpace(key)) return (null, null, null, null, "Bundle-Manifest hat keinen Key.");
            return (key, Get("Name") ?? Get("name"), Get("Version") ?? Get("version"), Get("Description") ?? Get("description"), null);
        }
        catch (Exception ex) { return (null, null, null, null, "Ungültiges Bundle: " + ex.Message); }
    }
}

// ---- inputs (shared by REST + MCP) ------------------------------------------
public record StoreTemplateInput(string Name, string? Description, string? AccentColor, string? SecondaryColor,
    string? HeadingFont, string? BodyFont, string? ButtonStyle, string? HeadingColor, string? TextColor,
    string? BackgroundColor, string? AltBackground, string? ContainerWidth, string? ButtonRadius,
    string? HeaderBackground, string? HeaderTextColor, string? HeaderPadding, string? CustomCss, string? CustomJs,
    string? LayoutHtml, string? MenuMapJson, string? ParametersJson, string? ParamValuesJson, string? PartsJson);
public record StoreComponentInput(string Type, string Name, string? Description, string? Icon, string? FieldsJson, string? TemplateHtml);
public record StoreMailTemplateInput(string Key, string? Name, string? Description, string Subject, string? Body, bool? Enabled, bool? IsHtml);

// ---- rows of the store lists (shared by REST + MCP) ---------------------------
// UsedBy / UploadedAt are only there where the list asks for them; a null one stays out of the JSON.
public record StorePluginRow(string Key, string Name, string Version, string Description, int SizeBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? UploadedAt);
public record StoreTemplateRow(string Name, string Description, string AccentColor, string HeadingFont, string BodyFont,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy);
public record StoreComponentRow(string Type, string Name, string Description, string Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy);
public record StoreMailTemplateRow(string Key, string Name, string Subject, bool Enabled, bool IsHtml,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy);
public record StoreOverview(List<StorePluginRow> Plugins, List<StoreTemplateRow> Templates, List<StoreComponentRow> Components, List<StoreMailTemplateRow> MailTemplates);
