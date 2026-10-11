using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MatCMS.Cloud.Data;
using MatCMS.Cloud.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Backup and restore of the CLOUD's own configuration — not of the sites (those have their own backups): its settings
/// (general, SMTP, AI, notifications, backup defaults, optionally the server-specific hosting block), all profiles with
/// everything in them, the store, the cloud's users and which instance belongs to which profile. One zip, stored under
/// appdata/config-backups, downloadable and uploadable — so moving to a new server, or getting a broken cloud back, is
/// "download here, upload there, restore", through the UI, /api/v1/cloud/config-backups or MCP alike.
/// <para>Secrets (SMTP password, AI key, proxy keys, profile secrets, 2FA secrets) are encrypted at rest with THIS
/// installation's key ring — useless anywhere else. They only travel when a passphrase is given, then decrypted here and
/// re-encrypted with it (PBKDF2-SHA256 + AES-GCM); without one they are left out and the restore keeps what the target
/// has. Cloud users are restored ADD-ONLY: an existing account is never changed — a restore must not be a way to reset a
/// password or strip a second factor (the same rule as the profile rollout and the instance restore).</para>
/// </summary>
public sealed class CloudConfigBackupService
{
    public const int Format = 1;

    /// <summary>Every section, in restore order (users before notifications, which name them; store before profiles,
    /// which link to it; profiles before assignments).</summary>
    public static readonly string[] AllSections = { "general", "smtp", "ai", "backup", "store", "profiles", "users", "notifications", "assignments", "hosting" };

    /// <summary>What a restore takes unless told otherwise: everything but the server-specific hosting block — its
    /// addresses, proxy and ports belong to the machine the backup came from.</summary>
    public static readonly string[] DefaultRestoreSections = AllSections.Where(s => s != "hosting").ToArray();

    private static readonly Dictionary<string, string[]> SettingSections = new()
    {
        ["general"] = new[] { SettingKeys.CloudName, SettingKeys.ForceHttpsUrls, SettingKeys.Require2fa, SettingKeys.AutoUpdateLocal,
            SettingKeys.LogRequests, SettingKeys.LogRetentionErrorsDays, SettingKeys.LogRetentionRequestsDays },
        ["smtp"] = SettingKeys.Smtp,
        ["ai"] = new[] { SettingKeys.AiProvider, SettingKeys.AiModel, SettingKeys.AiBaseUrl, SettingKeys.AiApiKey },
        ["notifications"] = new[] { SettingKeys.NotifyMatrix, SettingKeys.NotifyRecipients, SettingKeys.NotifyOffline, SettingKeys.NotifyUpdate },
        ["backup"] = new[] { SettingKeys.BackupQuotaGb, SettingKeys.BackupKeepDaily, SettingKeys.BackupKeepWeekly, SettingKeys.BackupKeepMonthly, SettingKeys.BackupMaxCount },
        // Runtime state (pending routes, the wildcard's route id/error) is deliberately not here: it describes routes on
        // the old server's proxy, not configuration.
        ["hosting"] = new[] { SettingKeys.CanonicalUrl, SettingKeys.HostingEnabled, SettingKeys.HostingMode, SettingKeys.HostingCaddyAdminUrl,
            SettingKeys.HostingCaddyServer, SettingKeys.HostingProxyUpstream, SettingKeys.HostingProxyNetwork, SettingKeys.HostingProxyUpstreamHost,
            SettingKeys.HostingMatcadUrl, SettingKeys.HostingMatcadToken, SettingKeys.HostingAutoDomainEnabled, SettingKeys.HostingAutoDomainBase,
            SettingKeys.HostingWildcardEnabled, SettingKeys.HostingWildcardDnsProvider, SettingKeys.HostingWildcardDnsCredentials,
            SettingKeys.HostingEdgeTrustedIps, SettingKeys.HostingEdgeEnabled, SettingKeys.HostingEdgeUseHostProxy, SettingKeys.HostingEdgeMode,
            SettingKeys.HostingEdgeCaddyAdminUrl, SettingKeys.HostingEdgeCaddyServer, SettingKeys.HostingEdgeMatcadUrl, SettingKeys.HostingEdgeMatcadToken,
            SettingKeys.HostingPortFrom, SettingKeys.HostingPortTo, SettingKeys.HostingNamePattern },
    };

    /// <summary>Stored encrypted (SecretProtector) — exported only inside the passphrase-protected block.</summary>
    private static readonly HashSet<string> SecretSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        SettingKeys.SmtpPassword, SettingKeys.AiApiKey, SettingKeys.HostingMatcadToken, SettingKeys.HostingEdgeMatcadToken,
        SettingKeys.HostingWildcardDnsCredentials,
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppDbContext _db;
    private readonly CloudContext _cloud;
    private readonly SecretProtector _secrets;
    private readonly IDataProtector _totp;
    private readonly VersionService _version;
    private readonly string _dir;

