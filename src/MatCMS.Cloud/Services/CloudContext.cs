using MatCMS.Cloud.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Per-request accessor for the key/value settings, so views can read them without each page model
/// re-querying. Loads the whole (tiny) settings table once per request and caches it.
/// </summary>
public class CloudContext
{
    private readonly AppDbContext _db;
    private Dictionary<string, string?>? _settings;

    public CloudContext(AppDbContext db) => _db = db;

    private Dictionary<string, string?> Settings =>
        _settings ??= _db.CloudSettings.AsNoTracking()
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Settings.TryGetValue(key, out var v) ? v : null;

    public bool Flag(string key) =>
        (Get(key) ?? "").Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    public string CloudName
    {
        get
        {
            var v = Get(SettingKeys.CloudName);
            return string.IsNullOrWhiteSpace(v) ? "MatCMS.Cloud" : v!;
        }
    }

    public string LogoUrl => "/img/logo.svg";

    /// <summary>Absolute base URL for links in notification mails. Falls back to the current request,
    /// which is only correct when not behind a scheme-changing proxy — hence the setting.</summary>
    public string CanonicalBaseUrl(HttpRequest? request)
    {
        var configured = (Get(SettingKeys.CanonicalUrl) ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(configured)) return configured;
        return request is null ? "" : $"{request.Scheme}://{request.Host}";
    }

    /// <summary>Writes a batch of settings and clears the per-request cache. Only the given keys are
    /// touched, so one settings form never wipes another's values.</summary>
    public async Task SaveAsync(IDictionary<string, string?> values)
    {
        var keys = values.Keys.ToList();
        var rows = await _db.CloudSettings.Where(s => keys.Contains(s.Key)).ToListAsync();
        foreach (var (key, value) in values)
        {
            var row = rows.FirstOrDefault(r => r.Key == key);
            if (row is null)
                _db.CloudSettings.Add(new Models.CloudSetting { Key = key, Value = value });
            else
                row.Value = value;
        }
        await _db.SaveChangesAsync();
        _settings = null;
    }
}
