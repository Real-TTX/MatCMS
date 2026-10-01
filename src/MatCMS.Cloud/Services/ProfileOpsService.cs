using System.Text.Json;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Profile management for machines — what <c>/api/v1/profiles</c> (Api/ProfileApi.cs) and the MCP profile tools
/// (Mcp/ProfileTools.cs) both call, so the two cannot drift. It mirrors the admin Profile pages and goes through the
/// same services: every write bumps <see cref="ProfileService.TouchAsync"/> (the sync trigger — a change that does
/// not bump the revision never arrives), user passwords are hashed and never stored in clear, SMTP/translation
/// secrets are encrypted, and a free setting refuses a group key. The RIGHTS are checked by the callers (they know
/// the key); this class only does the work and says what went wrong.
/// </summary>
public sealed class ProfileOpsService
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    private readonly AuthService _auth;
    private readonly SecretProtector _secrets;
    private readonly InstanceService _instances;

    public ProfileOpsService(AppDbContext db, ProfileService profiles, AuthService auth, SecretProtector secrets, InstanceService instances)
    {
        _db = db; _profiles = profiles; _auth = auth; _secrets = secrets; _instances = instances;
    }

    /// <summary>The outcome of one operation. <paramref name="Code"/> is the HTTP status REST answers with
    /// (200, 400 bad input, 404 not found); MCP turns a failure into an error message.</summary>
    public sealed record Result(bool Ok, int Code, string? Error, object? Data)
    {
        public static Result Done(object? data = null) => new(true, 200, null, data ?? new { ok = true });
        public static Result Bad(string error) => new(false, 400, error, null);
        public static Result NotFound(string error) => new(false, 404, error, null);
    }

    private static readonly Result NoProfile = Result.NotFound("Profil nicht gefunden.");
    private static readonly Result NoInstance = Result.NotFound("Instanz nicht gefunden.");

    public static SyncMode ParseMode(string? v, SyncMode fallback) => v?.Trim().ToLowerInvariant() switch
    {
        "keep" => SyncMode.Keep,
        "add" => SyncMode.Add,
        "once" => SyncMode.Once,
        _ => fallback
    };

    private static bool IsValidJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; } catch { return false; }
    }

    private Task<bool> ExistsAsync(int id) => _db.Profiles.AnyAsync(x => x.Id == id);

    // ---- read -------------------------------------------------------------------------------------

    public async Task<object> ListAsync() => await _db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new
    {
        id = p.Id,
        name = p.Name,
        description = p.Description,
        isDefault = p.IsDefault,
        revision = p.Revision,
        joinCode = p.JoinCode,
        instanceCount = _db.Instances.Count(i => i.ProfileId == p.Id)
    }).ToListAsync();

    public async Task<Result> GetAsync(int id)
    {
        var p = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        var settings = await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == id)
            .Select(s => new { s.Key, value = s.IsSecret ? "***" : s.Value, s.IsSecret }).ToListAsync();
        var users = await _db.ProfileUsers.AsNoTracking().Where(u => u.ProfileId == id)
            .Select(u => new { u.Username, u.Email, u.DisplayName, u.Role }).ToListAsync();
        var plugins = await _db.ProfilePlugins.AsNoTracking().Where(x => x.ProfileId == id)
            .Select(x => new { x.Key, x.Name, x.Version, x.Description, sizeBytes = x.Bundle.Length }).ToListAsync();
        var components = await _db.ProfileComponents.AsNoTracking().Where(x => x.ProfileId == id)
            .Select(x => new { x.Type, x.Name, x.Description, x.Icon }).ToListAsync();
        var templates = await _db.ProfileTemplates.AsNoTracking().Where(x => x.ProfileId == id)
            .Select(x => new { x.Name, x.AccentColor, x.HeadingFont, x.BodyFont }).ToListAsync();
        var mailTemplates = await _db.ProfileMailTemplates.AsNoTracking().Where(x => x.ProfileId == id)
            .Select(x => new { x.Key, x.Name, x.Subject, x.Enabled, x.IsHtml }).ToListAsync();
        var instances = await _db.Instances.AsNoTracking().Where(i => i.ProfileId == id)
            .Select(i => new { i.PublicId, i.Name, appliedRevision = i.AppliedRevision }).ToListAsync();
        return Result.Done(new
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
    }

    // ---- lifecycle --------------------------------------------------------------------------------

    public async Task<Result> CreateAsync(string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result.Bad("Name ist erforderlich.");
        var p = await _profiles.CreateAsync(name.Trim(), description);
        return Result.Done(new { id = p.Id, name = p.Name, joinCode = p.JoinCode });
    }

    public sealed record Patch(string? Name, string? Description, bool? AutoApprove, bool? AutoUpdateLocal,
        bool? NotifyOffline, bool? NotifyUpdate, string? NotifyRecipients, string? ActivateTemplateName,
        bool? SyncSettings, bool? SyncUsers, bool? SyncPlugins, bool? SyncComponents, bool? SyncTemplates, bool? SyncMailTemplates,
        bool? RemoveDefaultAdmin, string? SettingsMode, string? UsersMode, string? PluginsMode, string? ComponentsMode,
        string? TemplatesMode, string? MailTemplatesMode);

    /// <summary>Only the fields given change.</summary>
    public async Task<Result> PatchAsync(int id, Patch b)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
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
        // Users never "keep": add-only is unconditional on the instance, so the profile must not promise more.
        if (b.UsersMode is not null) p.UsersMode = ParseMode(b.UsersMode, p.UsersMode) is var um && um == SyncMode.Keep ? SyncMode.Add : um;
        if (b.PluginsMode is not null) p.PluginsMode = ParseMode(b.PluginsMode, p.PluginsMode);
        if (b.ComponentsMode is not null) p.ComponentsMode = ParseMode(b.ComponentsMode, p.ComponentsMode);
        if (b.TemplatesMode is not null) p.TemplatesMode = ParseMode(b.TemplatesMode, p.TemplatesMode);
        if (b.MailTemplatesMode is not null) p.MailTemplatesMode = ParseMode(b.MailTemplatesMode, p.MailTemplatesMode);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> SetAiAsync(int id, bool? syncAi, int? monthlyTokenBudget, string? instruction, bool? backupBeforeAiChange)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        if (syncAi is { } s) p.SyncAi = s;
        if (monthlyTokenBudget is { } tb) p.AiMonthlyTokenBudget = tb <= 0 ? null : tb;
        if (instruction is not null) p.AiInstruction = instruction;
        if (backupBeforeAiChange is { } bc) p.BackupBeforeAiChange = bc;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        // Detach any instances first so they fall back to the default profile rather than a dangling id.
        await _db.Instances.Where(i => i.ProfileId == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ProfileId, (int?)null));
        _db.Profiles.Remove(p);
        await _db.SaveChangesAsync();
        return Result.Done();
    }

    public async Task<Result> DuplicateAsync(int id)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        var copy = await _profiles.DuplicateAsync(id);
        return Result.Done(new { id = copy.Id, name = copy.Name, joinCode = copy.JoinCode });
    }

    public async Task<Result> MakeDefaultAsync(int id)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        await _profiles.SetDefaultAsync(id);
        return Result.Done();
    }

    public async Task<Result> RotateJoinCodeAsync(int id)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        return Result.Done(new { joinCode = await _profiles.RotateJoinCodeAsync(p) });
    }

    public async Task<Result> TouchAsync(int id)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    // ---- settings ---------------------------------------------------------------------------------

    public async Task<Result> RecommendedAsync(int id)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        var missing = await _profiles.MissingRecommendedAsync(id);
        return Result.Done(new
        {
            recommended = InstanceSettingCatalog.Recommended.Select(e => new { key = e.Key, value = e.Recommended, label = e.Label }),
            missing = missing.Select(e => e.Key),
        });
    }

    public async Task<Result> AddRecommendedAsync(int id)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        return Result.Done(new { added = await _profiles.AddRecommendedAsync(id) });
    }

    public async Task<Result> SetSettingAsync(int id, string? key, string? value)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        if (string.IsNullOrWhiteSpace(key)) return Result.Bad("Key ist erforderlich.");
        var k = key.Trim();
        // A group key as a free row would be skipped by the rollout unless that group happened to be on.
        if (ProfileService.IsGroupKey(k))
            return Result.Bad("Dieser Schlüssel gehört zu einer Gruppe (SMTP/Übersetzung/Backup/KI) und wird über den jeweiligen Gruppen-Endpunkt gesetzt.");
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == id && s.Key == k);
        if (row is null) { row = new ProfileSetting { ProfileId = id, Key = k }; _db.ProfileSettings.Add(row); }
        row.Value = value;
        row.IsSecret = false;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> DeleteSettingAsync(int id, string key)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == id && s.Key == key);
        if (row is null) return Result.NotFound("Einstellung nicht gefunden.");
        _db.ProfileSettings.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    // ---- users (add-only on the instances; hashed here) -------------------------------------------

    public async Task<Result> UpsertUserAsync(int id, string? username, string? email, string? displayName, string? password, string? passwordHash, string? role)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        if (string.IsNullOrWhiteSpace(username)) return Result.Bad("Username ist erforderlich.");
        var uname = username.Trim();
        var row = await _db.ProfileUsers.FirstOrDefaultAsync(u => u.ProfileId == id && u.Username == uname);
        var isNew = row is null;
        if (isNew) { row = new ProfileUser { ProfileId = id, Username = uname }; _db.ProfileUsers.Add(row); }
        // A ready-made hash (import) or a plaintext password to hash here; plaintext is never stored.
        if (!string.IsNullOrWhiteSpace(passwordHash)) row!.PasswordHash = passwordHash!;
        else if (!string.IsNullOrWhiteSpace(password)) row!.PasswordHash = _auth.HashPassword(password!);
        else if (isNew) return Result.Bad("Für einen neuen Benutzer ist ein Passwort (oder passwordHash) erforderlich.");
        row!.Email = email ?? row.Email;
        row.DisplayName = displayName ?? row.DisplayName;
        row.Role = string.IsNullOrWhiteSpace(role) ? (row.Role ?? "Admin") : role!.Trim();
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done(new { ok = true, created = isNew });
    }

    public async Task<Result> DeleteUserAsync(int id, string username)
    {
        var row = await _db.ProfileUsers.FirstOrDefaultAsync(u => u.ProfileId == id && u.Username == username);
        if (row is null) return Result.NotFound("Benutzer nicht gefunden.");
        _db.ProfileUsers.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    // ---- components / templates / mail templates --------------------------------------------------

    public async Task<Result> UpsertComponentAsync(int id, string? type, string? name, string? description, string? icon, string? fieldsJson, string? templateHtml)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(name)) return Result.Bad("Type und Name sind erforderlich.");
        if (!string.IsNullOrWhiteSpace(fieldsJson) && !IsValidJson(fieldsJson!)) return Result.Bad("FieldsJson ist kein gültiges JSON.");
        var t = type.Trim().ToLowerInvariant();
        var row = await _db.ProfileComponents.FirstOrDefaultAsync(c => c.ProfileId == id && c.Type == t);
        if (row is null) { row = new ProfileComponent { ProfileId = id, Type = t }; _db.ProfileComponents.Add(row); }
        row.Name = name.Trim();
        row.Description = description;
        row.Icon = icon;
        row.FieldsJson = fieldsJson ?? row.FieldsJson;
        row.TemplateHtml = templateHtml ?? row.TemplateHtml;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> DeleteComponentAsync(int id, string type)
    {
        var row = await _db.ProfileComponents.FirstOrDefaultAsync(c => c.ProfileId == id && c.Type == type.ToLowerInvariant());
        if (row is null) return Result.NotFound("Komponente nicht gefunden.");
        _db.ProfileComponents.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public sealed record Template(string Name, string? AccentColor, string? SecondaryColor, string? HeadingFont, string? BodyFont,
        string? ButtonStyle, string? HeadingColor, string? TextColor, string? BackgroundColor, string? CustomCss, string? CustomJs,
        string? LayoutHtml, string? MenuMapJson, string? ParametersJson, string? ParamValuesJson);

    public async Task<Result> UpsertTemplateAsync(int id, Template b)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        if (string.IsNullOrWhiteSpace(b.Name)) return Result.Bad("Name ist erforderlich.");
        if (b.MenuMapJson is { Length: > 0 } && !IsValidJson(b.MenuMapJson)) return Result.Bad("MenuMapJson ist kein gültiges JSON.");
        if (b.ParametersJson is { Length: > 0 } && !IsValidJson(b.ParametersJson)) return Result.Bad("ParametersJson ist kein gültiges JSON.");
        var name = b.Name.Trim();
        var row = await _db.ProfileTemplates.FirstOrDefaultAsync(t => t.ProfileId == id && t.Name == name);
        if (row is null) { row = new ProfileTemplate { ProfileId = id, Name = name }; _db.ProfileTemplates.Add(row); }
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
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> DeleteTemplateAsync(int id, string name)
    {
        var row = await _db.ProfileTemplates.FirstOrDefaultAsync(t => t.ProfileId == id && t.Name == name);
        if (row is null) return Result.NotFound("Template nicht gefunden.");
        _db.ProfileTemplates.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> UpsertMailTemplateAsync(int id, string? key, string? name, string? description, string? subject, string? body, bool? enabled, bool? isHtml)
    {
        if (!await ExistsAsync(id)) return NoProfile;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(subject)) return Result.Bad("Key und Subject sind erforderlich.");
        var mk = key.Trim();
        var row = await _db.ProfileMailTemplates.FirstOrDefaultAsync(m => m.ProfileId == id && m.Key == mk);
        if (row is null) { row = new ProfileMailTemplate { ProfileId = id, Key = mk }; _db.ProfileMailTemplates.Add(row); }
        row.Name = name ?? row.Name;
        row.Description = description ?? row.Description;
        row.Subject = subject;
        row.Body = body ?? row.Body;
        row.Enabled = enabled ?? row.Enabled;
        row.IsHtml = isHtml ?? row.IsHtml;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> DeleteMailTemplateAsync(int id, string key)
    {
        var row = await _db.ProfileMailTemplates.FirstOrDefaultAsync(m => m.ProfileId == id && m.Key == key);
        if (row is null) return Result.NotFound("Mail-Template nicht gefunden.");
        _db.ProfileMailTemplates.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    // ---- groups: SMTP / translation ---------------------------------------------------------------

    public async Task<Result> SetSmtpAsync(int id, string? mailSource, string? host, int? port, string? user, string? password, string? fromEmail, string? fromName, bool? ssl)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        p.SyncSmtp = true;
        p.MailSource = MailSources.Normalise(mailSource);
        if (p.MailSource == MailSources.Own)
        {
            await UpsertSettingAsync(id, "smtp.host", host);
            await UpsertSettingAsync(id, "smtp.port", port?.ToString());
            await UpsertSettingAsync(id, "smtp.user", user);
            await UpsertSettingAsync(id, "smtp.fromEmail", fromEmail);
            await UpsertSettingAsync(id, "smtp.fromName", fromName);
            await UpsertSettingAsync(id, "smtp.ssl", (ssl ?? false) ? "true" : "false");
            // Empty keeps the stored password; only a new one is encrypted and written.
            if (!string.IsNullOrWhiteSpace(password)) await UpsertSettingAsync(id, "smtp.password", _secrets.Protect(password), secret: true);
        }
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    /// <summary>Stops rolling the group out; the stored values survive so switching it on again restores them.</summary>
    public async Task<Result> DisableSmtpAsync(int id)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        p.SyncSmtp = false;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> SetTranslationAsync(int id, string? provider, string? apiKey, string? url)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        p.SyncTranslation = true;
        if (provider is not null) await UpsertSettingAsync(id, "translate.provider", provider);
        if (url is not null) await UpsertSettingAsync(id, "translate.url", url);
        if (!string.IsNullOrWhiteSpace(apiKey)) await UpsertSettingAsync(id, "translate.apiKey", _secrets.Protect(apiKey), secret: true);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    public async Task<Result> DisableTranslationAsync(int id)
    {
        var p = await _db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NoProfile;
        p.SyncTranslation = false;
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    private async Task UpsertSettingAsync(int profileId, string key, string? value, bool secret = false)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Key == key);
        if (row is null) { row = new ProfileSetting { ProfileId = profileId, Key = key }; _db.ProfileSettings.Add(row); }
        row.Value = value;
        row.IsSecret = secret;
    }

    // ---- plugins (upload stays REST: a raw ZIP is no chat payload) --------------------------------

    public async Task<Result> DeletePluginAsync(int id, string pluginKey)
    {
        var row = await _db.ProfilePlugins.FirstOrDefaultAsync(x => x.ProfileId == id && x.Key == pluginKey);
        if (row is null) return Result.NotFound("Plugin nicht gefunden.");
        _db.ProfilePlugins.Remove(row);
        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        return Result.Done();
    }

    // ---- instance ↔ profile -----------------------------------------------------------------------
    // The instance is resolved by the caller (it alone knows the key's scope) and passed in TRACKED.

    public async Task<Result> AssignmentAsync(Instance inst)
    {
        Profile? prof = inst.ProfileId is int pid ? await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid) : null;
        return Result.Done(new
        {
            profileId = inst.ProfileId,
            profileName = prof?.Name,
            appliedRevision = inst.AppliedRevision,
            profileRevision = prof?.Revision,
            inSync = prof != null && inst.AppliedRevision == prof.Revision,
            lastSyncError = inst.LastSyncError
        });
    }

    public async Task<Result> AssignAsync(Instance inst, int? profileId)
    {
        if (profileId is int pid && !await ExistsAsync(pid)) return NoProfile;
        inst.ProfileId = profileId;
        // Force a re-pull even if revision numbers coincide (mirrors the admin assignment handler).
        inst.AppliedRevision = 0;
        inst.LastSyncError = null;
        _instances.Log(inst, InstanceEventKind.SyncApplied, "Profil über die API zugewiesen.");
        await _db.SaveChangesAsync();
        return Result.Done();
    }

    public async Task<Result> UnassignAsync(Instance inst)
    {
        inst.ProfileId = null;
        await _db.SaveChangesAsync();
        return Result.Done();
    }

    /// <summary>There is no cloud-side apply — the instance is made to re-pull its configuration on its next beat.</summary>
    public async Task<Result> ResyncAsync(Instance inst)
    {
        inst.AppliedRevision = 0;
        inst.LastSyncError = null;
        await _db.SaveChangesAsync();
        return Result.Done(new { ok = true, message = "Neu-Synchronisierung vorgemerkt — die Instanz zieht die Konfiguration beim nächsten Kontakt." });
    }

    public static Result InstanceNotFound => NoInstance;
}
