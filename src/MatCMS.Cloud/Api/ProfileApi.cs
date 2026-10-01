using System.IO.Compression;
using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using MatCMS.Shared;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Api;

/// <summary>
/// The profile-management surface of the operator API (<c>/api/v1/profiles</c> and the instance↔profile
/// assignment endpoints). The work is <see cref="ProfileOpsService"/>, shared with the MCP profile tools; this file
/// is transport only — the key, its rights, the status codes, and the plugin ZIP up/download, which stays REST.
/// Reads need only a valid key; writes need <see cref="ApiKey.CanManageProfiles"/>; an instance call additionally
/// honours the key's instance scope.
/// </summary>
public static class ProfileApi
{
    // ---- auth helpers ----
    private static async Task<(ApiKey? key, IResult? error)> CallerAsync(HttpContext ctx, ApiKeyService keys)
    {
        var key = await keys.AuthenticateAsync(ctx.Request.Headers.Authorization.ToString(), ctx.RequestAborted);
        return key is null
            ? (null, Results.Json(new { error = "Ungültiger oder fehlender API-Schlüssel." }, statusCode: StatusCodes.Status401Unauthorized))
            : (key, null);
    }

    private static IResult? RequireManage(ApiKey key) =>
        key.CanManageProfiles ? null
            : Results.Json(new { error = "Dieser Schlüssel darf keine Profile verwalten." }, statusCode: StatusCodes.Status403Forbidden);

