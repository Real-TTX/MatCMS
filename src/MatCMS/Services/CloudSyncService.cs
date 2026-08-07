using MatCMS.Data;
using MatCMS.Models;
using MatCMS.Shared;
using Microsoft.EntityFrameworkCore;
namespace MatCMS.Services;

/// <summary>
/// Applies the configuration a MatCMS.Cloud profile hands out: settings, users, components and
/// plugins. Pull-based — the cloud only ever tells us a revision changed, we decide when to fetch
/// and apply.
/// <para>Three rules hold across every payload, and they are the difference between a sync you can
/// trust and one that eats a customer's site:</para>
/// <list type="bullet">
/// <item><b>Users are add-only.</b> Never updated, never deleted — an operator must not be able to
/// lock themselves out of their own site through a cloud setting.</item>
/// <item><b>Nothing is deleted</b> that the profile no longer contains. Removing a plugin from a
/// profile stops future rollouts; it does not rip it out of running sites.</item>
/// <item><b>A section that is null is untouched.</b> "Profile doesn't sync this" and "profile syncs
/// an empty list" must never look the same.</item>
/// </list>
/// </summary>
public class CloudSyncService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CloudSyncService> _log;

    public CloudSyncService(AppDbContext db, IWebHostEnvironment env, ILogger<CloudSyncService> log)
    {
        _db = db;
        _env = env;
        _log = log;
    }

    public sealed record SyncResult(bool Ok, int Revision, string? Error, List<string> Applied,
        List<SyncItemReport> Report);

    /// <summary>What this apply did, item by item. Collected while applying and reported to the cloud
    /// on the next heartbeat — the cloud derives nothing, it only keeps the record.</summary>
    private readonly List<SyncItemReport> _report = new();

    private void Report(string kind, string id, string outcome, string? detail = null) =>
        _report.Add(new SyncItemReport { Kind = kind, Id = id, Outcome = outcome, Detail = detail });

    /// <summary>Names the payloads carry in the seed mark. Stable strings, not enum numbers — the
    /// mark outlives builds.</summary>
    private const string PayloadSettings = "settings";
    private const string PayloadUsers = "users";
    private const string PayloadComponents = "components";
    private const string PayloadTemplates = "templates";
    private const string PayloadPlugins = "plugins";

    /// <summary>What to do with one payload this time round.</summary>
    /// <param name="Run">False = skip it entirely (a "once" payload already seeded here).</param>
    /// <param name="Overwrite">Make existing items match the profile, instead of only adding.</param>
    /// <param name="Seed">Record the payload as seeded once it has been applied without error.</param>
    private readonly record struct PayloadPlan(bool Run, bool Overwrite, bool Seed);

    /// <summary>
    /// Turns a profile's mode for one payload into a decision. "once" is the interesting one: the
    /// FIRST apply is a full rollout — seeding a site with half a configuration because something
    /// happened to exist there already would be useless — and every apply after it does nothing at
    /// all. Anything unrecognised is read as "add": a mode this build does not know must not be
    /// allowed to overwrite a live site.
    /// </summary>
    private static PayloadPlan Plan(string payload, string? mode, HashSet<string> seeded) =>
        (mode ?? "").Trim().ToLowerInvariant() switch
        {
            "once" => seeded.Contains(payload) ? new(false, false, false) : new(true, true, true),
            "keep" => new(true, true, false),
            _ => new(true, false, false)
        };

    /// <summary>Lists everything a skipped "once" payload would have contained. The cloud asked what
    /// happened — "nothing, and here is exactly what that covers" is an answer, silence is not.</summary>
    private void ReportSkippedOnce(string kind, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            Report(kind, id.Trim(), "skipped-once", "Einmalige Übernahme ist bereits erfolgt.");
        }
    }

    /// <summary>
    /// Applies a whole configuration. <paramref name="fetchPlugin"/> downloads one plugin bundle by
    /// key — passed in so this class stays free of HTTP and can be reasoned about (and tested)
    /// without a cloud.
    /// </summary>
    public async Task<SyncResult> ApplyAsync(
        InstanceConfig config, Func<string, CancellationToken, Task<byte[]?>> fetchPlugin,
        CancellationToken ct = default)
    {
        var applied = new List<string>();
        _report.Clear();
        try
        {
            var seeded = await LoadSeededAsync(config.ProfileId, ct);
            var newlySeeded = new List<string>();

            if (config.Settings is not null)
            {
                var plan = Plan(PayloadSettings, config.SettingsMode, seeded);
                if (plan.Run)
                {
                    applied.Add($"{await ApplySettingsAsync(config.Settings, plan.Overwrite, ct)} Einstellungen");
                    if (plan.Seed) newlySeeded.Add(PayloadSettings);
                }
                else ReportSkippedOnce("setting", config.Settings.Keys);
            }

            if (config.Users is not null)
            {
                var plan = Plan(PayloadUsers, config.UsersMode, seeded);
                if (plan.Run)
                {
                    applied.Add($"{await ApplyUsersAsync(config.Users, ct)} Benutzer");
                    if (plan.Seed) newlySeeded.Add(PayloadUsers);
                }
                else ReportSkippedOnce("user", config.Users.Select(u => u.Username));
            }

            if (config.Components is not null)
            {
                var plan = Plan(PayloadComponents, config.ComponentsMode, seeded);
                if (plan.Run)
                {
                    applied.Add($"{await ApplyComponentsAsync(config.Components, plan.Overwrite, ct)} Komponenten");
                    if (plan.Seed) newlySeeded.Add(PayloadComponents);
                }
                else ReportSkippedOnce("component", config.Components.Select(c => c.Type));
            }

            if (config.Templates is not null)
            {
                var plan = Plan(PayloadTemplates, config.TemplatesMode, seeded);
                if (plan.Run)
                {
                    applied.Add($"{await ApplyTemplatesAsync(config.Templates, plan.Overwrite, config.ActivateTemplate, ct)} Templates");
                    if (plan.Seed) newlySeeded.Add(PayloadTemplates);
                }
                else ReportSkippedOnce("template", config.Templates.Select(t => t.Name));
            }

            if (config.Plugins is not null)
            {
                var plan = Plan(PayloadPlugins, config.PluginsMode, seeded);
                if (plan.Run)
                {
                    applied.Add($"{await ApplyPluginsAsync(config.Plugins, plan.Overwrite, fetchPlugin, ct)} Plugins");
                    if (plan.Seed) newlySeeded.Add(PayloadPlugins);
                }
                else ReportSkippedOnce("plugin", config.Plugins.Select(p => p.Key));
            }

            // Only after everything went through: a payload that threw must be allowed to seed again
            // on the next attempt, otherwise a single failed rollout would freeze it forever.
            if (newlySeeded.Count > 0)
            {
                seeded.UnionWith(newlySeeded);
                await SetSeededAsync(config.ProfileId, seeded, ct);
            }

            await SetStateAsync(config.Revision, null, ct);
            _log.LogInformation("Cloud configuration revision {Revision} applied: {Applied}",
                config.Revision, string.Join(", ", applied));
            await SetReportAsync(ct);
            return new(true, config.Revision, null, applied, _report);
        }
        catch (Exception ex)
        {
            // Keep the previously applied revision: a failed apply must not look like a successful
            // one, and the cloud shows the error verbatim.
            var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await SetStateAsync(await AppliedRevisionAsync(ct), message, ct);
            _log.LogWarning(ex, "Applying cloud configuration failed");
            await SetReportAsync(ct);
            return new(false, config.Revision, message, applied, _report);
        }
    }

    // --- Settings -----------------------------------------------------------

    private async Task<int> ApplySettingsAsync(
        Dictionary<string, string?> settings, bool overwrite, CancellationToken ct)
    {
        // Guard: the cloud link keys live in the same table. Letting a profile push those would let
        // one instance's configuration hijack another's cloud connection.
        var forbidden = SettingKeys.Cloud.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var (key, value) in settings)
        {
            if (string.IsNullOrWhiteSpace(key) || forbidden.Contains(key)) continue;

            var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (row is null)
            {
                _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value ?? "" });
                Report("setting", key, "installed");
                count++;
            }
            else if (overwrite || string.IsNullOrEmpty(row.Value))
            {
                // Only a real change counts — re-sending an identical value is not an update, and
                // reporting it as one would make every revision look like it touched the whole site.
                if (row.Value != (value ?? "")) { Report("setting", key, "updated"); count++; }
                else Report("setting", key, "skipped-exists", "Wert ist bereits gesetzt");
                row.Value = value ?? "";
            }
            else
            {
                Report("setting", key, "skipped-exists", "Eigener Wert bleibt erhalten");
            }
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    // --- Users --------------------------------------------------------------

    /// <summary>Add-only by design: an existing account is left exactly as it is, including its
    /// password. The cloud can hand out new logins, never take one over.</summary>
    private async Task<int> ApplyUsersAsync(List<ConfigUser> users, CancellationToken ct)
    {
        var count = 0;
        foreach (var u in users)
        {
            var name = (u.Username ?? "").Trim();
            if (name.Length == 0 || string.IsNullOrWhiteSpace(u.PasswordHash)) continue;

            if (await _db.Users.AnyAsync(x => x.Username == name, ct))
            {
                Report("user", name, "skipped-exists");
                continue;
            }

            _db.Users.Add(new User
            {
                Username = name,
                Email = u.Email,
                DisplayName = u.DisplayName,
                PasswordHash = u.PasswordHash,
                Role = string.IsNullOrWhiteSpace(u.Role) ? "Admin" : u.Role
            });
            Report("user", name, "installed");
            count++;
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    // --- Components ---------------------------------------------------------

    private async Task<int> ApplyComponentsAsync(
        List<ConfigComponent> components, bool overwrite, CancellationToken ct)
    {
        var count = 0;
        foreach (var c in components)
        {
            var type = (c.Type ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0) continue;

            var row = await _db.Components.FirstOrDefaultAsync(x => x.Type == type, ct);
            if (row is null)
            {
                _db.Components.Add(new Component
                {
                    Type = type,
                    Name = c.Name,
                    Description = c.Description,
                    Icon = c.Icon,
                    FieldsJson = string.IsNullOrWhiteSpace(c.FieldsJson) ? "[]" : c.FieldsJson,
                    TemplateHtml = c.TemplateHtml
                });
                Report("component", type, "installed");
                count++;
            }
            else if (overwrite)
            {
                row.Name = c.Name;
                row.Description = c.Description;
                row.Icon = c.Icon;
                row.FieldsJson = string.IsNullOrWhiteSpace(c.FieldsJson) ? "[]" : c.FieldsJson;
                row.TemplateHtml = c.TemplateHtml;
                Report("component", type, "updated");
                count++;
            }
            else
            {
                Report("component", type, "skipped-exists");
            }
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    // --- Templates ----------------------------------------------------------

    /// <summary>
    /// Rolls out themes by <c>Name</c>. Two deliberate restraints:
    /// <list type="bullet">
    /// <item>The <b>active</b> template is only switched when the profile explicitly names one
    /// (<paramref name="activate"/>). Changing which design a live customer site runs must be a
    /// decision, not a side effect of a config sync.</item>
    /// <item><c>ParamValuesJson</c> — the values a site's own admin tuned on the published template
    /// parameters — is only taken over when the template is NEW here. Overwriting it would throw
    /// away per-site customisation on every revision bump.</item>
    /// </list>
    /// </summary>
    private async Task<int> ApplyTemplatesAsync(
        List<ConfigTemplate> templates, bool overwrite, string? activate, CancellationToken ct)
    {
        var count = 0;
        foreach (var t in templates)
        {
            var name = (t.Name ?? "").Trim();
            if (name.Length == 0) continue;

            var row = await _db.Templates.FirstOrDefaultAsync(x => x.Name == name, ct);
            var isNew = row is null;
            if (row is null)
            {
                row = new Template { Name = name };
                _db.Templates.Add(row);
            }
            else if (!overwrite)
            {
                Report("template", name, "skipped-exists");
                continue;
            }

            row.AccentColor = t.AccentColor;
            row.SecondaryColor = t.SecondaryColor;
            row.HeadingFont = t.HeadingFont;
            row.BodyFont = t.BodyFont;
            row.ButtonStyle = t.ButtonStyle;
            row.HeadingColor = t.HeadingColor;
            row.TextColor = t.TextColor;
            row.BackgroundColor = t.BackgroundColor;
            row.AltBackground = t.AltBackground;
            row.ContainerWidth = t.ContainerWidth;
            row.ButtonRadius = t.ButtonRadius;
            row.HeaderBackground = t.HeaderBackground;
            row.HeaderTextColor = t.HeaderTextColor;
            row.HeaderPadding = t.HeaderPadding;
            row.CustomCss = t.CustomCss;
            row.CustomJs = t.CustomJs;
            row.LayoutHtml = t.LayoutHtml;
            row.MenuMapJson = string.IsNullOrWhiteSpace(t.MenuMapJson) ? "{}" : t.MenuMapJson;
            row.ParametersJson = string.IsNullOrWhiteSpace(t.ParametersJson) ? "[]" : t.ParametersJson;
            row.SchemaVersion = t.SchemaVersion <= 0 ? 1 : t.SchemaVersion;
            row.PartsJson = string.IsNullOrWhiteSpace(t.PartsJson) ? "{}" : t.PartsJson;
            if (isNew)
                row.ParamValuesJson = string.IsNullOrWhiteSpace(t.ParamValuesJson) ? "{}" : t.ParamValuesJson;

            Report("template", name, isNew ? "installed" : "updated");
            count++;
        }

        await _db.SaveChangesAsync(ct);

        // Activation is a separate step: exactly one template is active, so this both sets the named
        // one and clears the rest. An unknown name is ignored rather than leaving the site with no
        // active design at all.
        var wanted = (activate ?? "").Trim();
        if (wanted.Length > 0)
        {
            var target = await _db.Templates.FirstOrDefaultAsync(x => x.Name == wanted, ct);
            if (target is null)
                Report("template", wanted, "failed", "Soll aktiviert werden, ist hier aber nicht vorhanden.");
            else if (!target.IsActive)
            {
                foreach (var other in await _db.Templates.Where(x => x.IsActive).ToListAsync(ct))
                    other.IsActive = false;
                target.IsActive = true;
                await _db.SaveChangesAsync(ct);
                Report("template", wanted, "updated", "Als aktives Design gesetzt");
            }
        }

        return count;
    }

    // --- Plugins ------------------------------------------------------------

    /// <summary>
    /// Downloads and imports a bundle only when the installed version differs — a profile with a
    /// dozen plugins otherwise re-imports everything on every revision bump.
    /// <para>Imported plugins stay DISABLED (that is <c>PluginPackager.ImportAsync</c>'s own rule):
    /// plugin code runs server-side, so it takes a human on the instance to switch it on.</para>
    /// </summary>
    private async Task<int> ApplyPluginsAsync(
        List<ConfigPlugin> plugins, bool overwrite,
        Func<string, CancellationToken, Task<byte[]?>> fetchPlugin, CancellationToken ct)
    {
        var count = 0;
        foreach (var p in plugins)
        {
            var key = (p.Key ?? "").Trim();
            if (key.Length == 0) continue;

            var installed = await _db.Plugins.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (installed is not null)
            {
                if (!overwrite)
                {
                    Report("plugin", key, "skipped-exists", $"Version {installed.Version}");
                    continue;
                }
                if (string.Equals(installed.Version ?? "", p.Version ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    Report("plugin", key, "skipped-exists", $"Version {installed.Version} bereits installiert");
                    continue; // same version — nothing to do
                }
            }

            var bundle = await fetchPlugin(key, ct);
            if (bundle is null || bundle.Length == 0)
            {
                // Reported BEFORE the throw: the apply aborts here, and the report is what tells the
                // cloud which plugin broke it — the error message alone would only say that one did.
                Report("plugin", key, "failed", "Paket konnte nicht geladen werden.");
                throw new InvalidOperationException($"Plugin-Paket '{key}' konnte nicht geladen werden.");
            }

            using var ms = new MemoryStream(bundle);
            var (plugin, updated, error) = await PluginPackager.ImportAsync(ms, _env, _db);
            if (plugin is null)
            {
                Report("plugin", key, "failed", error ?? "Import fehlgeschlagen.");
                throw new InvalidOperationException($"Plugin '{key}': {error ?? "Import fehlgeschlagen."}");
            }
            Report("plugin", key, updated ? "updated" : "installed", $"Version {plugin.Version}");
            count++;
        }

        return count;
    }

    // --- Applied state ------------------------------------------------------

    public async Task<int> AppliedRevisionAsync(CancellationToken ct = default)
    {
        var raw = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.CloudAppliedRevision)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var v) ? v : 0;
    }

    public async Task<string?> LastErrorAsync(CancellationToken ct = default)
    {
        var raw = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.CloudSyncError)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>Persists the report so the next heartbeat can carry it even if the apply happened
    /// minutes earlier or the container restarted in between.</summary>
    private async Task SetReportAsync(CancellationToken ct)
    {
        await UpsertAsync(SettingKeys.CloudSyncReport,
            System.Text.Json.JsonSerializer.Serialize(_report), ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The stored report, for the heartbeat. Empty when nothing has been applied yet.</summary>
    public async Task<List<SyncItemReport>?> LastReportAsync(CancellationToken ct = default)
    {
        var raw = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.CloudSyncReport)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<SyncItemReport>>(raw); }
        catch { return null; }
    }

    /// <summary>
    /// Which payloads a "once" profile has already seeded here. Scoped to the profile: a mark written
    /// for profile 3 says nothing about profile 7, so moving this site to another profile lets that
    /// one seed as well instead of silently rolling out nothing.
    /// </summary>
    private async Task<HashSet<string>> LoadSeededAsync(int profileId, CancellationToken ct)
    {
        var raw = await _db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SettingKeys.CloudSeeded)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        var parts = (raw ?? "").Split('|', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var storedProfile) || storedProfile != profileId)
            return new(StringComparer.OrdinalIgnoreCase);

        return parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SetSeededAsync(int profileId, IEnumerable<string> payloads, CancellationToken ct)
    {
        await UpsertAsync(SettingKeys.CloudSeeded, $"{profileId}|{string.Join(',', payloads)}", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Persisted, not in-memory: the applied revision must survive a restart, otherwise
    /// every container restart re-applies the whole configuration.</summary>
    private async Task SetStateAsync(int revision, string? error, CancellationToken ct)
    {
        await UpsertAsync(SettingKeys.CloudAppliedRevision, revision.ToString(), ct);
        await UpsertAsync(SettingKeys.CloudSyncError, error ?? "", ct);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Clears the applied revision so the next heartbeat pulls and applies afresh. Used when
    /// the link changes — a new cloud or profile means the old revision number means nothing.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await UpsertAsync(SettingKeys.CloudAppliedRevision, "0", ct);
        await UpsertAsync(SettingKeys.CloudSyncError, "", ct);
        // The old report describes a configuration from a cloud or profile that no longer applies —
        // keeping it would show the new cloud a rollout it never made. Same for the seed marks: a
        // different cloud's profile ids mean nothing here.
        await UpsertAsync(SettingKeys.CloudSyncReport, "", ct);
        await UpsertAsync(SettingKeys.CloudSeeded, "", ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
