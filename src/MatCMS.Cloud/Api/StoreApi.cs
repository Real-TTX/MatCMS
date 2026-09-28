using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Api;

/// <summary>
/// The store-management surface of the operator API (<c>/api/v1/store</c>): the cloud-wide catalogue of
/// templates, plugins, components and mail-templates that profiles select from and instances browse. All
/// writes go through <see cref="StoreService"/> (shared with the MCP <c>StoreTools</c>), which maps the
/// input and — crucially — bumps every profile that SELECTED the changed entry, so a store edit actually
/// reaches the instances. Reads need only a valid key; writes need <see cref="ApiKey.CanManageStore"/>.
/// The store is cloud-wide and carries no per-instance scope, so these endpoints ignore the key's list.
/// <para>Plugin bundles travel as the raw ZIP exactly as <c>ProfileApi</c> handles them.</para>
/// </summary>
public static class StoreApi
{
    // ---- auth helpers ----
    private static async Task<(ApiKey? key, IResult? error)> CallerAsync(HttpContext ctx, ApiKeyService keys)
    {
        var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        return key is null
            ? (null, Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized))
            : (key, null);
    }

    private static IResult? RequireStore(ApiKey key) =>
        key.CanManageStore ? null
            : Results.Json(new { error = "Dieser Schlüssel darf den Store nicht verwalten." }, statusCode: StatusCodes.Status403Forbidden);

    public static void MapStoreApi(this WebApplication app)
    {
        // ---- Store overview -------------------------------------------------------
        app.MapGet("/api/v1/store", async (HttpContext ctx, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;

            var plugins = await db.StorePlugins.AsNoTracking().OrderBy(p => p.Name).Select(p => new
            {
                p.Key, p.Name, p.Version, p.Description, sizeBytes = p.Bundle.Length,
                usedBy = db.ProfileStorePlugins.Count(x => x.StorePluginId == p.Id)
            }).ToListAsync();
            var templates = await db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => new
            {
                t.Name, t.Description, t.AccentColor, t.HeadingFont, t.BodyFont,
                usedBy = db.ProfileStoreTemplates.Count(x => x.StoreTemplateId == t.Id)
            }).ToListAsync();
            var components = await db.StoreComponents.AsNoTracking().OrderBy(c => c.Name).Select(c => new
            {
                c.Type, c.Name, c.Description, c.Icon,
                usedBy = db.ProfileStoreComponents.Count(x => x.StoreComponentId == c.Id)
            }).ToListAsync();
            var mailTemplates = await db.StoreMailTemplates.AsNoTracking().OrderBy(m => m.Key).Select(m => new
            {
                m.Key, m.Name, m.Subject, m.Enabled, m.IsHtml,
                usedBy = db.ProfileStoreMailTemplates.Count(x => x.StoreMailTemplateId == m.Id)
            }).ToListAsync();

            return Results.Ok(new { canManageStore = key!.CanManageStore, plugins, templates, components, mailTemplates });
        }).RequireRateLimiting("operatorApi");

        // ---- Templates (identity = Name) ------------------------------------------
        app.MapGet("/api/v1/store/templates", async (HttpContext ctx, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await db.StoreTemplates.AsNoTracking().OrderBy(t => t.Name)
                .Select(t => new { t.Name, t.Description, t.AccentColor, t.HeadingFont, t.BodyFont }).ToListAsync();
            return Results.Ok(new { templates = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/templates/{name}", async (HttpContext ctx, string name, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var t = await db.StoreTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name);
            return t is null ? NotFound("Template") : Results.Ok(t);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/store/templates", async (HttpContext ctx, ApiKeyService keys, StoreService store, StoreTemplateInput b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            if (string.IsNullOrWhiteSpace(b.Name)) return BadReq("Name ist erforderlich.");
            foreach (var (label, json) in new[] { ("MenuMapJson", b.MenuMapJson), ("ParametersJson", b.ParametersJson), ("ParamValuesJson", b.ParamValuesJson), ("PartsJson", b.PartsJson) })
                if (json is { Length: > 0 } && !IsValidJson(json)) return BadReq($"{label} ist kein gültiges JSON.");
            var created = await store.UpsertTemplateAsync(b);
            return Results.Ok(new { ok = true, created, name = b.Name.Trim() });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/store/templates/{name}", async (HttpContext ctx, string name, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            return await store.DeleteTemplateAsync(name) ? Results.Ok(new { ok = true }) : NotFound("Template");
        }).RequireRateLimiting("operatorApi");

        // ---- Components (identity = Type) -----------------------------------------
        app.MapGet("/api/v1/store/components", async (HttpContext ctx, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await db.StoreComponents.AsNoTracking().OrderBy(c => c.Name)
                .Select(c => new { c.Type, c.Name, c.Description, c.Icon }).ToListAsync();
            return Results.Ok(new { components = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/components/{type}", async (HttpContext ctx, string type, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var c = await db.StoreComponents.AsNoTracking().FirstOrDefaultAsync(x => x.Type == type.ToLowerInvariant());
            return c is null ? NotFound("Komponente") : Results.Ok(c);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/store/components", async (HttpContext ctx, ApiKeyService keys, StoreService store, StoreComponentInput b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            if (string.IsNullOrWhiteSpace(b.Type) || string.IsNullOrWhiteSpace(b.Name)) return BadReq("Type und Name sind erforderlich.");
            if (!string.IsNullOrWhiteSpace(b.FieldsJson) && !IsValidJson(b.FieldsJson!)) return BadReq("FieldsJson ist kein gültiges JSON.");
            var created = await store.UpsertComponentAsync(b);
            return Results.Ok(new { ok = true, created, type = b.Type.Trim().ToLowerInvariant() });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/store/components/{type}", async (HttpContext ctx, string type, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            return await store.DeleteComponentAsync(type) ? Results.Ok(new { ok = true }) : NotFound("Komponente");
        }).RequireRateLimiting("operatorApi");

        // ---- Mail templates (identity = Key) --------------------------------------
        app.MapGet("/api/v1/store/mail-templates", async (HttpContext ctx, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await db.StoreMailTemplates.AsNoTracking().OrderBy(m => m.Key)
                .Select(m => new { m.Key, m.Name, m.Subject, m.Enabled, m.IsHtml }).ToListAsync();
            return Results.Ok(new { mailTemplates = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/mail-templates/{key}", async (HttpContext ctx, string key, ApiKeyService keys, AppDbContext db) =>
        {
            var (k, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var m = await db.StoreMailTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
            return m is null ? NotFound("Mail-Template") : Results.Ok(m);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/store/mail-templates", async (HttpContext ctx, ApiKeyService keys, StoreService store, StoreMailTemplateInput b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            if (string.IsNullOrWhiteSpace(b.Key) || string.IsNullOrWhiteSpace(b.Subject)) return BadReq("Key und Subject sind erforderlich.");
            var created = await store.UpsertMailTemplateAsync(b);
            return Results.Ok(new { ok = true, created, key = b.Key.Trim() });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/store/mail-templates/{key}", async (HttpContext ctx, string key, ApiKeyService keys, StoreService store) =>
        {
            var (k, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(k!) is { } g) return g;
            return await store.DeleteMailTemplateAsync(key) ? Results.Ok(new { ok = true }) : NotFound("Mail-Template");
        }).RequireRateLimiting("operatorApi");

        // ---- Plugins (raw ZIP bundle, identity = Key) -----------------------------
        app.MapGet("/api/v1/store/plugins", async (HttpContext ctx, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await db.StorePlugins.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new { p.Key, p.Name, p.Version, p.Description, sizeBytes = p.Bundle.Length, p.UploadedAt }).ToListAsync();
            return Results.Ok(new { plugins = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/plugins/{pluginKey}/download", async (HttpContext ctx, string pluginKey, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var row = await db.StorePlugins.AsNoTracking().FirstOrDefaultAsync(x => x.Key == pluginKey);
            if (row is null) return NotFound("Plugin");
            return Results.File(row.Bundle, "application/zip", $"{row.Key}.zip");
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/store/plugins", async (HttpContext ctx, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;

            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = 64L * 1024 * 1024;
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return BadReq("Kein Bundle im Body.");

            var res = await store.UpsertPluginAsync(bytes, ctx.RequestAborted);
            if (res is null) return BadReq("Ungültiges Plugin-Bundle (kein gültiges plugin.json / kein Key).");
            return Results.Ok(new { ok = true, created = res.Created, key = res.Key, name = res.Name, version = res.Version });
        }).RequireRateLimiting("operatorApi").DisableAntiforgery();

        app.MapDelete("/api/v1/store/plugins/{pluginKey}", async (HttpContext ctx, string pluginKey, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            return await store.DeletePluginAsync(pluginKey) ? Results.Ok(new { ok = true }) : NotFound("Plugin");
        }).RequireRateLimiting("operatorApi");
    }

    // ---- helpers ----
    private static IResult BadReq(string msg) => Results.Json(new { error = msg }, statusCode: StatusCodes.Status400BadRequest);
    private static IResult NotFound(string what) => Results.Json(new { error = $"{what} nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);

    private static bool IsValidJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; } catch { return false; }
    }
}
