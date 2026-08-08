using System.Security.Cryptography;
using System.Text;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Cloud.Services;

/// <summary>
/// Owns profiles: the join codes instances enroll with, the configuration they receive, and the
/// revision counter that drives the whole sync.
/// </summary>
public class ProfileService
{
    private readonly AppDbContext _db;
    private readonly SecretProtector _secrets;

    public ProfileService(AppDbContext db, SecretProtector secrets)
    {
        _db = db;
        _secrets = secrets;
    }

    /// <summary>
    /// Human-typeable join code: uppercase, grouped, and drawn from an alphabet without the
    /// characters people confuse (0/O, 1/I). Codes get read off a screen and typed into another
    /// machine, so ambiguity here turns into support tickets.
    /// </summary>
    public static string NewJoinCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }

    /// <summary>Normalises user input so a pasted code with stray spaces or lowercase still matches.</summary>
    public static string NormalizeJoinCode(string? raw) =>
        (raw ?? "").Trim().ToUpperInvariant().Replace(" ", "");

    public async Task<Profile> CreateAsync(string name, string? description = null)
    {
        var profile = new Profile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Neues Profil" : name.Trim(),
            Description = description?.Trim() ?? "",
            JoinCode = NewJoinCode(),
            IsDefault = !await _db.Profiles.AnyAsync()
        };
        _db.Profiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
    }

    /// <summary>Resolves an enrolling instance's join code to its profile. Compared in constant time
    /// so a code cannot be recovered character by character through timing.</summary>
    public async Task<Profile?> FindByJoinCodeAsync(string? code)
    {
        var wanted = NormalizeJoinCode(code);
        if (wanted.Length == 0) return null;

        // The candidate set is tiny (one row per profile), so scanning it keeps the comparison
        // constant-time without needing an indexed lookup on a secret.
        var wantedBytes = Encoding.UTF8.GetBytes(wanted);
        foreach (var profile in await _db.Profiles.ToListAsync())
        {
            var actual = Encoding.UTF8.GetBytes(NormalizeJoinCode(profile.JoinCode));
            if (actual.Length == wantedBytes.Length && CryptographicOperations.FixedTimeEquals(actual, wantedBytes))
                return profile;
        }
        return null;
    }

    public async Task<Profile?> DefaultProfileAsync() =>
        await _db.Profiles.FirstOrDefaultAsync(p => p.IsDefault);

    /// <summary>Makes one profile the default and clears the flag everywhere else.</summary>
    public async Task SetDefaultAsync(int profileId)
    {
        foreach (var p in await _db.Profiles.ToListAsync())
            p.IsDefault = p.Id == profileId;
        await _db.SaveChangesAsync();
    }

    public async Task<string> RotateJoinCodeAsync(Profile profile)
    {
        profile.JoinCode = NewJoinCode();
        await _db.SaveChangesAsync();
        return profile.JoinCode;
    }

    /// <summary>
    /// Marks the profile changed: every assigned instance sees a higher revision on its next
    /// heartbeat and pulls the new configuration. Call this after ANY change to the profile's
    /// payload or strategy — a change that does not bump the revision is a change that silently
    /// never arrives.
    /// </summary>
    public async Task TouchAsync(int profileId)
    {
        var profile = await _db.Profiles.FindAsync(profileId);
        if (profile is null) return;
        profile.Revision++;
        await _db.SaveChangesAsync();
    }

    /// <summary>The mode as it goes over the wire. Strings, not the enum's numbers: an instance that
    /// predates a mode falls back to the cautious "add" instead of misreading an unknown number.</summary>
    public static string Wire(SyncMode mode) => mode switch
    {
        SyncMode.Add => "add",
        SyncMode.Once => "once",
        _ => "keep"
    };

    /// <summary>
    /// Keys store rows by the identity the INSTANCE uses, case-insensitively — and deliberately
    /// "last one wins" rather than <c>ToDictionary</c>'s throw. The uniqueness behind these names is
    /// a SQLite index with BINARY collation, which is case-SENSITIVE: a store can genuinely hold
    /// both "Alpha" and "alpha". With a throwing dictionary that combination would fail every
    /// <c>/config</c> request for every instance on any profile that selected both, permanently, and
    /// nothing on the instance side would say why.
    /// </summary>
    private static Dictionary<string, TValue> Index<TRow, TValue>(
        IEnumerable<TRow> rows, Func<TRow, string> key, Func<TRow, TValue> value)
    {
        var map = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) map[key(row)] = value(row);
        return map;
    }

    /// <summary>True for the keys that make up the SMTP settings group.</summary>
    public static bool IsSmtpKey(string key) =>
        key.StartsWith("smtp.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the payload an approved instance downloads. Sections the profile does not
    /// sync are left null, which the instance reads as "don't touch this".</summary>
    public async Task<InstanceConfig> BuildConfigAsync(Profile profile, CancellationToken ct = default)
    {
        var config = new InstanceConfig
        {
            Revision = profile.Revision,
            ProfileName = profile.Name,
            ProfileId = profile.Id,
            SettingsMode = Wire(profile.SettingsMode),
            ComponentsMode = Wire(profile.ComponentsMode),
            PluginsMode = Wire(profile.PluginsMode),
            TemplatesMode = Wire(profile.TemplatesMode),
            UsersMode = Wire(profile.UsersMode),
            // Only meaningful when templates are actually rolled out — otherwise the instance would
            // be told to activate a design it never received.
            ActivateTemplate = profile.SyncTemplates ? profile.ActivateTemplateName : null
        };

        // Each payload is resolved the same way: what the profile SELECTED from the global store,
        // then the profile's OWN items on top. A local item with the same identity wins — that is
        // what "override" means here, and it is why the merge is keyed on the instance-side identity
        // (setting key, username, component type, template name, plugin key) and not on a row id.
        if (profile.SyncSettings)
        {
            // Secrets are stored encrypted and only decrypted here, on the way to the instance over
            // its authenticated channel — they are never held in the clear at rest.
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            // Global first: the cloud's own SMTP configuration, if this profile passes it on.
            if (profile.SyncSmtp && profile.UseGlobalSmtp)
            {
                var smtpKeys = SettingKeys.Smtp;
                foreach (var row in await _db.CloudSettings.AsNoTracking().Where(s => smtpKeys.Contains(s.Key)).ToListAsync(ct))
                    settings[row.Key] = _secrets.Unprotect(row.Value);
            }

            // The profile's own rows on top — that is the override. SMTP is skipped unless the group
            // is ticked ON: values stay stored here so unticking loses nothing, but a group nobody
            // switched on must not overwrite a live site's mail configuration.
            foreach (var local in await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == profile.Id).ToListAsync(ct))
            {
                if (!profile.SyncSmtp && IsSmtpKey(local.Key)) continue;
                settings[local.Key] = local.IsSecret ? _secrets.Unprotect(local.Value) : local.Value;
            }

            config.Settings = settings;
        }

        if (profile.SyncUsers)
        {
            // Global first: the cloud's own accounts that were assigned to this profile. Same hash
            // format as MatCMS uses, so the account works on the instance as it does here.
            var globalUsers = await _db.ProfileGlobalUsers.AsNoTracking()
                .Where(x => x.ProfileId == profile.Id)
                .Select(x => x.User!)
                .ToListAsync(ct);
            var users = Index(globalUsers, u => u.Username, u => new ConfigUser
                {
                    Username = u.Username, Email = u.Email, DisplayName = u.DisplayName,
                    PasswordHash = u.PasswordHash, Role = u.Role
                });

            foreach (var local in await _db.ProfileUsers.AsNoTracking().Where(u => u.ProfileId == profile.Id).ToListAsync(ct))
                users[local.Username] = new ConfigUser
                {
                    Username = local.Username, Email = local.Email, DisplayName = local.DisplayName,
                    PasswordHash = local.PasswordHash, Role = local.Role
                };

            config.Users = users.Values.ToList();
        }

        if (profile.SyncComponents)
        {
            var storeComponents = await _db.ProfileStoreComponents.AsNoTracking()
                .Where(x => x.ProfileId == profile.Id)
                .Select(x => x.StoreComponent!)
                .ToListAsync(ct);
            var components = Index(storeComponents, c => c.Type, c => new ConfigComponent
                {
                    Type = c.Type, Name = c.Name, Description = c.Description,
                    Icon = c.Icon, FieldsJson = c.FieldsJson, TemplateHtml = c.TemplateHtml
                });

            foreach (var local in await _db.ProfileComponents.AsNoTracking().Where(c => c.ProfileId == profile.Id).ToListAsync(ct))
                components[local.Type] = new ConfigComponent
                {
                    Type = local.Type, Name = local.Name, Description = local.Description,
                    Icon = local.Icon, FieldsJson = local.FieldsJson, TemplateHtml = local.TemplateHtml
                };

            config.Components = components.Values.ToList();
        }

        if (profile.SyncTemplates)
        {
            var storeTemplates = await _db.ProfileStoreTemplates.AsNoTracking()
                .Where(x => x.ProfileId == profile.Id)
                .Select(x => x.StoreTemplate!)
                .ToListAsync(ct);
            var templates = Index(storeTemplates, t => t.Name, t => new ConfigTemplate
                {
                    Name = t.Name,
                    AccentColor = t.AccentColor, SecondaryColor = t.SecondaryColor,
                    HeadingFont = t.HeadingFont, BodyFont = t.BodyFont, ButtonStyle = t.ButtonStyle,
                    HeadingColor = t.HeadingColor, TextColor = t.TextColor,
                    BackgroundColor = t.BackgroundColor, AltBackground = t.AltBackground,
                    ContainerWidth = t.ContainerWidth, ButtonRadius = t.ButtonRadius,
                    HeaderBackground = t.HeaderBackground, HeaderTextColor = t.HeaderTextColor,
                    HeaderPadding = t.HeaderPadding, CustomCss = t.CustomCss, CustomJs = t.CustomJs,
                    LayoutHtml = t.LayoutHtml, MenuMapJson = t.MenuMapJson,
                    ParametersJson = t.ParametersJson, ParamValuesJson = t.ParamValuesJson,
                    SchemaVersion = t.SchemaVersion, PartsJson = t.PartsJson
                });

            foreach (var local in await _db.ProfileTemplates.AsNoTracking().Where(t => t.ProfileId == profile.Id).ToListAsync(ct))
                templates[local.Name] = new ConfigTemplate
                {
                    Name = local.Name,
                    AccentColor = local.AccentColor, SecondaryColor = local.SecondaryColor,
                    HeadingFont = local.HeadingFont, BodyFont = local.BodyFont, ButtonStyle = local.ButtonStyle,
                    HeadingColor = local.HeadingColor, TextColor = local.TextColor,
                    BackgroundColor = local.BackgroundColor, AltBackground = local.AltBackground,
                    ContainerWidth = local.ContainerWidth, ButtonRadius = local.ButtonRadius,
                    HeaderBackground = local.HeaderBackground, HeaderTextColor = local.HeaderTextColor,
                    HeaderPadding = local.HeaderPadding, CustomCss = local.CustomCss, CustomJs = local.CustomJs,
                    LayoutHtml = local.LayoutHtml, MenuMapJson = local.MenuMapJson,
                    ParametersJson = local.ParametersJson, ParamValuesJson = local.ParamValuesJson,
                    SchemaVersion = local.SchemaVersion, PartsJson = local.PartsJson
                };

            config.Templates = templates.Values.ToList();
        }

        if (profile.SyncPlugins)
        {
            // Metadata only — the bundles are fetched one by one, and only for plugins whose version
            // actually differs from what the instance already has.
            var storePlugins = await _db.ProfileStorePlugins.AsNoTracking()
                .Where(x => x.ProfileId == profile.Id)
                .Select(x => x.StorePlugin!)
                .ToListAsync(ct);
            var plugins = Index(storePlugins, p => p.Key, p => new ConfigPlugin { Key = p.Key, Name = p.Name, Version = p.Version });

            foreach (var local in await _db.ProfilePlugins.AsNoTracking()
                         .Where(p => p.ProfileId == profile.Id)
                         .Select(p => new { p.Key, p.Name, p.Version }).ToListAsync(ct))
                plugins[local.Key] = new ConfigPlugin { Key = local.Key, Name = local.Name, Version = local.Version };

            config.Plugins = plugins.Values.ToList();
        }

        return config;
    }

    /// <summary>The effective policy for an instance: its profile's, or the global settings when it
    /// has no profile. This is the single place that decides which of the two wins.</summary>
    public sealed record Policy(bool AutoUpdateLocal, bool NotifyOffline, bool NotifyUpdate, string? Recipients);

    public static Policy PolicyFor(Profile? profile, Func<string, bool> globalFlag, string? globalRecipients) =>
        profile is null
            ? new Policy(globalFlag(SettingKeys.AutoUpdateLocal), globalFlag(SettingKeys.NotifyOffline),
                         globalFlag(SettingKeys.NotifyUpdate), globalRecipients)
            : new Policy(profile.AutoUpdateLocal, profile.NotifyOffline, profile.NotifyUpdate,
                         string.IsNullOrWhiteSpace(profile.NotifyRecipients) ? globalRecipients : profile.NotifyRecipients);
}