    public CloudConfigBackupService(AppDbContext db, CloudContext cloud, SecretProtector secrets, IDataProtectionProvider dp,
        VersionService version, IWebHostEnvironment env)
    {
        _db = db; _cloud = cloud; _secrets = secrets; _version = version;
        // The same purpose string as TwoFactorService — a 2FA secret is decrypted and re-encrypted exactly as it uses it.
        _totp = dp.CreateProtector("MatCMS.Cloud.TwoFactor.Secret.v1");
        _dir = Path.Combine(env.ContentRootPath, "appdata", "config-backups");
    }

    /// <summary>The setting keys of a settings section (general, smtp, ai, notifications, backup, hosting), or empty.</summary>
    public static IReadOnlyList<string> SectionKeys(string section) => SettingSections.TryGetValue(section, out var k) ? k : Array.Empty<string>();

    public static bool IsSecretSetting(string key) => SecretSettings.Contains(key);

    /// <summary>What the Einstellungen page edits and REST/MCP may change directly. Notifications and hosting have their
    /// own endpoints.</summary>
    public static readonly string[] EditableSections = { "general", "smtp", "ai", "backup" };

    /// <summary>The editable settings by section; a secret only says whether it is set.</summary>
    public Dictionary<string, Dictionary<string, object?>> GetSettings() =>
        EditableSections.ToDictionary(s => s, s => SectionKeys(s).ToDictionary(k => k,
            k => IsSecretSetting(k) ? (object?)new { set = !string.IsNullOrEmpty(_cloud.Get(k)) } : _cloud.Get(k)));

