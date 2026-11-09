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
        // A new profile starts with the recommended settings — rows like any other, so they can be changed or
        // deleted; they are only the starting point that makes every cloud feature work out of the box.
        foreach (var e in InstanceSettingCatalog.Recommended)
            _db.ProfileSettings.Add(new ProfileSetting { ProfileId = profile.Id, Key = e.Key, Value = e.Recommended });
        await _db.SaveChangesAsync();
        return profile;
    }

    /// <summary>The recommended settings a profile does not carry (neither with the recommended nor any other value).</summary>
    public async Task<List<InstanceSettingCatalog.Entry>> MissingRecommendedAsync(int profileId)
    {
        var keys = await _db.ProfileSettings.Where(s => s.ProfileId == profileId).Select(s => s.Key).ToListAsync();
        return InstanceSettingCatalog.Recommended
            .Where(e => !keys.Any(k => string.Equals(k, e.Key, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Adds the missing recommended rows (never touching a row that exists, whatever its value) and bumps the
    /// revision so they roll out. Returns how many were added.</summary>
    public async Task<int> AddRecommendedAsync(int profileId)
    {
        var missing = await MissingRecommendedAsync(profileId);
        foreach (var e in missing)
            _db.ProfileSettings.Add(new ProfileSetting { ProfileId = profileId, Key = e.Key, Value = e.Recommended });
        if (missing.Count == 0) return 0;
        await _db.SaveChangesAsync();
        await TouchAsync(profileId);
        return missing.Count;
    }

    /// <summary>
    /// Clones a profile with everything it rolls out — every setting, user, plugin (bundle and all),
    /// component, template and mail template it OWNS, plus every store item and global user it merely
    /// REFERENCES — into a brand-new profile. What is NOT copied is what makes a profile itself: the
    /// clone gets a fresh join code, is never the default, starts at revision 1 and has zero assigned
    /// instances (an instance stays with the profile it enrolled in). Rows are read detached
    /// (<c>AsNoTracking</c>) and re-added with <c>Id = 0</c> under the new <c>ProfileId</c>, so every
    /// column travels without being named here — a field added to a payload later cannot be silently
    /// dropped from the copy. Store links keep their <c>Store*Id</c>: a reference, not a second copy.
    /// </summary>
    public async Task<Profile> DuplicateAsync(int sourceId)
    {
        var clone = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sourceId)
            ?? throw new InvalidOperationException("Profil nicht gefunden.");

        // Name has a unique index, so find a free "(Kopie)" / "(Kopie 2)" / … variant first.
        var stem = clone.Name;
        var name = $"{stem} (Kopie)";
        for (var n = 2; await _db.Profiles.AnyAsync(p => p.Name == name); n++)
            name = $"{stem} (Kopie {n})";

        clone.Id = 0;
        clone.Name = name;
        clone.JoinCode = NewJoinCode();
        clone.IsDefault = false;
        clone.Revision = 1;
        clone.CreatedAt = DateTime.UtcNow;
        _db.Profiles.Add(clone);
        await _db.SaveChangesAsync();   // assigns clone.Id

        // Re-insert each payload row under the new profile. Detached rows given Id = 0 become fresh
        // inserts; only Id and ProfileId are touched, so bundles, encrypted secret values and every
        // other column come along unchanged.
        async Task Copy<T>(IQueryable<T> query, Action<T> repoint) where T : class
        {
            var rows = await query.AsNoTracking().ToListAsync();
            foreach (var r in rows) repoint(r);
            _db.AddRange(rows);
        }

        // Own-copy payloads (the profile's own data).
        await Copy(_db.ProfileSettings.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileUsers.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfilePlugins.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileComponents.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileTemplates.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileMailTemplates.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        // Store selections + global users (references — the shared store/user row is untouched).
        await Copy(_db.ProfileStorePlugins.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileStoreTemplates.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileStoreComponents.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileStoreMailTemplates.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await Copy(_db.ProfileGlobalUsers.Where(x => x.ProfileId == sourceId), x => { x.Id = 0; x.ProfileId = clone.Id; });
        await _db.SaveChangesAsync();

        return clone;
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

    /// <summary>The bundle (ZIP) of a plugin for the instances of a profile. Same precedence as the config itself: the profile's own plugin
    /// overrides the store entry it selected, so an instance never gets a different bundle than the config promised. Null when neither has it.</summary>
    public async Task<byte[]?> FindPluginBundleAsync(int profileId, string key)
    {
        // With inheritance: nearest profile first, the same order in which the config lets a child's plugin win. The
        // profile itself is always asked (as before); a parent only while it rolls plugins out at all.
        var self = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId);
        var chain = self is null ? new List<Profile>() : await ProfileInheritance.ChainAsync(_db, self);
        var ids = chain.Count == 0 ? new List<int> { profileId }
            : chain.AsEnumerable().Reverse().Where(p => p.Id == profileId || p.SyncPlugins).Select(p => p.Id).ToList();
        foreach (var id in ids)
        {
            var bundle = (await _db.ProfilePlugins.AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProfileId == id && p.Key == key))?.Bundle;

            bundle ??= (await _db.ProfileStorePlugins.AsNoTracking()
                .Where(x => x.ProfileId == id && x.StorePlugin!.Key == key)
                .Select(x => x.StorePlugin!)
                .FirstOrDefaultAsync())?.Bundle;
            if (bundle is not null) return bundle;
        }
        return null;
    }

    /// <summary>Sets (or clears, with null) the profile <paramref name="profileId"/> inherits from. Refuses a loop, itself, a
    /// missing profile and a chain deeper than <see cref="ProfileInheritance.MaxDepth"/>; returns the reason, or null when
    /// done. Bumps the revision (of the profile and everything below it) — a new parent is a new configuration.</summary>
    public async Task<string?> SetParentAsync(int profileId, int? parentId)
    {
        var all = await _db.Profiles.ToDictionaryAsync(p => p.Id);
        if (!all.TryGetValue(profileId, out var profile)) return "Profil nicht gefunden.";
        if (parentId is <= 0) parentId = null;
        if (profile.ParentId == parentId) return null;
        if (ProfileInheritance.ParentProblem(profileId, parentId, all) is { } problem) return problem;
        profile.ParentId = parentId;
        await _db.SaveChangesAsync();
        await TouchAsync(profileId);
        return null;
    }

    /// <summary>The profiles <paramref name="profileId"/> may inherit from: every other profile except the ones below it.</summary>
    public async Task<List<Profile>> ParentChoicesAsync(int profileId)
    {
        var all = await _db.Profiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        var below = ProfileInheritance.Descendants(profileId, all);
        var map = all.ToDictionary(p => p.Id);
        return all.Where(p => p.Id != profileId && !below.Contains(p.Id) && ProfileInheritance.ParentProblem(profileId, p.Id, map) is null).ToList();
    }

    /// <summary>The names of the profiles that inherit directly from <paramref name="profileId"/> — a profile with children
    /// cannot be deleted (they would silently lose half their configuration).</summary>
    public async Task<List<string>> ChildNamesAsync(int profileId) =>
        await _db.Profiles.AsNoTracking().Where(p => p.ParentId == profileId).OrderBy(p => p.Name).Select(p => p.Name).ToListAsync();

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
        // A change to a parent is a change to every profile that inherits from it: their instances compare only their OWN
        // profile's revision, so each one below is bumped too (otherwise a parent's edit would silently never arrive there).
        var all = await _db.Profiles.ToListAsync();
        foreach (var id in ProfileInheritance.Descendants(profileId, all))
            all.First(p => p.Id == id).Revision++;
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

    /// <summary>The machine-translation credentials. Prefix-matched like the SMTP block, so a key
    /// added to the group later needs no change here.</summary>
    public static bool IsTranslateKey(string key) =>
        key.StartsWith("translate.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The backup policy: whether a site backs itself up, how often, and whether the result
    /// is handed to the cloud. A group like the other two — the pieces only make sense together, and
    /// "back this site up automatically" must be switched on deliberately, not inherited because a
    /// field was once filled in.</summary>
    public static bool IsBackupKey(string key) =>
        key.StartsWith("backup.", StringComparison.OrdinalIgnoreCase);

    /// <summary>AI keys belong to the AI group. Its own switch (<see cref="Profile.SyncAi"/>) drives a
    /// dedicated wire field (<c>AiTransport</c>), and — crucially — the central provider key must NEVER
    /// be pushable as a free row, so <c>ai.*</c> is treated as a group key and excluded from the
    /// free-settings rollout / suggestion list.</summary>
    public static bool IsAiKey(string key) =>
        key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for any key that belongs to a settings GROUP rather than to the free key/value
    /// rows. Those keys are only rolled out when their own group is switched on, so a free row
    /// carrying one would sit in the profile looking active and never arrive anywhere — which is why
    /// the setting editor refuses them outright.</summary>
    public static bool IsGroupKey(string key) =>
        IsSmtpKey(key) || IsTranslateKey(key) || IsBackupKey(key) || IsAiKey(key)
        || string.Equals(key, "mail.transport", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the payload an approved instance downloads. Sections the profile does not
    /// sync are left null, which the instance reads as "don't touch this".
    /// <para>With inheritance (<see cref="ProfileInheritance"/>) every section is gathered over the chain, root first, so a
    /// child's item of the same identity is written last and wins. A profile's rows count only while ITS OWN switch for
    /// that group is on. <paramref name="origins"/>, when given, learns which profile each item came from ("kind:identity" →
    /// profile name) — that is what the profile page's "Wirksam" tab shows.</para></summary>
    public async Task<InstanceConfig> BuildConfigAsync(Profile profile, CancellationToken ct = default, Dictionary<string, string>? origins = null)
    {
        var chain = await ProfileInheritance.ChainAsync(_db, profile, ct);
        var eff = ProfileInheritance.Effective(chain);
        void From(string kind, string id, Profile layer) { if (origins is not null) origins[kind + ":" + id] = layer.Name; }

        var config = new InstanceConfig
        {
            Revision = profile.Revision,
            ProfileName = profile.Name,
            ProfileId = profile.Id,
            SettingsMode = Wire(eff.SettingsMode),
            ComponentsMode = Wire(eff.ComponentsMode),
            PluginsMode = Wire(eff.PluginsMode),
            TemplatesMode = Wire(eff.TemplatesMode),
            MailTemplatesMode = Wire(eff.MailTemplatesMode),
            UsersMode = Wire(eff.UsersMode),
            // Only meaningful when templates are actually rolled out — otherwise the instance would
            // be told to activate a design it never received.
            ActivateTemplate = eff.SyncTemplates ? eff.ActivateTemplateName : null,
            // Independent of the user rollout: the instance's own guards (default password untouched +
            // another Admin present) decide whether it is safe to drop the default admin.
            RemoveDefaultAdmin = eff.RemoveDefaultAdmin,
            NativePlugins = NativePluginsPolicy.Normalize(eff.NativePlugins) is { Length: > 0 } np ? np : null
        };

        // Each payload is resolved the same way: what the profile SELECTED from the global store,
        // then the profile's OWN items on top. A local item with the same identity wins — that is
        // what "override" means here, and it is why the merge is keyed on the instance-side identity
        // (setting key, username, component type, template name, plugin key) and not on a row id.
        // Three independent groups share one section on the wire: the free key/value rows, the mail
        // configuration and the translation credentials. Each is governed by its OWN switch.
        //
        // It used to hang on SyncSettings alone, which was a trap: ticking "SMTP ausrollen" did
        // nothing at all unless the umbrella switch happened to be on too, and nothing said so. The
        // profile's Einstellungen tab lists the three as separate things, so they have to behave as
        // separate things.
        if (eff.SyncSettings || eff.SyncSmtp || eff.SyncTranslation || eff.SyncBackup)
        {
            // Secrets are stored encrypted and only decrypted here, on the way to the instance over
            // its authenticated channel — they are never held in the clear at rest.
            var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            void DropSmtp() { foreach (var k in settings.Keys.Where(IsSmtpKey).ToList()) settings.Remove(k); }
            string? mailState = null;

            foreach (var layer in chain)
            {
                // Mail, per layer: the global values or this layer's own, never a half-merge of global and own within one
                // profile — that is what the read-only form promises. With the relay there is nothing to roll out at all,
                // because the instance will not be sending anything itself. Own SMTP below another own or global block
                // (inheritance) only overrides what it fills in.
                var overlay = false;
                if (layer.SyncSmtp)
                {
                    if (layer.MailSource == MailSources.Global)
                    {
                        DropSmtp();
                        var smtpKeys = SettingKeys.Smtp;
                        foreach (var row in await _db.CloudSettings.AsNoTracking().Where(s => smtpKeys.Contains(s.Key)).ToListAsync(ct))
                        {
                            settings[row.Key] = _secrets.Unprotect(row.Value);
                            From("setting", row.Key, layer);
                        }
                    }
                    else if (layer.MailSource == MailSources.Cloud) DropSmtp();
                    else if (mailState is MailSources.Own or MailSources.Global) overlay = true;
                    else DropSmtp();
                    mailState = layer.MailSource;
                }

                // The layer's own rows. Each group only while its own switch is on: values stay stored here so
                // unticking loses nothing, but a group nobody switched on must not overwrite a live site.
                foreach (var local in await _db.ProfileSettings.AsNoTracking().Where(s => s.ProfileId == layer.Id).ToListAsync(ct))
                {
                    if (IsSmtpKey(local.Key))
                    {
                        if (!layer.SyncSmtp || layer.MailSource != MailSources.Own) continue;
                        var value = local.IsSecret ? _secrets.Unprotect(local.Value) : local.Value;
                        // Over SMTP values from above, an empty field is "not mine to say" — a child that only needs another
                        // sender address enters only that. A profile on its own rolls out exactly what it stores.
                        if (overlay && string.IsNullOrEmpty(value)) continue;
                        settings[local.Key] = value;
                        From("setting", local.Key, layer);
                        continue;
                    }
                    // Same rule for the translation credentials and the backup policy: stored here either way, rolled
                    // out only when their own group is switched on.
                    if (IsTranslateKey(local.Key)) { if (!layer.SyncTranslation) continue; }
                    else if (IsBackupKey(local.Key)) { if (!layer.SyncBackup) continue; }
                    // Everything else is a free key/value row and belongs to the free-settings switch.
                    else if (!layer.SyncSettings) continue;

                    settings[local.Key] = local.IsSecret ? _secrets.Unprotect(local.Value) : local.Value;
                    From("setting", local.Key, layer);
                }
            }

            // "The site's name as sender name" travels with SMTP the instance sends through itself; for the relay the
            // cloud names the sender on each message (MailSpool). Always written while such SMTP is in effect, so switching
            // it off reaches the sites too. An older instance stores it as an unknown row and ignores it.
            if (eff.SyncSmtp && eff.MailSource != MailSources.Cloud)
                settings[SmtpFromSiteNameKey] = eff.MailFromSiteName ? "1" : "0";

            config.Settings = settings;
        }

        // How the instance should send. Only when this profile actually decides mail delivery —
        // otherwise the site keeps doing whatever it was doing, which is the whole point of the
        // group switch.
        if (eff.SyncSmtp && eff.MailSource == MailSources.Cloud)
            config.MailTransport = "cloud";

        // AI: when the profile's AI group is on, the instance relays model calls through the cloud.
        // The credential itself is never rolled out — only this "use the relay" flag.
        if (eff.SyncAi)
        {
            config.AiTransport = "cloud";
            config.AiInstruction = string.IsNullOrWhiteSpace(eff.AiInstruction) ? null : eff.AiInstruction.Trim();
            // Per-action guidance map (empty/"{}" = only built-in defaults on the instance).
            var guides = (eff.AiActionGuidesJson ?? "").Trim();
            config.AiActionGuidesJson = guides is "" or "{}" ? null : guides;
        }

        if (eff.SyncUsers)
        {
            var users = new Dictionary<string, ConfigUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in chain.Where(l => l.SyncUsers))
            {
                // Global first: the cloud's own accounts that were assigned to this profile. Same hash
                // format as MatCMS uses, so the account works on the instance as it does here.
                foreach (var u in await _db.ProfileGlobalUsers.AsNoTracking().Where(x => x.ProfileId == layer.Id).Select(x => x.User!).ToListAsync(ct))
                {
                    users[u.Username] = new ConfigUser { Username = u.Username, Email = u.Email, DisplayName = u.DisplayName, PasswordHash = u.PasswordHash, Role = u.Role };
                    From("user", u.Username, layer);
                }
                foreach (var local in await _db.ProfileUsers.AsNoTracking().Where(u => u.ProfileId == layer.Id).ToListAsync(ct))
                {
                    users[local.Username] = new ConfigUser { Username = local.Username, Email = local.Email, DisplayName = local.DisplayName, PasswordHash = local.PasswordHash, Role = local.Role };
                    From("user", local.Username, layer);
                }
            }
            config.Users = users.Values.ToList();
        }

        if (eff.SyncComponents)
        {
            var components = new Dictionary<string, ConfigComponent>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in chain.Where(l => l.SyncComponents))
            {
                foreach (var c in await _db.ProfileStoreComponents.AsNoTracking().Where(x => x.ProfileId == layer.Id).Select(x => x.StoreComponent!).ToListAsync(ct))
                {
                    components[c.Type] = new ConfigComponent { Type = c.Type, Name = c.Name, Description = c.Description, Icon = c.Icon, FieldsJson = c.FieldsJson, TemplateHtml = c.TemplateHtml };
                    From("component", c.Type, layer);
                }
                foreach (var local in await _db.ProfileComponents.AsNoTracking().Where(c => c.ProfileId == layer.Id).ToListAsync(ct))
                {
                    components[local.Type] = new ConfigComponent { Type = local.Type, Name = local.Name, Description = local.Description, Icon = local.Icon, FieldsJson = local.FieldsJson, TemplateHtml = local.TemplateHtml };
                    From("component", local.Type, layer);
                }
            }
            config.Components = components.Values.ToList();
        }

        if (eff.SyncMailTemplates)
        {
            var mails = new Dictionary<string, ConfigMailTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in chain.Where(l => l.SyncMailTemplates))
            {
                foreach (var m in await _db.ProfileStoreMailTemplates.AsNoTracking().Where(x => x.ProfileId == layer.Id).Select(x => x.StoreMailTemplate!).ToListAsync(ct))
                {
                    mails[m.Key] = new ConfigMailTemplate { Key = m.Key, Name = m.Name, Description = m.Description, Subject = m.Subject, Body = m.Body, Enabled = m.Enabled, IsHtml = m.IsHtml };
                    From("mail", m.Key, layer);
                }
                foreach (var local in await _db.ProfileMailTemplates.AsNoTracking().Where(m => m.ProfileId == layer.Id).ToListAsync(ct))
                {
                    mails[local.Key] = new ConfigMailTemplate { Key = local.Key, Name = local.Name, Description = local.Description, Subject = local.Subject, Body = local.Body, Enabled = local.Enabled, IsHtml = local.IsHtml };
                    From("mail", local.Key, layer);
                }
            }
            config.MailTemplates = mails.Values.ToList();
        }

        if (eff.SyncTemplates)
        {
            var templates = new Dictionary<string, ConfigTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in chain.Where(l => l.SyncTemplates))
            {
                foreach (var t in await _db.ProfileStoreTemplates.AsNoTracking().Where(x => x.ProfileId == layer.Id).Select(x => x.StoreTemplate!).ToListAsync(ct))
                {
                    templates[t.Name] = new ConfigTemplate
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
                    };
                    From("template", t.Name, layer);
                }
                foreach (var local in await _db.ProfileTemplates.AsNoTracking().Where(t => t.ProfileId == layer.Id).ToListAsync(ct))
                {
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
                    From("template", local.Name, layer);
                }
            }
            config.Templates = templates.Values.ToList();
        }

        if (eff.SyncPlugins)
        {
            // Metadata only — the bundles are fetched one by one, and only for plugins whose version
            // actually differs from what the instance already has (FindPluginBundleAsync walks the same chain).
            var plugins = new Dictionary<string, ConfigPlugin>(StringComparer.OrdinalIgnoreCase);
            foreach (var layer in chain.Where(l => l.SyncPlugins))
            {
                foreach (var p in await _db.ProfileStorePlugins.AsNoTracking().Where(x => x.ProfileId == layer.Id).Select(x => x.StorePlugin!).ToListAsync(ct))
                {
                    plugins[p.Key] = new ConfigPlugin { Key = p.Key, Name = p.Name, Version = p.Version };
                    From("plugin", p.Key, layer);
                }
                foreach (var local in await _db.ProfilePlugins.AsNoTracking().Where(p => p.ProfileId == layer.Id)
                             .Select(p => new { p.Key, p.Name, p.Version }).ToListAsync(ct))
                {
                    plugins[local.Key] = new ConfigPlugin { Key = local.Key, Name = local.Name, Version = local.Version };
                    From("plugin", local.Key, layer);
                }
            }
            config.Plugins = plugins.Values.ToList();
        }

        return config;
    }

    /// <summary>The instance setting that makes a site use its own name as the sender name of SMTP it sends itself.</summary>
    public const string SmtpFromSiteNameKey = "smtp.fromSiteName";

    /// <summary>
    /// The configuration ONE instance gets: its profile's (with inheritance) plus what the operator decided for this site alone —
    /// its native-plugin switch and its sender name. The sender name only reaches SMTP the profile rolls out (a fixed name over
    /// "the site's name"); for the relay the cloud names the sender on each message, and a site that keeps its own mail
    /// configuration is left alone.
    /// </summary>
    public async Task<InstanceConfig> BuildConfigForAsync(Instance instance, CancellationToken ct = default)
    {
        if (instance.Profile is null) return new InstanceConfig { Revision = 0 };
        var config = await BuildConfigAsync(instance.Profile, ct);
        // A site's own native-plugin decision wins over its profile's (Instance.NativePlugins).
        if (NativePluginsPolicy.Normalize(instance.NativePlugins) is { Length: > 0 } own) config.NativePlugins = own;
        var name = (instance.MailFromName ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (name.Length > 0 && config.Settings is { } settings && settings.ContainsKey(SmtpFromSiteNameKey))
        {
            settings[SettingKeys.SmtpFromName] = name.Length > 100 ? name[..100] : name;
            settings[SmtpFromSiteNameKey] = "0";
        }
        return config;
    }

    /// <summary>The effective policy for an instance: its profile's, or the global settings when it
    /// has no profile. This is the single place that decides which of the two wins.</summary>
    public sealed record Policy(bool AutoUpdateLocal, bool NotifyOffline, bool NotifyUpdate, string? Recipients);

    // WHO is told is the notification matrix (NotificationService); the policy only says whether this instance
    // raises the event at all — a profile can switch it off for its sites. Without a profile both are on.
    public static Policy PolicyFor(Profile? profile, Func<string, bool> globalFlag) =>
        profile is null
            ? new Policy(globalFlag(SettingKeys.AutoUpdateLocal), true, true, null)
            : new Policy(profile.AutoUpdateLocal, profile.NotifyOffline, profile.NotifyUpdate, profile.NotifyRecipients);
}
