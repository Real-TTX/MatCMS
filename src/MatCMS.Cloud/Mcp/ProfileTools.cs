using System.ComponentModel;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MatCMS.Cloud.Mcp;

/// <summary>
/// Profiles for an AI client — the same operations as <c>/api/v1/profiles</c>, through the same
/// <see cref="ProfileOpsService"/>, so a profile edited by an agent behaves exactly like one edited in the admin: every
/// write bumps the revision and rolls out on the instances' next beat. Reads need any key; writes need the key's
/// profile right; instance calls honour its scope. Plugin bundles stay REST (a ZIP is no chat payload).
/// </summary>
[McpServerToolType]
public class ProfileTools
{
    private const string Rollout = " Every change bumps the profile's revision; the assigned instances apply it on their next heartbeat (about a minute).";

    private static void RequireManage(McpContext me)
    {
        if (!me.Key.CanManageProfiles) throw new McpException("Dieser Schlüssel darf keine Profile verwalten.");
    }

    private static object Unwrap(ProfileOpsService.Result r) => r.Ok ? r.Data! : throw new McpException(r.Error ?? "Fehlgeschlagen.");

    private static async Task<object> WriteAsync(McpContext me, Func<Task<ProfileOpsService.Result>> op)
    {
        RequireManage(me);
        return Unwrap(await op());
    }

    // Same opaque answer for "does not exist" and "outside this key's scope" as the REST calls.
    private static async Task<Instance> InstanceAsync(AppDbContext db, McpContext me, string instanceId, CancellationToken ct)
    {
        var inst = await db.Instances.FirstOrDefaultAsync(i => i.PublicId == instanceId, ct);
        if (inst is null || !ApiKeyService.CanAccess(me.Key, inst)) throw new McpException("Instanz nicht gefunden.");
        return inst;
    }

    // ---- read -------------------------------------------------------------------------------------

    [McpServerTool(Name = "list_profiles"), Description("List the configuration profiles: id, name, description, whether it is the default, revision, join code and how many instances use it. canManageProfiles says whether this key may change them.")]
    public static async Task<object> ListProfiles(McpContext me, ProfileOpsService ops) =>
        new { canManageProfiles = me.Key.CanManageProfiles, profiles = await ops.ListAsync() };

    [McpServerTool(Name = "get_profile"), Description("One profile in full: policy, which payloads it syncs and how (keep = stay in line, add = only create missing, once = seed once), AI settings, mail source, and its settings (secrets masked), users, plugins, components, templates, mail templates and assigned instances.")]
    public static async Task<object> GetProfile(ProfileOpsService ops, [Description("Profile id (from list_profiles).")] int profileId) =>
        Unwrap(await ops.GetAsync(profileId));

    [McpServerTool(Name = "get_recommended_settings"), Description("The settings every profile should carry for the full feature set, and which of them this profile is missing.")]
    public static async Task<object> GetRecommendedSettings(ProfileOpsService ops, [Description("Profile id.")] int profileId) =>
        Unwrap(await ops.RecommendedAsync(profileId));

    // ---- lifecycle --------------------------------------------------------------------------------

    [McpServerTool(Name = "create_profile"), Description("Create a profile. Returns its id and join code (what a new instance enters to join it). It starts with the recommended settings." + Rollout)]
    public static Task<object> CreateProfile(McpContext me, ProfileOpsService ops, string name, string? description = null) =>
        WriteAsync(me, () => ops.CreateAsync(name, description));