    /// <summary>Sets editable settings. Null values are skipped; a secret is only set by a non-empty value (encrypted)
    /// and only cleared through <paramref name="clear"/> — an empty field must not wipe a password by accident.</summary>
    public async Task<(bool Ok, string? Error, List<string> Changed)> SetSettingsAsync(IDictionary<string, string?>? values, IEnumerable<string>? clear)
    {
        var allowed = EditableSections.SelectMany(SectionKeys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, string?>();
        foreach (var (k, v) in values ?? new Dictionary<string, string?>())
        {
            if (!allowed.Contains(k)) return (false, $"„{k}“ ist hier nicht einstellbar (erlaubt: {string.Join(", ", allowed)}).", new());
            if (v is null) continue;
            if (IsSecretSetting(k)) { if (v.Length > 0) changes[k] = _secrets.Protect(v); }
            else changes[k] = v.Trim();
        }
        foreach (var k in clear ?? Array.Empty<string>())
        {
            if (!allowed.Contains(k)) return (false, $"„{k}“ ist hier nicht einstellbar.", new());
            changes[k] = "";
        }
        if (changes.Count > 0) await _cloud.SaveAsync(changes);
        return (true, null, changes.Keys.ToList());
    }

    // ---- the document ------------------------------------------------------------------------------------------------

    public sealed class Doc
    {
        public int Format { get; set; } = CloudConfigBackupService.Format;
        public DateTime CreatedAt { get; set; }
        public string Source { get; set; } = "";
        public string? SourceUrl { get; set; }
        public string? Version { get; set; }
        public string? Note { get; set; }
        public List<string> Sections { get; set; } = new();
        public Dictionary<string, string?> Settings { get; set; } = new();
        public List<ProfileDoc>? Profiles { get; set; }
        public StoreDoc? Store { get; set; }
        public List<UserDoc>? Users { get; set; }
        public List<AssignmentDoc>? Assignments { get; set; }
        /// <summary>Passphrase-encrypted map of secrets (see <see cref="SecretBox"/>); null = none included.</summary>
        public SecretBox? Secrets { get; set; }
    }

    public sealed class ProfileDoc
    {
        public Profile Profile { get; set; } = new();
        public List<ProfileSetting> Settings { get; set; } = new();
        public List<ProfileUser> Users { get; set; } = new();
        public List<ProfilePlugin> Plugins { get; set; } = new();
        public List<ProfileTemplate> Templates { get; set; } = new();
        public List<ProfileComponent> Components { get; set; } = new();
        public List<ProfileMailTemplate> MailTemplates { get; set; } = new();
        public List<string> StorePlugins { get; set; } = new();
        public List<string> StoreTemplates { get; set; } = new();
        public List<string> StoreComponents { get; set; } = new();
        public List<string> StoreMailTemplates { get; set; } = new();
        public List<string> GlobalUsers { get; set; } = new();
    }

    public sealed class StoreDoc
    {
        public List<StorePlugin> Plugins { get; set; } = new();
        public List<StoreTemplate> Templates { get; set; } = new();
        public List<StoreComponent> Components { get; set; } = new();
        public List<StoreMailTemplate> MailTemplates { get; set; } = new();
    }

    public sealed class UserDoc
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string Role { get; set; } = "";
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
        public string? Theme { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public string? RecoveryCodes { get; set; }
        /// <summary>Instance scope of an Operator, by instance NAME — ids differ between clouds.</summary>
        public List<string> Instances { get; set; } = new();
    }

    public sealed class AssignmentDoc
    {
        public string Instance { get; set; } = "";
        public string? PublicId { get; set; }
        public string Profile { get; set; } = "";
    }

    public sealed class SecretBox
    {
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        /// <summary>Ciphertext followed by the 16-byte GCM tag, base64.</summary>
        public string Data { get; set; } = "";
    }

    public sealed record Info(string Id, DateTime CreatedAt, long Size, string Source, string? Version, List<string> Sections, bool HasSecrets, string? Note);
    public sealed record Report(bool Ok, bool DryRun, List<string> Lines);

    // ---- storage -----------------------------------------------------------------------------------------------------

    private static readonly Regex IdRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.zip$", RegexOptions.Compiled);

    private string? PathOf(string? id) =>
        id is not null && IdRx.IsMatch(id) && File.Exists(Path.Combine(_dir, id)) ? Path.Combine(_dir, id) : null;

    public string? FilePath(string id) => PathOf(id);

    public List<Info> List()
    {
        if (!Directory.Exists(_dir)) return new();
        var list = new List<Info>();
        foreach (var f in Directory.GetFiles(_dir, "*.zip"))
        {
            try
            {
                var d = Read(File.ReadAllBytes(f));
                list.Add(new(Path.GetFileName(f), d.CreatedAt, new FileInfo(f).Length, d.Source, d.Version, d.Sections, d.Secrets is not null, d.Note));
            }
            catch { /* a file that is not a config backup is simply not listed */ }
        }
        return list.OrderByDescending(i => i.CreatedAt).ToList();
    }

    public bool Delete(string id)
    {
        var p = PathOf(id);
        if (p is null) return false;
        File.Delete(p);
        return true;
    }

    private static Doc Read(byte[] zip)
    {
        using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var e = za.GetEntry("cloud-config.json") ?? throw new InvalidDataException("Keine Cloud-Sicherung (cloud-config.json fehlt).");
        using var s = e.Open();
        var d = JsonSerializer.Deserialize<Doc>(s, Json) ?? throw new InvalidDataException("Leere Sicherung.");
        if (d.Format > Format) throw new InvalidDataException($"Die Sicherung hat Format {d.Format}, diese Cloud kennt bis {Format} — erst die Cloud aktualisieren.");
        return d;
    }

    private string Store(Doc d, string stem)
    {
        Directory.CreateDirectory(_dir);
        var safe = Regex.Replace(stem, "[^A-Za-z0-9._-]+", "-").Trim('-');
        if (safe.Length == 0) safe = "cloud-config";
        if (safe.Length > 100) safe = safe[..100];
        var id = $"{safe}.zip";
        for (var i = 2; File.Exists(Path.Combine(_dir, id)); i++) id = $"{safe}-{i}.zip";
        using var fs = File.Create(Path.Combine(_dir, id));
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        using var es = za.CreateEntry("cloud-config.json", CompressionLevel.Optimal).Open();
        JsonSerializer.Serialize(es, d, Json);
        return id;
    }

    /// <summary>Takes an uploaded backup (from another cloud, or one downloaded earlier) into the list.</summary>
    public (Info? Info, string? Error) Upload(byte[] bytes, string? fileName)
    {
        Doc d;
        try { d = Read(bytes); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { return (null, ex.Message); }
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "") is { Length: > 0 } n ? n : $"upload-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var id = Store(d, stem);
        return (List().FirstOrDefault(i => i.Id == id), null);
    }

    // ---- create ------------------------------------------------------------------------------------------------------

    public static List<string> Normalise(IEnumerable<string>? sections, IEnumerable<string> fallback)
    {
        var want = (sections ?? Array.Empty<string>()).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToHashSet();
        return want.Count == 0 ? fallback.ToList() : AllSections.Where(want.Contains).ToList();
    }

    public async Task<(Info? Info, string? Error)> CreateAsync(IEnumerable<string>? sections, string? passphrase, string? note, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(passphrase) && passphrase.Length < 8) return (null, "Das Passwort der Sicherung braucht mindestens 8 Zeichen.");
        var secs = Normalise(sections, AllSections);
        var secrets = new Dictionary<string, string>();
        var d = new Doc
        {
            CreatedAt = DateTime.UtcNow,
            Source = _cloud.Get(SettingKeys.CloudName) is { Length: > 0 } cn ? cn : "MatCMS Cloud",
            SourceUrl = _cloud.Get(SettingKeys.CanonicalUrl),
            Version = _version.Current,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Sections = secs,
        };

        var users = await _db.Users.AsNoTracking().ToListAsync(ct);
        foreach (var (section, keys) in SettingSections)
        {
            if (!secs.Contains(section)) continue;
            foreach (var key in keys)
            {
                var raw = _cloud.Get(key);
                if (raw is null) continue;
                if (SecretSettings.Contains(key))
                {
                    if (_secrets.Unprotect(raw) is { Length: > 0 } plain) secrets["setting:" + key] = plain;
                    continue;
                }
                // The matrix names users by id — ids differ on another cloud, so they travel as "u:@username".
                d.Settings[key] = key == SettingKeys.NotifyMatrix ? MapMatrixUsers(raw, id => users.FirstOrDefault(u => u.Id == id)?.Username is { } n ? "@" + n : null) : raw;
            }
        }

        if (secs.Contains("store"))
            d.Store = new StoreDoc
            {
                Plugins = await _db.StorePlugins.AsNoTracking().ToListAsync(ct),
                Templates = await _db.StoreTemplates.AsNoTracking().ToListAsync(ct),
                Components = await _db.StoreComponents.AsNoTracking().ToListAsync(ct),
                MailTemplates = await _db.StoreMailTemplates.AsNoTracking().ToListAsync(ct),
            };

        if (secs.Contains("profiles"))
        {
            d.Profiles = new();
            foreach (var p in await _db.Profiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct))
            {
                var pd = new ProfileDoc
                {
                    Profile = p,
                    Settings = await _db.ProfileSettings.AsNoTracking().Where(x => x.ProfileId == p.Id).ToListAsync(ct),
                    Users = await _db.ProfileUsers.AsNoTracking().Where(x => x.ProfileId == p.Id).ToListAsync(ct),
                    Plugins = await _db.ProfilePlugins.AsNoTracking().Where(x => x.ProfileId == p.Id).ToListAsync(ct),
                    Templates = await _db.ProfileTemplates.AsNoTracking().Where(x => x.ProfileId == p.Id).ToListAsync(ct),
                    Components = await _db.ProfileComponents.AsNoTracking().Where(x => x.ProfileId == p.Id).ToListAsync(ct),
                    MailTemplates = await _db.ProfileMailTemplates.AsNoTracking().Where(x => x.ProfileId == p.Id).ToListAsync(ct),
                    StorePlugins = await _db.ProfileStorePlugins.Where(x => x.ProfileId == p.Id).Select(x => x.StorePlugin!.Key).ToListAsync(ct),
                    StoreTemplates = await _db.ProfileStoreTemplates.Where(x => x.ProfileId == p.Id).Select(x => x.StoreTemplate!.Name).ToListAsync(ct),
                    StoreComponents = await _db.ProfileStoreComponents.Where(x => x.ProfileId == p.Id).Select(x => x.StoreComponent!.Type).ToListAsync(ct),
                    StoreMailTemplates = await _db.ProfileStoreMailTemplates.Where(x => x.ProfileId == p.Id).Select(x => x.StoreMailTemplate!.Key).ToListAsync(ct),
                    GlobalUsers = await _db.ProfileGlobalUsers.Where(x => x.ProfileId == p.Id).Select(x => x.User!.Username).ToListAsync(ct),
                };
                foreach (var s in pd.Settings.Where(s => s.IsSecret))
                {
                    if (_secrets.Unprotect(s.Value) is { Length: > 0 } plain) secrets[$"profile:{p.Name}:{s.Key}"] = plain;
                    s.Value = null;
                }
                d.Profiles.Add(pd);
            }
        }

