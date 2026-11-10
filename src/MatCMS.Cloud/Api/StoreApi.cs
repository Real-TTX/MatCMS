using System.Text.Json;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Http.Features;

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
        app.MapGet("/api/v1/store", async (HttpContext ctx, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var o = await store.OverviewAsync();

            return Results.Ok(new { canManageStore = key!.CanManageStore, plugins = o.Plugins, templates = o.Templates, components = o.Components, mailTemplates = o.MailTemplates });
        }).RequireRateLimiting("operatorApi");

        // ---- Templates (identity = Name) ------------------------------------------
        app.MapGet("/api/v1/store/templates", async (HttpContext ctx, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await store.ListTemplatesAsync();
            return Results.Ok(new { templates = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/templates/{name}", async (HttpContext ctx, string name, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var t = await store.GetTemplateAsync(name);
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
        app.MapGet("/api/v1/store/components", async (HttpContext ctx, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await store.ListComponentsAsync();
            return Results.Ok(new { components = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/components/{type}", async (HttpContext ctx, string type, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var c = await store.GetComponentAsync(type);
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
        app.MapGet("/api/v1/store/mail-templates", async (HttpContext ctx, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await store.ListMailTemplatesAsync();
            return Results.Ok(new { mailTemplates = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/mail-templates/{key}", async (HttpContext ctx, string key, ApiKeyService keys, StoreService store) =>
        {
            var (k, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var m = await store.GetMailTemplateAsync(key);
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
        app.MapGet("/api/v1/store/plugins", async (HttpContext ctx, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await store.ListPluginsAsync();
            return Results.Ok(new { plugins = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/plugins/{pluginKey}/download", async (HttpContext ctx, string pluginKey, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var row = await store.GetPluginAsync(pluginKey);
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

        // { "publisher": "…", "publisherUrl": "https://…" } — who publishes the plugin; an empty publisher clears it. Not in the bundle.
        app.MapPut("/api/v1/store/plugins/{pluginKey}/publisher", async (HttpContext ctx, string pluginKey, PublisherBody body, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            if (!await store.SetPluginPublisherAsync(pluginKey, body.Publisher, body.PublisherUrl, ctx.RequestAborted)) return NotFound("Plugin");
            var clean = StoreService.CleanPublisher(body.Publisher, body.PublisherUrl);
            return Results.Ok(new { ok = true, publisher = clean?.Name ?? "", publisherUrl = clean?.Url ?? "" });
        }).RequireRateLimiting("operatorApi");

        // ---- a plugin's pictures (the store dialog's gallery). Bytes travel raw, like the bundle — REST only, not MCP. ----
        app.MapGet("/api/v1/store/plugins/{pluginKey}/images", async (HttpContext ctx, string pluginKey, ApiKeyService keys, StoreService store) =>
        {
            var (_, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await store.ListPluginImagesAsync(pluginKey, ctx.RequestAborted);
            return list is null ? NotFound("Plugin") : Results.Ok(new { images = list.Select(i => new { id = i.Id, sort = i.Sort, contentType = i.ContentType, size = i.Size, caption = i.Caption }) });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/store/plugins/{pluginKey}/images/{id:int}", async (HttpContext ctx, string pluginKey, int id, ApiKeyService keys, StoreService store) =>
        {
            var (_, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var img = await store.GetPluginImageAsync(id, ctx.RequestAborted);
            if (img is null || (await store.ListPluginImagesAsync(pluginKey, ctx.RequestAborted))?.Any(i => i.Id == id) != true) return NotFound("Bild");
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(img.Data, img.ContentType);
        }).RequireRateLimiting("operatorApi");

        // POST the raw picture as the body (PNG, JPEG or WebP, ≤ 3 MB); ?caption= optional. Added at the end of the gallery.
        app.MapPost("/api/v1/store/plugins/{pluginKey}/images", async (HttpContext ctx, string pluginKey, string? caption, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > StoreService.MaxPluginImageBytes) return BadReq($"Das Bild ist zu groß (max. {StoreService.MaxPluginImageBytes / 1024 / 1024} MB).");
            }
            var (id, err) = await store.AddPluginImageAsync(pluginKey, ms.ToArray(), caption, ctx.RequestAborted);
            return id is null ? (err == "Plugin nicht gefunden." ? NotFound("Plugin") : BadReq(err!)) : Results.Ok(new { ok = true, id });
        }).RequireRateLimiting("operatorApi").DisableAntiforgery();

        // { "caption": "…" } and/or { "move": -1 | 1 } — one picture's caption or its place in the gallery.
        app.MapPatch("/api/v1/store/plugins/{pluginKey}/images/{id:int}", async (HttpContext ctx, string pluginKey, int id, ImagePatch body, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            var found = true;
            if (body.Caption is not null) found = await store.SetPluginImageCaptionAsync(pluginKey, id, body.Caption, ctx.RequestAborted);
            if (found && body.Move is { } m && m != 0) found = await store.MovePluginImageAsync(pluginKey, id, m, ctx.RequestAborted);
            return found ? Results.Ok(new { ok = true }) : NotFound("Bild");
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/store/plugins/{pluginKey}/images/{id:int}", async (HttpContext ctx, string pluginKey, int id, ApiKeyService keys, StoreService store) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireStore(key!) is { } g) return g;
            return await store.DeletePluginImageAsync(pluginKey, id, ctx.RequestAborted) ? Results.Ok(new { ok = true }) : NotFound("Bild");
        }).RequireRateLimiting("operatorApi");
    }

    private sealed record ImagePatch(string? Caption, int? Move);
    private sealed record PublisherBody(string? Publisher, string? PublisherUrl);

    // ---- helpers ----
    private static IResult BadReq(string msg) => Results.Json(new { error = msg }, statusCode: StatusCodes.Status400BadRequest);
    private static IResult NotFound(string what) => Results.Json(new { error = $"{what} nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);

    private static bool IsValidJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; } catch { return false; }
    }
}
