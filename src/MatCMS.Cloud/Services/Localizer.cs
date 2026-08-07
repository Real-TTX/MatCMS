using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MatCMS.Cloud.Services;

/// <summary>
/// Lightweight, git-friendly localizer. Strings live in <c>Resources/&lt;culture&gt;.json</c>
/// (flat key → text maps). The current UI culture is set by RequestLocalization (cookie / Accept-Language).
/// Missing keys fall back to the authoring culture (de) and finally to the key itself.
/// Usage in views: <c>@T["nav.instances"]</c>.
/// <para>Unlike MatCMS this app has no public content, so there is only ONE culture axis: the admin
/// UI language. Adding a language = drop Resources/&lt;code&gt;.json and add the code below.</para>
/// </summary>
public class Localizer
{
    /// <summary>The language the resource files are authored in and the ultimate fallback.</summary>
    public const string FallbackCulture = "de";

    public static readonly string[] SupportedCultures = ["de", "en"];

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["de"] = "Deutsch", ["en"] = "English"
    };

    public static string DisplayName(string code) =>
        DisplayNames.TryGetValue((code ?? "").ToLowerInvariant(), out var n) ? n : (code ?? "").ToUpperInvariant();

    private readonly IWebHostEnvironment _env;
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> _cache = new();

    public Localizer(IWebHostEnvironment env) => _env = env;

    public string this[string key] => Get(key) ?? key;

    public string this[string key, params object[] args]
    {
        get
        {
            var v = Get(key);
            return v is null ? key : string.Format(v, args);
        }
    }

    private string? Get(string key)
    {
        // Use Name (e.g. "de", "de-DE") and take the language part. NB: under InvariantGlobalization,
        // TwoLetterISOLanguageName collapses to "iv", so a non-default culture would never match.
        var culture = CultureInfo.CurrentUICulture.Name.Split('-', 2)[0];
        if (string.IsNullOrEmpty(culture)) culture = FallbackCulture;
        foreach (var c in culture == FallbackCulture ? new[] { culture } : new[] { culture, FallbackCulture })
        {
            if (Load(c).TryGetValue(key, out var v))
                return v;
        }
        return null;
    }

    private Dictionary<string, string> Load(string culture) =>
        _cache.GetOrAdd(culture, c =>
        {
            var path = Path.Combine(_env.ContentRootPath, "Resources", c + ".json");
            if (!File.Exists(path)) return new();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new();
            }
            catch
            {
                return new();
            }
        });
}