        if (secs.Contains("users"))
        {
            var scope = await _db.UserInstances.AsNoTracking().Select(x => new { x.UserId, x.Instance!.Name }).ToListAsync(ct);
            d.Users = users.Select(u =>
            {
                if (u.TwoFactorEnabled && !string.IsNullOrEmpty(u.TotpSecret))
                {
                    try { secrets[$"user:{u.Username}:totp"] = _totp.Unprotect(u.TotpSecret); } catch { /* unreadable: left out */ }
                }
                return new UserDoc
                {
                    Username = u.Username, PasswordHash = u.PasswordHash, Role = u.Role, DisplayName = u.DisplayName, Email = u.Email,
                    Theme = u.Theme, CreatedAt = u.CreatedAt, TwoFactorEnabled = u.TwoFactorEnabled, RecoveryCodes = u.RecoveryCodes,
                    Instances = scope.Where(s => s.UserId == u.Id).Select(s => s.Name).ToList(),
                };
            }).ToList();
        }

        if (secs.Contains("assignments"))
            d.Assignments = await _db.Instances.AsNoTracking().Where(i => i.ProfileId != null)
                .Select(i => new AssignmentDoc { Instance = i.Name, PublicId = i.PublicId, Profile = i.Profile!.Name }).ToListAsync(ct);

        if (!string.IsNullOrEmpty(passphrase) && secrets.Count > 0) d.Secrets = Seal(JsonSerializer.Serialize(secrets), passphrase);

