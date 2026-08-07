using MatCMS.Data;
using MatCMS.Models;
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

    public sealed record SyncResult(bool Ok, int Revision, string? Error, List<string> Applied);

    /// <summary>
    /// Applies a whole configuration. <paramref name="fetchPlugin"/> downloads one plugin bundle by
    /// key — passed in so this class stays free of HTTP and can be reasoned about (and tested)
    /// without a cloud.
    /// </summary>
    public async Task<SyncResult> ApplyAsync(
        CloudConfig config, Func<string, CancellationToken, Task<byte[]?>> fetchPlugin,
        CancellationToken ct = default)
    {
        var applied = new List<string>();
        try
        {
            if (config.Settings is not null)
                applied.Add($"{await ApplySettingsAsync(config.Settings, config.OverwriteSettings, ct)} Einstellungen");

            if (config.Users is not null)
                applied.Add($"{await ApplyUsersAsync(config.Users, ct)} Benutzer");

            if (config.Components is not null)
                applied.Add($"{await ApplyComponentsAsync(config.Components, config.OverwriteComponents, ct)} Komponenten");

            if (config.Templates is not null)
                applied.Add($"{await ApplyTemplatesAsync(config.Templates, config.OverwriteTemplates, config.ActivateTemplate, ct)} Templates");

            if (config.Plugins is not null)
                applied.Add($"{await ApplyPluginsAsync(config.Plugins, config.OverwritePlugins, fetchPlugin, ct)} Plugins");

            await SetStateAsync(config.Revision, null, ct);
            _log.LogInformation("Cloud configuration revision {Revision} applied: {Applied}",
                config.Revision, string.Join(", ", applied));
            return new(true, config.Revision, null, applied);
        }
        catch (Exception ex)
        {
            // Keep the previously applied revision: a failed apply must not look like a successful
            // one, and the cloud shows the error verbatim.
            var message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await SetStateAsync(await AppliedRevisionAsync(ct), message, ct);
            _log.LogWarning(ex, "Applying cloud configuration failed");
            return new(false, config.Revision, message, applied);
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
                count++;
            }
            else if (overwrite || string.IsNullOrEmpty(row.Value))
            {
                if (row.Value != (value ?? "")) count++;
                row.Value = value ?? "";
            }
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    // --- Users --------------------------------------------------------------

    /// <summary>Add-only by design: an existing account is left exactly as it is, including its
    /// password. The cloud can hand out new logins, never take one over.</summary>
    private async Task<int> ApplyUsersAsync(List<CloudConfigUser> users, CancellationToken ct)
    {
        var count = 0;
        foreach (var u in users)
        {
            var name = (u.Username ?? "").Trim();
            if (name.Length == 0 || string.IsNullOrWhiteSpace(u.PasswordHash)) continue;

            if (await _db.Users.AnyAsync(x => x.Username == name, ct)) continue;

            _db.Users.Add(new User
            {
                Username = name,
                Email = u.Email,
                DisplayName = u.DisplayName,
                PasswordHash = u.PasswordHash,
                Role = string.IsNullOrWhiteSpace(u.Role) ? "Admin" : u.Role
            });
            count++;
        }

        await _db.SaveChangesAsync(ct);
        return count;
    }

    // --- Components ---------------------------------------------------------

    private async Task<int> ApplyComponentsAsync(
        List<CloudConfigComponent> components, bool overwrite, CancellationToken ct)
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
                count++;
            }
            else if (overwrite)
            {
                row.Name = c.Name;
                row.Description = c.Description;
                row.Icon = c.Icon;
                row.FieldsJson = string.IsNullOrWhiteSpace(c.FieldsJson) ? "[]" : c.FieldsJson;
                row.TemplateHtml = c.TemplateHtml;
                count++;
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
        List<CloudConfigTemplate> templates, bool overwrite, string? activate, CancellationToken ct)
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
            else if (!overwrite) continue;

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
            if (target is not null && !target.IsActive)
            {
                foreach (var other in await _db.Templates.Where(x => x.IsActive).ToListAsync(ct))
                    other.IsActive = false;
                target.IsActive = true;
                await _db.SaveChangesAsync(ct);
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
        List<CloudConfigPlugin> plugins, bool overwrite,
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
                if (!overwrite) continue;
                if (string.Equals(installed.Version ?? "", p.Version ?? "", StringComparison.OrdinalIgnoreCase))
                    continue; // same version — nothing to do
            }

            var bundle = await fetchPlugin(key, ct);
            if (bundle is null || bundle.Length == 0)
                throw new InvalidOperationException($"Plugin-Paket '{key}' konnte nicht geladen werden.");

            using var ms = new MemoryStream(bundle);
            var (plugin, _, error) = await PluginPackager.ImportAsync(ms, _env, _db);
            if (plugin is null)
                throw new InvalidOperationException($"Plugin '{key}': {error ?? "Import fehlgeschlagen."}");
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
        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var row = await _db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) _db.SiteSettings.Add(new SiteSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
