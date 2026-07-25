using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MatCMS.Services;

/// <summary>
/// Lightweight, git-friendly localizer. Strings live in <c>Resources/&lt;culture&gt;.json</c>
/// (flat key → text maps). The current UI culture is set by RequestLocalization (cookie/default).
/// Missing keys fall back to the default culture (de) and finally to the key itself.
/// Usage in views: <c>@T["nav.pages"]</c>.
/// </summary>
public class Localizer
{
    public const string DefaultCulture = "de";

    /// <summary>
    /// All cultures the site supports, for both the admin UI language and the public content
    /// locales. German ("de") is the default and is served at the root URLs; every other entry is
    /// served under a culture prefix (/en, /fr …). Adding a language = drop a Resources/&lt;c&gt;.json
    /// and add the code here — routing, the language switcher and this localizer pick it up.
    /// </summary>
    public static readonly string[] SupportedCultures = [DefaultCulture /* , "en" */];

    /// <summary>Supported cultures other than the default (served under a URL prefix).</summary>
    public static readonly IReadOnlyList<string> NonDefaultCultures =
        SupportedCultures.Where(c => c != DefaultCulture).ToArray();

    /// <summary>True if <paramref name="culture"/> is one of the supported cultures.</summary>
    public static bool IsSupported(string? culture) =>
        !string.IsNullOrEmpty(culture) && SupportedCultures.Contains(culture);

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
        // Use Name (e.g. "de", "de-DE", "en") and take the language part. NB: under
        // InvariantGlobalization, TwoLetterISOLanguageName collapses to "iv", so we must
        // rely on Name here — otherwise a non-default culture could never be matched.
        var culture = CultureInfo.CurrentUICulture.Name.Split('-', 2)[0];
        if (string.IsNullOrEmpty(culture)) culture = DefaultCulture;
        foreach (var c in culture == DefaultCulture ? new[] { culture } : new[] { culture, DefaultCulture })
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