        var id = Store(d, $"cloud-config-{d.CreatedAt:yyyyMMdd-HHmmss}");
        return (List().FirstOrDefault(i => i.Id == id), null);
    }

    // ---- restore -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Restores <paramref name="sections"/> of backup <paramref name="id"/> (default: everything but hosting).
    /// <paramref name="dryRun"/> only reports what would change. Settings of a section are overwritten with what the
    /// backup holds (keys it does not hold stay); profiles are matched by NAME and replaced, or created; store items are
    /// matched by key/name/type and updated, or created; users are only ADDED; assignments match instances by name.
    /// </summary>
    public async Task<Report> RestoreAsync(string id, IEnumerable<string>? sections, string? passphrase, bool dryRun, CancellationToken ct = default)
    {
        var lines = new List<string>();
        var path = PathOf(id);
        if (path is null) return new(false, dryRun, new() { "Sicherung nicht gefunden." });
        Doc d;
        try { d = Read(await File.ReadAllBytesAsync(path, ct)); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { return new(false, dryRun, new() { ex.Message }); }

        var secs = Normalise(sections, DefaultRestoreSections).Where(d.Sections.Contains).ToList();
        if (secs.Count == 0) return new(false, dryRun, new() { "Keiner der gewählten Bereiche ist in dieser Sicherung enthalten." });

        Dictionary<string, string> secrets = new();
        if (d.Secrets is not null)
        {
            if (string.IsNullOrEmpty(passphrase)) lines.Add("Geheime Werte sind enthalten, aber kein Passwort angegeben — sie werden NICHT wiederhergestellt; vorhandene bleiben.");
            else
            {
                var open = Open(d.Secrets, passphrase);
                if (open is null) return new(false, dryRun, new() { "Das Passwort der Sicherung stimmt nicht." });
                secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(open) ?? new();
            }
        }
        else lines.Add("Die Sicherung enthält keine geheimen Werte (ohne Passwort erstellt) — Passwörter und Schlüssel bleiben, wie sie hier sind.");

        // Users first — the notification matrix and profiles name them.
        if (secs.Contains("users") && d.Users is not null) await RestoreUsersAsync(d.Users, secrets, dryRun, lines, ct);

        foreach (var (section, keys) in SettingSections)
        {
            if (!secs.Contains(section)) continue;
            var changes = new Dictionary<string, string?>();
            foreach (var key in keys)
            {
                string? value;
                if (SecretSettings.Contains(key))
                {
                    if (!secrets.TryGetValue("setting:" + key, out var plain)) continue;
                    value = _secrets.Protect(plain);
                    if (_secrets.Unprotect(_cloud.Get(key)) == plain) continue;
                }
                else
                {
                    if (!d.Settings.TryGetValue(key, out value)) continue;
                    if (key == SettingKeys.NotifyMatrix && value is not null)
                    {
                        var names = await _db.Users.AsNoTracking().ToDictionaryAsync(u => u.Username.ToLowerInvariant(), u => u.Id, ct);
                        value = MapMatrixUsers(value, null, n => names.TryGetValue(n.ToLowerInvariant(), out var uid) ? uid : null);
                    }
                    if (_cloud.Get(key) == value) continue;
                }
                changes[key] = value;
            }
            lines.Add(changes.Count == 0 ? $"{Label(section)}: unverändert." : $"{Label(section)}: {changes.Count} Wert(e) — {string.Join(", ", changes.Keys)}.");
            if (!dryRun && changes.Count > 0) await _cloud.SaveAsync(changes);
        }

        if (secs.Contains("store") && d.Store is not null) await RestoreStoreAsync(d.Store, dryRun, lines, ct);
        if (secs.Contains("profiles") && d.Profiles is not null) await RestoreProfilesAsync(d.Profiles, secrets, dryRun, lines, ct);
        if (secs.Contains("assignments") && d.Assignments is not null) await RestoreAssignmentsAsync(d.Assignments, dryRun, lines, ct);

        if (!dryRun) await _db.SaveChangesAsync(ct);
        return new(true, dryRun, lines);
    }

    private static string Label(string section) => section switch
    {
        "general" => "Allgemein", "smtp" => "SMTP", "ai" => "KI", "notifications" => "Benachrichtigungen", "backup" => "Backup-Vorgaben",
        "hosting" => "Server/Hosting", "store" => "Store", "profiles" => "Profile", "users" => "Benutzer", "assignments" => "Zuordnungen",
        _ => section,
    };

    private async Task RestoreUsersAsync(List<UserDoc> users, Dictionary<string, string> secrets, bool dryRun, List<string> lines, CancellationToken ct)
    {
        var existing = (await _db.Users.Select(u => u.Username).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var instances = await _db.Instances.Select(i => new { i.Id, i.Name }).ToListAsync(ct);
        int added = 0, kept = 0, no2fa = 0;
        foreach (var u in users)
        {
            if (existing.Contains(u.Username)) { kept++; continue; }
            added++;
            var totp = secrets.GetValueOrDefault($"user:{u.Username}:totp");
            var twoFa = u.TwoFactorEnabled && totp is not null;
            if (u.TwoFactorEnabled && !twoFa) no2fa++;
            if (dryRun) continue;
            var user = new User
            {
                Username = u.Username, PasswordHash = u.PasswordHash, Role = u.Role, DisplayName = u.DisplayName, Email = u.Email, Theme = u.Theme,
                CreatedAt = u.CreatedAt == default ? DateTime.UtcNow : u.CreatedAt,
                // Without its secret a second factor cannot be checked — the account would be locked out. It comes back
                // without 2FA (and without the recovery codes that belong to it); the 2FA policy, if on, asks again.
                TwoFactorEnabled = twoFa, TotpSecret = twoFa ? _totp.Protect(totp!) : null, RecoveryCodes = twoFa ? u.RecoveryCodes : null,
            };
            foreach (var name in u.Instances)
                if (instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) is { } inst)
                    user.Instances.Add(new UserInstance { InstanceId = inst.Id });
            _db.Users.Add(user);
        }
        if (!dryRun) await _db.SaveChangesAsync(ct);
        lines.Add($"Benutzer: {added} neu, {kept} schon vorhanden (bleiben unverändert)" + (no2fa > 0 ? $", {no2fa} ohne 2FA (Secret nicht dabei — neu einrichten)." : "."));
    }

    private async Task RestoreStoreAsync(StoreDoc s, bool dryRun, List<string> lines, CancellationToken ct)
    {
        int created = 0, updated = 0, same = 0;
        void Count(bool isNew, bool changed) { if (isNew) created++; else if (changed) updated++; else same++; }

        foreach (var src in s.Plugins)
        {
            var t = await _db.StorePlugins.FirstOrDefaultAsync(x => x.Key == src.Key, ct);
            var changed = t is null || t.Version != src.Version || !t.Bundle.AsSpan().SequenceEqual(src.Bundle);
            Count(t is null, changed);
            if (dryRun || !changed) continue;
            if (t is null) { t = new StorePlugin { Key = src.Key }; _db.StorePlugins.Add(t); }
            t.Name = src.Name; t.Version = src.Version; t.Description = src.Description; t.Bundle = src.Bundle; t.UploadedAt = src.UploadedAt;
        }
        foreach (var src in s.Templates)
        {
            var t = await _db.StoreTemplates.FirstOrDefaultAsync(x => x.Name == src.Name, ct);
            var isNew = t is null;
            t ??= new StoreTemplate();
            var changed = isNew || CopyScalars(src, t, dryRun);
            Count(isNew, changed);
            if (!dryRun && isNew) { CopyScalars(src, t, false); _db.StoreTemplates.Add(t); }
        }
        foreach (var src in s.Components)
        {
            var t = await _db.StoreComponents.FirstOrDefaultAsync(x => x.Type == src.Type, ct);
            var isNew = t is null;
            t ??= new StoreComponent();
            var changed = isNew || CopyScalars(src, t, dryRun);
            Count(isNew, changed);
            if (!dryRun && isNew) { CopyScalars(src, t, false); _db.StoreComponents.Add(t); }
        }
        foreach (var src in s.MailTemplates)
        {
            var t = await _db.StoreMailTemplates.FirstOrDefaultAsync(x => x.Key == src.Key, ct);
            var isNew = t is null;
            t ??= new StoreMailTemplate();
            var changed = isNew || CopyScalars(src, t, dryRun);
            Count(isNew, changed);
            if (!dryRun && isNew) { CopyScalars(src, t, false); _db.StoreMailTemplates.Add(t); }
        }
        if (!dryRun) await _db.SaveChangesAsync(ct);
        lines.Add($"Store: {created} neu, {updated} aktualisiert, {same} gleich.");
    }

    /// <summary>Copies the plain (string/number/bool/date) properties except Id and foreign keys; returns whether any
    /// differed. With <paramref name="compareOnly"/> nothing is written.</summary>
    private static bool CopyScalars<T>(T src, T dst, bool compareOnly)
    {
        var changed = false;
        foreach (var p in typeof(T).GetProperties())
        {
            if (!p.CanWrite || p.Name == "Id" || p.Name.EndsWith("Id", StringComparison.Ordinal) && p.PropertyType == typeof(int)) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(double) || t == typeof(byte[]))) continue;
            var a = p.GetValue(src);
            var b = p.GetValue(dst);
            var same = a is byte[] ab && b is byte[] bb ? ab.AsSpan().SequenceEqual(bb) : Equals(a, b);
            if (same) continue;
            changed = true;
            if (!compareOnly) p.SetValue(dst, a);
        }
        return changed;
    }

    private async Task RestoreProfilesAsync(List<ProfileDoc> profiles, Dictionary<string, string> secrets, bool dryRun, List<string> lines, CancellationToken ct)
    {
        foreach (var pd in profiles)
        {
            var src = pd.Profile;
            var target = await _db.Profiles.FirstOrDefaultAsync(p => p.Name == src.Name, ct);
            var what = $"Profil „{src.Name}“: {(target is null ? "wird angelegt" : "wird ersetzt")} — {pd.Settings.Count} Einstellung(en), {pd.Users.Count} Benutzer, " +
                       $"{pd.Plugins.Count + pd.StorePlugins.Count} Plugin(s), {pd.Components.Count + pd.StoreComponents.Count} Komponente(n), " +
                       $"{pd.Templates.Count + pd.StoreTemplates.Count} Template(s), {pd.MailTemplates.Count + pd.StoreMailTemplates.Count} Mail-Template(s)";
            var notes = new List<string>();
            if (dryRun) { lines.Add(what + "."); continue; }

            if (target is null)
            {
                target = new Profile { Name = src.Name, CreatedAt = src.CreatedAt == default ? DateTime.UtcNow : src.CreatedAt };
                _db.Profiles.Add(target);
            }
            var keepRevision = target.Revision;
            var keepId = target.Id;
            var keepCode = target.JoinCode;
            var wasDefault = target.IsDefault;
            CopyScalars(src, target, false);
            target.Id = keepId;
            // There is always a default profile: restoring a non-default one over it must not leave the cloud without.
            target.IsDefault = src.IsDefault || wasDefault;
            target.Revision = keepRevision + 1;   // the instances pick the restored configuration up on their next beat
            // The join code comes along unless another profile here already uses it (codes must stay unique).
            var codeTaken = await _db.Profiles.AnyAsync(p => p.Id != keepId && p.JoinCode == src.JoinCode, ct);
            target.JoinCode = codeTaken ? (string.IsNullOrEmpty(keepCode) ? ProfileService.NewJoinCode() : keepCode) : src.JoinCode;
            if (codeTaken) notes.Add("Join-Code war hier schon vergeben, der bisherige bleibt");
            await _db.SaveChangesAsync(ct);

            // Replace the payload rows.
            _db.ProfileSettings.RemoveRange(_db.ProfileSettings.Where(x => x.ProfileId == target.Id));
            _db.ProfileUsers.RemoveRange(_db.ProfileUsers.Where(x => x.ProfileId == target.Id));
            _db.ProfilePlugins.RemoveRange(_db.ProfilePlugins.Where(x => x.ProfileId == target.Id));
            _db.ProfileTemplates.RemoveRange(_db.ProfileTemplates.Where(x => x.ProfileId == target.Id));
            _db.ProfileComponents.RemoveRange(_db.ProfileComponents.Where(x => x.ProfileId == target.Id));
            _db.ProfileMailTemplates.RemoveRange(_db.ProfileMailTemplates.Where(x => x.ProfileId == target.Id));
            _db.ProfileStorePlugins.RemoveRange(_db.ProfileStorePlugins.Where(x => x.ProfileId == target.Id));
            _db.ProfileStoreTemplates.RemoveRange(_db.ProfileStoreTemplates.Where(x => x.ProfileId == target.Id));
            _db.ProfileStoreComponents.RemoveRange(_db.ProfileStoreComponents.Where(x => x.ProfileId == target.Id));
            _db.ProfileStoreMailTemplates.RemoveRange(_db.ProfileStoreMailTemplates.Where(x => x.ProfileId == target.Id));
            _db.ProfileGlobalUsers.RemoveRange(_db.ProfileGlobalUsers.Where(x => x.ProfileId == target.Id));
            await _db.SaveChangesAsync(ct);

            var missingSecrets = 0;
            foreach (var s in pd.Settings)
            {
                var value = s.Value;
                if (s.IsSecret)
                {
                    if (!secrets.TryGetValue($"profile:{src.Name}:{s.Key}", out var plain)) { missingSecrets++; continue; }
                    value = _secrets.Protect(plain);
                }
                _db.ProfileSettings.Add(new ProfileSetting { ProfileId = target.Id, Key = s.Key, Value = value, IsSecret = s.IsSecret });
            }
            if (missingSecrets > 0) notes.Add($"{missingSecrets} geheime Einstellung(en) ohne Passwort nicht übernommen");
            foreach (var x in pd.Users) { x.Id = 0; x.ProfileId = target.Id; x.Profile = null; _db.ProfileUsers.Add(x); }
            foreach (var x in pd.Plugins) { x.Id = 0; x.ProfileId = target.Id; x.Profile = null; _db.ProfilePlugins.Add(x); }
            foreach (var x in pd.Templates) { x.Id = 0; x.ProfileId = target.Id; x.Profile = null; _db.ProfileTemplates.Add(x); }
            foreach (var x in pd.Components) { x.Id = 0; x.ProfileId = target.Id; x.Profile = null; _db.ProfileComponents.Add(x); }
            foreach (var x in pd.MailTemplates) { x.Id = 0; x.ProfileId = target.Id; x.Profile = null; _db.ProfileMailTemplates.Add(x); }

            var missing = new List<string>();
            foreach (var k in pd.StorePlugins)
                if (await _db.StorePlugins.FirstOrDefaultAsync(x => x.Key == k, ct) is { } sp) _db.ProfileStorePlugins.Add(new() { ProfileId = target.Id, StorePluginId = sp.Id }); else missing.Add(k);
            foreach (var k in pd.StoreTemplates)
                if (await _db.StoreTemplates.FirstOrDefaultAsync(x => x.Name == k, ct) is { } st) _db.ProfileStoreTemplates.Add(new() { ProfileId = target.Id, StoreTemplateId = st.Id }); else missing.Add(k);
            foreach (var k in pd.StoreComponents)
                if (await _db.StoreComponents.FirstOrDefaultAsync(x => x.Type == k, ct) is { } sc) _db.ProfileStoreComponents.Add(new() { ProfileId = target.Id, StoreComponentId = sc.Id }); else missing.Add(k);
            foreach (var k in pd.StoreMailTemplates)
                if (await _db.StoreMailTemplates.FirstOrDefaultAsync(x => x.Key == k, ct) is { } sm) _db.ProfileStoreMailTemplates.Add(new() { ProfileId = target.Id, StoreMailTemplateId = sm.Id }); else missing.Add(k);
            foreach (var n in pd.GlobalUsers)
                if (await _db.Users.FirstOrDefaultAsync(u => u.Username == n, ct) is { } gu) _db.ProfileGlobalUsers.Add(new() { ProfileId = target.Id, UserId = gu.Id }); else missing.Add("@" + n);
            if (missing.Count > 0) notes.Add("nicht gefunden (Store/Benutzer mit wiederherstellen?): " + string.Join(", ", missing));

            if (src.IsDefault)
                await _db.Profiles.Where(p => p.Id != target.Id && p.IsDefault).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDefault, false), ct);
            await _db.SaveChangesAsync(ct);
            lines.Add(what + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")." : "."));
        }
    }

    private async Task RestoreAssignmentsAsync(List<AssignmentDoc> list, bool dryRun, List<string> lines, CancellationToken ct)
    {
        int set = 0, same = 0, missing = 0;
        var profiles = await _db.Profiles.ToDictionaryAsync(p => p.Name, p => p.Id, ct);
        var instances = await _db.Instances.ToListAsync(ct);
        foreach (var a in list)
        {
            // The same cloud restored: the public id still matches. A new cloud: the sites re-enrolled under new ids — the name.
            var inst = instances.FirstOrDefault(i => i.PublicId == a.PublicId)
                       ?? instances.FirstOrDefault(i => string.Equals(i.Name, a.Instance, StringComparison.OrdinalIgnoreCase));
            if (inst is null || !profiles.TryGetValue(a.Profile, out var pid)) { missing++; continue; }
            if (inst.ProfileId == pid) { same++; continue; }
            set++;
            if (!dryRun) inst.ProfileId = pid;
        }
        if (!dryRun) await _db.SaveChangesAsync(ct);
        lines.Add($"Zuordnungen: {set} geändert, {same} schon richtig, {missing} Instanz/Profil hier nicht gefunden.");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>Rewrites the user rows of the notification matrix: export turns "u:&lt;id&gt;" into "u:@name", import back.</summary>
    private static string MapMatrixUsers(string raw, Func<int, string?>? toName, Func<string, int?>? toId = null)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(raw);
            // Stored with the web naming policy ("rows"/"key"); PascalCase read too, for a hand-made value.
            var keyName = node?["rows"] is not null ? "key" : "Key";
            if ((node?["rows"] ?? node?["Rows"]) is not System.Text.Json.Nodes.JsonArray rows) return raw;
            foreach (var r in rows.ToList())
            {
                var key = r?[keyName]?.GetValue<string>();
                if (key is null || !key.StartsWith("u:")) continue;
                string? mapped = null;
                if (toName is not null && int.TryParse(key[2..], out var uid)) mapped = toName(uid) is { } n ? "u:" + n : null;
                else if (toId is not null && key.StartsWith("u:@")) mapped = toId(key[3..]) is int nid ? "u:" + nid : null;
                else continue;
                if (mapped is null) rows.Remove(r); else r![keyName] = mapped;
            }
            return node.ToJsonString();
        }
        catch { return raw; }
    }

    private const int Iterations = 310_000;

    private static SecretBox Seal(string plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, data, cipher, tag);
        return new SecretBox { Iterations = Iterations, Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce),
            Data = Convert.ToBase64String(cipher.Concat(tag).ToArray()) };
    }

    private static string? Open(SecretBox box, string passphrase)
    {
        try
        {
            var all = Convert.FromBase64String(box.Data);
            var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, Convert.FromBase64String(box.Salt), box.Iterations, HashAlgorithmName.SHA256, 32);
            var plain = new byte[all.Length - 16];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(Convert.FromBase64String(box.Nonce), all.AsSpan(0, all.Length - 16), all.AsSpan(all.Length - 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }
}
