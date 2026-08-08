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

    // Each payload list shows the profile's OWN items and the ones it takes from the global store in
    // one place — the operator thinks in "templates this profile rolls out", not in where a row is
    // stored. What is left over is what the "Aus Global hinzufügen" picker offers.
    public List<StorePlugin> ChosenStorePlugins => StorePlugins.Where(p => SelectedPlugins.Contains(p.Id)).ToList();
    public List<StoreTemplate> ChosenStoreTemplates => StoreTemplates.Where(t => SelectedTemplates.Contains(t.Id)).ToList();
    public List<StoreComponent> ChosenStoreComponents => StoreComponents.Where(c => SelectedComponents.Contains(c.Id)).ToList();
    public List<Models.User> ChosenGlobalUsers => GlobalUsers.Where(u => SelectedUsers.Contains(u.Id)).ToList();

    // Tile views. Built from the SAME two sources as the tables above and in the same order, so
    // switching the view never changes what is listed — only how it looks.
    public Shared.PayloadTileList UserTiles => new(
    [
        .. ChosenGlobalUsers.Select(u => new Shared.PayloadTile(
            Url.Page("/Admin/Users/Edit", new { id = u.Id })!, u.Username, u.Email ?? "", true,
            $"{u.Username} {u.Email} {u.DisplayName}",
            NoteKey: UserOverridden(u.Username) ? "profiles.overriddenLocally" : null)),
        .. Users.Select(u => new Shared.PayloadTile(
            Url.Page("User", new { profileId = Item.Id, id = u.Id })!, u.Username, u.Email ?? "", false,
            $"{u.Username} {u.Email} {u.DisplayName}"))
    ], "profiles.noUsers");

    public Shared.PayloadTileList PluginTiles => new(
    [
        .. ChosenStorePlugins.Select(p => new Shared.PayloadTile(
            Url.Page("/Admin/Store/Plugin", new { id = p.Id })!, p.Name, $"{p.Key} · {p.Version}", true,
            $"{p.Name} {p.Key} {p.Description}",
            NoteKey: PluginOverridden(p.Key) ? "profiles.overriddenLocally" : null)),
        .. Plugins.Select(p => new Shared.PayloadTile(
            Url.Page("Plugin", new { profileId = Item.Id, id = p.Id })!, p.Name, $"{p.Key} · {p.Version}", false,
            $"{p.Name} {p.Key} {p.Description}"))
    ], "profiles.noPlugins");

    public Shared.PayloadTileList ComponentTiles => new(
    [
        .. ChosenStoreComponents.Select(c => new Shared.PayloadTile(
            Url.Page("/Admin/Store/Component", new { id = c.Id })!, c.Name, c.Type, true,
            $"{c.Name} {c.Type} {c.Description}",
            NoteKey: ComponentOverridden(c.Type) ? "profiles.overriddenLocally" : null)),
        .. Components.Select(c => new Shared.PayloadTile(
            Url.Page("Component", new { profileId = Item.Id, id = c.Id })!, c.Name, c.Type, false,
            $"{c.Name} {c.Type} {c.Description}"))
    ], "profiles.noComponents");

    public Shared.PayloadTileList TemplateTiles => new(
    [
        .. ChosenStoreTemplates.Select(t => new Shared.PayloadTile(
            Url.Page("/Admin/Store/Template", new { id = t.Id })!, t.Name, t.Description ?? "", true,
            t.Name, Accent: t.AccentColor,
            NoteKey: TemplateOverridden(t.Name) ? "profiles.overriddenLocally" : null,
            PreviewUrl: Url.Page("/Admin/TemplatePreview", new { kind = "store", id = t.Id }))),
        .. Templates.Select(t => new Shared.PayloadTile(
            Url.Page("Template", new { profileId = Item.Id, id = t.Id })!, t.Name,
            $"{t.HeadingFont} / {t.BodyFont}", false,
            $"{t.Name} {t.HeadingFont} {t.BodyFont}", Accent: t.AccentColor,
            NoteKey: Item.ActivateTemplateName == t.Name ? "profiles.templateActive" : null,
            PreviewUrl: Url.Page("/Admin/TemplatePreview", new { kind = "profile", id = t.Id })))
    ], "profiles.noTemplates");

    public StorePicker PluginPicker => new(Item.Id, "plugins",
        StorePlugins.Where(p => !SelectedPlugins.Contains(p.Id))
            .Select(p => new PickerItem(p.Id, p.Name, $"{p.Key} {p.Version}")).ToList());

    public StorePicker TemplatePicker => new(Item.Id, "templates",
        StoreTemplates.Where(t => !SelectedTemplates.Contains(t.Id))
            .Select(t => new PickerItem(t.Id, t.Name, t.Description ?? "")).ToList());

    public StorePicker ComponentPicker => new(Item.Id, "components",
        StoreComponents.Where(c => !SelectedComponents.Contains(c.Id))
            .Select(c => new PickerItem(c.Id, c.Name, c.Type)).ToList());

    public StorePicker UserPicker => new(Item.Id, "users",
        GlobalUsers.Where(u => !SelectedUsers.Contains(u.Id))
            .Select(u => new PickerItem(u.Id, u.Username, u.Email ?? "")).ToList());

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

    /// <summary>The cloud's own SMTP values. Shown read-only in the profile form while "use the
    /// global configuration" is ticked, so the operator sees what would actually be rolled out
    /// instead of a set of empty boxes.</summary>
    public Dictionary<string, string?> GlobalSmtp { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Global(string key) => GlobalSmtp.TryGetValue(key, out var v) ? (v ?? "") : "";

    /// <summary>What the form shows for an SMTP key: the global value while the global configuration
    /// is in use, the profile's own otherwise.</summary>
    public string SmtpField(string key) => Item.UseGlobalSmtp ? Global(key) : Setting(key);

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

        var smtpKeys = SettingKeys.Smtp;
        GlobalSmtp = await _db.CloudSettings.AsNoTracking().Where(x => smtpKeys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Takes entries from the global store into this profile. Additive on purpose: the operator picks
    /// from a list of what is NOT in the profile yet and adds it, exactly like creating one — removing
    /// is the row action on the list, not the absence of a tick in a form they may never have opened.
    /// </summary>
    public async Task<IActionResult> OnPostAddFromStoreAsync(int id, string kind, int[]? ids)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage("Index");

        var wanted = (ids ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            TempData["FlashError"] = "Nichts ausgewählt.";
            return RedirectToPage(new { id, tab = kind });
        }

        var added = kind switch
        {
            "plugins" => await AddAsync(_db.ProfileStorePlugins, x => x.ProfileId == id, x => x.StorePluginId,
                wanted, sid => new ProfileStorePlugin { ProfileId = id, StorePluginId = sid }),
            "templates" => await AddAsync(_db.ProfileStoreTemplates, x => x.ProfileId == id, x => x.StoreTemplateId,
                wanted, sid => new ProfileStoreTemplate { ProfileId = id, StoreTemplateId = sid }),
            "components" => await AddAsync(_db.ProfileStoreComponents, x => x.ProfileId == id, x => x.StoreComponentId,
                wanted, sid => new ProfileStoreComponent { ProfileId = id, StoreComponentId = sid }),
            "users" => await AddAsync(_db.ProfileGlobalUsers, x => x.ProfileId == id, x => x.UserId,
                wanted, sid => new ProfileGlobalUser { ProfileId = id, UserId = sid }),
            _ => 0
        };

        if (added > 0)
        {
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(id);
        }
        TempData["Flash"] = $"{added} aus dem Store übernommen.";
        return RedirectToPage(new { id, tab = kind });
    }

    /// <summary>Drops one global entry from this profile. The entry itself stays in the store, and
    /// anything already rolled out stays on the instances — this only stops future rollouts.</summary>
    public async Task<IActionResult> OnPostRemoveFromStoreAsync(int id, string kind, int storeId)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage("Index");

        switch (kind)
        {
            case "plugins":
                _db.ProfileStorePlugins.RemoveRange(_db.ProfileStorePlugins.Where(x => x.ProfileId == id && x.StorePluginId == storeId));
                break;
            case "templates":
                _db.ProfileStoreTemplates.RemoveRange(_db.ProfileStoreTemplates.Where(x => x.ProfileId == id && x.StoreTemplateId == storeId));
                break;
            case "components":
                _db.ProfileStoreComponents.RemoveRange(_db.ProfileStoreComponents.Where(x => x.ProfileId == id && x.StoreComponentId == storeId));
                break;
            case "users":
                _db.ProfileGlobalUsers.RemoveRange(_db.ProfileGlobalUsers.Where(x => x.ProfileId == id && x.UserId == storeId));
                break;
            default:
                return RedirectToPage(new { id });
        }

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        TempData["Flash"] = "Aus dem Profil entfernt.";
        return RedirectToPage(new { id, tab = kind });
    }

    /// <summary>Adds the links that are not there yet and reports how many. Re-adding something the
    /// profile already has is a no-op, not a duplicate row.</summary>
    private async Task<int> AddAsync<TLink>(
        Microsoft.EntityFrameworkCore.DbSet<TLink> set,
        System.Linq.Expressions.Expression<Func<TLink, bool>> mine,
        Func<TLink, int> targetId, List<int> wanted, Func<int, TLink> create) where TLink : class
    {
        var existing = (await set.Where(mine).ToListAsync()).Select(targetId).ToHashSet();
        var fresh = wanted.Where(w => !existing.Contains(w)).ToList();
        foreach (var add in fresh) set.Add(create(add));
        return fresh.Count;
    }

    // --- General + policy ---------------------------------------------------

    public async Task<IActionResult> OnPostGeneralAsync(
        int id, string name, string? description, bool autoApprove, bool isDefault,
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
        int id, bool syncSmtp, bool useGlobalSmtp, bool clearPassword,
        string? host, string? port, string? user, string? password,
        string? fromEmail, string? fromName, bool ssl)
    {
        var profile = await _db.Profiles.FindAsync(id);
        if (profile is null) return RedirectToPage("Index");

        profile.SyncSmtp = syncSmtp;

        // With the group switched off the fields are hidden, and hidden inputs still post — empty.
        // Writing them would wipe values the operator only meant to stop rolling out, so the switch
        // alone is saved and everything below is left as it stands. That INCLUDES UseGlobalSmtp: it
        // is the one value with no rendered field to re-post it, so overwriting it here would lose
        // it for good and quietly stop the global mail configuration from being rolled out when the
        // group is switched back on.
        if (!syncSmtp)
        {
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(id);
            TempData["Flash"] = "SMTP wird von diesem Profil nicht ausgerollt.";
            return RedirectToPage(new { id, tab = "settings" });
        }

        profile.UseGlobalSmtp = useGlobalSmtp;

        // With the global configuration in use the fields are shown READ-ONLY, filled with the global
        // values — so what posts back is the global data, not this profile's. Writing it would
        // quietly copy the global values into the profile and they would stop following the global
        // ones. The profile's own values stay untouched and reappear the moment the box is unticked.
        if (useGlobalSmtp)
        {
            await _db.SaveChangesAsync();
            await _profiles.TouchAsync(id);
            TempData["Flash"] = "Globale SMTP-Einstellungen werden ausgerollt.";
            return RedirectToPage(new { id, tab = "settings" });
        }

        // An empty password keeps the stored one — the field is rendered blank on purpose, so saving
        // the form must not wipe the secret.
        await UpsertSettingAsync(id, "smtp.host", host?.Trim());
        await UpsertSettingAsync(id, "smtp.port", port?.Trim());
        await UpsertSettingAsync(id, "smtp.user", user?.Trim());
        // Encrypted before it ever reaches the database. An empty field keeps the stored value, so
        // saving the form does not wipe the secret.
        if (clearPassword)
            await UpsertSettingAsync(id, "smtp.password", "", secret: true);
        else if (!string.IsNullOrEmpty(password))
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

            var name = JsonImport.Text(root, "Name").Trim();
            if (name.Length == 0)
            {
                TempData["FlashError"] = "Im JSON fehlt der Name.";
                return RedirectToPage(new { id, tab = "templates" });
            }

            parsed = new ProfileTemplate
            {
                Name = name,
                AccentColor = JsonImport.Text(root, "AccentColor", "#de7e11"),
                SecondaryColor = JsonImport.Text(root, "SecondaryColor"),
                HeadingFont = JsonImport.Text(root, "HeadingFont", "Geologica"),
                BodyFont = JsonImport.Text(root, "BodyFont", "Inter"),
                ButtonStyle = JsonImport.Text(root, "ButtonStyle", "solid"),
                HeadingColor = JsonImport.Text(root, "HeadingColor", "#010101"),
                TextColor = JsonImport.Text(root, "TextColor", "#1a1a1a"),
                BackgroundColor = JsonImport.Text(root, "BackgroundColor", "#ffffff"),
                AltBackground = JsonImport.Text(root, "AltBackground", "#f6f7f9"),
                ContainerWidth = JsonImport.Text(root, "ContainerWidth", "1180"),
                ButtonRadius = JsonImport.Text(root, "ButtonRadius", "0"),
                HeaderBackground = JsonImport.Text(root, "HeaderBackground"),
                HeaderTextColor = JsonImport.Text(root, "HeaderTextColor"),
                HeaderPadding = JsonImport.Text(root, "HeaderPadding", "16"),
                CustomCss = JsonImport.Text(root, "CustomCss"),
                CustomJs = JsonImport.Text(root, "CustomJs"),
                LayoutHtml = JsonImport.Text(root, "LayoutHtml"),
                MenuMapJson = JsonImport.Raw(root, "MenuMapJson", "{}"),
                ParametersJson = JsonImport.Raw(root, "ParametersJson", "[]"),
                ParamValuesJson = JsonImport.Raw(root, "ParamValuesJson", "{}"),
                SchemaVersion = JsonImport.Int(root, "SchemaVersion", 1),
                PartsJson = JsonImport.Raw(root, "PartsJson", "{}")
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

    /// <summary>
    /// Imports a component from the JSON a component editor exports. Same shape on both sides, so a
    /// component built on an instance can be pasted straight into a profile.
    /// <para>The TYPE is the identity: importing one that already exists updates it in place rather
    /// than creating a second row the sync would then fight over.</para>
    /// </summary>
    public async Task<IActionResult> OnPostImportComponentAsync(int id, string? componentJson)
    {
        using var doc = Services.JsonImport.TryParse(componentJson);
        if (doc is null)
        {
            TempData["FlashError"] = "Bitte gültiges Komponenten-JSON einfügen.";
            return RedirectToPage(new { id, tab = "components" });
        }

        var root = doc.RootElement;
        var type = Services.JsonImport.Text(root, "Type").Trim().ToLowerInvariant();
        var name = Services.JsonImport.Text(root, "Name").Trim();
        if (type.Length == 0 || name.Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlen Typ oder Name.";
            return RedirectToPage(new { id, tab = "components" });
        }

        var row = await _db.ProfileComponents.FirstOrDefaultAsync(c => c.ProfileId == id && c.Type == type);
        if (row is null)
        {
            row = new ProfileComponent { ProfileId = id, Type = type };
            _db.ProfileComponents.Add(row);
        }
        row.Name = name;
        row.Description = Services.JsonImport.Text(root, "Description");
        row.Icon = Services.JsonImport.Text(root, "Icon");
        row.FieldsJson = Services.JsonImport.Raw(root, "FieldsJson", "[]");
        row.TemplateHtml = Services.JsonImport.Text(root, "TemplateHtml");

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        TempData["Flash"] = $"Komponente \"{row.Name}\" importiert.";
        return RedirectToPage(new { id, tab = "components" });
    }

    /// <summary>
    /// Imports a user from JSON. The password arrives as a HASH — the same value the sync carries, so
    /// an account can be moved between profiles without anybody handling the plaintext. A JSON
    /// without one is refused rather than creating an account nobody can log into.
    /// </summary>
    public async Task<IActionResult> OnPostImportUserAsync(int id, string? userJson)
    {
        using var doc = Services.JsonImport.TryParse(userJson);
        if (doc is null)
        {
            TempData["FlashError"] = "Bitte gültiges Benutzer-JSON einfügen.";
            return RedirectToPage(new { id, tab = "users" });
        }

        var root = doc.RootElement;
        var username = Services.JsonImport.Text(root, "Username").Trim();
        var hash = Services.JsonImport.Text(root, "PasswordHash").Trim();
        if (username.Length == 0 || hash.Length == 0)
        {
            TempData["FlashError"] = "Im JSON fehlen Benutzername oder Passwort-Hash.";
            return RedirectToPage(new { id, tab = "users" });
        }

        var row = await _db.ProfileUsers.FirstOrDefaultAsync(u => u.ProfileId == id && u.Username == username);
        if (row is null)
        {
            row = new ProfileUser { ProfileId = id, Username = username };
            _db.ProfileUsers.Add(row);
        }
        row.Email = Services.JsonImport.Text(root, "Email");
        row.DisplayName = Services.JsonImport.Text(root, "DisplayName");
        row.PasswordHash = hash;
        row.Role = Services.JsonImport.Text(root, "Role", "Admin");

        await _db.SaveChangesAsync();
        await _profiles.TouchAsync(id);
        TempData["Flash"] = $"Benutzer \"{row.Username}\" importiert.";
        return RedirectToPage(new { id, tab = "users" });
    }
}
