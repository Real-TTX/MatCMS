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
        row.Origin = ""; row.OriginVersion = "";   // an own save makes it the operator's (MatcmsStoreService sets it again after a take-over)
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
        row.Origin = ""; row.OriginVersion = "";   // an own save makes it the operator's (MatcmsStoreService sets it again after a take-over)
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
        // The publisher is the store's to say; the bundle may only suggest one while the entry has none.
        if (row.Publisher.Length == 0 && ReadBundlePublisher(bundle) is { } pub && CleanPublisher(pub.Name, pub.Url) is { } clean)
            (row.Publisher, row.PublisherUrl) = clean;
        await _db.SaveChangesAsync(ct);
        await TouchAsync(_db.ProfileStorePlugins.Where(x => x.StorePluginId == row.Id).Select(x => x.ProfileId), ct);
        return new PluginResult(isNew, row.Key, row.Name, row.Version);
    }

    /// <summary>Sets (or, with an empty name, clears) the publisher of a store plugin. Not part of the bundle and not rolled out, so no
    /// profile revision moves — the sites read it from the catalogue. False = no such plugin.</summary>
    public async Task<bool> SetPluginPublisherAsync(string key, string? publisher, string? url, CancellationToken ct = default)
    {
        var row = await _db.StorePlugins.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) return false;
        (row.Publisher, row.PublisherUrl) = CleanPublisher(publisher, url) ?? ("", "");
        await _db.SaveChangesAsync(ct);
        return true;
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

    // ---- Plugin pictures (screenshots in the store dialog) -----------------------------
    // Not part of the bundle and not part of a profile: a picture changes nothing a site runs, so no profile is touched.

    /// <summary>At most this many pictures per plugin, each at most this large — the store dialog shows a few, and every site fetches them.</summary>
    public const int MaxPluginImages = 8;
    public const int MaxPluginImageBytes = 3 * 1024 * 1024;

    public sealed record PluginImageInfo(int Id, int Sort, string ContentType, int Size, string Caption);

    /// <summary>
    /// The picture's type from its own first bytes. PNG, JPEG and WebP only: no SVG (it is code, and these are shown in every site's admin),
    /// no GIF. The cloud ships no image library, so the bytes are kept as they came — which is why the type is never taken on trust.
    /// </summary>
    public static string? SniffImage(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    public async Task<List<PluginImageInfo>?> ListPluginImagesAsync(string key, CancellationToken ct = default)
    {
        var id = await _db.StorePlugins.Where(x => x.Key == key).Select(x => (int?)x.Id).FirstOrDefaultAsync(ct);
        if (id is null) return null;
        return await _db.StorePluginImages.AsNoTracking().Where(x => x.StorePluginId == id).OrderBy(x => x.Sort).ThenBy(x => x.Id)
            .Select(x => new PluginImageInfo(x.Id, x.Sort, x.ContentType, x.Data.Length, x.Caption)).ToListAsync(ct);
    }

    public Task<StorePluginImage?> GetPluginImageAsync(int id, CancellationToken ct = default) =>
        _db.StorePluginImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    private static string CleanCaption(string? caption)
    {
        var c = (caption ?? "").Trim();
        return c.Length > 200 ? c[..200] : c;
    }

    /// <summary>Adds a picture at the end of the gallery. Returns the new id, or an error in words.</summary>
    public async Task<(int? id, string? error)> AddPluginImageAsync(string key, byte[] data, string? caption, CancellationToken ct = default)
    {
        var plugin = await _db.StorePlugins.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (plugin is null) return (null, "Plugin nicht gefunden.");
        if (data.Length == 0) return (null, "Die Datei ist leer.");
        if (data.Length > MaxPluginImageBytes) return (null, $"Das Bild ist zu groß (max. {MaxPluginImageBytes / 1024 / 1024} MB).");
        var type = SniffImage(data);
        if (type is null) return (null, "Nur PNG, JPEG oder WebP.");
        var have = await _db.StorePluginImages.Where(x => x.StorePluginId == plugin.Id).Select(x => x.Sort).ToListAsync(ct);
        if (have.Count >= MaxPluginImages) return (null, $"Höchstens {MaxPluginImages} Bilder pro Plugin.");
        var row = new StorePluginImage
        {
            StorePluginId = plugin.Id, Sort = have.Count == 0 ? 0 : have.Max() + 1, ContentType = type, Data = data, Caption = CleanCaption(caption)
        };
        _db.StorePluginImages.Add(row);
        await _db.SaveChangesAsync(ct);
        return (row.Id, null);
    }

    public async Task<bool> DeletePluginImageAsync(string key, int imageId, CancellationToken ct = default)
    {
        var row = await _db.StorePluginImages.FirstOrDefaultAsync(x => x.Id == imageId && x.StorePlugin!.Key == key, ct);
        if (row is null) return false;
        _db.StorePluginImages.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Moves a picture one place earlier (<paramref name="delta"/> &lt; 0) or later (&gt; 0); the gallery is renumbered 0, 1, 2 … on the way.</summary>
    public async Task<bool> MovePluginImageAsync(string key, int imageId, int delta, CancellationToken ct = default)
    {
        var list = await _db.StorePluginImages.Where(x => x.StorePlugin!.Key == key).OrderBy(x => x.Sort).ThenBy(x => x.Id).ToListAsync(ct);
        var i = list.FindIndex(x => x.Id == imageId);
        if (i < 0) return false;
        var j = Math.Clamp(i + Math.Sign(delta), 0, list.Count - 1);
        (list[i], list[j]) = (list[j], list[i]);
        for (var n = 0; n < list.Count; n++) list[n].Sort = n;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetPluginImageCaptionAsync(string key, int imageId, string? caption, CancellationToken ct = default)
    {
        var row = await _db.StorePluginImages.FirstOrDefaultAsync(x => x.Id == imageId && x.StorePlugin!.Key == key, ct);
        if (row is null) return false;
        row.Caption = CleanCaption(caption);
        await _db.SaveChangesAsync(ct);
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
            .Select(p => new StorePluginRow(p.Key, p.Name, p.Version, p.Description, p.Bundle.Length, _db.ProfileStorePlugins.Count(x => x.StorePluginId == p.Id), null, p.Publisher, p.PublisherUrl))
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
    public async Task<StoreCatalog> CatalogAsync()
    {
        // The pictures' ids and captions only — never their bytes, which a site fetches one by one when its dialog shows them.
        var images = (await _db.StorePluginImages.AsNoTracking().OrderBy(x => x.Sort).ThenBy(x => x.Id)
                .Select(x => new { x.StorePluginId, x.Id, x.Caption }).ToListAsync())
            .ToLookup(x => x.StorePluginId, x => new CatalogImage { Id = x.Id, Caption = x.Caption });
        var plugins = await _db.StorePlugins.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new { p.Id, Plugin = new CatalogPlugin { Key = p.Key, Name = p.Name, Version = p.Version, Description = p.Description, Publisher = p.Publisher, PublisherUrl = p.PublisherUrl, Origin = p.Origin, Signature = p.Signature, SignatureKeyId = p.SignatureKeyId } })
            .ToListAsync();
        foreach (var p in plugins) p.Plugin.Images = images[p.Id].ToList();
        return await CatalogRestAsync(plugins.Select(p => p.Plugin).ToList());
    }

    private async Task<StoreCatalog> CatalogRestAsync(List<CatalogPlugin> plugins) => new StoreCatalog
    {
        Plugins = plugins,
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
        var pictures = (await _db.StorePluginImages.AsNoTracking().OrderBy(x => x.Sort).ThenBy(x => x.Id)
                .Select(x => new { x.StorePluginId, x.Id, x.Caption }).ToListAsync(ct))
            .ToLookup(x => x.StorePluginId);
        foreach (var p in await _db.StorePlugins.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct))
        {
            var native = NativeBundleInfo.Read(p.Bundle);
            // Pictures by their PUBLIC address (relative to the cloud): a website's showcase passes them on through its own route,
            // so a visitor's browser never has to talk to the cloud.
            var images = new System.Text.Json.Nodes.JsonArray();
            foreach (var img in pictures[p.Id])
                images.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = img.Id, ["caption"] = img.Caption, ["url"] = PublicImagePath(p.Key, img.Id) });
            plugins.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = p.Key, ["name"] = p.Name, ["version"] = p.Version, ["description"] = p.Description,
                ["kind"] = native is null ? "script" : "native", ["api"] = native?.Api ?? "", ["updated"] = p.UploadedAt.ToString("yyyy-MM-dd"),
                ["publisher"] = p.Publisher, ["publisherUrl"] = p.PublisherUrl, ["images"] = images
            });
        }
        // A template's LOOK (colours, fonts, button corners) — what a showcase needs to draw a small picture of a page in it. Public
        // anyway: every site using the template shows exactly these values. Never its layout, CSS or script.
        var templates = new System.Text.Json.Nodes.JsonArray();
        foreach (var t in await _db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name)
                     .Select(t => new
                     {
                         t.Name, t.Description, t.AccentColor, t.SecondaryColor, t.HeadingColor, t.TextColor, t.BackgroundColor, t.AltBackground,
                         t.HeaderBackground, t.HeaderTextColor, t.HeadingFont, t.BodyFont, t.ButtonStyle, t.ButtonRadius
                     }).ToListAsync(ct))
            templates.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["accent"] = t.AccentColor, ["secondary"] = t.SecondaryColor,
                ["heading"] = t.HeadingColor, ["text"] = t.TextColor, ["background"] = t.BackgroundColor, ["alt"] = t.AltBackground,
                ["headerBg"] = t.HeaderBackground, ["headerText"] = t.HeaderTextColor, ["headingFont"] = t.HeadingFont, ["bodyFont"] = t.BodyFont,
                ["buttonStyle"] = t.ButtonStyle, ["buttonRadius"] = t.ButtonRadius
            });
        var components = new System.Text.Json.Nodes.JsonArray();
        foreach (var c in await _db.StoreComponents.AsNoTracking().OrderBy(c => c.Name).Select(c => new { c.Type, c.Name, c.Description, c.Icon }).ToListAsync(ct))
            components.Add(new System.Text.Json.Nodes.JsonObject { ["type"] = c.Type, ["name"] = c.Name, ["description"] = c.Description, ["icon"] = c.Icon });
        return new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = 1, ["generated"] = DateTime.UtcNow.ToString("O"), ["plugins"] = plugins, ["templates"] = templates, ["components"] = components
        };
    }

    /// <summary>Where the public may fetch one picture of a store plugin (see <c>GET /api/public/store/plugins/{key}/image/{id}</c>).</summary>
    public static string PublicImagePath(string key, int id) => $"/api/public/store/plugins/{Uri.EscapeDataString(key)}/image/{id}";

    /// <summary>One picture for the PUBLIC: only when it belongs to the named plugin (an id alone must not walk through every picture).</summary>
    public Task<StorePluginImage?> GetPublicPluginImageAsync(string key, int id, CancellationToken ct = default) =>
        _db.StorePluginImages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.StorePlugin!.Key == key, ct);

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
            .Select(p => new StorePluginRow(p.Key, p.Name, p.Version, p.Description, p.Bundle.Length, null, p.UploadedAt, p.Publisher, p.PublisherUrl)).ToListAsync(ct);

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

    /// <summary>The publisher a bundle's manifest suggests (<c>Publisher</c>, <c>PublisherUrl</c> — plain strings like every manifest field the
    /// cloud reads), or null when it names none or cannot be read.</summary>
    public static (string Name, string Url)? ReadBundlePublisher(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            if (zip.GetEntry(PluginBundle.ManifestEntry) is not { } entry) return null;
            using var r = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(r.ReadToEnd());
            string Get(string p) => doc.RootElement.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = Get("Publisher");
            return name.Trim().Length == 0 ? null : (name, Get("PublisherUrl"));
        }
        catch { return null; }
    }

    public const int MaxPublisherLength = 80;

    /// <summary>A publisher as it may be stored: the name trimmed, without line breaks, at most <see cref="MaxPublisherLength"/>; the address
    /// only as an absolute http(s) URL (anything else is dropped — it is rendered as a link on public websites). Null = the name is empty.</summary>
    public static (string Name, string Url)? CleanPublisher(string? name, string? url)
    {
        var n = string.Join(' ', (name ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (n.Length == 0) return null;
        if (n.Length > MaxPublisherLength) n = n[..MaxPublisherLength].TrimEnd();
        var u = (url ?? "").Trim();
        u = u.Length <= 300 && Uri.TryCreate(u, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            ? parsed.ToString() : "";
        return (n, u);
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? UploadedAt,
    string Publisher = "", string PublisherUrl = "");
public record StoreTemplateRow(string Name, string Description, string AccentColor, string HeadingFont, string BodyFont,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy);
public record StoreComponentRow(string Type, string Name, string Description, string Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy);
public record StoreMailTemplateRow(string Key, string Name, string Subject, bool Enabled, bool IsHtml,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UsedBy);
public record StoreOverview(List<StorePluginRow> Plugins, List<StoreTemplateRow> Templates, List<StoreComponentRow> Components, List<StoreMailTemplateRow> MailTemplates);