    public static void MapProfileApi(this WebApplication app)
    {
        // ---- Profiles: list + read ------------------------------------------------
        app.MapGet("/api/v1/profiles", async (HttpContext ctx, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            return Results.Ok(new { canManageProfiles = key!.CanManageProfiles, profiles = await ops.ListAsync() });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/profiles/{id:int}", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var r = await ops.GetAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Profiles: create / update / delete / lifecycle -----------------------
        app.MapPost("/api/v1/profiles", async (HttpContext ctx, ProfileCreateDto body, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.CreateAsync(body.Name, body.Description);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPatch("/api/v1/profiles/{id:int}", async (HttpContext ctx, int id, ProfileOpsService.Patch b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.PatchAsync(id, b);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/profiles/{id:int}/ai", async (HttpContext ctx, int id, ProfileAiDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.SetAiAsync(id, b.SyncAi, b.AiMonthlyTokenBudget, b.AiInstruction, b.BackupBeforeAiChange);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeleteAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/duplicate", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DuplicateAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/make-default", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.MakeDefaultAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/rotate-join-code", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.RotateJoinCodeAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/touch", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.TouchAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Recommended settings: what is missing, and add it (existing rows are never changed) ----
        app.MapGet("/api/v1/profiles/{id:int}/settings/recommended", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var r = await ops.RecommendedAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/settings/recommended", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.AddRecommendedAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Free settings --------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/settings", async (HttpContext ctx, int id, SettingDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.SetSettingAsync(id, b.Key, b.Value);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/settings/{settingKey}", async (HttpContext ctx, int id, string settingKey, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeleteSettingAsync(id, settingKey);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Users (add-only on instances; hashed here) ---------------------------
        app.MapPost("/api/v1/profiles/{id:int}/users", async (HttpContext ctx, int id, ProfileUserDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.UpsertUserAsync(id, b.Username, b.Email, b.DisplayName, b.Password, b.PasswordHash, b.Role);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/users/{username}", async (HttpContext ctx, int id, string username, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeleteUserAsync(id, username);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Components -----------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/components", async (HttpContext ctx, int id, ComponentDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.UpsertComponentAsync(id, b.Type, b.Name, b.Description, b.Icon, b.FieldsJson, b.TemplateHtml);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/components/{type}", async (HttpContext ctx, int id, string type, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeleteComponentAsync(id, type);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Templates ------------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/templates", async (HttpContext ctx, int id, ProfileOpsService.Template b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.UpsertTemplateAsync(id, b);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/templates/{name}", async (HttpContext ctx, int id, string name, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeleteTemplateAsync(id, name);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Mail templates -------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/mail-templates", async (HttpContext ctx, int id, MailTemplateDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.UpsertMailTemplateAsync(id, b.Key, b.Name, b.Description, b.Subject, b.Body, b.Enabled, b.IsHtml);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/mail-templates/{mailKey}", async (HttpContext ctx, int id, string mailKey, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeleteMailTemplateAsync(id, mailKey);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- SMTP / Translation groups -------------------------------------------
        app.MapPut("/api/v1/profiles/{id:int}/smtp", async (HttpContext ctx, int id, SmtpDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.SetSmtpAsync(id, b.MailSource, b.Host, b.Port, b.User, b.Password, b.FromEmail, b.FromName, b.Ssl);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/smtp", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DisableSmtpAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/profiles/{id:int}/translation", async (HttpContext ctx, int id, TranslationDto b, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.SetTranslationAsync(id, b.Provider, b.ApiKey, b.Url);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/translation", async (HttpContext ctx, int id, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DisableTranslationAsync(id);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Plugins (raw ZIP bundle) --------------------------------------------
        app.MapGet("/api/v1/profiles/{id:int}/plugins/{pluginKey}/download", async (HttpContext ctx, int id, string pluginKey, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var row = await db.ProfilePlugins.AsNoTracking().FirstOrDefaultAsync(x => x.ProfileId == id && x.Key == pluginKey);
            if (row is null) return Results.NotFound();
            return Results.File(row.Bundle, "application/zip", $"{row.Key}.zip");
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/plugins", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();

            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = 64L * 1024 * 1024;
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return BadReq("Kein Bundle im Body.");

            var (pk, pname, pver, pdesc, mErr) = ReadBundleMeta(bytes);
            if (mErr is not null) return BadReq(mErr);
            var row = await db.ProfilePlugins.FirstOrDefaultAsync(x => x.ProfileId == id && x.Key == pk);
            if (row is null) { row = new ProfilePlugin { ProfileId = id, Key = pk! }; db.ProfilePlugins.Add(row); }
            row.Name = pname ?? pk!;
            row.Version = pver ?? "";
            row.Description = pdesc;
            row.Bundle = bytes;
            row.UploadedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true, key = pk, name = row.Name, version = row.Version });
        }).RequireRateLimiting("operatorApi").DisableAntiforgery();

        app.MapDelete("/api/v1/profiles/{id:int}/plugins/{pluginKey}", async (HttpContext ctx, int id, string pluginKey, ApiKeyService keys, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var r = await ops.DeletePluginAsync(id, pluginKey);
            return Send(r);
        }).RequireRateLimiting("operatorApi");

        // ---- Instance ↔ profile assignment + sync --------------------------------
        app.MapGet("/api/v1/instances/{publicId}/profile", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return Send(ProfileOpsService.InstanceNotFound);
            return Send(await ops.AssignmentAsync(inst));
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/instances/{publicId}/profile", async (HttpContext ctx, string publicId, AssignDto b, ApiKeyService keys, AppDbContext db, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return Send(ProfileOpsService.InstanceNotFound);
            return Send(await ops.AssignAsync(inst, b.ProfileId));
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/instances/{publicId}/profile", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return Send(ProfileOpsService.InstanceNotFound);
            return Send(await ops.UnassignAsync(inst));
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/instances/{publicId}/sync", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db, ProfileOpsService ops) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return Send(ProfileOpsService.InstanceNotFound);
            return Send(await ops.ResyncAsync(inst));
        }).RequireRateLimiting("operatorApi");
    }

    /// <summary>A service result as HTTP: the data on success, <c>{ error }</c> with its status otherwise.</summary>
    private static IResult Send(ProfileOpsService.Result r) =>
        r.Ok ? Results.Ok(r.Data) : Results.Json(new { error = r.Error }, statusCode: r.Code);

    // ---- helpers ----
    private static IResult BadReq(string msg) => Results.Json(new { error = msg }, statusCode: StatusCodes.Status400BadRequest);
    private static IResult NotFoundProfile() => Results.Json(new { error = "Profil nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);

    // Read a plugin bundle's manifest (plugin.json) enough to key/name/version it. Mirrors the admin
    // ReadBundleMeta: the instance still receives the exact ZIP its own importer expects.
    private static (string? key, string? name, string? version, string? desc, string? error) ReadBundleMeta(byte[] bytes)
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

    // ---- request DTOs ----
    public record ProfileCreateDto(string Name, string? Description);
    public record ProfileAiDto(bool? SyncAi, int? AiMonthlyTokenBudget, string? AiInstruction, bool? BackupBeforeAiChange);
    public record SettingDto(string Key, string? Value);
    public record ProfileUserDto(string Username, string? Email, string? DisplayName, string? Password, string? PasswordHash, string? Role);
    public record ComponentDto(string Type, string Name, string? Description, string? Icon, string? FieldsJson, string? TemplateHtml);
    public record MailTemplateDto(string Key, string? Name, string? Description, string Subject, string? Body, bool? Enabled, bool? IsHtml);
    public record SmtpDto(string? MailSource, string? Host, int? Port, string? User, string? Password, string? FromEmail, string? FromName, bool? Ssl);
    public record TranslationDto(string? Provider, string? ApiKey, string? Url);
    public record AssignDto(int? ProfileId);
}
