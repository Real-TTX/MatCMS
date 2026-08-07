using System.Text.Json;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Cloud.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Pages.Admin.Profiles;

/// <summary>
/// The profile itself: general settings, policy, which payloads to roll out, and a LIST per payload.
/// Editing an individual template/component/plugin happens on its own page (Template/Component/Plugin)
/// — the same split MatCMS uses, and the reason this page stays readable.
/// <para>Every handler that changes something the instances receive ends in
/// <c>ProfileService.TouchAsync</c> — a change that does not bump the revision is a change that
/// silently never arrives.</para>
/// </summary>
public class EditModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly ProfileService _profiles;
    private readonly AuthService _auth;
    private readonly SecretProtector _secrets;

    public EditModel(AppDbContext db, ProfileService profiles, AuthService auth, SecretProtector secrets)
    {
        _db = db;
        _profiles = profiles;
        _auth = auth;
        _secrets = secrets;
    }

    public Profile Item { get; private set; } = new();
    public List<ProfileSetting> Settings { get; private set; } = new();
    public List<ProfileUser> Users { get; private set; } = new();
    public List<ProfilePlugin> Plugins { get; private set; } = new();
    public List<ProfileComponent> Components { get; private set; } = new();
    public List<ProfileTemplate> Templates { get; private set; } = new();
    public List<Instance> Instances { get; private set; } = new();

    // --- What is available globally, and what this profile has taken from it ------------------
    public List<StorePlugin> StorePlugins { get; private set; } = new();
    public List<StoreTemplate> StoreTemplates { get; private set; } = new();
    public List<StoreComponent> StoreComponents { get; private set; } = new();
    public List<Models.User> GlobalUsers { get; private set; } = new();

    public HashSet<int> SelectedPlugins { get; private set; } = new();
    public HashSet<int> SelectedTemplates { get; private set; } = new();
    public HashSet<int> SelectedComponents { get; private set; } = new();
    public HashSet<int> SelectedUsers { get; private set; } = new();

    /// <summary>True when the profile also defines its own item of that identity — the selection is
    /// then overridden locally, and the UI says so instead of leaving the operator to work it out.</summary>
    public bool PluginOverridden(string key) => Plugins.Any(p => p.Key == key);
    public bool TemplateOverridden(string name) => Templates.Any(t => t.Name == name);
    public bool ComponentOverridden(string type) => Components.Any(c => c.Type == type);
    public bool UserOverridden(string username) => Users.Any(u => u.Username == username);

    /// <summary>One row of the strategy form: which payload, which modes it may have, what is set.</summary>
    public sealed record ModeRow(string Field, string LabelKey, string Value, string[] Options);

    /// <summary>
    /// The strategy form, driven by data so all five payloads look and behave identically. Users are
    /// the odd one out: they get no "keep" option, because the cloud never rewrites an existing
    /// account — offering the choice would promise something the instance refuses to do.
    /// </summary>
    public List<ModeRow> Modes =>
    [
        new("settingsMode", "profiles.payloadSettings", ProfileService.Wire(Item.SettingsMode), ["keep", "add", "once"]),
        new("usersMode", "profiles.payloadUsers", ProfileService.Wire(Item.UsersMode), ["add", "once"]),
        new("pluginsMode", "profiles.payloadPlugins", ProfileService.Wire(Item.PluginsMode), ["keep", "add", "once"]),
        new("componentsMode", "profiles.payloadComponents", ProfileService.Wire(Item.ComponentsMode), ["keep", "add", "once"]),
        new("templatesMode", "profiles.payloadTemplates", ProfileService.Wire(Item.TemplatesMode), ["keep", "add", "once"])
    ];

    /// <summary>Falls back to the stored mode rather than to a default: a form that arrives without
    /// the field (an older browser cache, a partial post) must not silently reset the strategy.</summary>
    private static SyncMode ParseMode(string? raw, SyncMode fallback) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "keep" => SyncMode.Keep,
        "add" => SyncMode.Add,
        "once" => SyncMode.Once,
        _ => fallback
    };

    /// <summary>Settings that get their own SMTP form; everything else shows in the free key/value list.</summary>
    public static readonly string[] SmtpKeys =
    [
        "smtp.host", "smtp.port", "smtp.user", "smtp.password", "smtp.fromEmail", "smtp.fromName", "smtp.ssl"
    ];

    public string Setting(string key) => Settings.FirstOrDefault(s => s.Key == key)?.Value ?? "";

    public bool SettingFlag(string key) =>
        Setting(key).Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    public List<ProfileSetting> OtherSettings =>
        Settings.Where(s => !SmtpKeys.Contains(s.Key)).OrderBy(s => s.Key).ToList();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id)) return RedirectToPage("Index");
        return Page();
    }

    private async Task<bool> LoadAsync(int id)
    {
        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return false;

        Item = profile;
        Settings = await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == id).ToListAsync();
        Users = await _db.ProfileUsers.AsNoTracking().Where(u => u.ProfileId == id).OrderBy(u => u.Username).ToListAsync();
        // Never load the bundle blobs for a listing — a few plugins with assets would drag megabytes
        // through memory on every page render for nothing.
        Plugins = await _db.ProfilePlugins.AsNoTracking().Where(p => p.ProfileId == id)
            .Select(p => new ProfilePlugin
            {
                Id = p.Id, ProfileId = p.ProfileId, Key = p.Key, Name = p.Name,
                Version = p.Version, Description = p.Description, UploadedAt = p.UploadedAt
            })
            .OrderBy(p => p.Name).ToListAsync();
        Components = await _db.ProfileComponents.AsNoTracking().Where(c => c.ProfileId == id).OrderBy(c => c.Name).ToListAsync();
        // Same reasoning: a template carries a lot of text, the listing only needs its identity.
        Templates = await _db.ProfileTemplates.AsNoTracking().Where(t => t.ProfileId == id)
            .Select(t => new ProfileTemplate
            {
                Id = t.Id, ProfileId = t.ProfileId, Name = t.Name,
                AccentColor = t.AccentColor, HeadingFont = t.HeadingFont, BodyFont = t.BodyFont
            })
            .OrderBy(t => t.Name).ToListAsync();
        Instances = await _db.Instances.AsNoTracking().Where(i => i.ProfileId == id).OrderBy(i => i.Name).ToListAsync();

        // Bundles and template bodies are never loaded here — the picker only needs identities.
        StorePlugins = await _db.StorePlugins.AsNoTracking()
            .Select(p => new StorePlugin { Id = p.Id, Key = p.Key, Name = p.Name, Version = p.Version, Description = p.Description })
            .OrderBy(p => p.Name).ToListAsync();
        StoreTemplates = await _db.StoreTemplates.AsNoTracking()
            .Select(t => new StoreTemplate { Id = t.Id, Name = t.Name, Description = t.Description, AccentColor = t.AccentColor })
            .OrderBy(t => t.Name).ToListAsync();
        StoreComponents = await _db.StoreComponents.AsNoTracking()
            .Select(c => new StoreComponent { Id = c.Id, Type = c.Type, Name = c.Name, Description = c.Description })
            .OrderBy(c => c.Name).ToListAsync();
        GlobalUsers = await _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync();

        SelectedPlugins = (await _db.ProfileStorePlugins.AsNoTracking().Where(x => x.ProfileId == id).Select(x => x.StorePluginId).ToListAsync()).ToHashSet();
        SelectedTemplates = (await _db.ProfileStoreTemplates.AsNoTracking().Where(x => x.ProfileId == id).Select(x => x.StoreTemplateId).ToListAsync()).ToHashSet();
        SelectedComponents = (await _db.ProfileStoreComponents.AsNoTracking().Where(x => x.ProfileId == id).Select(x => x.StoreComponentId).ToListAsync()).ToHashSet();
        SelectedUsers = (await _db.ProfileGlobalUsers.AsNoTracking().Where(x => x.ProfileId == id).Select(x => x.UserId).ToListAsync()).ToHashSet();
        return true;
    }

    /// <summary>
    /// Saves what this profile takes from the global side: store entries by reference, and the
    /// cloud's own user accounts. Written as a full replace of the selection — the form posts every
    /// ticked box, so anything absent was unticked.
    /// </summary>
    public async Task<IActionResult> OnPostSelectionsAsync(
        int id, int[]? storePlugins, int[]? storeTemplates, int[]? storeComponents, int[]? globalUsers)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage("Index");

        await ReplaceAsync(_db.ProfileStorePlugins, x => x.ProfileId == id, storePlugins,
            pid => new ProfileStorePlugin { ProfileId = id, StorePluginId = pid }, x => x.StorePluginId);
        await ReplaceAsync(_db.ProfileStoreTemplates, x => x.ProfileId == id, storeTemplates,
            tid => new ProfileStoreTemplate { ProfileId = id, StoreTemplateId = tid }, x => x.StoreTemplateId);
        await ReplaceAsync(_db.ProfileStoreComponents, x => x.ProfileId == id, storeComponents,
            cid => new ProfileStoreComponent { ProfileId = id, StoreComponentId = cid }, x => x.StoreComponentId);
        await ReplaceAsync(_db.ProfileGlobalUsers, x => x.ProfileId == id, globalUsers,
            uid => new ProfileGlobalUser { ProfileId = id, UserId = uid }, x => x.UserId);

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        TempData["Flash"] = "Auswahl gespeichert.";
        return RedirectToPage(new { id, tab = "global" });
    }

    /// <summary>Replaces a profile's selection of one kind: drop what is no longer ticked, add what
    /// is new, leave the rest alone so unchanged rows keep their identity.</summary>
    private async Task ReplaceAsync<TLink>(
        Microsoft.EntityFrameworkCore.DbSet<TLink> set,
        System.Linq.Expressions.Expression<Func<TLink, bool>> mine,
        int[]? wanted, Func<int, TLink> create, Func<TLink, int> targetId) where TLink : class
    {
        var chosen = (wanted ?? []).ToHashSet();
        var current = await set.Where(mine).ToListAsync();

        foreach (var row in current.Where(r => !chosen.Contains(targetId(r))))
            set.Remove(row);

        var existing = current.Select(targetId).ToHashSet();
        foreach (var add in chosen.Where(c => !existing.Contains(c)))
            set.Add(create(add));
    }

    // --- General + policy ---------------------------------------------------

    public async Task<IActionResult> OnPostGeneralAsync(
        int id, string name, string? description, bool autoApprove, bool isDefault,
        bool useGlobalSmtp,
        bool autoUpdateLocal, bool notifyOffline, bool notifyUpdate, string? notifyRecipients,
        bool syncSettings, bool syncUsers, bool syncPlugins, bool syncComponents, bool syncTemplates,
        string? settingsMode, string? usersMode, string? pluginsMode, string? componentsMode, string? templatesMode,
        string? activateTemplateName)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage("Index");

        if (!string.IsNullOrWhiteSpace(name)) profile.Name = name.Trim();
        profile.Description = description?.Trim() ?? "";
        profile.AutoApprove = autoApprove;
        profile.AutoUpdateLocal = autoUpdateLocal;
        profile.NotifyOffline = notifyOffline;
        profile.NotifyUpdate = notifyUpdate;
        profile.NotifyRecipients = string.IsNullOrWhiteSpace(notifyRecipients) ? null : notifyRecipients.Trim();
        profile.UseGlobalSmtp = useGlobalSmtp;
        profile.SyncSettings = syncSettings;
        profile.SyncUsers = syncUsers;
        profile.SyncPlugins = syncPlugins;
        profile.SyncComponents = syncComponents;
        profile.SyncTemplates = syncTemplates;
        profile.SettingsMode = ParseMode(settingsMode, profile.SettingsMode);
        profile.PluginsMode = ParseMode(pluginsMode, profile.PluginsMode);
        profile.ComponentsMode = ParseMode(componentsMode, profile.ComponentsMode);
        profile.TemplatesMode = ParseMode(templatesMode, profile.TemplatesMode);
        // Users never offer Keep — add-only is the whole point — so anything but "once" is Add.
        profile.UsersMode = ParseMode(usersMode, profile.UsersMode) == SyncMode.Once ? SyncMode.Once : SyncMode.Add;
        profile.ActivateTemplateName = string.IsNullOrWhiteSpace(activateTemplateName) ? null : activateTemplateName.Trim();

        await _db.SaveChangesAsync();

        // Exactly one profile is the default, so this both sets and clears. Unticking the box on the
        // current default is ignored — some profile has to catch instances that resolve to none.
        if (isDefault && !profile.IsDefault) await _profiles.SetDefaultAsync(id);

        await _profiles.TouchAsync(id);
        TempData["Flash"] = "Profil gespeichert.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRotateCodeAsync(int id)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage("Index");

        var code = await _profiles.RotateJoinCodeAsync(profile);
        TempData["Flash"] = $"Neuer Join-Code: {code}";
        return RedirectToPage(new { id, tab = "general" });
    }

    // --- Settings payload ---------------------------------------------------

    public async Task<IActionResult> OnPostSmtpAsync(
        int id, string? host, string? port, string? user, string? password,
        string? fromEmail, string? fromName, bool ssl)
    {
        // An empty password keeps the stored one — the field is rendered blank on purpose, so saving
        // the form must not wipe the secret.
        await UpsertSettingAsync(id, "smtp.host", host?.Trim());
        await UpsertSettingAsync(id, "smtp.port", port?.Trim());
        await UpsertSettingAsync(id, "smtp.user", user?.Trim());
        // Encrypted before it ever reaches the database. An empty field keeps the stored value, so
        // saving the form does not wipe the secret.
        if (!string.IsNullOrEmpty(password))
            await UpsertSettingAsync(id, "smtp.password", _secrets.Protect(password), secret: true);
        await UpsertSettingAsync(id, "smtp.fromEmail", fromEmail?.Trim());
        await UpsertSettingAsync(id, "smtp.fromName", fromName?.Trim());
        await UpsertSettingAsync(id, "smtp.ssl", ssl ? "1" : "0");

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        TempData["Flash"] = "SMTP-Einstellungen gespeichert.";
        return RedirectToPage(new { id, tab = "settings" });
    }


    public async Task<IActionResult> OnPostDeleteSettingAsync(int id, int settingId)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.Id == settingId && s.ProfileId == id);
        if (row is not null)
        {
            _db.ProfileSettings.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(id);
        }
        return RedirectToPage(new { id, tab = "settings" });
    }

    private async Task UpsertSettingAsync(int profileId, string key, string? value, bool secret = false)
    {
        var row = await _db.ProfileSettings.FirstOrDefaultAsync(s => s.ProfileId == profileId && s.Key == key);
        if (row is null)
            _db.ProfileSettings.Add(new ProfileSetting { ProfileId = profileId, Key = key, Value = value, IsSecret = secret });
        else
        {
            row.Value = value;
            row.IsSecret = secret || row.IsSecret;
        }
    }

    // --- Users payload ------------------------------------------------------


    public async Task<IActionResult> OnPostDeleteUserAsync(int id, int userId)
    {
        var row = await _db.ProfileUsers.FirstOrDefaultAsync(u => u.Id == userId && u.ProfileId == id);
        if (row is not null)
        {
            _db.ProfileUsers.Remove(row);
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(id);
            // Deliberately not propagated: users are add-only on the instance, so removing one here
            // stops future rollouts but never deletes the account on a running site.
            TempData["Flash"] = "Benutzer aus dem Profil entfernt. Bereits ausgerollte Konten bleiben auf den Instanzen bestehen.";
        }
        return RedirectToPage(new { id, tab = "users" });
    }

    // --- Plugin upload ------------------------------------------------------
    // Editing an uploaded plugin happens on the Plugin page; this only takes new bundles in.



    // --- Template import ----------------------------------------------------

    /// <summary>
    /// Takes the JSON that MatCMS's template editor exports (*Template → Als JSON exportieren*), so a
    /// theme can be designed on a real site and rolled out from here without retyping it.
    /// </summary>
    public async Task<IActionResult> OnPostImportTemplateAsync(int id, string? templateJson)
    {
        if (string.IsNullOrWhiteSpace(templateJson))
        {
            TempData["FlashError"] = "Bitte das Template-JSON einfügen.";
            return RedirectToPage(new { id, tab = "templates" });
        }

        ProfileTemplate parsed;
        try
        {
            using var doc = JsonDocument.Parse(templateJson);
            var root = doc.RootElement;
            string S(string prop, string fallback = "")
            {
                foreach (var candidate in new[] { prop, char.ToLowerInvariant(prop[0]) + prop[1..] })
                    if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? fallback;
                return fallback;
            }
            int I(string prop, int fallback)
            {
                foreach (var candidate in new[] { prop, char.ToLowerInvariant(prop[0]) + prop[1..] })
                    if (root.TryGetProperty(candidate, out var v) && v.ValueKind == JsonValueKind.Number)
                        return v.GetInt32();
                return fallback;
            }

            var name = S("Name").Trim();
            if (name.Length == 0)
            {
                TempData["FlashError"] = "Im JSON fehlt der Name.";
                return RedirectToPage(new { id, tab = "templates" });
            }

            parsed = new ProfileTemplate
            {
                Name = name,
                AccentColor = S("AccentColor", "#de7e11"),
                SecondaryColor = S("SecondaryColor"),
                HeadingFont = S("HeadingFont", "Geologica"),
                BodyFont = S("BodyFont", "Inter"),
                ButtonStyle = S("ButtonStyle", "solid"),
                HeadingColor = S("HeadingColor", "#010101"),
                TextColor = S("TextColor", "#1a1a1a"),
                BackgroundColor = S("BackgroundColor", "#ffffff"),
                AltBackground = S("AltBackground", "#f6f7f9"),
                ContainerWidth = S("ContainerWidth", "1180"),
                ButtonRadius = S("ButtonRadius", "0"),
                HeaderBackground = S("HeaderBackground"),
                HeaderTextColor = S("HeaderTextColor"),
                HeaderPadding = S("HeaderPadding", "16"),
                CustomCss = S("CustomCss"),
                CustomJs = S("CustomJs"),
                LayoutHtml = S("LayoutHtml"),
                MenuMapJson = S("MenuMapJson", "{}"),
                ParametersJson = S("ParametersJson", "[]"),
                ParamValuesJson = S("ParamValuesJson", "{}"),
                SchemaVersion = I("SchemaVersion", 1),
                PartsJson = S("PartsJson", "{}")
            };
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Das JSON konnte nicht gelesen werden: {ex.Message}";
            return RedirectToPage(new { id, tab = "templates" });
        }

        var existing = await _db.ProfileTemplates.FirstOrDefaultAsync(t => t.ProfileId == id && t.Name == parsed.Name);
        if (existing is null)
        {
            parsed.ProfileId = id;
            _db.ProfileTemplates.Add(parsed);
        }
        else
        {
            parsed.Id = existing.Id;
            parsed.ProfileId = id;
            _db.Entry(existing).CurrentValues.SetValues(parsed);
        }

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        TempData["Flash"] = $"Template \"{parsed.Name}\" importiert.";
        return RedirectToPage(new { id, tab = "templates" });
    }
}
