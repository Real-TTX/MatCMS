using System.Net.Http.Json;
using MatCMS.Data;
using MatCMS.Models;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Services;

/// <summary>
/// Browses the connected MatCMS.Cloud's store and installs from it on demand — the "Weiter
/// durchsuchen…" path. Independent of the profile sync: this is the site pulling something it wants,
/// not the cloud rolling something out.
/// <para>Installing reuses the exact same code the sync applier uses (<see cref="PluginPackager"/>
/// for a bundle, the template/component upserts here), so an item installed from the catalogue is
/// indistinguishable from one that arrived through a profile.</para>
/// </summary>
public class CloudCatalogService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly CloudService _cloud;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CloudCatalogService> _log;

    public CloudCatalogService(
        AppDbContext db, IHttpClientFactory http, CloudService cloud,
        IWebHostEnvironment env, ILogger<CloudCatalogService> log)
    {
        _db = db;
        _http = http;
        _cloud = cloud;
        _env = env;
        _log = log;
    }

    public sealed class Catalog
    {
        public List<CatalogPlugin> Plugins { get; set; } = new();
        public List<CatalogTemplate> Templates { get; set; } = new();
        public List<CatalogComponent> Components { get; set; } = new();
    }

    public sealed class CatalogPlugin
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public sealed class CatalogTemplate
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string AccentColor { get; set; } = "";
    }

    public sealed class CatalogComponent
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>True when a cloud is connected at all — the browse button only makes sense then.</summary>
    public async Task<bool> IsAvailableAsync() => (await _cloud.GetSettingsAsync()).Configured;

    /// <summary>Fetches the catalogue. Never throws — a cloud that is down must not break the
    /// plugins page, it just means there is nothing to browse right now.</summary>
    public async Task<(Catalog? catalog, string? error)> GetCatalogAsync(CancellationToken ct = default)
    {
        var settings = await _cloud.GetSettingsAsync();
        if (!settings.Configured) return (null, "Es ist keine Cloud verbunden.");

        try
        {
            var client = CreateClient(settings);
            var res = await client.GetAsync($"{settings.Url}/api/store/{settings.InstanceId}/catalog", ct);
            if (!res.IsSuccessStatusCode)
                return (null, res.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Die Cloud hat den Zugriff abgelehnt — ist diese Instanz freigegeben?"
                    : $"Die Cloud antwortete mit HTTP {(int)res.StatusCode}.");

            return (await res.Content.ReadFromJsonAsync<Catalog>(ct), null);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Catalogue fetch failed");
            return (null, $"Die Cloud war nicht erreichbar: {ex.Message}");
        }
    }

    public async Task<(bool ok, string message)> InstallPluginAsync(string key, CancellationToken ct = default)
    {
        var settings = await _cloud.GetSettingsAsync();
        if (!settings.Configured) return (false, "Es ist keine Cloud verbunden.");

        try
        {
            var client = CreateClient(settings);
            var res = await client.GetAsync($"{settings.Url}/api/store/{settings.InstanceId}/plugin/{Uri.EscapeDataString(key)}", ct);
            if (!res.IsSuccessStatusCode) return (false, $"Paket nicht erhalten (HTTP {(int)res.StatusCode}).");

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            // The instance's own importer, exactly as for a manual upload — which also means the
            // plugin arrives DISABLED and somebody has to switch it on.
            var (plugin, updated, error) = await PluginPackager.ImportAsync(stream, _env, _db);
            if (plugin is null) return (false, error ?? "Import fehlgeschlagen.");

            return (true, updated
                ? $"Plugin \"{plugin.Name}\" aktualisiert. Es ist deaktiviert, bis du es freischaltest."
                : $"Plugin \"{plugin.Name}\" installiert. Es ist deaktiviert, bis du es freischaltest.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Installing plugin {Key} from the catalogue failed", key);
            return (false, ex.Message);
        }
    }

    public async Task<(bool ok, string message)> InstallTemplateAsync(string name, CancellationToken ct = default)
    {
        var settings = await _cloud.GetSettingsAsync();
        if (!settings.Configured) return (false, "Es ist keine Cloud verbunden.");

        try
        {
            var client = CreateClient(settings);
            var res = await client.GetAsync($"{settings.Url}/api/store/{settings.InstanceId}/template/{Uri.EscapeDataString(name)}", ct);
            if (!res.IsSuccessStatusCode) return (false, $"Template nicht erhalten (HTTP {(int)res.StatusCode}).");

            var t = await res.Content.ReadFromJsonAsync<CloudConfigTemplate>(ct);
            if (t is null || string.IsNullOrWhiteSpace(t.Name)) return (false, "Leeres Template erhalten.");

            var row = await _db.Templates.FirstOrDefaultAsync(x => x.Name == t.Name, ct);
            var isNew = row is null;
            if (row is null)
            {
                row = new Template { Name = t.Name };
                _db.Templates.Add(row);
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
            // Only on a fresh install: an existing template's tuned parameter values belong to this
            // site and must survive a re-install.
            if (isNew) row.ParamValuesJson = string.IsNullOrWhiteSpace(t.ParamValuesJson) ? "{}" : t.ParamValuesJson;

            await _db.SaveChangesAsync(ct);
            // Never activated automatically — installing a design is not the same as switching to it.
            return (true, isNew
                ? $"Template \"{row.Name}\" installiert. Unter Templates aktivieren, um es zu verwenden."
                : $"Template \"{row.Name}\" aktualisiert.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Installing template {Name} from the catalogue failed", name);
            return (false, ex.Message);
        }
    }

    public async Task<(bool ok, string message)> InstallComponentAsync(string type, CancellationToken ct = default)
    {
        var settings = await _cloud.GetSettingsAsync();
        if (!settings.Configured) return (false, "Es ist keine Cloud verbunden.");

        try
        {
            var client = CreateClient(settings);
            var res = await client.GetAsync($"{settings.Url}/api/store/{settings.InstanceId}/component/{Uri.EscapeDataString(type)}", ct);
            if (!res.IsSuccessStatusCode) return (false, $"Komponente nicht erhalten (HTTP {(int)res.StatusCode}).");

            var c = await res.Content.ReadFromJsonAsync<CloudConfigComponent>(ct);
            if (c is null || string.IsNullOrWhiteSpace(c.Type)) return (false, "Leere Komponente erhalten.");

            var slug = c.Type.Trim().ToLowerInvariant();
            var row = await _db.Components.FirstOrDefaultAsync(x => x.Type == slug, ct);
            var isNew = row is null;
            if (row is null)
            {
                row = new Component { Type = slug };
                _db.Components.Add(row);
            }

            row.Name = c.Name;
            row.Description = c.Description;
            row.Icon = c.Icon;
            row.FieldsJson = string.IsNullOrWhiteSpace(c.FieldsJson) ? "[]" : c.FieldsJson;
            row.TemplateHtml = c.TemplateHtml;

            await _db.SaveChangesAsync(ct);
            return (true, isNew
                ? $"Komponente \"{row.Name}\" installiert."
                : $"Komponente \"{row.Name}\" aktualisiert.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Installing component {Type} from the catalogue failed", type);
            return (false, ex.Message);
        }
    }

    private HttpClient CreateClient(CloudService.CloudSettings settings)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MatCMS-Instance");
        client.DefaultRequestHeaders.Add(CloudProtocol.TokenHeader, settings.Token);
        return client;
    }
}
