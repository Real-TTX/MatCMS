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
/// The profile-management surface of the operator API (<c>/api/v1/profiles</c> and the
/// instance↔profile assignment endpoints). It mirrors the admin Profile pages field for field and goes
/// through the SAME services (<see cref="ProfileService"/>, <see cref="AuthService"/>,
/// <see cref="SecretProtector"/>), so behaviour is identical: every write bumps
/// <see cref="ProfileService.TouchAsync"/> (the sync trigger), user passwords are hashed and never
/// stored in clear, SMTP/translation secrets are DataProtection-encrypted, and free settings refuse a
/// group key. Reads need only a valid key; writes need <see cref="ApiKey.CanManageProfiles"/>;
/// assigning an instance additionally honours the key's instance scope.
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

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static SyncMode ParseMode(string? v, SyncMode fallback) => v?.Trim().ToLowerInvariant() switch
    {
        "keep" => SyncMode.Keep,
        "add" => SyncMode.Add,
        "once" => SyncMode.Once,
        _ => fallback
    };

    public static void MapProfileApi(this WebApplication app)
    {
        // ---- Profiles: list + read ------------------------------------------------
        app.MapGet("/api/v1/profiles", async (HttpContext ctx, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var list = await db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new
            {
                id = p.Id,
                name = p.Name,
                description = p.Description,
                isDefault = p.IsDefault,
                revision = p.Revision,
                joinCode = p.JoinCode,
                instanceCount = db.Instances.Count(i => i.ProfileId == p.Id)
            }).ToListAsync();
            return Results.Ok(new { canManageProfiles = key!.CanManageProfiles, profiles = list });
        }).RequireRateLimiting("operatorApi");

        app.MapGet("/api/v1/profiles/{id:int}", async (HttpContext ctx, int id, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var p = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();

            var settings = await db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == id)
                .Select(s => new { s.Key, value = s.IsSecret ? "***" : s.Value, s.IsSecret }).ToListAsync();
            var users = await db.ProfileUsers.AsNoTracking().Where(u => u.ProfileId == id)
                .Select(u => new { u.Username, u.Email, u.DisplayName, u.Role }).ToListAsync();
            var plugins = await db.ProfilePlugins.AsNoTracking().Where(x => x.ProfileId == id)
                .Select(x => new { x.Key, x.Name, x.Version, x.Description, sizeBytes = x.Bundle.Length }).ToListAsync();
            var components = await db.ProfileComponents.AsNoTracking().Where(x => x.ProfileId == id)
                .Select(x => new { x.Type, x.Name, x.Description, x.Icon }).ToListAsync();
            var templates = await db.ProfileTemplates.AsNoTracking().Where(x => x.ProfileId == id)
                .Select(x => new { x.Name, x.AccentColor, x.HeadingFont, x.BodyFont }).ToListAsync();
            var mailTemplates = await db.ProfileMailTemplates.AsNoTracking().Where(x => x.ProfileId == id)
                .Select(x => new { x.Key, x.Name, x.Subject, x.Enabled, x.IsHtml }).ToListAsync();
            var instances = await db.Instances.AsNoTracking().Where(i => i.ProfileId == id)
                .Select(i => new { i.PublicId, i.Name, appliedRevision = i.AppliedRevision }).ToListAsync();

            return Results.Ok(new
            {
                p.Id, p.Name, p.Description, p.JoinCode, p.AutoApprove, p.IsDefault, p.Revision,
                p.ActivateTemplateName,
                sync = new { p.SyncSettings, p.SyncSmtp, p.SyncTranslation, p.SyncBackup, p.SyncUsers, p.SyncPlugins, p.SyncComponents, p.SyncTemplates, p.SyncMailTemplates, p.SyncAi },
                modes = new
                {
                    settings = ProfileService.Wire(p.SettingsMode),
                    users = ProfileService.Wire(p.UsersMode),
                    plugins = ProfileService.Wire(p.PluginsMode),
                    components = ProfileService.Wire(p.ComponentsMode),
                    templates = ProfileService.Wire(p.TemplatesMode),
                    mailTemplates = ProfileService.Wire(p.MailTemplatesMode)
                },
                ai = new { p.SyncAi, p.AiMonthlyTokenBudget, p.AiInstruction, p.BackupBeforeAiChange },
                mailSource = p.MailSource,
                settings, users, plugins, components, templates, mailTemplates, instances
            });
        }).RequireRateLimiting("operatorApi");

        // ---- Profiles: create / update / delete / lifecycle -----------------------
        app.MapPost("/api/v1/profiles", async (HttpContext ctx, ApiKeyService keys, ProfileService profiles, ProfileCreateDto body) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (string.IsNullOrWhiteSpace(body.Name)) return BadReq("Name ist erforderlich.");
            var p = await profiles.CreateAsync(body.Name.Trim(), body.Description);
            return Results.Ok(new { id = p.Id, name = p.Name, joinCode = p.JoinCode });
        }).RequireRateLimiting("operatorApi");

        app.MapPatch("/api/v1/profiles/{id:int}", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db, ProfilePatchDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();

            if (b.Name is not null) p.Name = b.Name.Trim();
            if (b.Description is not null) p.Description = b.Description;
            if (b.AutoApprove is { } aa) p.AutoApprove = aa;
            if (b.AutoUpdateLocal is { } au) p.AutoUpdateLocal = au;
            if (b.NotifyOffline is { } no) p.NotifyOffline = no;
            if (b.NotifyUpdate is { } nu) p.NotifyUpdate = nu;
            if (b.NotifyRecipients is not null) p.NotifyRecipients = b.NotifyRecipients;
            if (b.ActivateTemplateName is not null) p.ActivateTemplateName = b.ActivateTemplateName;
            if (b.SyncSettings is { } ss) p.SyncSettings = ss;
            if (b.SyncUsers is { } su) p.SyncUsers = su;
            if (b.SyncPlugins is { } sp) p.SyncPlugins = sp;
            if (b.SyncComponents is { } sc) p.SyncComponents = sc;
            if (b.SyncTemplates is { } st) p.SyncTemplates = st;
            if (b.SyncMailTemplates is { } sm) p.SyncMailTemplates = sm;
            if (b.RemoveDefaultAdmin is { } rd) p.RemoveDefaultAdmin = rd;
            if (b.SettingsMode is not null) p.SettingsMode = ParseMode(b.SettingsMode, p.SettingsMode);
            if (b.UsersMode is not null) p.UsersMode = ParseMode(b.UsersMode, p.UsersMode) == SyncMode.Keep ? SyncMode.Add : ParseMode(b.UsersMode, p.UsersMode); // users never "keep"
            if (b.PluginsMode is not null) p.PluginsMode = ParseMode(b.PluginsMode, p.PluginsMode);
            if (b.ComponentsMode is not null) p.ComponentsMode = ParseMode(b.ComponentsMode, p.ComponentsMode);
            if (b.TemplatesMode is not null) p.TemplatesMode = ParseMode(b.TemplatesMode, p.TemplatesMode);
            if (b.MailTemplatesMode is not null) p.MailTemplatesMode = ParseMode(b.MailTemplatesMode, p.MailTemplatesMode);

            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/profiles/{id:int}/ai", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db, ProfileAiDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            if (b.SyncAi is { } s) p.SyncAi = s;
            if (b.AiMonthlyTokenBudget is { } tb) p.AiMonthlyTokenBudget = tb <= 0 ? null : tb;
            if (b.AiInstruction is not null) p.AiInstruction = b.AiInstruction;
            if (b.BackupBeforeAiChange is { } bc) p.BackupBeforeAiChange = bc;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}", async (HttpContext ctx, int id, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            // Detach any instances first so they fall back to global policy rather than a dangling id.
            await db.Instances.Where(i => i.ProfileId == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ProfileId, (int?)null));
            db.Profiles.Remove(p);
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/duplicate", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            var copy = await profiles.DuplicateAsync(id);
            return Results.Ok(new { id = copy.Id, name = copy.Name, joinCode = copy.JoinCode });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/make-default", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            await profiles.SetDefaultAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/rotate-join-code", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            var code = await profiles.RotateJoinCodeAsync(p);
            return Results.Ok(new { joinCode = code });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/profiles/{id:int}/touch", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- Free settings --------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/settings", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db, SettingDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            if (string.IsNullOrWhiteSpace(b.Key)) return BadReq("Key ist erforderlich.");
            var k = b.Key.Trim();
            if (ProfileService.IsGroupKey(k))
                return BadReq("Dieser Schlüssel gehört zu einer Gruppe (SMTP/Übersetzung/Backup/KI) und wird über den jeweiligen Gruppen-Endpunkt gesetzt.");
            var row = await db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == id && s.Key == k);
            if (row is null) { row = new ProfileSetting { ProfileId = id, Key = k }; db.ProfileSettings.Add(row); }
            row.Value = b.Value;
            row.IsSecret = false;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/settings/{key}", async (HttpContext ctx, int id, string key, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (k, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(k!) is { } g) return g;
            var row = await db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == id && s.Key == key);
            if (row is null) return Results.NotFound();
            db.ProfileSettings.Remove(row);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- Users (add-only on instances; hashed here) ---------------------------
        app.MapPost("/api/v1/profiles/{id:int}/users", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AuthService auth, AppDbContext db, ProfileUserDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            if (string.IsNullOrWhiteSpace(b.Username)) return BadReq("Username ist erforderlich.");
            var uname = b.Username.Trim();
            var row = await db.ProfileUsers.FirstOrDefaultAsync(u => u.ProfileId == id && u.Username == uname);
            var isNew = row is null;
            if (isNew) { row = new ProfileUser { ProfileId = id, Username = uname }; db.ProfileUsers.Add(row); }
            // Accept a ready-made hash (import) or a plaintext password to hash here; never store plaintext.
            if (!string.IsNullOrWhiteSpace(b.PasswordHash)) row!.PasswordHash = b.PasswordHash!;
            else if (!string.IsNullOrWhiteSpace(b.Password)) row!.PasswordHash = auth.HashPassword(b.Password!);
            else if (isNew) return BadReq("Für einen neuen Benutzer ist ein Passwort (oder passwordHash) erforderlich.");
            row!.Email = b.Email ?? row.Email;
            row.DisplayName = b.DisplayName ?? row.DisplayName;
            row.Role = string.IsNullOrWhiteSpace(b.Role) ? (row.Role ?? "Admin") : b.Role!.Trim();
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true, created = isNew });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/users/{username}", async (HttpContext ctx, int id, string username, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var row = await db.ProfileUsers.FirstOrDefaultAsync(u => u.ProfileId == id && u.Username == username);
            if (row is null) return Results.NotFound();
            db.ProfileUsers.Remove(row);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- Components -----------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/components", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db, ComponentDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            if (string.IsNullOrWhiteSpace(b.Type) || string.IsNullOrWhiteSpace(b.Name)) return BadReq("Type und Name sind erforderlich.");
            if (!string.IsNullOrWhiteSpace(b.FieldsJson) && !IsValidJson(b.FieldsJson!)) return BadReq("FieldsJson ist kein gültiges JSON.");
            var type = b.Type.Trim().ToLowerInvariant();
            var row = await db.ProfileComponents.FirstOrDefaultAsync(c => c.ProfileId == id && c.Type == type);
            if (row is null) { row = new ProfileComponent { ProfileId = id, Type = type }; db.ProfileComponents.Add(row); }
            row.Name = b.Name.Trim();
            row.Description = b.Description;
            row.Icon = b.Icon;
            row.FieldsJson = b.FieldsJson ?? row.FieldsJson;
            row.TemplateHtml = b.TemplateHtml ?? row.TemplateHtml;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/components/{type}", async (HttpContext ctx, int id, string type, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var row = await db.ProfileComponents.FirstOrDefaultAsync(c => c.ProfileId == id && c.Type == type.ToLowerInvariant());
            if (row is null) return Results.NotFound();
            db.ProfileComponents.Remove(row);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- Templates ------------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/templates", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db, TemplateDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            if (string.IsNullOrWhiteSpace(b.Name)) return BadReq("Name ist erforderlich.");
            if (b.MenuMapJson is not null && b.MenuMapJson.Length > 0 && !IsValidJson(b.MenuMapJson)) return BadReq("MenuMapJson ist kein gültiges JSON.");
            if (b.ParametersJson is not null && b.ParametersJson.Length > 0 && !IsValidJson(b.ParametersJson)) return BadReq("ParametersJson ist kein gültiges JSON.");
            var name = b.Name.Trim();
            var row = await db.ProfileTemplates.FirstOrDefaultAsync(t => t.ProfileId == id && t.Name == name);
            if (row is null) { row = new ProfileTemplate { ProfileId = id, Name = name }; db.ProfileTemplates.Add(row); }
            row.AccentColor = b.AccentColor ?? row.AccentColor;
            row.SecondaryColor = b.SecondaryColor ?? row.SecondaryColor;
            row.HeadingFont = b.HeadingFont ?? row.HeadingFont;
            row.BodyFont = b.BodyFont ?? row.BodyFont;
            row.ButtonStyle = b.ButtonStyle ?? row.ButtonStyle;
            row.HeadingColor = b.HeadingColor ?? row.HeadingColor;
            row.TextColor = b.TextColor ?? row.TextColor;
            row.BackgroundColor = b.BackgroundColor ?? row.BackgroundColor;
            row.CustomCss = b.CustomCss ?? row.CustomCss;
            row.CustomJs = b.CustomJs ?? row.CustomJs;
            row.LayoutHtml = b.LayoutHtml ?? row.LayoutHtml;
            row.MenuMapJson = b.MenuMapJson ?? row.MenuMapJson;
            row.ParametersJson = b.ParametersJson ?? row.ParametersJson;
            row.ParamValuesJson = b.ParamValuesJson ?? row.ParamValuesJson;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/templates/{name}", async (HttpContext ctx, int id, string name, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var row = await db.ProfileTemplates.FirstOrDefaultAsync(t => t.ProfileId == id && t.Name == name);
            if (row is null) return Results.NotFound();
            db.ProfileTemplates.Remove(row);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- Mail templates -------------------------------------------------------
        app.MapPost("/api/v1/profiles/{id:int}/mail-templates", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db, MailTemplateDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            if (!await db.Profiles.AnyAsync(x => x.Id == id)) return NotFoundProfile();
            if (string.IsNullOrWhiteSpace(b.Key) || string.IsNullOrWhiteSpace(b.Subject)) return BadReq("Key und Subject sind erforderlich.");
            var mk = b.Key.Trim();
            var row = await db.ProfileMailTemplates.FirstOrDefaultAsync(m => m.ProfileId == id && m.Key == mk);
            if (row is null) { row = new ProfileMailTemplate { ProfileId = id, Key = mk }; db.ProfileMailTemplates.Add(row); }
            row.Name = b.Name ?? row.Name;
            row.Description = b.Description ?? row.Description;
            row.Subject = b.Subject;
            row.Body = b.Body ?? row.Body;
            row.Enabled = b.Enabled ?? row.Enabled;
            row.IsHtml = b.IsHtml ?? row.IsHtml;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/mail-templates/{key}", async (HttpContext ctx, int id, string key, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (k, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(k!) is { } g) return g;
            var row = await db.ProfileMailTemplates.FirstOrDefaultAsync(m => m.ProfileId == id && m.Key == key);
            if (row is null) return Results.NotFound();
            db.ProfileMailTemplates.Remove(row);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- SMTP / Translation groups -------------------------------------------
        app.MapPut("/api/v1/profiles/{id:int}/smtp", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, SecretProtector secrets, AppDbContext db, SmtpDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            p.SyncSmtp = true;
            p.MailSource = MailSources.Normalise(b.MailSource);
            if (p.MailSource == MailSources.Own)
            {
                await UpsertSetting(db, id, "smtp.host", b.Host);
                await UpsertSetting(db, id, "smtp.port", b.Port?.ToString());
                await UpsertSetting(db, id, "smtp.user", b.User);
                await UpsertSetting(db, id, "smtp.fromEmail", b.FromEmail);
                await UpsertSetting(db, id, "smtp.fromName", b.FromName);
                await UpsertSetting(db, id, "smtp.ssl", (b.Ssl ?? false) ? "true" : "false");
                if (!string.IsNullOrWhiteSpace(b.Password))
                    await UpsertSetting(db, id, "smtp.password", secrets.Protect(b.Password), secret: true);
            }
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/smtp", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            p.SyncSmtp = false; // stored values survive; only the rollout stops
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/profiles/{id:int}/translation", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, SecretProtector secrets, AppDbContext db, TranslationDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            p.SyncTranslation = true;
            if (b.Provider is not null) await UpsertSetting(db, id, "translate.provider", b.Provider);
            if (b.Url is not null) await UpsertSetting(db, id, "translate.url", b.Url);
            if (!string.IsNullOrWhiteSpace(b.ApiKey)) await UpsertSetting(db, id, "translate.apiKey", secrets.Protect(b.ApiKey), secret: true);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/profiles/{id:int}/translation", async (HttpContext ctx, int id, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return NotFoundProfile();
            p.SyncTranslation = false;
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
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

        app.MapDelete("/api/v1/profiles/{id:int}/plugins/{pluginKey}", async (HttpContext ctx, int id, string pluginKey, ApiKeyService keys, ProfileService profiles, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var row = await db.ProfilePlugins.FirstOrDefaultAsync(x => x.ProfileId == id && x.Key == pluginKey);
            if (row is null) return Results.NotFound();
            db.ProfilePlugins.Remove(row);
            await db.SaveChangesAsync();
            await profiles.TouchAsync(id);
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        // ---- Instance ↔ profile assignment + sync --------------------------------
        app.MapGet("/api/v1/instances/{publicId}/profile", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            var inst = await db.Instances.AsNoTracking().FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            Profile? prof = inst.ProfileId is int pid ? await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid) : null;
            return Results.Ok(new
            {
                profileId = inst.ProfileId,
                profileName = prof?.Name,
                appliedRevision = inst.AppliedRevision,
                profileRevision = prof?.Revision,
                inSync = prof != null && inst.AppliedRevision == prof.Revision,
                lastSyncError = inst.LastSyncError
            });
        }).RequireRateLimiting("operatorApi");

        app.MapPut("/api/v1/instances/{publicId}/profile", async (HttpContext ctx, string publicId, ApiKeyService keys, InstanceService instances, AppDbContext db, AssignDto b) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            if (b.ProfileId is int pid && !await db.Profiles.AnyAsync(p => p.Id == pid)) return NotFoundProfile();
            inst.ProfileId = b.ProfileId;
            // Force a re-pull even if revision numbers coincide (mirrors the admin assignment handler).
            inst.AppliedRevision = 0;
            inst.LastSyncError = null;
            instances.Log(inst, InstanceEventKind.SyncApplied, "Profil über die API zugewiesen.");
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapDelete("/api/v1/instances/{publicId}/profile", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            inst.ProfileId = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("operatorApi");

        app.MapPost("/api/v1/instances/{publicId}/sync", async (HttpContext ctx, string publicId, ApiKeyService keys, AppDbContext db) =>
        {
            var (key, error) = await CallerAsync(ctx, keys);
            if (error is not null) return error;
            if (RequireManage(key!) is { } g) return g;
            var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == publicId);
            if (inst is null || !ApiKeyService.CanAccess(key!, inst)) return NotFoundInstance();
            // No cloud-side apply exists — force the instance to re-pull /config on its next heartbeat.
            inst.AppliedRevision = 0;
            inst.LastSyncError = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { ok = true, message = "Neu-Synchronisierung vorgemerkt — die Instanz zieht die Konfiguration beim nächsten Kontakt." });
        }).RequireRateLimiting("operatorApi");
    }

    // ---- helpers ----
    private static IResult BadReq(string msg) => Results.Json(new { error = msg }, statusCode: StatusCodes.Status400BadRequest);
    private static IResult NotFoundProfile() => Results.Json(new { error = "Profil nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);
    private static IResult NotFoundInstance() => Results.Json(new { error = "Instanz nicht gefunden." }, statusCode: StatusCodes.Status404NotFound);

    private static bool IsValidJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; } catch { return false; }
    }

    private static async Task UpsertSetting(AppDbContext db, int profileId, string key, string? value, bool secret = false)
    {
        var row = await db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Key == key);
        if (row is null) { row = new ProfileSetting { ProfileId = profileId, Key = key }; db.ProfileSettings.Add(row); }
        row.Value = value;
        row.IsSecret = secret;
    }

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
    public record ProfilePatchDto(string? Name, string? Description, bool? AutoApprove, bool? AutoUpdateLocal,
        bool? NotifyOffline, bool? NotifyUpdate, string? NotifyRecipients, string? ActivateTemplateName,
        bool? SyncSettings, bool? SyncUsers, bool? SyncPlugins, bool? SyncComponents, bool? SyncTemplates, bool? SyncMailTemplates,
        bool? RemoveDefaultAdmin, string? SettingsMode, string? UsersMode, string? PluginsMode, string? ComponentsMode,
        string? TemplatesMode, string? MailTemplatesMode);
    public record ProfileAiDto(bool? SyncAi, int? AiMonthlyTokenBudget, string? AiInstruction, bool? BackupBeforeAiChange);
    public record SettingDto(string Key, string? Value);
    public record ProfileUserDto(string Username, string? Email, string? DisplayName, string? Password, string? PasswordHash, string? Role);
    public record ComponentDto(string Type, string Name, string? Description, string? Icon, string? FieldsJson, string? TemplateHtml);
    public record TemplateDto(string Name, string? AccentColor, string? SecondaryColor, string? HeadingFont, string? BodyFont,
        string? ButtonStyle, string? HeadingColor, string? TextColor, string? BackgroundColor, string? CustomCss, string? CustomJs,
        string? LayoutHtml, string? MenuMapJson, string? ParametersJson, string? ParamValuesJson);
    public record MailTemplateDto(string Key, string? Name, string? Description, string Subject, string? Body, bool? Enabled, bool? IsHtml);
    public record SmtpDto(string? MailSource, string? Host, int? Port, string? User, string? Password, string? FromEmail, string? FromName, bool? Ssl);
    public record TranslationDto(string? Provider, string? ApiKey, string? Url);
    public record AssignDto(int? ProfileId);
}