    [McpServerTool(Name = "update_profile"), Description("Change a profile's policy. Only the fields given change. Modes: keep | add | once (users never keep — they are add-only on the instances). activateTemplateName names the template that becomes the sites' live design; empty = the sites decide." + Rollout)]
    public static Task<object> UpdateProfile(McpContext me, ProfileOpsService ops, int profileId,
        string? name = null, string? description = null, bool? autoApprove = null, bool? autoUpdateLocal = null,
        bool? notifyOffline = null, bool? notifyUpdate = null, [Description("Additional notification addresses, comma-separated.")] string? notifyRecipients = null,
        string? activateTemplateName = null,
        bool? syncSettings = null, bool? syncUsers = null, bool? syncPlugins = null, bool? syncComponents = null, bool? syncTemplates = null, bool? syncMailTemplates = null,
        bool? removeDefaultAdmin = null,
        string? settingsMode = null, string? usersMode = null, string? pluginsMode = null, string? componentsMode = null, string? templatesMode = null, string? mailTemplatesMode = null) =>
        WriteAsync(me, () => ops.PatchAsync(profileId, new ProfileOpsService.Patch(name, description, autoApprove, autoUpdateLocal,
            notifyOffline, notifyUpdate, notifyRecipients, activateTemplateName, syncSettings, syncUsers, syncPlugins, syncComponents,
            syncTemplates, syncMailTemplates, removeDefaultAdmin, settingsMode, usersMode, pluginsMode, componentsMode, templatesMode, mailTemplatesMode)));

    [McpServerTool(Name = "set_profile_ai"), Description("AI settings of a profile: whether its sites may use the cloud's AI, a monthly token budget per site (0 = unlimited), an instruction added to every request, and whether a site takes a backup before an AI change." + Rollout)]
    public static Task<object> SetProfileAi(McpContext me, ProfileOpsService ops, int profileId,
        bool? syncAi = null, int? monthlyTokenBudget = null, string? instruction = null, bool? backupBeforeAiChange = null) =>
        WriteAsync(me, () => ops.SetAiAsync(profileId, syncAi, monthlyTokenBudget, instruction, backupBeforeAiChange));

    [McpServerTool(Name = "delete_profile"), Description("Delete a profile. Its instances keep running and fall back to the default profile; nothing on the sites is removed. Confirm with the user first.")]
    public static Task<object> DeleteProfile(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.DeleteAsync(profileId));

    [McpServerTool(Name = "duplicate_profile"), Description("Copy a profile with all its payloads under a new name and join code.")]
    public static Task<object> DuplicateProfile(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.DuplicateAsync(profileId));

    [McpServerTool(Name = "set_default_profile"), Description("Make a profile the default: the fallback for instances without one and for backup quota/retention.")]
    public static Task<object> SetDefaultProfile(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.MakeDefaultAsync(profileId));

    [McpServerTool(Name = "rotate_join_code"), Description("Issue a new join code for a profile; the old one stops working at once. Instances already joined are not affected.")]
    public static Task<object> RotateJoinCode(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.RotateJoinCodeAsync(profileId));

    [McpServerTool(Name = "republish_profile"), Description("Bump a profile's revision without changing it, so every assigned instance applies it again on its next heartbeat.")]
    public static Task<object> RepublishProfile(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.TouchAsync(profileId));

    // ---- settings ---------------------------------------------------------------------------------

    [McpServerTool(Name = "add_recommended_settings"), Description("Add the recommended settings this profile is missing. Existing rows are never changed." + Rollout)]
    public static Task<object> AddRecommendedSettings(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.AddRecommendedAsync(profileId));

    [McpServerTool(Name = "set_profile_setting"), Description("Set one free key/value setting rolled out to the profile's sites (keys as the CMS's settings, e.g. sitemap.enabled, stats.enabled, antispam.level). Group keys (smtp.*, translate.*, backup.*, ai.*) are refused — use their own tools." + Rollout)]
    public static Task<object> SetProfileSetting(McpContext me, ProfileOpsService ops, int profileId, string key, string? value) =>
        WriteAsync(me, () => ops.SetSettingAsync(profileId, key, value));

    [McpServerTool(Name = "delete_profile_setting"), Description("Remove a free setting from the profile. Sites keep the value they have; only the rollout stops." + Rollout)]
    public static Task<object> DeleteProfileSetting(McpContext me, ProfileOpsService ops, int profileId, string key) =>
        WriteAsync(me, () => ops.DeleteSettingAsync(profileId, key));

