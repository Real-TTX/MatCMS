using System.ComponentModel;
using MatCMS.Cloud.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// MCP tools for the cloud's own configuration — the same service and the same rights as /api/v1/cloud/settings and
/// /api/v1/cloud/config-backups: read/change the Einstellungen (Allgemein, SMTP, KI, Backup-Vorgaben), back the whole
/// configuration up and restore it, and the profile's backup group. Moving a configuration to ANOTHER cloud needs the
/// file: download it via REST there, upload it here (an MCP call cannot carry the zip).
/// </summary>
[McpServerToolType]
public class CloudConfigTools
{
    private static void RequireConfig(McpContext me)
    {
        var k = me.Key;
        if (!(k.AllInstances && k.CanManageProfiles && k.CanManageStore))
            throw new McpException("Die Cloud-Konfiguration braucht einen Schlüssel für alle Instanzen mit den Rechten „Profile“ und „Store“.");
    }

    private static void RequireFull(McpContext me, IEnumerable<string> sections)
    {
        if (sections.Any(s => s is "users" or "hosting") && !(me.Key.CanRestore && me.Key.CanManageHosting))
            throw new McpException("Benutzer und Server/Hosting brauchen zusätzlich die Rechte „Wiederherstellen“ und „Hosting“.");
    }

    [McpServerTool(Name = "get_cloud_settings"), Description(
        "Read this cloud's own settings by section: general (name, forced https, 2FA policy, auto-update, logging), smtp (the mail server sites use when they send through the cloud), ai (provider, model, base URL, key), backup (quota and retention defaults). Secrets only say whether they are set.")]
    public static object GetSettings(McpContext me, CloudConfigBackupService svc)
    {
        RequireConfig(me);
        return svc.GetSettings();
    }

    [McpServerTool(Name = "set_cloud_settings"), Description(
        "Change this cloud's own settings: values = { key: value } with the keys from get_cloud_settings (e.g. smtp.host, smtp.port, smtp.user, smtp.password, smtp.fromEmail, smtp.ssl = 1/0). A secret is set by a non-empty value and cleared only via clear = [key].")]
    public static async Task<object> SetSettings(McpContext me, CloudConfigBackupService svc,
        Dictionary<string, string?>? values = null, List<string>? clear = null)
    {
        RequireConfig(me);
        var (ok, error, changed) = await svc.SetSettingsAsync(values, clear);
        if (!ok) throw new McpException(error ?? "Fehlgeschlagen.");
        return new { ok = true, changed };
    }

    [McpServerTool(Name = "start_chatgpt_login"), Description(
        "Sign the cloud in to a ChatGPT account for its AI features (device code, like Codex CLI and Hermes): returns a user code and the verification URL. Tell the user to open the URL and type the code within 15 minutes — they must do that themselves — then call get_chatgpt_login every few seconds until connected = true. Afterwards set ai.provider = 'chatgpt' and ai.model (a slug from get_chatgpt_login.models) with set_cloud_settings. Unofficial route (Codex's public client id): it can stop working when OpenAI changes it.")]
    public static async Task<object> StartChatGptLogin(McpContext me, ChatGptAuthService chatgpt, CancellationToken ct)
    {
        RequireConfig(me);
        var (pending, error) = await chatgpt.StartLoginAsync(ct);
        if (pending is null) throw new McpException(error ?? "Die Anmeldung konnte nicht gestartet werden.");
        return new { ok = true, userCode = pending.UserCode, verificationUrl = pending.VerificationUrl, expiresAt = pending.ExpiresAt, intervalSeconds = pending.IntervalSeconds };
    }

    [McpServerTool(Name = "get_chatgpt_login"), Description(
        "State of the cloud's ChatGPT sign-in: connected, account email and plan, when the access token expires, the running login (user code, verification URL, expiry) or why the last one failed; the selectable models once connected. While a login is pending each call asks OpenAI once per poll interval whether the code was confirmed. Never returns a token.")]
    public static async Task<object> GetChatGptLogin(McpContext me, ChatGptAuthService chatgpt, CancellationToken ct)
    {
        RequireConfig(me);
        var status = await chatgpt.GetStatusAsync(poll: true, ct);
        return ChatGptAuthService.StatusJson(status, status.Connected ? await chatgpt.ListModelsAsync(ct) : null);
    }

    [McpServerTool(Name = "logout_chatgpt"), Description(
        "Sign the cloud out of its ChatGPT account: the stored tokens are deleted and a running login is dropped. The AI features fall silent until an API-key provider or a new sign-in is set up — confirm with the user first.")]
    public static async Task<object> LogoutChatGpt(McpContext me, ChatGptAuthService chatgpt, CancellationToken ct)
    {
        RequireConfig(me);
        await chatgpt.LogoutAsync(ct);
        return new { ok = true };
    }