    [McpServerTool(Name = "set_profile_smtp"), Description("Switch the SMTP group on. mailSource: 'own' = the sites send through the SMTP server given here, 'cloud' = they hand mail to the cloud. An empty password keeps the stored one." + Rollout)]
    public static Task<object> SetProfileSmtp(McpContext me, ProfileOpsService ops, int profileId, string? mailSource = null, string? host = null,
        int? port = null, string? user = null, string? password = null, string? fromEmail = null, string? fromName = null, bool? ssl = null) =>
        WriteAsync(me, () => ops.SetSmtpAsync(profileId, mailSource, host, port, user, password, fromEmail, fromName, ssl));

    [McpServerTool(Name = "disable_profile_smtp"), Description("Stop rolling out SMTP; the sites keep their own mail configuration. The stored values survive." + Rollout)]
    public static Task<object> DisableProfileSmtp(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.DisableSmtpAsync(profileId));

    [McpServerTool(Name = "set_profile_translation"), Description("Switch the translation group on (provider, URL, API key; an empty key keeps the stored one)." + Rollout)]
    public static Task<object> SetProfileTranslation(McpContext me, ProfileOpsService ops, int profileId, string? provider = null, string? apiKey = null, string? url = null) =>
        WriteAsync(me, () => ops.SetTranslationAsync(profileId, provider, apiKey, url));

    [McpServerTool(Name = "disable_profile_translation"), Description("Stop rolling out the translation settings. The stored values survive." + Rollout)]
    public static Task<object> DisableProfileTranslation(McpContext me, ProfileOpsService ops, int profileId) =>
        WriteAsync(me, () => ops.DisableTranslationAsync(profileId));

    // ---- payloads ---------------------------------------------------------------------------------

    [McpServerTool(Name = "upsert_profile_user"), Description("Add or change a user the profile creates on its sites. Users are add-only there: never changed or deleted on a site once created. The password is hashed here and never stored in clear; required for a new user." + Rollout)]
    public static Task<object> UpsertProfileUser(McpContext me, ProfileOpsService ops, int profileId, string username, string? password = null,
        string? email = null, string? displayName = null, [Description("Admin (default) or another CMS role.")] string? role = null) =>
        WriteAsync(me, () => ops.UpsertUserAsync(profileId, username, email, displayName, password, null, role));

    [McpServerTool(Name = "delete_profile_user"), Description("Remove a user from the profile. Sites keep the account (users are add-only)." + Rollout)]
    public static Task<object> DeleteProfileUser(McpContext me, ProfileOpsService ops, int profileId, string username) =>
        WriteAsync(me, () => ops.DeleteUserAsync(profileId, username));

    [McpServerTool(Name = "upsert_profile_component"), Description("Add or change a component (a custom block) of the profile; type is its identity on the sites. fieldsJson = the field list as JSON, templateHtml uses {{fieldId}} placeholders." + Rollout)]
    public static Task<object> UpsertProfileComponent(McpContext me, ProfileOpsService ops, int profileId, string type, string name,
        string? description = null, string? icon = null, string? fieldsJson = null, string? templateHtml = null) =>
        WriteAsync(me, () => ops.UpsertComponentAsync(profileId, type, name, description, icon, fieldsJson, templateHtml));

    [McpServerTool(Name = "delete_profile_component"), Description("Remove a component from the profile. Sites keep theirs; only the rollout stops." + Rollout)]
    public static Task<object> DeleteProfileComponent(McpContext me, ProfileOpsService ops, int profileId, string type) =>
        WriteAsync(me, () => ops.DeleteComponentAsync(profileId, type));

    [McpServerTool(Name = "upsert_profile_template"), Description("Add or change a template (design) of the profile; name is its identity. Only the fields given change. layoutHtml must contain {{content}}." + Rollout)]
    public static Task<object> UpsertProfileTemplate(McpContext me, ProfileOpsService ops, int profileId, string name,
        string? accentColor = null, string? secondaryColor = null, string? headingFont = null, string? bodyFont = null, string? buttonStyle = null,
        string? headingColor = null, string? textColor = null, string? backgroundColor = null, string? customCss = null, string? customJs = null,
        string? layoutHtml = null, string? menuMapJson = null, string? parametersJson = null, string? paramValuesJson = null) =>
        WriteAsync(me, () => ops.UpsertTemplateAsync(profileId, new ProfileOpsService.Template(name, accentColor, secondaryColor, headingFont, bodyFont,
            buttonStyle, headingColor, textColor, backgroundColor, customCss, customJs, layoutHtml, menuMapJson, parametersJson, paramValuesJson)));

    [McpServerTool(Name = "delete_profile_template"), Description("Remove a template from the profile. Sites keep theirs." + Rollout)]
    public static Task<object> DeleteProfileTemplate(McpContext me, ProfileOpsService ops, int profileId, string name) =>
        WriteAsync(me, () => ops.DeleteTemplateAsync(profileId, name));

    [McpServerTool(Name = "upsert_profile_mail_template"), Description("Add or change a mail template of the profile (key = its identity, e.g. form.notify)." + Rollout)]
    public static Task<object> UpsertProfileMailTemplate(McpContext me, ProfileOpsService ops, int profileId, string key, string subject,
        string? body = null, string? name = null, string? description = null, bool? enabled = null, bool? isHtml = null) =>
        WriteAsync(me, () => ops.UpsertMailTemplateAsync(profileId, key, name, description, subject, body, enabled, isHtml));

    [McpServerTool(Name = "delete_profile_mail_template"), Description("Remove a mail template from the profile." + Rollout)]
    public static Task<object> DeleteProfileMailTemplate(McpContext me, ProfileOpsService ops, int profileId, string key) =>
        WriteAsync(me, () => ops.DeleteMailTemplateAsync(profileId, key));

    [McpServerTool(Name = "delete_profile_plugin"), Description("Remove a plugin from the profile. Sites keep it installed. (Uploading a plugin bundle is REST: POST /api/v1/profiles/{id}/plugins.)" + Rollout)]
    public static Task<object> DeleteProfilePlugin(McpContext me, ProfileOpsService ops, int profileId, string pluginKey) =>
        WriteAsync(me, () => ops.DeletePluginAsync(profileId, pluginKey));

    // ---- instance ↔ profile -----------------------------------------------------------------------

    [McpServerTool(Name = "get_instance_profile"), Description("Which profile an instance uses and whether it has applied the profile's current revision (inSync), with the last sync error.")]
    public static async Task<object> GetInstanceProfile(McpContext me, AppDbContext db, ProfileOpsService ops, string instanceId, CancellationToken ct) =>
        Unwrap(await ops.AssignmentAsync(await InstanceAsync(db, me, instanceId, ct)));

    [McpServerTool(Name = "assign_instance_profile"), Description("Assign an instance to a profile (it applies it on its next heartbeat), or remove the assignment with profileId omitted — the instance then falls back to the default profile. Nothing is deleted on the site either way.")]
    public static async Task<object> AssignInstanceProfile(McpContext me, AppDbContext db, ProfileOpsService ops, string instanceId, int? profileId = null, CancellationToken ct = default)
    {
        RequireManage(me);
        var inst = await InstanceAsync(db, me, instanceId, ct);
        return Unwrap(profileId is null ? await ops.UnassignAsync(inst) : await ops.AssignAsync(inst, profileId));
    }

    [McpServerTool(Name = "resync_instance"), Description("Make an instance apply its profile again on its next heartbeat, even if it reports the current revision.")]
    public static async Task<object> ResyncInstance(McpContext me, AppDbContext db, ProfileOpsService ops, string instanceId, CancellationToken ct)
    {
        RequireManage(me);
        return Unwrap(await ops.ResyncAsync(await InstanceAsync(db, me, instanceId, ct)));
    }
}