    [McpServerTool(Name = "test_ai"), Description(
        "Send one short test message through the cloud's saved AI provider (API key or ChatGPT sign-in) and return the answer, or the reason it failed. Booked to no instance's budget.")]
    public static async Task<object> TestAi(McpContext me, AiService ai, CancellationToken ct)
    {
        RequireConfig(me);
        var r = await ai.TestAsync(ct);
        if (!r.Ok) throw new McpException(r.Error ?? "Der KI-Test ist fehlgeschlagen.");
        return new { ok = true, text = r.Text, promptTokens = r.PromptTokens, completionTokens = r.CompletionTokens };
    }

    [McpServerTool(Name = "list_cloud_config_backups"), Description(
        "List the stored backups of this cloud's own configuration (settings, profiles, store, users, assignments, optionally the server/hosting block) — not site backups. Returns the section names too.")]
    public static object List(McpContext me, CloudConfigBackupService svc)
    {
        RequireConfig(me);
        return new
        {
            sections = CloudConfigBackupService.AllSections,
            defaultRestore = CloudConfigBackupService.DefaultRestoreSections,
            backups = svc.List().Select(i => new { id = i.Id, i.CreatedAt, sizeBytes = i.Size, i.Source, i.Version, i.Sections, i.HasSecrets, i.Note }),
        };
    }

    [McpServerTool(Name = "create_cloud_config_backup"), Description(
        "Back up this cloud's configuration. sections omitted = all (general, smtp, ai, backup, store, profiles, users, notifications, assignments, hosting). Secrets (SMTP password, AI key, proxy keys, 2FA secrets) are only included with a passphrase (min. 8 characters) and are encrypted with it — tell the user to keep it. Returns the backup id.")]
    public static async Task<object> Create(McpContext me, CloudConfigBackupService svc,
        [Description("Section names; omit for all.")] List<string>? sections = null,
        [Description("Passphrase that encrypts the secrets; omit to leave secrets out.")] string? passphrase = null,
        [Description("Free note shown in the list.")] string? note = null, CancellationToken ct = default)
    {
        RequireConfig(me);
        var secs = CloudConfigBackupService.Normalise(sections, CloudConfigBackupService.AllSections);
        RequireFull(me, secs);
        var (info, error) = await svc.CreateAsync(secs, passphrase, note, ct);
        if (info is null) throw new McpException(error ?? "Fehlgeschlagen.");
        return new { ok = true, id = info.Id, info.Sections, info.HasSecrets, sizeBytes = info.Size };
    }

    [McpServerTool(Name = "restore_cloud_config_backup"), Description(
        "Restore a configuration backup into THIS cloud. dryRun (default TRUE) only reports what would change — show that to the user and get confirmation before running with dryRun=false. sections omitted = everything except 'hosting' (server-specific: addresses, proxy, ports). Settings of a section are overwritten, profiles replaced/created by name, store items updated/created, users ONLY added (existing accounts never change), instances assigned to profiles by name. Secrets need the passphrase used when the backup was made.")]
    public static async Task<object> Restore(McpContext me, CloudConfigBackupService svc,
        [Description("The backup id from list_cloud_config_backups.")] string id,
        [Description("Section names; omit for the default (all but hosting).")] List<string>? sections = null,
        [Description("The backup's passphrase, to restore its secrets.")] string? passphrase = null,
        [Description("Only report, change nothing (default true).")] bool dryRun = true, CancellationToken ct = default)
    {
        RequireConfig(me);
        var secs = CloudConfigBackupService.Normalise(sections, CloudConfigBackupService.DefaultRestoreSections);
        RequireFull(me, secs);
        var r = await svc.RestoreAsync(id, secs, passphrase, dryRun, ct);
        if (!r.Ok) throw new McpException(string.Join(" ", r.Lines));
        return new { ok = true, dryRun = r.DryRun, report = r.Lines };
    }

    [McpServerTool(Name = "delete_cloud_config_backup"), Description("Delete a stored configuration backup of this cloud (confirm with the user first).")]
    public static object Delete(McpContext me, CloudConfigBackupService svc, [Description("The backup id.")] string id)
    {
        RequireConfig(me);
        if (!svc.Delete(id)) throw new McpException("Sicherung nicht gefunden.");
        return new { ok = true };
    }

    [McpServerTool(Name = "set_profile_backup"), Description(
        "Put the backup policy into a profile and roll it out to its sites (the 'Backup' group of a profile): enabled, intervalHours (1–720), retain (1–100 kept on the site), which parts, and whether each backup is also uploaded to the cloud. remove=true stops rolling it out (nothing is switched off on the sites).")]
    public static async Task<object> SetProfileBackup(McpContext me, ProfileOpsService ops, int profileId,
        bool enabled = true, int intervalHours = 24, int retain = 7, bool toCloud = true, bool remove = false, CancellationToken ct = default)
    {
        if (!me.Key.CanManageProfiles) throw new McpException("Dieser Schlüssel darf keine Profile verwalten.");
        var r = remove ? await ops.RemoveBackupAsync(profileId)
            : await ops.SetBackupAsync(profileId, new BackupSchedule { Enabled = enabled, IntervalHours = intervalHours, Retain = retain }, toCloud);
        if (!r.Ok) throw new McpException(r.Error ?? "Fehlgeschlagen.");
        return new { ok = true };
    }
}
